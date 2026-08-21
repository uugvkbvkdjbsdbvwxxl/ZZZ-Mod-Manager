using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using Xunit;

namespace ZZZModManager.Tests;

public sealed class WindowCloseBehaviorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zzz-mm-close-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LegacyConfigDefaultsToDirectExit()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, "{\"SchemaVersion\":2,\"GameExecutablePath\":\"game.exe\"}", Encoding.UTF8);

        var config = new JsonFileStore().Load(path, () => new AppConfig());

        Assert.Equal(WindowCloseBehavior.Exit, config.CloseBehavior);
        Assert.False(WindowCloseBehaviorPolicy.ShouldHideOnClose(config.CloseBehavior, forceExit: false));
    }

    [Fact]
    public void HideBehaviorRoundTripsAndCanBeOverriddenByExplicitExit()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.json");
        var store = new JsonFileStore();
        store.Save(path, new AppConfig
        {
            SchemaVersion = 3,
            CloseBehavior = WindowCloseBehavior.HideToBackground
        });

        var loaded = store.Load(path, () => new AppConfig());

        Assert.Equal(WindowCloseBehavior.HideToBackground, loaded.CloseBehavior);
        Assert.True(WindowCloseBehaviorPolicy.ShouldHideOnClose(loaded.CloseBehavior, forceExit: false));
        Assert.False(WindowCloseBehaviorPolicy.ShouldHideOnClose(loaded.CloseBehavior, forceExit: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
