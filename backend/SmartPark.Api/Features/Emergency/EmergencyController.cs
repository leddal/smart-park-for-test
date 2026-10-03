using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Api.Features.Emergency;

public sealed class EmergencyService(ParkDbContext db, OverviewCacheService cache)
{
    public async Task<ParkEvent> CreateAsync(EventRequest request, Guid userId, CancellationToken ct = default)
    {
        if (request.AssetId is not null && !await db.Assets.AnyAsync(x => x.Id == request.AssetId, ct)) throw new ApiException("Asset not found", 404);
        if (request.AlertId is not null && !await db.Alerts.AnyAsync(x => x.Id == request.AlertId, ct)) throw new ApiException("Alert not found", 404);
        var item = new ParkEvent { Number = $"EVT-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", Title = request.Title.Trim(), Category = request.Category.Trim(), Severity = request.Severity, AssetId = request.AssetId, AlertId = request.AlertId, Description = request.Description ?? "", Status = "Open", Longitude = request.Longitude, Latitude = request.Latitude };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AddRange(item, new EventLog { Event = item, UserId = userId, Action = "Created", Text = item.Description }, new OutboxMessage { Kind = "EventCreated", PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { eventId = item.Id }) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); await cache.InvalidateAsync(ct); return item;
    }
}

[ApiController]
[Route("api/emergency")]
[Authorize(Policy = Policies.Manager)]
public sealed class EmergencyController(ParkDbContext db, EmergencyService events, OverviewCacheService cache, AuditService audit) : ControllerBase
{
    [HttpGet("events")]
    public async Task<PageResult<object>> List(string? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var q = db.ParkEvents.AsNoTracking().AsQueryable(); if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        var p = await q.OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.Number, x.Title, x.Category, x.Severity, x.AssetId, x.AlertId, x.Status, x.Version, x.Longitude, x.Latitude, x.CreatedAt }).ToPageAsync(page, pageSize, ct);
        return new PageResult<object>(p.Items.Cast<object>().ToList(), p.Total, p.Page, p.PageSize);
    }
    [HttpGet("events/{id:guid}")]
    public async Task<object> Detail(Guid id, CancellationToken ct)
    {
        var item = await db.ParkEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Event not found", 404);
        var logs = await db.EventLogs.AsNoTracking().Where(x => x.EventId == id).OrderBy(x => x.OccurredAt).ToListAsync(ct);
        var workOrders = await db.WorkOrders.AsNoTracking().Where(x => x.EventId == id).OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.Number, x.Title, x.Status, x.AssigneeId, x.DueAt }).ToListAsync(ct);
        return new { item.Id, item.Number, item.Title, item.Category, item.Severity, item.AssetId, item.AlertId, item.Description, item.Status, item.Version, item.Longitude, item.Latitude, logs, workOrders };
    }
    [Authorize(Policy = Policies.Manager), HttpPost("events")]
    public async Task<IActionResult> Create(EventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Category)) throw new ApiException("Validation failed", 400);
        var item = await events.CreateAsync(request, User.Id(), ct); await audit.WriteAsync("Create", "Event", item.Id, new { item.Number }, ct); return Created($"/api/emergency/events/{item.Id}", new { item.Id, item.Number, item.Status, item.Version });
    }
    [Authorize(Policy = Policies.Manager), HttpPost("events/{id:guid}/transition")]
    public async Task<object> Transition(Guid id, EventTransitionRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await LockEventAsync(id, ct);
        if (item.Version != request.Version) throw new ApiException("Version conflict", 409, "Refresh the event before continuing.");
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ApiException("Validation failed", 400, "A transition reason is required.");
        switch (request.Action)
        {
            case "RequestClosure":
                if (item.Status is "Closed" or "FalseAlarm") throw new ApiException("Invalid transition", 409);
                item.Status = "PendingClosure"; break;
            case "Close":
                if (item.Status != "PendingClosure") throw new ApiException("Invalid transition", 409, "Request closure before closing the event.");
                if (await db.WorkOrders.AnyAsync(x => x.EventId == id && x.Status != "Completed" && x.Status != "Cancelled", ct)) throw new ApiException("Event cannot close", 409, "All related work orders must be complete.");
                if (item.AlertId is not null && await db.Alerts.AnyAsync(x => x.Id == item.AlertId && x.Status == "Active", ct)) throw new ApiException("Event cannot close", 409, "The related alert is still active.");
                item.Status = "Closed"; db.OutboxMessages.Add(new OutboxMessage { Kind = "EventClosed", PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { eventId = item.Id }) }); break;
            case "FalseAlarm":
                if (item.Status is "Closed" or "FalseAlarm") throw new ApiException("Invalid transition", 409);
                item.Status = "FalseAlarm";
                var work = await db.WorkOrders.Where(x => x.EventId == id && x.Status != "Completed" && x.Status != "Cancelled").ToListAsync(ct); foreach (var order in work) { order.Status = "Cancelled"; order.Version++; db.WorkOrderLogs.Add(new WorkOrderLog { WorkOrderId = order.Id, UserId = User.Id(), Action = "CancelledByFalseAlarm", Text = request.Text }); }
                if (item.AlertId is not null) { var alert = await db.Alerts.SingleOrDefaultAsync(x => x.Id == item.AlertId, ct); if (alert is not null && alert.Status == "Active") alert.SuppressedUntilRecovery = true; }
                break;
            default: throw new ApiException("Validation failed", 400, "Unsupported event action.");
        }
        item.Version++; db.EventLogs.Add(new EventLog { EventId = id, UserId = User.Id(), Action = request.Action, Text = request.Text }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); await cache.InvalidateAsync(ct); await audit.WriteAsync(request.Action, "Event", id, null, ct); return new { item.Id, item.Status, item.Version };
    }
    private async Task<ParkEvent> LockEventAsync(Guid eventId, CancellationToken ct) => await db.ParkEvents.FromSqlInterpolated($"SELECT * FROM \"ParkEvents\" WHERE \"Id\" = {eventId} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw new ApiException("Event not found", 404);
    [HttpGet("statistics")]
    public async Task<object> Statistics(CancellationToken ct)
    {
        var all = await db.ParkEvents.AsNoTracking().ToListAsync(ct); var closed = all.Where(x => x.Status == "Closed").ToList();
        return new { total = all.Count, open = all.Count(x => x.Status is not "Closed" and not "FalseAlarm"), closed = closed.Count, byCategory = all.GroupBy(x => x.Category).Select(x => new { category = x.Key, count = x.Count() }), bySeverity = all.GroupBy(x => x.Severity).Select(x => new { severity = x.Key, count = x.Count() }), closureRate = all.Count == 0 ? 0 : Math.Round((decimal)closed.Count / all.Count * 100, 1), averageProcessingHours = closed.Count == 0 ? (decimal?)null : Math.Round((decimal)closed.Average(x => (x.UpdatedAt - x.CreatedAt).TotalHours), 2) };
    }
}

public sealed record EventRequest(string Title, string Category, string Severity, Guid? AssetId, Guid? AlertId, string? Description, decimal? Longitude, decimal? Latitude);
public sealed record EventTransitionRequest(string Action, int Version, string Text);
