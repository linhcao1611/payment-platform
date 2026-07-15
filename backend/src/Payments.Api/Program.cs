using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Middleware;
using Payments.Infrastructure;
using Payments.Infrastructure.Gateway;
using Payments.Infrastructure.Idempotency;
using Payments.Worker;
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// JSON to stdout, which is what a log shipper wants — no file sinks, no rotation, the
// container runtime owns that. ReadFrom.Configuration keeps levels tunable per environment
// without a redeploy. Card fields can't leak here by construction: the domain only ever
// holds last4 and brand (see the PCI note on Payment).
builder.Services.AddSerilog((_, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddDbContext<PaymentsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Payments")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IIdempotencyStore, IdempotencyStore>();
builder.Services.AddScoped<ISettlementQueue, SettlementQueue>();
builder.Services.Configure<FakeGatewayOptions>(
    builder.Configuration.GetSection(FakeGatewayOptions.SectionName));
builder.Services.AddSingleton<IPaymentGateway, FakePaymentGateway>();

// The settlement worker rides in the API process today. It is a project reference, not a
// class here, so promoting it to its own deployment is a hosting change rather than a rewrite.
builder.Services.Configure<SettlementOptions>(
    builder.Configuration.GetSection(SettlementOptions.SectionName));
builder.Services.AddHostedService<SettlementWorker>();

// /healthz is liveness: the process is up and can serve. Deliberately checks nothing else —
// a liveness probe that depends on Postgres would have k8s kill every API pod during a
// database blip, turning a recoverable incident into an outage.
// /readyz is readiness: can this instance actually do useful work right now?
builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("Payments")!,
        name: "postgres",
        tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Dev convenience: apply migrations on startup. In production this would be a
// separate deploy step (migration job / pipeline gate), not app startup.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();

// Correlation first: everything downstream — request logs, handler logs, the audit rows and
// the settlement job — must carry the same id, so the scope has to wrap them all.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

// RED for free: http_requests_received_total, http_request_duration_seconds,
// http_requests_in_progress, labelled by method/endpoint/status — bounded because the label
// is the route template, not the raw path (which would put payment ids in label values).
app.UseHttpMetrics();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Prometheus text format. No collector ships with this: a scrape config or an OTLP exporter
// is the one config change that connects it to a real stack — that's the seam, not a
// pretending-to-be-production dashboard.
app.MapMetrics();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
