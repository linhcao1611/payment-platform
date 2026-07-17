using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Payments.Api.Demo;

namespace Payments.Api.Controllers;

public sealed record DemoTrafficStatusResponse(bool Enabled, bool Paused, double PaymentsPerMinute);

/// <summary>
/// Runtime control for <see cref="DemoTrafficGenerator"/>. Not merchant-scoped — this toggles a
/// demo aid, not merchant data — so it takes no X-Merchant-Id and lives outside /api/payments.
/// </summary>
[ApiController]
[Route("api/demo/traffic")]
public sealed class DemoTrafficController(
    DemoTrafficControl control, IOptions<DemoTrafficOptions> options) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DemoTrafficStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<DemoTrafficStatusResponse> Status() => Ok(BuildStatus());

    [HttpPost("pause")]
    [ProducesResponseType<DemoTrafficStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<DemoTrafficStatusResponse> Pause()
    {
        if (!options.Value.Enabled)
            return Disabled();

        control.Pause();
        return Ok(BuildStatus());
    }

    [HttpPost("resume")]
    [ProducesResponseType<DemoTrafficStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public ActionResult<DemoTrafficStatusResponse> Resume()
    {
        if (!options.Value.Enabled)
            return Disabled();

        control.Resume();
        return Ok(BuildStatus());
    }

    private DemoTrafficStatusResponse BuildStatus() =>
        new(options.Value.Enabled, control.IsPaused, options.Value.PaymentsPerMinute);

    private ObjectResult Disabled()
    {
        var result = Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Demo traffic generation is disabled",
            detail: "DemoTraffic:Enabled is false, so there is nothing running to pause or resume.");
        ((ProblemDetails)result.Value!).Extensions["errorCode"] = "demo_traffic_disabled";
        return result;
    }
}
