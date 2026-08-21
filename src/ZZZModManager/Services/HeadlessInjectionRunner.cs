using System.Diagnostics;

namespace ZZZModManager.Services;

/// <summary>
/// Establishes the XXMI hook before WPF creates a window. This is the same
/// ordering used by XXMI Launcher and avoids WPF's Direct3D state causing
/// HookLibrary to return error 200.
/// </summary>
public static class HeadlessInjectionRunner
{
    public static int? TryRun(IReadOnlyList<string> args)
    {
        if (!args.Any(arg => string.Equals(arg, "--inject-helper", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            var gamePath = ReadOption(args, "--game");
            var runtimePath = ReadOption(args, "--runtime");
            var timeout = ReadIntOption(args, "--timeout", 30);
            Console.Out.WriteLine(Run(gamePath, runtimePath, timeout));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Run(string gamePath, string runtimePath, int timeoutSeconds)
    {
        gamePath = Path.GetFullPath(gamePath);
        runtimePath = Path.GetFullPath(runtimePath);
        if (!File.Exists(gamePath))
        {
            throw new FileNotFoundException("找不到绝区零可执行文件。", gamePath);
        }

        var loaderPath = Path.Combine(runtimePath, "3dmloader.dll");
        var d3d11Path = Path.Combine(runtimePath, "d3d11.dll");
        if (!File.Exists(loaderPath) || !File.Exists(d3d11Path))
        {
            throw new FileNotFoundException("ZZMI 注入文件不完整，需要 3dmloader.dll 和 d3d11.dll。", runtimePath);
        }

        var processName = Path.GetFileName(gamePath);
        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(gamePath))
            .FirstOrDefault(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });
        if (existing is not null)
        {
            existing.Dispose();
            throw new InvalidOperationException("绝区零已经在运行，请先关闭当前游戏后再从管理器启动。已有进程不会被强制结束。 ");
        }

        using var injector = new NativeInjector(loaderPath);
        var useDirectInjection = false;
        try
        {
            injector.HookLibrary(d3d11Path, processName);
        }
        catch (HookLibraryException ex) when (ex.ErrorCode == 200 && injector.SupportsDirectInjection)
        {
            useDirectInjection = true;
        }

        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = gamePath,
                WorkingDirectory = Path.GetDirectoryName(gamePath)!,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("无法启动绝区零进程。 ");

            if (useDirectInjection)
            {
                var injectResult = injector.Inject((uint)process.Id, d3d11Path, timeoutSeconds);
                if (injectResult != 0)
                {
                    throw new InvalidOperationException($"ZZMI Direct Inject 失败，错误码 {injectResult}。 ");
                }

                if (!injector.VerifyInjection(d3d11Path, processName, timeoutSeconds))
                {
                    throw new InvalidOperationException("Direct Inject 已返回成功，但未能验证 d3d11.dll 已加载。请检查杀毒软件、管理员权限和游戏是否使用 DX11。 ");
                }
            }
            else if (!injector.WaitForInjection(timeoutSeconds))
            {
                throw new InvalidOperationException("等待 ZZMI 注入超时，未检测到 d3d11.dll。请检查 d3d11_log.txt。 ");
            }

            return $"游戏已启动并完成 ZZMI 注入（PID {process.Id}）。";
        }
        finally
        {
            process?.Dispose();
            injector.UnhookLibrary();
        }
    }

    private static string ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                return args[index + 1];
            }
        }

        throw new ArgumentException($"注入助手缺少参数 {name}。 ");
    }

    private static int ReadIntOption(IReadOnlyList<string> args, string name, int fallback)
    {
        try
        {
            return Math.Clamp(int.Parse(ReadOption(args, name)), 5, 180);
        }
        catch (ArgumentException)
        {
            return fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
