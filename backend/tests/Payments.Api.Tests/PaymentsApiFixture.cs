using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
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
/// One container is shared by the whole collection, and the shared <see cref="Api"/> host has
/// the settlement worker **off**.
///
/// That last part is load-bearing. An always-on worker polling the shared database silently
/// competes with every test: a payment left in Captured gets Settled a few milliseconds later,
/// so any test asserting on an exact audit trail passes or fails depending on how fast the
/// machine is. A test that needs a worker asks for its own database
/// (<see cref="CreateIsolatedDatabaseAsync"/>) and its own host
/// (<see cref="CreateHost(string, bool)"/>), so it is the only thing draining that queue and
/// decides exactly when polling starts.
/// </summary>
public sealed class PaymentsApiFixture : IAsyncLifetime
{
    // Pinned to the same image as docker-compose, so tests and local runs agree on the engine.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private WebApplicationFactory<Program>? _api;

    public WebApplicationFactory<Program> Api => _api!;

    /// <summary>
    /// A fresh, empty database on the same container, for a test that needs to be the *only*
    /// worker in play. The shared database has <see cref="ApiWithWorker"/> polling it, and that
    /// worker will happily claim a job out from under a test's own host — so anything asserting
    /// on which gateway calls happened needs a queue nobody else is draining. The host migrates
    /// it on startup.
    /// </summary>
    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }
            .ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _api = new PaymentsApiFactory(_postgres.GetConnectionString(), workerEnabled: false);
        _ = _api.Services; // force the host to build and migrate now, not inside the first test
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();

        await _postgres.DisposeAsync();
    }

    /// <summary>
    /// A host against a database of the caller's choosing — pair it with
    /// <see cref="CreateIsolatedDatabaseAsync"/> to control exactly when a worker starts
    /// polling, which the shared hosts can't offer.
    /// </summary>
    public WebApplicationFactory<Program> CreateHost(string connectionString, bool workerEnabled) =>
        new PaymentsApiFactory(connectionString, workerEnabled);

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
