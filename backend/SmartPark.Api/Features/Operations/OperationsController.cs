using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;
using SmartPark.Api.Storage;

namespace SmartPark.Api.Features.Operations;

[ApiController]
[Route("api/operations")]
[Authorize(Policy = Policies.Internal)]
public sealed class OperationsController(ParkDbContext db, LocalFileStore files, OverviewCacheService cache, AuditService audit) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, string[]> Templates = new Dictionary<string, string[]>
    {
        ["Maintenance"] = ["检查设备外观", "执行保养项目", "记录下次保养日期"], ["Repair"] = ["确认故障现象", "完成维修处理", "测试恢复结果"], ["Flood"] = ["检查排水设施", "检查重点区域", "记录防汛措施"], ["PlantCare"] = ["检查植物健康", "执行养护项目", "记录养护结果"], ["Patrol"] = ["完成巡更路线", "记录异常情况", "确认安全设施"], ["Cleaning"] = ["完成保洁区域", "清运垃圾", "检查环境卫生"], ["Inspection"] = ["完成检查表", "记录发现问题", "确认整改建议"]
    };
    [HttpGet("templates")]
    public object GetTemplates() => Templates.Select(x => new { type = x.Key, checklist = x.Value }).ToList();

    [HttpGet("work-orders")]
    public async Task<PageResult<object>> List(string? status, string[]? statuses, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = db.WorkOrders.AsNoTracking().AsQueryable(); if (User.IsInRole(ParkRoles.Worker) && !User.IsManager()) query = query.Where(x => x.AssigneeId == User.Id()); var requestedStatuses = (statuses ?? []).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray(); if (requestedStatuses.Length > 0) query = query.Where(x => requestedStatuses.Contains(x.Status)); else if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        var p = await query.OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.Number, x.Title, x.Type, x.AssetId, x.EventId, x.AssigneeId, x.DueAt, x.Priority, x.Status, x.Version, x.CreatedAt }).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Cast<object>().ToList(), p.Total, p.Page, p.PageSize);
    }
    [HttpGet("work-orders/{id:guid}")]
    public async Task<object> Detail(Guid id, CancellationToken ct)
    {
        var item = await db.WorkOrders.AsNoTracking().Include(x => x.Checklist).Include(x => x.Feedbacks).Include(x => x.Logs).Include(x => x.Attachments).ThenInclude(x => x.File).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Work order not found", 404); EnsureAccess(item);
        return new { item.Id, item.Number, item.Title, item.Type, item.AssetId, item.EventId, item.AssigneeId, item.DueAt, item.Priority, details = System.Text.Json.JsonDocument.Parse(item.DetailsJson).RootElement, item.Status, item.Version, checklist = item.Checklist.Select(x => new { x.Id, x.Name, x.Result, x.Completed }), feedbacks = item.Feedbacks.OrderBy(x => x.SubmittedAt).Select(x => new { x.Id, x.AuthorId, x.Text, x.SubmittedAt }), logs = item.Logs.OrderBy(x => x.OccurredAt == default ? x.CreatedAt : x.OccurredAt).Select(x => new { x.Id, x.UserId, x.Action, x.Text, occurredAt = x.OccurredAt == default ? x.CreatedAt : x.OccurredAt }), attachments = item.Attachments.Select(x => new { id = x.FileId, x.FileId, fileName = x.File!.OriginalName, x.File!.ContentType, x.File!.Length, url = "/api/files/" + x.FileId }) };
    }
    [Authorize(Policy = Policies.Manager), HttpPost("work-orders")]
    public async Task<IActionResult> Create(WorkOrderRequest request, CancellationToken ct)
    {
        if (!Templates.ContainsKey(request.Type) || string.IsNullOrWhiteSpace(request.Title) || request.AssigneeId is null) throw new ApiException("Validation failed", 400, "Type, title and assignee are required.");
        var assignee = await db.Users.FindAsync([request.AssigneeId.Value], ct) ?? throw new ApiException("Assignee not found", 404);
        if (!await IsWorkerAsync(assignee.Id, ct)) throw new ApiException("Validation failed", 400, "Assignee must be an enabled worker.");
        if (request.AssetId is not null && !await db.Assets.AnyAsync(x => x.Id == request.AssetId, ct)) throw new ApiException("Asset not found", 404);
        var items = request.Checklist is { Count: > 0 } ? request.Checklist.Distinct(StringComparer.Ordinal).ToList() : Templates[request.Type].ToList();
        var detailsJson = !string.IsNullOrWhiteSpace(request.DetailsJson) ? ValidateJson(request.DetailsJson) : request.Details is { } details ? details.GetRawText() : "{}";
        var item = new WorkOrder { Number = $"WO-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", Title = request.Title.Trim(), Type = request.Type, AssetId = request.AssetId, EventId = request.EventId, AssigneeId = request.AssigneeId, DueAt = request.DueAt, Priority = request.Priority ?? "Normal", DetailsJson = ValidateJson(detailsJson), Status = "Assigned", Checklist = items.Select(x => new WorkOrderChecklistItem { Name = x }).ToList(), Logs = [new WorkOrderLog { UserId = User.Id(), Action = "Created", Text = "Work order assigned" }] };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (request.EventId is Guid eventId)
        {
            var evt = await LockEventAsync(eventId, ct);
            if (evt.Status is "Closed" or "FalseAlarm") throw new ApiException("Invalid transition", 409, "Work orders cannot be created for closed or false-alarm events.");
            if (evt.Status is "Open" or "PendingClosure")
            {
                evt.Status = "Assigned";
                evt.Version++;
                db.EventLogs.Add(new EventLog { EventId = evt.Id, UserId = User.Id(), Action = "WorkOrderAssigned", Text = $"Work order {item.Number} assigned." });
            }
        }
        db.WorkOrders.Add(item); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); await cache.InvalidateAsync(ct); await audit.WriteAsync("Create", "WorkOrder", item.Id, new { item.Number }, ct); return Created($"/api/operations/work-orders/{item.Id}", new { item.Id, item.Number, item.Status, item.Version });
    }
    [HttpPost("work-orders/{id:guid}/transition")]
    public async Task<object> Transition(Guid id, WorkOrderTransitionRequest request, CancellationToken ct)
    {
        var item = await db.WorkOrders.Include(x => x.Checklist).Include(x => x.Attachments).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Work order not found", 404); EnsureAccess(item);
        if (item.Version != request.Version) throw new ApiException("Version conflict", 409, "Refresh the work order before continuing.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        switch (request.Action)
        {
            case "Accept": RequireOwn(item); RequireStatus(item, "Assigned"); item.Status = "Accepted"; break;
            case "Start": RequireOwn(item); RequireStatus(item, "Accepted"); item.Status = "InProgress"; await MoveEventToProcessingAsync(item.EventId, item.Id, ct); break;
            case "Submit":
                RequireOwn(item); RequireStatus(item, "InProgress"); if (string.IsNullOrWhiteSpace(request.Text)) throw new ApiException("Validation failed", 400, "Submit feedback text is required.");
                var results = request.ChecklistResults ?? []; foreach (var checklist in item.Checklist) { var result = results.SingleOrDefault(x => x.Name == checklist.Name); if (result is null || string.IsNullOrWhiteSpace(result.Result)) throw new ApiException("Validation failed", 400, $"Checklist item '{checklist.Name}' needs a result."); checklist.Result = result.Result.Trim(); checklist.Completed = true; }
                var feedback = new WorkOrderFeedback { WorkOrderId = item.Id, AuthorId = User.Id(), Text = request.Text.Trim(), SubmittedAt = DateTimeOffset.UtcNow }; db.WorkOrderFeedbacks.Add(feedback);
                await BindAttachmentsAsync(item, feedback, request.AttachmentIds ?? [], ct); item.Status = "PendingReview"; break;
            case "Approve":
                RequireManager(); RequireStatus(item, "PendingReview"); item.Status = "Completed"; if (item.AssetId is not null) db.AssetMaintenanceHistories.Add(new AssetMaintenanceHistory { AssetId = item.AssetId.Value, WorkOrderId = item.Id, Summary = request.Text ?? "Approved work order", CompletedAt = DateTimeOffset.UtcNow }); await MoveEventToPendingClosureAsync(item.EventId, item.Id, ct); db.OutboxMessages.Add(new OutboxMessage { Kind = "WorkOrderCompleted", PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { workOrderId = item.Id, eventId = item.EventId }) }); break;
            case "Reject": RequireManager(); RequireStatus(item, "PendingReview"); if (string.IsNullOrWhiteSpace(request.Text)) throw new ApiException("Validation failed", 400, "Rejection reason is required."); item.Status = "InProgress"; break;
            case "Cancel": RequireManager(); if (item.Status is "Completed" or "Cancelled") throw new ApiException("Invalid transition", 409); if (string.IsNullOrWhiteSpace(request.Text)) throw new ApiException("Validation failed", 400, "Cancellation reason is required."); item.Status = "Cancelled"; break;
            case "Reassign": RequireManager(); if (item.Status is "Completed" or "Cancelled" or "PendingReview" || request.AssigneeId is null) throw new ApiException("Invalid transition", 409); if (!await IsWorkerAsync(request.AssigneeId.Value, ct)) throw new ApiException("Validation failed", 400, "Assignee must be an enabled worker."); item.AssigneeId = request.AssigneeId; item.Status = "Assigned"; break;
            default: throw new ApiException("Validation failed", 400, "Unsupported work order action.");
        }
        item.Version++; db.WorkOrderLogs.Add(new WorkOrderLog { WorkOrderId = item.Id, UserId = User.Id(), Action = request.Action, Text = request.Text }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); await cache.InvalidateAsync(ct); await audit.WriteAsync(request.Action, "WorkOrder", item.Id, null, ct); return new { item.Id, item.Status, item.Version };
    }
    [HttpPost("work-orders/{id:guid}/photos")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadPhoto(Guid id, [FromForm] IFormFile file, CancellationToken ct)
    {
        var order = await db.WorkOrders.Include(x => x.Attachments).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Work order not found", 404); EnsureAccess(order); RequireOwn(order); if (order.Status != "InProgress") throw new ApiException("Invalid transition", 409, "Photos may be uploaded while work is in progress."); if (await db.StoredFiles.CountAsync(x => x.WorkOrderId == id && x.OwnerId == User.Id() && x.IsTemporary && x.Kind == "photo", ct) >= 5) throw new ApiException("Attachment limit reached", 400, "At most five photos may be attached to a feedback.");
        var stored = await files.SaveAsync(file, "photo", User.Id(), false, true, ct); stored.WorkOrderId = id; await db.SaveChangesAsync(ct); return Created($"/api/files/{stored.Id}", new { id = stored.Id, fileName = stored.OriginalName });
    }
    [Authorize(Policy = Policies.Manager), HttpGet("flood-plans")]
    public async Task<PageResult<FloodPlan>> FloodPlans(int page = 1, int pageSize = 20, CancellationToken ct = default) => await db.FloodPlans.AsNoTracking().OrderBy(x => x.Name).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Manager), HttpPost("flood-plans")]
    public async Task<IActionResult> CreateFloodPlan(FloodPlanRequest request, CancellationToken ct) { if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Content)) throw new ApiException("Validation failed", 400); var plan = new FloodPlan { Name = request.Name.Trim(), Content = request.Content.Trim(), Status = request.Status ?? "Active" }; db.FloodPlans.Add(plan); await db.SaveChangesAsync(ct); return Created($"/api/operations/flood-plans/{plan.Id}", plan); }
    private void EnsureAccess(WorkOrder order) { if (User.IsInRole(ParkRoles.Worker) && !User.IsManager() && order.AssigneeId != User.Id()) throw new ApiException("Forbidden", 403); }
    private void RequireOwn(WorkOrder order) { if (order.AssigneeId != User.Id()) throw new ApiException("Forbidden", 403); }
    private void RequireManager() { if (!User.IsManager()) throw new ApiException("Forbidden", 403); }
    private static void RequireStatus(WorkOrder order, string status) { if (order.Status != status) throw new ApiException("Invalid transition", 409, $"Work order must be {status}."); }
    private async Task<bool> IsWorkerAsync(Guid id, CancellationToken ct) => await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id where u.Id == id && !u.Disabled && r.Name == ParkRoles.Worker select u.Id).AnyAsync(ct);
    private async Task BindAttachmentsAsync(WorkOrder item, WorkOrderFeedback feedback, IReadOnlyList<Guid> fileIds, CancellationToken ct) { if (fileIds.Count > 5) throw new ApiException("Attachment limit reached", 400); var distinct = fileIds.Distinct().ToArray(); var stored = await db.StoredFiles.Where(x => distinct.Contains(x.Id)).ToListAsync(ct); if (stored.Count != distinct.Length || stored.Any(x => x.OwnerId != User.Id() || x.WorkOrderId != item.Id || !x.IsTemporary || x.Kind != "photo")) throw new ApiException("Invalid attachment", 400); foreach (var file in stored) { file.IsTemporary = false; db.WorkOrderAttachments.Add(new WorkOrderAttachment { WorkOrderId = item.Id, FileId = file.Id, FeedbackId = feedback.Id }); } }
    private async Task MoveEventToProcessingAsync(Guid? eventId, Guid workOrderId, CancellationToken ct)
    {
        if (eventId is null) return;
        var evt = await LockEventAsync(eventId.Value, ct);
        if (evt.Status is "Closed" or "FalseAlarm") throw new ApiException("Invalid transition", 409, "Closed or false-alarm events cannot receive work-order updates.");
        evt.Status = "Processing";
        evt.Version++;
        db.EventLogs.Add(new EventLog { EventId = evt.Id, UserId = User.Id(), Action = "WorkOrderStarted", Text = $"Work order {workOrderId} started." });
    }
    private async Task MoveEventToPendingClosureAsync(Guid? eventId, Guid approvedWorkOrderId, CancellationToken ct)
    {
        if (eventId is null) return;
        var evt = await LockEventAsync(eventId.Value, ct);
        if (evt.Status is "Closed" or "FalseAlarm") throw new ApiException("Invalid transition", 409, "Closed or false-alarm events cannot receive work-order updates.");
        var remaining = await db.WorkOrders.AnyAsync(x => x.EventId == eventId && x.Id != approvedWorkOrderId && x.Status != "Completed" && x.Status != "Cancelled", ct);
        if (!remaining)
        {
            evt.Status = "PendingClosure";
            evt.Version++;
            db.EventLogs.Add(new EventLog { EventId = evt.Id, UserId = User.Id(), Action = "TasksCompleted", Text = "All related work orders completed." });
        }
    }
    private async Task<ParkEvent> LockEventAsync(Guid eventId, CancellationToken ct) => await db.ParkEvents.FromSqlInterpolated($"SELECT * FROM \"ParkEvents\" WHERE \"Id\" = {eventId} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw new ApiException("Event not found", 404);
    private static string ValidateJson(string json) { try { using var _ = System.Text.Json.JsonDocument.Parse(json); return json; } catch (System.Text.Json.JsonException) { throw new ApiException("Validation failed", 400, "Details must be valid JSON."); } }
}

[ApiController]
[Route("api/files")]
public sealed class FilesController(ParkDbContext db, LocalFileStore files) : ControllerBase
{
    [AllowAnonymous, HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var stored = await db.StoredFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("File not found", 404);
        if (!stored.IsPublic)
        {
            if (!User.Identity!.IsAuthenticated || !User.IsInternal()) throw new ApiException("Forbidden", 403);
            if (User.IsInRole(ParkRoles.Worker) && !User.IsManager()) { var allowed = stored.WorkOrderId is not null && await db.WorkOrders.AnyAsync(x => x.Id == stored.WorkOrderId && x.AssigneeId == User.Id(), ct); if (!allowed) throw new ApiException("Forbidden", 403); }
        }
        var path = files.GetPath(stored); if (!System.IO.File.Exists(path)) throw new ApiException("File missing", 404); return PhysicalFile(path, stored.ContentType, stored.OriginalName, enableRangeProcessing: true);
    }
}

public sealed record WorkOrderRequest(string Title, string Type, Guid? AssetId, Guid? EventId, Guid? AssigneeId, DateTimeOffset? DueAt, string? Priority, string? DetailsJson, System.Text.Json.JsonElement? Details, List<string>? Checklist);
public sealed record ChecklistResult(string Name, string Result);
public sealed record WorkOrderTransitionRequest(string Action, int Version, string? Text, List<ChecklistResult>? ChecklistResults, List<Guid>? AttachmentIds, Guid? AssigneeId);
public sealed record FloodPlanRequest(string Name, string Content, string? Status);
