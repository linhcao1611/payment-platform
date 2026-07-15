using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Infrastructure.Entities;

namespace Payments.Infrastructure;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentTransition> PaymentTransitions => Set<PaymentTransition>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<SettlementJob> SettlementJobs => Set<SettlementJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.MerchantId).HasColumnName("merchant_id").HasMaxLength(64);
            e.Property(p => p.AmountMinor).HasColumnName("amount_minor");
            e.Property(p => p.Currency).HasColumnName("currency").HasMaxLength(3);
            e.Property(p => p.CardLast4).HasColumnName("card_last4").HasMaxLength(4);
            e.Property(p => p.CardBrand).HasColumnName("card_brand").HasMaxLength(32);
            // Status stored as text: human-readable in psql, and safe against enum reordering.
            e.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Description).HasColumnName("description").HasMaxLength(512);
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            // Postgres xmin as optimistic concurrency token: concurrent
            // capture/refund on the same payment cannot both win.
            e.Property<uint>("xmin").IsRowVersion();
            e.HasIndex(p => new { p.MerchantId, p.CreatedAt });
            e.HasIndex(p => p.Status);
            e.Ignore(p => p.PendingTransitions);
        });

        modelBuilder.Entity<PaymentTransition>(e =>
        {
            e.ToTable("payment_transitions");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.PaymentId).HasColumnName("payment_id");
            e.Property(t => t.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.Actor).HasColumnName("actor").HasMaxLength(128);
            e.Property(t => t.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(t => t.Reason).HasColumnName("reason").HasMaxLength(512);
            e.Property(t => t.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(t => t.PaymentId);
            e.HasOne<Payment>().WithMany().HasForeignKey(t => t.PaymentId);
        });

        modelBuilder.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(k => new { k.MerchantId, k.Operation, k.Key });
            e.Property(k => k.MerchantId).HasColumnName("merchant_id").HasMaxLength(64);
            e.Property(k => k.Operation).HasColumnName("operation").HasMaxLength(16);
            e.Property(k => k.Key).HasColumnName("key").HasMaxLength(128);
            e.Property(k => k.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            e.Property(k => k.ResponseStatusCode).HasColumnName("response_status_code");
            e.Property(k => k.ResponseBody).HasColumnName("response_body");
            e.Property(k => k.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<SettlementJob>(e =>
        {
            e.ToTable("settlement_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).HasColumnName("id");
            e.Property(j => j.PaymentId).HasColumnName("payment_id");
            e.Property(j => j.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
            e.Property(j => j.AttemptCount).HasColumnName("attempt_count");
            e.Property(j => j.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(j => j.LastError).HasColumnName("last_error").HasMaxLength(1024);
            e.Property(j => j.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.Property(j => j.CreatedAt).HasColumnName("created_at");
            e.Property(j => j.CompletedAt).HasColumnName("completed_at");
            // The worker's polling query: claimable jobs ordered by due time.
            e.HasIndex(j => new { j.Status, j.NextAttemptAt });
            e.HasOne<Payment>().WithMany().HasForeignKey(j => j.PaymentId);
        });
    }
}
