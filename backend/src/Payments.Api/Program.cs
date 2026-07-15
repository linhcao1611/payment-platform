using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Payments.Api.Demo;
using Payments.Api.Middleware;
using Payments.Infrastructure;
using Payments.Infrastructure.Gateway;
using Payments.Infrastructure.Idempotency;
using Payments.Worker;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// JSON to stdout, which is what a log shipper wants — no file sinks, no rotation, the
// container runtime owns that. ReadFrom.Configuration keeps levels tunable per environment
// without a redeploy. Card fields can't leak here by construction: the domain only ever
// holds last4 and brand (see the PCI note on Payment).
//
// Rendered rather than plain CompactJsonFormatter: the plain one emits only the message
// *template* (`@mt`), so a human reading these in Grafana or Kibana sees
// "Payment {PaymentId} captured" with the placeholders unfilled. This emits `@m` — the message
// with values substituted — while keeping every property as its own queryable field, plus `@i`,
// a hash of the template, which preserves the group-by-message-type that `@mt` would give.
// Structured for machines, readable for the person on call.
builder.Services.AddSerilog((_, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

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

// Registered before the worker so it runs first: hosted services start in registration order,
// and the seeded captures should be in the queue before anything drains it. Off by default —
// the compose demo profile is the only thing that turns it on.
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.SectionName));
builder.Services.AddHostedService<DemoDataSeeder>();

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

app.UseSerilogRequestLogging(options =>
{
    // Infrastructure traffic is relentless and says nothing: a k8s probe hits /healthz and
    // /readyz every few seconds per pod, and Prometheus scrapes /metrics on its own schedule,
    // forever. Logged at Information they drown the events that matter — locally they were 77%
    // of the stream — and in production you pay to ingest every one of them.
    //
    // Dropped only while they succeed. A failing probe is exactly the thing you need in the
    // log, so anything that throws or 5xxs is still an Error, whatever the path.
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= 500
            ? LogEventLevel.Error
            : IsInfrastructureEndpoint(httpContext.Request.Path)
                ? LogEventLevel.Verbose // below the configured minimum, so it never reaches a sink
                : LogEventLevel.Information;
});

static bool IsInfrastructureEndpoint(PathString path) =>
    path.StartsWithSegments("/healthz")
    || path.StartsWithSegments("/readyz")
    || path.StartsWithSegments("/metrics");

// RED for free: http_requests_received_total, http_request_duration_seconds,
// http_requests_in_progress, labelled by method/endpoint/status — bounded because the label
// is the route template, not the raw path (which would put payment ids in label values).
app.UseHttpMetrics();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Nothing is mapped at the root, so anyone who types the API's base URL — which is what
    // the README and every demo instinct point at — would get a bare 404 and reasonably
    // conclude the thing is broken. Send them to the API explorer instead. Scoped to
    // Development because that is exactly where Swagger is mounted; in production an
    // unmapped root correctly stays a 404.
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
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
