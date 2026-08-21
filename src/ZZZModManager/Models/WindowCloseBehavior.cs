namespace ZZZModManager.Models;

public enum WindowCloseBehavior
{
    Exit,
    HideToBackground
}

public static class WindowCloseBehaviorPolicy
{
    public static bool ShouldHideOnClose(WindowCloseBehavior behavior, bool forceExit) =>
        !forceExit && behavior == WindowCloseBehavior.HideToBackground;
}
