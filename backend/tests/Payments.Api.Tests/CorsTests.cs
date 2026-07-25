using System.Collections.Generic;
using System.Net.Http;

namespace Payments.Api.Tests;

/// <summary>
/// The compose deployment serves the dashboard and the API from one origin through nginx, so
/// it needs no CORS at all — and must not silently acquire a relaxed policy it never asked
/// for. The LocalStack deployment puts the dashboard on an S3 website and the API behind API
/// Gateway, which are genuinely different origins, so it configures them explicitly.
///
/// Both halves of that are tested: the policy works when configured, and stays absent when
/// not. The second test is the one that would catch someone "simplifying" the gate away.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public sealed class CorsTests(PaymentsApiFixture fixture)
{
    private const string Origin = "http://dashboard.example";

    private static HttpRequestMessage PaymentsRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/payments?pageSize=1");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Merchant-Id", "acme");
        return request;
    }

    [Fact]
    public async Task Configured_origin_gets_cors_headers()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false,
            new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = Origin });
        using var client = host.CreateClient();

        var response = await client.SendAsync(PaymentsRequest());

        Assert.Equal(
            Origin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Origin_outside_the_configured_list_gets_no_cors_headers()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false,
            new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = "http://somewhere.else" });
        using var client = host.CreateClient();

        var response = await client.SendAsync(PaymentsRequest());

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unconfigured_host_sends_no_cors_headers()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false);
        using var client = host.CreateClient();

        var response = await client.SendAsync(PaymentsRequest());

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Multiple_origins_are_read_as_a_comma_separated_list()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync();
        using var host = fixture.CreateHost(connectionString, workerEnabled: false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins"] = $"http://first.example, {Origin}",
            });
        using var client = host.CreateClient();

        var response = await client.SendAsync(PaymentsRequest());

        Assert.Equal(
            Origin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
