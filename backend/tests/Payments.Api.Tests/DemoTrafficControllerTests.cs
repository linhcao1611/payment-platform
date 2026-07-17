using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Api.Tests;

/// <summary>
/// The shared fixture's host never enables demo traffic (see PaymentsApiFixture), so this
/// exercises the "nothing is running" side of the contract. The behavioral pause/resume path
/// lives in DemoTrafficPauseTests, on its own host, for the same reason SettlementTests doesn't
/// share the fixture's host either.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class DemoTrafficControllerTests(PaymentsApiFixture fixture)
{
    private HttpClient Client => fixture.Api.CreateClient();

    [Fact]
    public async Task Status_reports_disabled_on_the_default_host()
    {
        var response = await Client.GetAsync("/api/demo/traffic");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Pause_returns_409_when_disabled()
    {
        var response = await Client.PostAsync("/api/demo/traffic/pause", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("demo_traffic_disabled", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Resume_returns_409_when_disabled()
    {
        var response = await Client.PostAsync("/api/demo/traffic/resume", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("demo_traffic_disabled", problem.GetProperty("errorCode").GetString());
    }
}
