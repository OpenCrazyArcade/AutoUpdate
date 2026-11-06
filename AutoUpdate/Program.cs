using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Managers;
using Microsoft.Extensions.Options;

using System.Linq;
using System.Windows;

namespace OpenCrazyArcade.AutoUpdate;

partial class Program { }

partial class Program {
    private readonly ILogger<Program> logger;
    private readonly App app;
    private readonly IHostApplicationLifetime lifetime;

    public Program(ILogger<Program> logger, App app, IHostApplicationLifetime lifetime)
    {
        this.logger = logger;
        this.app = app;
        this.lifetime = lifetime;

        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        app.Startup += App_Startup;
        app.Exit += App_Exit;

        lifetime.ApplicationStopping.Register(LifeTime_OnApplicationStopping);
    }

    private async void App_Startup(object sender, System.Windows.StartupEventArgs e)
    {
        await HostApp.StartAsync();
    }

    private async void App_Exit(object sender, ExitEventArgs e)
    {
        await HostApp.StopAsync();
    }

    private void LifeTime_OnApplicationStopping()
    {
        app.Dispatcher.Invoke(() => app.Shutdown());
    }

    void Run()
    {
        app.Run();
    }
}

partial class Program
{
    public static WebApplication HostApp { get; private set; }

    // One log file per run would otherwise pile up for ever.
    static void PruneOldLauncherLogs(string dir, int keep)
    {
        try
        {
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var f in new System.IO.DirectoryInfo(dir).GetFiles("*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { /* a launcher that cannot tidy up still has to start */ }
    }

    /// <summary>Requests from other instances / links; the launcher window drains them once it is up.</summary>
    public static RemoteCommands Remote { get; } = new();

    [STAThread]
    static void Main(string[] args)
    {
        bool afterUpdate = args.Contains("--after-update");
        string link = args.FirstOrDefault(ProtocolHandler.IsLink);

        // One launcher at a time. A launcher started by an update waits for its predecessor to leave.
        using var instance = SingleInstance.Acquire(afterUpdate ? 20000 : 0);
        if (!instance.IsOwner)
        {
            // Not the first: pass the request on (a link, or just "show yourself") to the running one and leave.
            if (link != null) InstanceControl.Post(InstanceControl.LinkPath, new { uri = link });
            else InstanceControl.Post(InstanceControl.ActivatePath, new { });
            return;
        }
        if (link != null) Remote.Submit("URI\t" + link);

        ProtocolHandler.Register();
        SelfUpdater.CleanUpLeftovers();

        HostApp = Build([]);

        HostApp.Services.GetRequiredService<Program>().Run();
    }

    static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Kestrel serves both pipes the launcher owns: the game's NMCO channel (raw connections) and the control
        // endpoint other launcher processes use (HTTP, see Controllers/InstanceController). Nothing listens on TCP.
        builder.WebHost.UseNamedPipes(o => o.CurrentUserOnly = true);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenNamedPipe(OpenCrazyArcade.Ipc.NmcoPipe.Name, o => o.UseConnectionHandler<ConnectionHandlers.NMCoreConnectionHandler>());
            o.ListenNamedPipe(InstanceControl.PipeName, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
        });
        builder.Services.AddControllers();

        // A launcher has no console, so until now everything it logged went nowhere. The file
        // sits beside the config, in %ProgramData%, which the player can reach without admin
        // rights and can be asked for over chat.
        PruneOldLauncherLogs(Models.LauncherConfig.LauncherLogDir, keep: 30);
        builder.Logging.AddProvider(new FileLoggerProvider(Models.LauncherConfig.LauncherLogPath));
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
        // The control endpoint is HTTP now: keep the framework's per-request chatter out of the player's log.
        builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);

        builder.Services.AddSingleton<Program>();
        builder.Services.AddSingleton<App>();
        builder.Services.AddSingleton(Models.LauncherConfig.Load());
        builder.Services.AddSingleton(Remote);
        builder.Services.AddSingleton<SelfUpdater>();

        /* NMCore */
        builder.Services.AddSingleton<Managers.NMCoreManager>();
        builder.Services.AddSingleton<Nmco.NmcoPipeRouter>();
        builder.Services.AddHostedService<Managers.NMCoreManager.HostedService>();

        /* UI */
        builder.Services.AddSingleton<Managers.UIManager>();
        builder.Services.AddHostedService<Managers.UIManager.HostedService>();
        builder.Services.AddSingleton<UI.Windows.LauncherWindow>();
        builder.Services.AddTransient<UI.Windows.DependenciesWindow>();

        var app = builder.Build();
        app.MapControllers();
        return app;
    }
}
