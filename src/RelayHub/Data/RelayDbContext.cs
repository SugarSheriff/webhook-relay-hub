using Microsoft.EntityFrameworkCore;

namespace RelayHub.Data;

public class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    public DbSet<WebhookMessage> Messages => Set<WebhookMessage>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // The database itself refuses a second copy of the same delivery,
        // so duplicates are blocked even if two arrive at the same instant.
        model.Entity<WebhookMessage>()
            .HasIndex(m => new { m.Source, m.DeliveryId })
            .IsUnique();

        model.Entity<Delivery>()
            .Property(d => d.Status)
            .HasConversion<string>();

        // The worker looks for "pending and due", so index exactly that.
        model.Entity<Delivery>()
            .HasIndex(d => new { d.Status, d.NextAttemptAtUtc });
    }
}
