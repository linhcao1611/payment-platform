using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Api.Tests;

/// <summary>
/// Its own host: the shared fixture host deliberately never enables demo traffic (see
/// PaymentsApiFixture), and tests here need DemoTraffic:Enabled=true to exercise the "something
/// is actually running" side of the contract.
///
/// This only proves the endpoints and DemoTrafficControl agree on state — it does not prove
/// DemoTrafficGenerator itself stops posting payments, because the generator resolves its own
/// base address from IServerAddressesFeature to self-call over HTTP, and WebApplicationFactory's
/// TestServer never populates that feature (confirmed by inspection: Addresses is empty under
/// the fixture's host). The generator's actual on/off behavior is verified by hand against the
/// real compose stack instead, the same way DemoTrafficGenerator itself was never covered here.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class DemoTrafficPauseTests(PaymentsApiFixture fixture)
{
    private async Task<HttpClient> EnabledClientAsync()
    {
        var host = fixture.CreateHost(
            await fixture.CreateIsolatedDatabaseAsync(),
            workerEnabled: false,
            extraConfig: new Dictionary<string, string?> { ["DemoTraffic:Enabled"] = "true" });

        return host.CreateClient();
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/demo/traffic");

    [Fact]
    public async Task Status_reports_enabled_and_unpaused_initially()
    {
        var client = await EnabledClientAsync();

        var status = await StatusAsync(client);

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Pause_then_resume_round_trips_through_status()
    {
        var client = await EnabledClientAsync();

        var pauseResponse = await client.PostAsync("/api/demo/traffic/pause", null);
        pauseResponse.EnsureSuccessStatusCode();
        var paused = await pauseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.True((await StatusAsync(client)).GetProperty("paused").GetBoolean());

        var resumeResponse = await client.PostAsync("/api/demo/traffic/resume", null);
        resumeResponse.EnsureSuccessStatusCode();
        var resumed = await resumeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(resumed.GetProperty("paused").GetBoolean());
        Assert.False((await StatusAsync(client)).GetProperty("paused").GetBoolean());
    }
}
