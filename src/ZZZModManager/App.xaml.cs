using System.Windows;
using ZZZModManager.Infrastructure;
using ZZZModManager.Services;

namespace ZZZModManager;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The injector must be started before WPF creates a window. WPF's
        // composition stack can load Direct3D in this process, which makes
        // 3dmloader's SetWindowsHookEx path return error 200. The manager
        // launches itself in this headless mode to establish the hook first.
        var helperExitCode = HeadlessInjectionRunner.TryRun(e.Args);
        if (helperExitCode is int exitCode)
        {
            Shutdown(exitCode);
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquire())
        {
            _singleInstance.SignalPrimary();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        _singleInstance.StartListening(() => Dispatcher.BeginInvoke(window.ActivateFromSecondaryInstance));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
