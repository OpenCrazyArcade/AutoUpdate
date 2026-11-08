using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Windows;

namespace OpenCrazyArcade.AutoUpdate;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly TaskCompletionSource readyTCS = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => readyTCS.Task;

    protected override void OnStartup(StartupEventArgs e)
    {
        Debug.WriteLine("AutoUpdate App Startup");
        readyTCS.SetResult();
        base.OnStartup(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        Debug.WriteLine("AutoUpdate App Activated");
        base.OnActivated(e);
    }
}

