using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SmartPark.Api.Data;

public sealed class ParkDbContext(DbContextOptions<ParkDbContext> options) : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Park> Parks => Set<Park>(); public DbSet<ParkZone> ParkZones => Set<ParkZone>(); public DbSet<MapLayer> MapLayers => Set<MapLayer>(); public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>(); public DbSet<StoredFile> StoredFiles => Set<StoredFile>(); public DbSet<AuditLog> AuditLogs => Set<AuditLog>(); public DbSet<SeedVersion> SeedVersions => Set<SeedVersion>();
    public DbSet<Asset> Assets => Set<Asset>(); public DbSet<Device> Devices => Set<Device>(); public DbSet<PlantProfile> PlantProfiles => Set<PlantProfile>(); public DbSet<FacilityProfile> FacilityProfiles => Set<FacilityProfile>(); public DbSet<AssetMaintenanceHistory> AssetMaintenanceHistories => Set<AssetMaintenanceHistory>();
    public DbSet<Tag> Tags => Set<Tag>(); public DbSet<Announcement> Announcements => Set<Announcement>(); public DbSet<Activity> Activities => Set<Activity>(); public DbSet<ActivitySession> ActivitySessions => Set<ActivitySession>(); public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<MetricDefinition> MetricDefinitions => Set<MetricDefinition>(); public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>(); public DbSet<VisitorCounterSample> VisitorCounterSamples => Set<VisitorCounterSample>(); public DbSet<AlertRule> AlertRules => Set<AlertRule>(); public DbSet<Alert> Alerts => Set<Alert>(); public DbSet<AlertLog> AlertLogs => Set<AlertLog>(); public DbSet<PestObservation> PestObservations => Set<PestObservation>(); public DbSet<SimulationState> SimulationStates => Set<SimulationState>(); public DbSet<MediaMaterial> MediaMaterials => Set<MediaMaterial>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>(); public DbSet<WorkOrderChecklistItem> WorkOrderChecklistItems => Set<WorkOrderChecklistItem>(); public DbSet<WorkOrderFeedback> WorkOrderFeedbacks => Set<WorkOrderFeedback>(); public DbSet<WorkOrderAttachment> WorkOrderAttachments => Set<WorkOrderAttachment>(); public DbSet<WorkOrderLog> WorkOrderLogs => Set<WorkOrderLog>(); public DbSet<FloodPlan> FloodPlans => Set<FloodPlan>();
    public DbSet<ParkEvent> ParkEvents => Set<ParkEvent>(); public DbSet<EventLog> EventLogs => Set<EventLog>(); public DbSet<ControlCommand> ControlCommands => Set<ControlCommand>(); public DbSet<IntegrationPlatform> IntegrationPlatforms => Set<IntegrationPlatform>(); public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>(); public DbSet<IntegrationAttempt> IntegrationAttempts => Set<IntegrationAttempt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        foreach (var entity in b.Model.GetEntityTypes().Where(x => typeof(Entity).IsAssignableFrom(x.ClrType)))
        {
            b.Entity(entity.ClrType).Property(nameof(Entity.CreatedAt)).HasColumnType("timestamp with time zone");
            b.Entity(entity.ClrType).Property(nameof(Entity.UpdatedAt)).HasColumnType("timestamp with time zone");
        }
        b.Entity<AppUser>().Property(x => x.DisplayName).HasMaxLength(120);
        b.Entity<Park>().ToTable(t => t.HasCheckConstraint("CK_Parks_VisitorInsideCount_NonNegative", "\"VisitorInsideCount\" >= 0"));
        b.Entity<ParkZone>().HasIndex(x => new { x.ParkId, x.Code }).IsUnique();
        b.Entity<MapLayer>().Property(x => x.GeoJson).HasColumnType("jsonb");
        b.Entity<ImportBatch>().Property(x => x.ErrorsJson).HasColumnType("jsonb"); b.Entity<ImportBatch>().Property(x => x.PayloadJson).HasColumnType("jsonb");
        b.Entity<Asset>().HasIndex(x => x.Code).IsUnique(); b.Entity<Asset>().HasIndex(x => x.PublicCode).IsUnique(); b.Entity<Asset>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Asset>().HasOne(x => x.Device).WithOne(x => x.Asset).HasForeignKey<Device>(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Asset>().HasOne(x => x.Plant).WithOne(x => x.Asset).HasForeignKey<PlantProfile>(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Asset>().HasOne(x => x.Facility).WithOne(x => x.Asset).HasForeignKey<FacilityProfile>(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Device>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Announcement>().HasIndex(x => new { x.Status, x.StartsAt, x.EndsAt });
        b.Entity<ActivitySession>().HasIndex(x => new { x.Status, x.StartsAt });
        b.Entity<ActivitySession>().ToTable(t => t.HasCheckConstraint("CK_ActivitySessions_Capacity", "\"Capacity\" > 0 AND \"ReservedCount\" >= 0 AND \"ReservedCount\" <= \"Capacity\""));
        b.Entity<Reservation>().HasIndex(x => new { x.VisitorId, x.SessionId }).IsUnique().HasFilter("\"Status\" IN ('Valid', 'CheckedIn')");
        b.Entity<Reservation>().HasIndex(x => x.Code).IsUnique();
        b.Entity<MetricDefinition>().HasIndex(x => x.Code).IsUnique();
        b.Entity<TelemetrySample>().HasIndex(x => new { x.DeviceId, x.MetricCode, x.CollectedAt });
        b.Entity<TelemetrySample>().HasIndex(x => new { x.DeviceId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        b.Entity<TelemetrySample>().ToTable(t => t.HasCheckConstraint("CK_TelemetrySamples_Source", "\"Source\" IN ('Seed', 'Simulation', 'Manual')"));
        b.Entity<VisitorCounterSample>().HasIndex(x => new { x.Direction, x.CollectedAt });
        b.Entity<VisitorCounterSample>().HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        b.Entity<VisitorCounterSample>().ToTable(t =>
        {
            t.HasCheckConstraint("CK_VisitorCounterSamples_Direction", "\"Direction\" IN ('In', 'Out')");
            t.HasCheckConstraint("CK_VisitorCounterSamples_Count", "\"Count\" > 0 AND \"Count\" <= 10000");
            t.HasCheckConstraint("CK_VisitorCounterSamples_Source", "\"Source\" IN ('Seed', 'Simulation', 'Manual')");
            t.HasCheckConstraint("CK_VisitorCounterSamples_Unit", "\"Unit\" = 'People'");
        });
        b.Entity<AlertRule>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Alert>().HasIndex(x => new { x.DeviceId, x.RuleId }).IsUnique().HasFilter("\"Status\" = 'Active'");
        b.Entity<WorkOrder>().HasIndex(x => x.Number).IsUnique(); b.Entity<WorkOrder>().HasIndex(x => new { x.AssigneeId, x.Status }); b.Entity<WorkOrder>().Property(x => x.DetailsJson).HasColumnType("jsonb"); b.Entity<WorkOrder>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<WorkOrderChecklistItem>().HasIndex(x => new { x.WorkOrderId, x.Name }).IsUnique();
        b.Entity<WorkOrderAttachment>().HasIndex(x => new { x.WorkOrderId, x.FileId }).IsUnique();
        b.Entity<ParkEvent>().HasIndex(x => x.Number).IsUnique(); b.Entity<ParkEvent>().HasIndex(x => x.AlertId).IsUnique().HasFilter("\"AlertId\" IS NOT NULL"); b.Entity<ParkEvent>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<ParkEvent>().HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ParkEvent>().HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ControlCommand>().HasIndex(x => new { x.DeviceId, x.Sequence }); b.Entity<ControlCommand>().HasIndex(x => x.Status);
        b.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        // Manual replay starts a new generation; publishing and consuming keep separate immutable logs.
        b.Entity<IntegrationAttempt>().Property(x => x.Stage).HasDefaultValue("Consume");
        b.Entity<IntegrationAttempt>().HasIndex(x => new { x.OutboxMessageId, x.Generation, x.Stage, x.Attempt }).IsUnique();
        foreach (var property in b.Model.GetEntityTypes().SelectMany(x => x.GetProperties()).Where(x => x.ClrType == typeof(decimal) || x.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(6);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp(); return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    private void Stamp()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in ChangeTracker.Entries<Entity>())
        {
            if (item.State == EntityState.Added) { item.Entity.CreatedAt = now; item.Entity.UpdatedAt = now; }
            if (item.State == EntityState.Modified) item.Entity.UpdatedAt = now;
        }
    }
}
