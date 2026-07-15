using Microsoft.EntityFrameworkCore;
using Payments.Api.Middleware;
using Payments.Infrastructure;
using Payments.Infrastructure.Gateway;
using Payments.Infrastructure.Idempotency;

var builder = WebApplication.CreateBuilder(args);

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
app.UseMiddleware<CorrelationIdMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
