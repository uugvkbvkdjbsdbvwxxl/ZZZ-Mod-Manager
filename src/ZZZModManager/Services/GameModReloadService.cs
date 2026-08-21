using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZZZModManager.Services;

public sealed record ModReloadResult
{
    public bool GameRunning { get; init; }
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IGameModReloadService
{
    ModReloadResult Reload(string? gameExecutablePath);
    ModReloadResult SendKey(string? gameExecutablePath, GameKeyChord chord, bool restorePreviousWindow = true);
    bool IsGameRunning(string? gameExecutablePath);
    int? GetGameProcessId(string? gameExecutablePath);
    ModReloadResult ActivateGame(string? gameExecutablePath);
}

[Flags]
public enum GameKeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

public readonly record struct GameKeyChord(ushort VirtualKey, GameKeyModifiers Modifiers);

public static class ManagerGameBindings
{
    public const ushort ReloadVirtualKey = 0x87; // VK_F24
    public const GameKeyModifiers ReloadModifiers = GameKeyModifiers.Control | GameKeyModifiers.Shift;
    public const string ReloadIniBinding = "ctrl no_alt shift VK_F24";

    public static GameKeyChord ReloadChord => new(ReloadVirtualKey, ReloadModifiers);

    public static bool IsReloadChord(GameKeyChord chord) =>
        chord.VirtualKey == ReloadVirtualKey && chord.Modifiers == ReloadModifiers;
}

/// <summary>
/// Sends ZZMI's reload shortcut and manager-owned live-switch shortcuts to the
/// running game.  It activates the game, waits for the foreground transition,
/// holds virtual-key events across several render frames, then restores the
/// previous window when requested.
/// </summary>
public sealed class GameModReloadService : IGameModReloadService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventUp = 0x0002;
    private const int ShowNormal = 1;
    private const int ReloadSettleMilliseconds = 2400;
    private const int KeyTransitionMilliseconds = 35;
    private const int KeyHoldMilliseconds = 70;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;

    public ModReloadResult Reload(string? gameExecutablePath)
    {
        var result = SendKey(gameExecutablePath, ManagerGameBindings.ReloadChord);
        return result with
        {
            Message = result.Succeeded
                ? "安全重载命令已发送；管理器无法读取游戏渲染结果，请在游戏内确认画面。"
                : result.Message
        };
    }

    public ModReloadResult SendKey(string? gameExecutablePath, GameKeyChord chord, bool restorePreviousWindow = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ModReloadResult { Message = "当前系统不支持游戏快捷键输入。" };
        }

        if (string.IsNullOrWhiteSpace(gameExecutablePath) || !File.Exists(gameExecutablePath))
        {
            return new ModReloadResult { Message = "未设置有效的游戏路径。" };
        }

        var process = FindGameProcess(gameExecutablePath);
        if (process is null)
        {
            return new ModReloadResult { Message = "未检测到正在运行的绝区零。" };
        }

        using (process)
        {
            var window = FindMainWindow(process);
            if (window == IntPtr.Zero)
            {
                return new ModReloadResult
                {
                    GameRunning = true,
                    Message = "已找到游戏进程，但没有可用游戏窗口。"
                };
            }

            var result = SendKeyToWindow(window, chord, restorePreviousWindow);
            return result with { GameRunning = true };
        }
    }

    public bool IsGameRunning(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath) || !File.Exists(gameExecutablePath))
        {
            return false;
        }

        using var process = FindGameProcess(gameExecutablePath);
        return process is not null;
    }

    public int? GetGameProcessId(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath) || !File.Exists(gameExecutablePath))
        {
            return null;
        }

        using var process = FindGameProcess(gameExecutablePath);
        return process?.Id;
    }

    public ModReloadResult ActivateGame(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath) || !File.Exists(gameExecutablePath))
        {
            return new ModReloadResult { Message = "未设置有效的游戏路径。" };
        }

        using var process = FindGameProcess(gameExecutablePath);
        if (process is null)
        {
            return new ModReloadResult { Message = "未检测到正在运行的绝区零。" };
        }

        var window = FindMainWindow(process);
        if (window == IntPtr.Zero)
        {
            return new ModReloadResult { GameRunning = true, Message = "已找到游戏进程，但没有可用游戏窗口。" };
        }

        ShowWindow(window, ShowNormal);
        var activated = SetForegroundWindow(window);
        return new ModReloadResult
        {
            GameRunning = true,
            Succeeded = activated,
            Message = activated ? "已切回绝区零。" : "无法将绝区零切换到前台。"
        };
    }

    private static ModReloadResult SendKeyToWindow(IntPtr window, GameKeyChord chord, bool restorePreviousWindow)
    {
        var previousWindow = GetForegroundWindow();
        try
        {
            ShowWindow(window, ShowNormal);
            SetForegroundWindow(window);
            if (!WaitForForeground(window))
            {
                return new ModReloadResult
                {
                    Message = "无法将绝区零切换到前台，未发送快捷键。"
                };
            }

            // 3DMigoto polls virtual-key state once per frame. Sending every
            // down/up event in one SendInput batch can release the modifiers
            // before that poll observes the chord. Send VK events one at a
            // time and hold the completed chord across several frames, which
            // also matches the behavior used by established 3DMigoto tools.
            var sent = SendChord(chord);
            // ZZMI rebuilds its include tree and creates large GPU resources
            // asynchronously. Returning focus after only a few hundred
            // milliseconds leaves model parts in a half-reloaded frame. Keep
            // Keep the manager-only reload chord in the game window long enough
            // for the reload work to settle;
            // manager-owned live toggles remain fast because they do not use
            // this path.
            Thread.Sleep(ManagerGameBindings.IsReloadChord(chord) ? ReloadSettleMilliseconds : 220);
            if (!sent)
            {
                // Window messages are only a last-resort delivery attempt.
                // They are never combined with a successful absolute-state
                // chord, so a command cannot be applied twice accidentally.
                PostChord(window, chord);
                return new ModReloadResult
                {
                    Message = "发送游戏快捷键失败（SendInput 未接受全部按键事件）。"
                };
            }

            return new ModReloadResult
            {
                Succeeded = true,
                Message = "实时切换命令已发送；管理器无法读取游戏渲染结果。"
            };
        }
        finally
        {
            if (restorePreviousWindow && previousWindow != IntPtr.Zero && previousWindow != window)
            {
                SetForegroundWindow(previousWindow);
            }
        }
    }

    private static bool WaitForForeground(IntPtr window)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetForegroundWindow() == window)
            {
                return true;
            }

            Thread.Sleep(15);
            SetForegroundWindow(window);
        }

        return GetForegroundWindow() == window;
    }

    private static bool SendChord(GameKeyChord chord)
    {
        var pressed = new List<ushort>(4);
        try
        {
            foreach (var modifier in ModifierKeys(chord.Modifiers))
            {
                if (!SendKeyboardEvent(modifier, keyUp: false))
                {
                    return false;
                }

                pressed.Add(modifier);
                Thread.Sleep(KeyTransitionMilliseconds);
            }

            if (!SendKeyboardEvent(chord.VirtualKey, keyUp: false))
            {
                return false;
            }

            pressed.Add(chord.VirtualKey);
            Thread.Sleep(KeyHoldMilliseconds);
            return true;
        }
        finally
        {
            foreach (var key in pressed.AsEnumerable().Reverse())
            {
                SendKeyboardEvent(key, keyUp: true);
                Thread.Sleep(KeyTransitionMilliseconds);
            }
        }
    }

    private static bool SendKeyboardEvent(ushort key, bool keyUp)
    {
        var input = CreateKeyboardInput(key, keyUp ? KeyEventUp : 0);
        return SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static void PostChord(IntPtr window, GameKeyChord chord)
    {
        foreach (var modifier in ModifierKeys(chord.Modifiers))
        {
            PostMessage(window, WmKeyDown, modifier, IntPtr.Zero);
        }

        PostMessage(window, WmKeyDown, chord.VirtualKey, IntPtr.Zero);
        PostMessage(window, WmKeyUp, chord.VirtualKey, IntPtr.Zero);
        foreach (var modifier in ModifierKeys(chord.Modifiers).Reverse())
        {
            PostMessage(window, WmKeyUp, modifier, IntPtr.Zero);
        }
    }

    private static IEnumerable<ushort> ModifierKeys(GameKeyModifiers modifiers)
    {
        if (modifiers.HasFlag(GameKeyModifiers.Control)) yield return 0x11;
        if (modifiers.HasFlag(GameKeyModifiers.Alt)) yield return 0x12;
        if (modifiers.HasFlag(GameKeyModifiers.Shift)) yield return 0x10;
    }

    private static Process? FindGameProcess(string gameExecutablePath)
    {
        var expectedPath = Path.GetFullPath(gameExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return null;
        }

        foreach (var process in processes.OrderByDescending(item => item.MainWindowHandle != IntPtr.Zero))
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    continue;
                }

                string? actualPath = null;
                try
                {
                    actualPath = process.MainModule?.FileName;
                }
                catch (Win32Exception)
                {
                    // Some game builds deny MainModule access. Process name is
                    // still a safe fallback when no other match is present.
                }

                if (actualPath is null
                    || string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        foreach (var process in processes)
        {
            process.Dispose();
        }

        return null;
    }

    private static IntPtr FindMainWindow(Process process)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            var targetProcessId = (uint)process.Id;
            var result = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var processId);
                if (processId == targetProcessId && IsWindowVisible(window))
                {
                    result = window;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return result;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static NativeInput CreateKeyboardInput(ushort key, uint flags)
    {
        return new NativeInput
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = GetMessageExtraInfo()
                }
            }
        };
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, ushort wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int inputSize);
}
