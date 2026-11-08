using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenCrazyArcade.AutoUpdate.Managers;

internal partial class UIManager(ILogger<UIManager> logger, IServiceProvider services)
{
    public async Task RunOnUIThread(Action action)
    {
        await services.GetRequiredService<App>().Ready;
        var app = services.GetRequiredService<App>();
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            await app.Dispatcher.InvokeAsync(action);
        }
    }

    public async Task ActivateLauncher()
    {
        try
        {
            if (!CheckingWebview2Runtime())
            {
                logger.LogWarning("WebView2 Runtime is not installed. Showing DependenciesWindow.");
                await RunOnUIThread(() =>
                {
                    var depWin = services.GetRequiredService<UI.Windows.DependenciesWindow>();
                    depWin.Show();
                });
                return;
            }

            await RunOnUIThread(() =>
            {
                var launcherWindow = services.GetRequiredService<UI.Windows.LauncherWindow>();
                if (launcherWindow.IsVisible)
                {
                    launcherWindow.WindowState = System.Windows.WindowState.Normal;
                    launcherWindow.Activate();
                }
                else
                {
                    launcherWindow.Show();
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to activate launcher: {Message}", ex.Message);
        }
    }

    public bool CheckingWebview2Runtime()
    {
        logger.LogInformation("Checking WebView2 Runtime...");
        try
        {
            string version = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            logger.LogInformation("WebView2 version found: {Version}", version);
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception ex)
        {
            logger.LogWarning("WebView2 check failed: {Message}", ex.Message);
            return false;
        }
    }
}

internal partial class UIManager
{
    public class HostedService(UIManager uiManager) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                await uiManager.ActivateLauncher();
            }, cancellationToken);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
