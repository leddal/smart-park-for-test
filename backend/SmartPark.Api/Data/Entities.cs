using Microsoft.AspNetCore.Identity;

namespace SmartPark.Api.Data;

public sealed class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Park : Entity
{
    public string Name { get; set; } = "智慧公园";
    public string Description { get; set; } = "";
    public string OpenHours { get; set; } = "08:00-20:00";
    public string Phone { get; set; } = "";
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
    public int VisitorInsideCount { get; set; }
    public List<ParkZone> Zones { get; set; } = [];
}
public sealed class ParkZone : Entity { public Guid ParkId { get; set; } public Park? Park { get; set; } public string Name { get; set; } = ""; public string Code { get; set; } = ""; public string? Description { get; set; } }
public sealed class MapLayer : Entity { public string Name { get; set; } = ""; public string Kind { get; set; } = ""; public string? GeoJson { get; set; } public Guid? FileId { get; set; } public string? BoundsJson { get; set; } public bool IsPublic { get; set; } }
public sealed class ImportBatch : Entity { public string Type { get; set; } = ""; public string LayerKind { get; set; } = ""; public string Status { get; set; } = "Preview"; public Guid? FileId { get; set; } public Guid? OperatorId { get; set; } public int Rows { get; set; } public string? ErrorsJson { get; set; } public string? PayloadJson { get; set; } }
public sealed class StoredFile : Entity { public string StorageName { get; set; } = ""; public string OriginalName { get; set; } = ""; public string ContentType { get; set; } = ""; public long Length { get; set; } public string Kind { get; set; } = ""; public bool IsPublic { get; set; } public bool IsTemporary { get; set; } public Guid? OwnerId { get; set; } public Guid? WorkOrderId { get; set; } }
public sealed class AuditLog : Entity { public Guid? UserId { get; set; } public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; public Guid? EntityId { get; set; } public DateTimeOffset OccurredAt { get; set; } public string? DataJson { get; set; } }
public sealed class SeedVersion : Entity { public string Name { get; set; } = ""; }

public sealed class Asset : Entity
{
    public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Category { get; set; } = "";
    public Guid? ZoneId { get; set; } public ParkZone? Zone { get; set; } public decimal? Longitude { get; set; } public decimal? Latitude { get; set; }
    public string Status { get; set; } = "Active"; public string PublicDescription { get; set; } = ""; public string PublicCode { get; set; } = ""; public int Version { get; set; }
    public Device? Device { get; set; } public PlantProfile? Plant { get; set; } public FacilityProfile? Facility { get; set; }
}
public sealed class Device : Entity
{
    public Guid AssetId { get; set; } public Asset? Asset { get; set; } public string Code { get; set; } = ""; public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true; public string? Model { get; set; } public string? Manufacturer { get; set; }
    public DateTimeOffset? LastTelemetryAt { get; set; } public string ControlState { get; set; } = "Stopped"; public long ControlSequence { get; set; } public DateTimeOffset? ControlExpiresAt { get; set; }
}
public sealed class PlantProfile : Entity { public Guid AssetId { get; set; } public Asset? Asset { get; set; } public string Species { get; set; } = ""; public decimal? DiameterCm { get; set; } public decimal? HeightM { get; set; } public string Health { get; set; } = "Good"; }
public sealed class FacilityProfile : Entity { public Guid AssetId { get; set; } public Asset? Asset { get; set; } public string Kind { get; set; } = ""; public string? Specification { get; set; } public int Quantity { get; set; } = 1; }
public sealed class AssetMaintenanceHistory : Entity { public Guid AssetId { get; set; } public Guid WorkOrderId { get; set; } public string Summary { get; set; } = ""; public DateTimeOffset CompletedAt { get; set; } }

public sealed class Tag : Entity { public string Name { get; set; } = ""; public string? Color { get; set; } public bool IsPublic { get; set; } = true; }
public sealed class Announcement : Entity { public string Title { get; set; } = ""; public string Body { get; set; } = ""; public DateTimeOffset? StartsAt { get; set; } public DateTimeOffset? EndsAt { get; set; } public Guid? EventId { get; set; } public string Status { get; set; } = "Draft"; public DateTimeOffset? PublishedAt { get; set; } }
public sealed class Activity : Entity { public string Title { get; set; } = ""; public string Description { get; set; } = ""; public string Location { get; set; } = ""; public string Status { get; set; } = "Draft"; public List<ActivitySession> Sessions { get; set; } = []; }
public sealed class ActivitySession : Entity { public Guid ActivityId { get; set; } public Activity? Activity { get; set; } public DateTimeOffset StartsAt { get; set; } public DateTimeOffset EndsAt { get; set; } public int Capacity { get; set; } public int ReservedCount { get; set; } public string Status { get; set; } = "Published"; public List<Reservation> Reservations { get; set; } = []; }
public sealed class Reservation : Entity { public Guid SessionId { get; set; } public ActivitySession? Session { get; set; } public Guid VisitorId { get; set; } public string Status { get; set; } = "Valid"; public string Code { get; set; } = ""; public DateTimeOffset? CheckedInAt { get; set; } public DateTimeOffset? CancelledAt { get; set; } }

public sealed class MetricDefinition : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string Unit { get; set; } = ""; public string DeviceType { get; set; } = ""; }
public sealed class TelemetrySample : Entity { public Guid DeviceId { get; set; } public Device? Device { get; set; } public string MetricCode { get; set; } = ""; public decimal Value { get; set; } public string Unit { get; set; } = ""; public DateTimeOffset CollectedAt { get; set; } public string Source { get; set; } = ""; public string? IdempotencyKey { get; set; } }
public sealed class VisitorCounterSample : Entity { public string Direction { get; set; } = "In"; public int Count { get; set; } public string Unit { get; set; } = "People"; public DateTimeOffset CollectedAt { get; set; } public string Source { get; set; } = "Seed"; public string? IdempotencyKey { get; set; } }
public sealed class AlertRule : Entity { public Guid? DeviceId { get; set; } public string? DeviceType { get; set; } public string MetricCode { get; set; } = ""; public string Unit { get; set; } = ""; public decimal? Lower { get; set; } public decimal? Upper { get; set; } public decimal? RecoveryLower { get; set; } public decimal? RecoveryUpper { get; set; } public string Severity { get; set; } = "Warning"; public bool Enabled { get; set; } = true; public int Version { get; set; } }
public sealed class Alert : Entity { public Guid DeviceId { get; set; } public Guid RuleId { get; set; } public Guid? EventId { get; set; } public string MetricCode { get; set; } = ""; public decimal Value { get; set; } public string Severity { get; set; } = ""; public string Status { get; set; } = "Active"; public DateTimeOffset TriggeredAt { get; set; } public DateTimeOffset? AcknowledgedAt { get; set; } public Guid? AcknowledgedById { get; set; } public DateTimeOffset? RecoveredAt { get; set; } public bool SuppressedUntilRecovery { get; set; } }
public sealed class AlertLog : Entity { public Guid AlertId { get; set; } public string Message { get; set; } = ""; public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class PestObservation : Entity { public Guid? DeviceId { get; set; } public string Species { get; set; } = ""; public int Count { get; set; } public DateTimeOffset ObservedAt { get; set; } public string Source { get; set; } = "Manual"; public string? Notes { get; set; } }
public sealed class SimulationState : Entity { public bool Enabled { get; set; } }
public sealed class MediaMaterial : Entity { public string Name { get; set; } = ""; public Guid? FileId { get; set; } public string? SamplePath { get; set; } public string ContentType { get; set; } = "audio/wav"; }

public sealed class WorkOrder : Entity
{
    public string Number { get; set; } = ""; public string Title { get; set; } = ""; public string Type { get; set; } = ""; public Guid? AssetId { get; set; } public Asset? Asset { get; set; } public Guid? EventId { get; set; } public ParkEvent? Event { get; set; }
    public Guid? AssigneeId { get; set; } public DateTimeOffset? DueAt { get; set; } public string Priority { get; set; } = "Normal"; public string DetailsJson { get; set; } = "{}"; public string Status { get; set; } = "Assigned"; public int Version { get; set; }
    public List<WorkOrderChecklistItem> Checklist { get; set; } = []; public List<WorkOrderFeedback> Feedbacks { get; set; } = []; public List<WorkOrderLog> Logs { get; set; } = []; public List<WorkOrderAttachment> Attachments { get; set; } = [];
}
public sealed class WorkOrderChecklistItem : Entity { public Guid WorkOrderId { get; set; } public WorkOrder? WorkOrder { get; set; } public string Name { get; set; } = ""; public string? Result { get; set; } public bool Completed { get; set; } }
public sealed class WorkOrderFeedback : Entity { public Guid WorkOrderId { get; set; } public WorkOrder? WorkOrder { get; set; } public Guid AuthorId { get; set; } public string Text { get; set; } = ""; public DateTimeOffset SubmittedAt { get; set; } }
public sealed class WorkOrderAttachment : Entity { public Guid WorkOrderId { get; set; } public WorkOrder? WorkOrder { get; set; } public Guid FileId { get; set; } public StoredFile? File { get; set; } public Guid? FeedbackId { get; set; } }
public sealed class WorkOrderLog : Entity { public Guid WorkOrderId { get; set; } public WorkOrder? WorkOrder { get; set; } public Guid? UserId { get; set; } public string Action { get; set; } = ""; public string? Text { get; set; } public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class FloodPlan : Entity { public string Name { get; set; } = ""; public string Content { get; set; } = ""; public string Status { get; set; } = "Active"; }

public sealed class ParkEvent : Entity
{
    public string Number { get; set; } = ""; public string Title { get; set; } = ""; public string Category { get; set; } = ""; public string Severity { get; set; } = "Warning"; public Guid? AssetId { get; set; } public Guid? AlertId { get; set; }
    public string Description { get; set; } = ""; public string Status { get; set; } = "Open"; public int Version { get; set; } public decimal? Longitude { get; set; } public decimal? Latitude { get; set; } public List<EventLog> Logs { get; set; } = []; public List<WorkOrder> WorkOrders { get; set; } = [];
}
public sealed class EventLog : Entity { public Guid EventId { get; set; } public ParkEvent? Event { get; set; } public Guid? UserId { get; set; } public string Action { get; set; } = ""; public string? Text { get; set; } public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow; }

public sealed class ControlCommand : Entity { public Guid DeviceId { get; set; } public Device? Device { get; set; } public Guid RequestedById { get; set; } public string Action { get; set; } = ""; public int? DurationSeconds { get; set; } public int? Brightness { get; set; } public string? Text { get; set; } public string Outcome { get; set; } = "Success"; public string Status { get; set; } = "Pending"; public long Sequence { get; set; } public DateTimeOffset? ProcessedAt { get; set; } public string? Receipt { get; set; } }

public sealed class IntegrationPlatform : Entity { public string Name { get; set; } = ""; public bool Enabled { get; set; } = true; public bool ForceFailure { get; set; } }
public sealed class OutboxMessage : Entity { public string Kind { get; set; } = ""; public Guid? PlatformId { get; set; } public string PayloadJson { get; set; } = "{}"; public string Status { get; set; } = "Pending"; public int Generation { get; set; } public int PublishAttempts { get; set; } public int PublishFailures { get; set; } public int Attempts { get; set; } public DateTimeOffset? PublishedAt { get; set; } public DateTimeOffset? ConsumedAt { get; set; } public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow; public string? LastError { get; set; } }
public sealed class IntegrationAttempt : Entity { public Guid OutboxMessageId { get; set; } public OutboxMessage? OutboxMessage { get; set; } public int Generation { get; set; } public string Stage { get; set; } = "Consume"; public int Attempt { get; set; } public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow; public bool Success { get; set; } public string Message { get; set; } = ""; }
