using Payments.Api.Demo;

namespace Payments.Api.Tests;

public class DemoTrafficControlTests
{
    [Fact]
    public void Starts_unpaused()
    {
        var control = new DemoTrafficControl();

        Assert.False(control.IsPaused);
    }

    [Fact]
    public void Pause_sets_IsPaused_true()
    {
        var control = new DemoTrafficControl();

        control.Pause();

        Assert.True(control.IsPaused);
    }

    [Fact]
    public void Resume_sets_IsPaused_false()
    {
        var control = new DemoTrafficControl();
        control.Pause();

        control.Resume();

        Assert.False(control.IsPaused);
    }
}
