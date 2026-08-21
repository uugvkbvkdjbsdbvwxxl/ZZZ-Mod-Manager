using Xunit;
using ZZZModManager.Infrastructure;

namespace ZZZModManager.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondaryInstanceSignalsThePrimaryInstance()
    {
        var name = $"ZZZModManager.Tests.{Guid.NewGuid():N}";
        using var activated = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator(name);
        Assert.True(primary.TryAcquire());
        primary.StartListening(activated.Set);

        using var secondary = new SingleInstanceCoordinator(name);
        Assert.False(secondary.TryAcquire());
        secondary.SignalPrimary();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
    }
}
