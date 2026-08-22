using System.Text;
using System.Windows;
using System.Windows.Threading;
using ZZZModManager.Infrastructure;
using ZZZModManager.Services;

namespace ZZZModManager;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A crash before MainWindow exists cannot reach AppLogger, and an unhandled
        // CLR exception kills a WPF process with exit code 0xE0434352 and no dialog.
        // Both hooks are installed first so startup failures leave a trace.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatal(e.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ReportFatal(exception);
        }
    }

    private static void ReportFatal(Exception exception)
    {
        var logPath = WriteCrashLog(exception);
        var location = logPath is null ? "崩溃日志写入失败。" : $"详细信息已写入：{logPath}";
        var message = $"启动或运行时发生未处理的错误，程序需要关闭。\n\n{exception.GetType().Name}: {exception.Message}\n\n{location}";

        try
        {
            MessageBox.Show(message, "ZZZ Mod Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (InvalidOperationException)
        {
            // No dispatcher left to show a dialog on; the log on disk is the record.
        }
    }

    private static string? WriteCrashLog(Exception exception)
    {
        var content = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] unhandled exception")
            .AppendLine(exception.ToString())
            .AppendLine()
            .ToString();

        // AppLogger only exists once MainWindow has been constructed, so write
        // straight to disk and fall back to TEMP when the D: root is unavailable.
        foreach (var directory in CrashLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.AppendAllText(path, content);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> CrashLogDirectories()
    {
        yield return new AppPaths().LogsRoot;
        yield return Path.Combine(Path.GetTempPath(), "ZZZModManager", "Logs");
    }
}
