namespace Payments.Api.Demo;

/// <summary>
/// Runtime pause/resume for <see cref="DemoTrafficGenerator"/>, separate from
/// <see cref="DemoTrafficOptions.Enabled"/>: Enabled decides whether the generator's loop runs
/// at all (startup-only), Paused decides whether a running loop is currently doing anything
/// (toggleable live, from the dashboard). No persistence — resets to unpaused on restart, which
/// is correct for a demo aid.
/// </summary>
public sealed class DemoTrafficControl
{
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;
}
