using Microsoft.EntityFrameworkCore;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;

namespace Reliant.Infrastructure.Persistence;

public class ReliantDbContext(DbContextOptions<ReliantDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Contribution> Contributions => Set<Contribution>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<StateTransition> StateTransitions => Set<StateTransition>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<JobDefinition> JobDefinitions => Set<JobDefinition>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<DeadLetterRecord> DeadLetterRecords => Set<DeadLetterRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsRowVersion();
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.ExternalId).IsUnique();
        });

        modelBuilder.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
        });

        modelBuilder.Entity<Campaign>(e =>
        {
            e.ToTable("campaigns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsRowVersion();
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<Contribution>(e =>
        {
            e.ToTable("contributions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalReference).HasMaxLength(256).IsRequired();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.OrganizationId, x.CampaignId });
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(512).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<StateTransition>(e =>
        {
            e.ToTable("state_transitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.FromState).HasConversion<int>();
            e.Property(x => x.ToState).HasConversion<int>();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.ContributionId);
            e.HasQueryFilter(x => false);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.ChangedBy).HasMaxLength(128).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityId });
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.MessageType).HasMaxLength(128).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            e.Property(x => x.CausationId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.Status, x.OccurredAt });
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.MessageId).HasMaxLength(128).IsRequired();
            e.Property(x => x.MessageType).HasMaxLength(128).IsRequired();
            e.Property(x => x.HandlerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.HandlerVersion).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<JobDefinition>(e =>
        {
            e.ToTable("job_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.HandlerName).HasMaxLength(128).IsRequired();
            e.Property(x => x.RetryPolicy).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<JobRun>(e =>
        {
            e.ToTable("job_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.QueueUrl).HasMaxLength(512).IsRequired();
            e.Property(x => x.MessageId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });

        modelBuilder.Entity<JobAttempt>(e =>
        {
            e.ToTable("job_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.ErrorCategory).HasConversion<int>();
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => x.JobRunId);
            e.HasQueryFilter(x => false);
        });

        modelBuilder.Entity<Lease>(e =>
        {
            e.ToTable("leases");
            e.HasKey(x => x.Id);
            e.Property(x => x.WorkerId).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.ExpiresAt);
            e.HasQueryFilter(x => false);
        });

        modelBuilder.Entity<Checkpoint>(e =>
        {
            e.ToTable("checkpoints");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Value).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => new { x.JobRunId, x.Key }).IsUnique();
            e.HasQueryFilter(x => false);
        });

        modelBuilder.Entity<DeadLetterRecord>(e =>
        {
            e.ToTable("dead_letter_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.OriginalMessageId).HasMaxLength(128).IsRequired();
            e.Property(x => x.MessageType).HasMaxLength(128).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.ErrorCategory).HasConversion<int>();
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasQueryFilter(x => x.OrganizationId == TenantFilterAccessor.CurrentOrganizationId);
        });
    }
}

public static class TenantFilterAccessor
{
    private static readonly AsyncLocal<Guid?> _currentOrganizationId = new();

    public static Guid CurrentOrganizationId => _currentOrganizationId.Value ?? Guid.Empty;

    public static void SetOrganizationId(Guid organizationId)
    {
        _currentOrganizationId.Value = organizationId;
    }

    public static void Clear()
    {
        _currentOrganizationId.Value = null;
    }
}
