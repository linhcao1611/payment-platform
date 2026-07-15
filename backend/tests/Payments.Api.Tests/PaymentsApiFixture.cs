using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Payments.Api.Tests;

/// <summary>
/// Boots the real API against a throwaway Postgres.
///
/// Testcontainers rather than the compose database on 5433: the tests must not depend on a
/// developer having run <c>docker compose up</c>, and must never touch data someone is
/// looking at. Migrations run on startup because the host boots in Development, so the schema
/// under test is the one the migrations actually produce — an in-memory provider would prove
/// nothing here, since every interesting behaviour in this system (SKIP LOCKED, unique-index
/// contention, xmin concurrency) is Postgres behaviour.
///
/// One container is shared by the whole collection and two hosts point at it:
/// <see cref="Api"/> with the settlement worker off, so a test's payment can sit in Captured
/// without a background thread moving it, and <see cref="ApiWithWorker"/> for the one test
/// that needs settlement to actually happen.
/// </summary>
public sealed class PaymentsApiFixture : IAsyncLifetime
{
    // Pinned to the same image as docker-compose, so tests and local runs agree on the engine.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private WebApplicationFactory<Program>? _api;
    private WebApplicationFactory<Program>? _apiWithWorker;

    public WebApplicationFactory<Program> Api => _api!;
    public WebApplicationFactory<Program> ApiWithWorker => _apiWithWorker!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Built sequentially, not lazily in parallel: both hosts migrate on startup, and two
        // concurrent MigrateAsync calls against one database race for the migration lock.
        _api = CreateHost(workerEnabled: false);
        _ = _api.Services;

        _apiWithWorker = CreateHost(workerEnabled: true);
        _ = _apiWithWorker.Services;
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
        if (_apiWithWorker is not null)
            await _apiWithWorker.DisposeAsync();

        await _postgres.DisposeAsync();
    }

    private WebApplicationFactory<Program> CreateHost(bool workerEnabled) =>
        new PaymentsApiFactory(_postgres.GetConnectionString(), workerEnabled);

    private sealed class PaymentsApiFactory(string connectionString, bool workerEnabled)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development is what triggers the migrate-on-startup branch in Program.cs.
            builder.UseEnvironment(Environments.Development);

            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Payments"] = connectionString,

                    // A deterministic gateway. The fake's randomness is there to make the
                    // failure paths demonstrable by hand; a test that flakes 15% of the time
                    // is worse than no test.
                    ["FakeGateway:AuthorizeDeclineRate"] = "0",
                    ["FakeGateway:SettleFailureRate"] = "0",
                    ["FakeGateway:MinLatencyMs"] = "0",
                    ["FakeGateway:MaxLatencyMs"] = "1",

                    ["Settlement:Enabled"] = workerEnabled ? "true" : "false",
                    ["Settlement:PollInterval"] = "00:00:00.100",
                }));
        }
    }
}

[CollectionDefinition(nameof(PaymentsApiCollection))]
public sealed class PaymentsApiCollection : ICollectionFixture<PaymentsApiFixture>;
