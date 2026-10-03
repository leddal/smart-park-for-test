using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;

namespace SmartPark.Api.Features.Services;

public sealed class ReservationService(ParkDbContext db)
{
    public async Task<Reservation> ReserveAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var session = await LockSessionAsync(sessionId, ct);
        var now = DateTimeOffset.UtcNow;
        if (session.Activity?.Status != "Published" || session.Status != "Published" || session.StartsAt <= now)
            throw new ApiException("Reservation unavailable", 409, "The activity or session is not open for reservations.");
        if (await db.Reservations.AsNoTracking().AnyAsync(x => x.VisitorId == userId && x.SessionId == sessionId && (x.Status == "Valid" || x.Status == "CheckedIn"), ct))
            throw new ApiException("Already reserved", 409, "Only one valid reservation is allowed per session.");

        var incremented = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivitySessions\" SET \"ReservedCount\" = \"ReservedCount\" + 1 WHERE \"Id\" = {sessionId} AND \"Status\" = {"Published"} AND \"ReservedCount\" < \"Capacity\"", ct);
        if (incremented != 1) throw new ApiException("Session full", 409, "No capacity remains.");

        var reservation = new Reservation { SessionId = sessionId, VisitorId = userId, Status = "Valid", Code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)), CreatedAt = now };
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reservation;
    }

    public async Task CancelAsync(Guid userId, Guid reservationId, bool internalActor, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var sessionId = await db.Reservations.AsNoTracking().Where(x => x.Id == reservationId).Select(x => (Guid?)x.SessionId).SingleOrDefaultAsync(ct) ?? throw new ApiException("Reservation not found", 404);
        var session = await LockSessionAsync(sessionId, ct);
        var reservation = await db.Reservations.SingleOrDefaultAsync(x => x.Id == reservationId, ct) ?? throw new ApiException("Reservation not found", 404);
        if (!internalActor && reservation.VisitorId != userId) throw new ApiException("Forbidden", 403);
        if (reservation.Status == "Cancelled") { await tx.CommitAsync(ct); return; }
        if (reservation.Status == "CheckedIn" || session.StartsAt <= DateTimeOffset.UtcNow) throw new ApiException("Reservation cannot be cancelled", 409);
        if (reservation.Status != "Valid") throw new ApiException("Reservation cannot be cancelled", 409);

        session.ReservedCount = Math.Max(0, session.ReservedCount - 1);
        reservation.Status = "Cancelled";
        reservation.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task CheckInAsync(Guid reservationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var sessionId = await db.Reservations.AsNoTracking().Where(x => x.Id == reservationId).Select(x => (Guid?)x.SessionId).SingleOrDefaultAsync(ct) ?? throw new ApiException("Reservation not found", 404);
        await LockSessionAsync(sessionId, ct);
        var reservation = await db.Reservations.SingleOrDefaultAsync(x => x.Id == reservationId, ct) ?? throw new ApiException("Reservation not found", 404);
        if (reservation.Status != "Valid") throw new ApiException("Reservation cannot be checked in", 409);
        reservation.Status = "CheckedIn";
        reservation.CheckedInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<ActivitySession> LockSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var activityId = await db.ActivitySessions.AsNoTracking().Where(x => x.Id == sessionId).Select(x => (Guid?)x.ActivityId).SingleOrDefaultAsync(ct) ?? throw new ApiException("Session not found", 404);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Activities\" SET \"Status\" = \"Status\" WHERE \"Id\" = {activityId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivitySessions\" SET \"ReservedCount\" = \"ReservedCount\" WHERE \"Id\" = {sessionId}", ct);
        return await db.ActivitySessions.Include(x => x.Activity).SingleOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new ApiException("Session not found", 404);
    }
}

[ApiController]
[Route("api/services")]
[Authorize(Policy = Policies.Manager)]
public sealed class ServicesController(ParkDbContext db, ReservationService reservations, OverviewCacheService cache, AuditService audit) : ControllerBase
{
    [HttpGet("tags")]
    public async Task<PageResult<Tag>> Tags(int page = 1, int pageSize = 20, CancellationToken ct = default) => await db.Tags.AsNoTracking().OrderBy(x => x.Name).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Administrator), HttpPost("tags")]
    public async Task<IActionResult> CreateTag(TagRequest request, CancellationToken ct) { var tag = new Tag { Name = request.Name.Trim(), Color = request.Color, IsPublic = request.IsPublic }; db.Tags.Add(tag); await db.SaveChangesAsync(ct); await audit.WriteAsync("Create", "Tag", tag.Id, null, ct); return Created($"/api/services/tags/{tag.Id}", tag); }
    [Authorize(Policy = Policies.Administrator), HttpPut("tags/{id:guid}")]
    public async Task<Tag> UpdateTag(Guid id, TagRequest request, CancellationToken ct) { var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Tag not found", 404); tag.Name = request.Name.Trim(); tag.Color = request.Color; tag.IsPublic = request.IsPublic; await db.SaveChangesAsync(ct); await audit.WriteAsync("Update", "Tag", id, null, ct); return tag; }
    [Authorize(Policy = Policies.Administrator), HttpDelete("tags/{id:guid}")]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken ct) { var tag = await db.Tags.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Tag not found", 404); db.Tags.Remove(tag); await db.SaveChangesAsync(ct); await audit.WriteAsync("Delete", "Tag", id, null, ct); return NoContent(); }

    [HttpGet("announcements")]
    public async Task<PageResult<Announcement>> Announcements(int page = 1, int pageSize = 20, CancellationToken ct = default) => await db.Announcements.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToPageAsync(page, pageSize, ct);
    [Authorize(Policy = Policies.Manager), HttpPost("announcements")]
    public async Task<IActionResult> CreateAnnouncement(AnnouncementRequest request, CancellationToken ct) { var item = ToAnnouncement(new Announcement(), request); db.Announcements.Add(item); await db.SaveChangesAsync(ct); await audit.WriteAsync("Create", "Announcement", item.Id, null, ct); return Created($"/api/services/announcements/{item.Id}", item); }
    [Authorize(Policy = Policies.Manager), HttpPut("announcements/{id:guid}")]
    public async Task<Announcement> UpdateAnnouncement(Guid id, AnnouncementRequest request, CancellationToken ct) { var item = await db.Announcements.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Announcement not found", 404); ToAnnouncement(item, request); await db.SaveChangesAsync(ct); await audit.WriteAsync("Update", "Announcement", id, null, ct); return item; }
    [Authorize(Policy = Policies.Manager), HttpPost("announcements/{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct) { var item = await db.Announcements.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Announcement not found", 404); item.Status = "Published"; item.PublishedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); await audit.WriteAsync("Publish", "Announcement", id, null, ct); return NoContent(); }
    [Authorize(Policy = Policies.Manager), HttpPost("announcements/{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct) { var item = await db.Announcements.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Announcement not found", 404); item.Status = "Withdrawn"; await db.SaveChangesAsync(ct); await audit.WriteAsync("Withdraw", "Announcement", id, null, ct); return NoContent(); }

    [HttpGet("activities")]
    public async Task<PageResult<object>> Activities(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var q = db.Activities.AsNoTracking().Include(x => x.Sessions).OrderByDescending(x => x.CreatedAt).AsQueryable(); var p = await q.ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Select(ToActivity).Cast<object>().ToList(), p.Total, p.Page, p.PageSize);
    }
    [Authorize(Policy = Policies.Manager), HttpPost("activities")]
    public async Task<IActionResult> CreateActivity(ActivityRequest request, CancellationToken ct) { var item = new Activity { Title = request.Title.Trim(), Description = request.Description ?? "", Location = request.Location ?? "", Status = request.Status ?? "Draft" }; db.Activities.Add(item); await db.SaveChangesAsync(ct); await audit.WriteAsync("Create", "Activity", item.Id, null, ct); return Created($"/api/services/activities/{item.Id}", ToActivity(item)); }
    [Authorize(Policy = Policies.Manager), HttpPut("activities/{id:guid}")]
    public async Task<object> UpdateActivity(Guid id, ActivityRequest request, CancellationToken ct)
    {
        if (request.Status == "Cancelled") throw new ApiException("Validation failed", 400, "Use the cancellation endpoint to cancel an activity.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var item = await LockActivityAsync(id, ct);
        if (item.Status == "Cancelled") throw new ApiException("Activity unavailable", 409, "Cancelled activities cannot be changed.");
        item.Title = request.Title.Trim(); item.Description = request.Description ?? ""; item.Location = request.Location ?? ""; item.Status = request.Status ?? item.Status;
        await db.Entry(item).Collection(x => x.Sessions).LoadAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Update", "Activity", id, null, ct);
        return ToActivity(item);
    }
    [Authorize(Policy = Policies.Manager), HttpPost("activities/{id:guid}/cancel")]
    public async Task<IActionResult> CancelActivity(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var item = await LockActivityAsync(id, ct);
        var sessionIds = await db.ActivitySessions.AsNoTracking().Where(x => x.ActivityId == id).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        foreach (var sessionId in sessionIds)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivitySessions\" SET \"ReservedCount\" = \"ReservedCount\" WHERE \"Id\" = {sessionId}", ct);
        var sessions = await db.ActivitySessions.Include(x => x.Reservations).Where(x => x.ActivityId == id).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        item.Status = "Cancelled";
        foreach (var session in sessions)
        {
            session.Status = "Cancelled";
            session.ReservedCount = 0;
            foreach (var reservation in session.Reservations.Where(x => x.Status == "Valid"))
            {
                reservation.Status = "Cancelled";
                reservation.CancelledAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        await audit.WriteAsync("Cancel", "Activity", id, null, ct);
        return NoContent();
    }
    [Authorize(Policy = Policies.Manager), HttpPost("activities/{id:guid}/sessions")]
    public async Task<IActionResult> AddSession(Guid id, SessionRequest request, CancellationToken ct)
    {
        if (request.EndsAt <= request.StartsAt || request.Capacity < 1 || request.Status == "Cancelled") throw new ApiException("Validation failed", 400);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var activity = await LockActivityAsync(id, ct);
        if (activity.Status == "Cancelled") throw new ApiException("Activity unavailable", 409, "Cancelled activities cannot have sessions.");
        var session = new ActivitySession { ActivityId = id, StartsAt = request.StartsAt, EndsAt = request.EndsAt, Capacity = request.Capacity, Status = request.Status ?? "Published" };
        db.ActivitySessions.Add(session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return Created($"/api/services/sessions/{session.Id}", ToSession(session));
    }
    [Authorize(Policy = Policies.Manager), HttpPut("sessions/{id:guid}")]
    public async Task<object> UpdateSession(Guid id, SessionRequest request, CancellationToken ct)
    {
        if (request.EndsAt <= request.StartsAt || request.Capacity < 1 || request.Status == "Cancelled") throw new ApiException("Validation failed", 400);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var session = await LockSessionAsync(id, ct);
        if (session.Activity?.Status == "Cancelled" || session.Status == "Cancelled") throw new ApiException("Activity unavailable", 409);
        if (request.Capacity < session.ReservedCount) throw new ApiException("Validation failed", 400, "Capacity cannot be below valid reservations.");
        session.StartsAt = request.StartsAt; session.EndsAt = request.EndsAt; session.Capacity = request.Capacity; session.Status = request.Status ?? session.Status;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return ToSession(session);
    }
    [Authorize(Policy = Policies.Manager), HttpPost("sessions/{id:guid}/cancel")]
    public async Task<IActionResult> CancelSession(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var session = await LockSessionAsync(id, ct);
        await db.Entry(session).Collection(x => x.Reservations).LoadAsync(ct);
        var now = DateTimeOffset.UtcNow;
        session.Status = "Cancelled";
        session.ReservedCount = 0;
        foreach (var reservation in session.Reservations.Where(x => x.Status == "Valid"))
        {
            reservation.Status = "Cancelled";
            reservation.CancelledAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await cache.InvalidateAsync(ct);
        return NoContent();
    }

    [HttpGet("reservations")]
    public async Task<PageResult<object>> Reservations(int page = 1, int pageSize = 20, CancellationToken ct = default) { var p = await db.Reservations.AsNoTracking().Include(x => x.Session).ThenInclude(x => x!.Activity).OrderByDescending(x => x.CreatedAt).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Select(ToReservation).Cast<object>().ToList(), p.Total, p.Page, p.PageSize); }
    [HttpPost("reservations/{id:guid}/check-in")]
    public async Task<IActionResult> CheckIn(Guid id, CancellationToken ct) { await reservations.CheckInAsync(id, ct); await audit.WriteAsync("CheckIn", "Reservation", id, null, ct); return NoContent(); }
    [HttpPost("reservations/check-in")]
    public async Task<IActionResult> CheckInByCode(CheckInRequest request, CancellationToken ct) { var r = await db.Reservations.SingleOrDefaultAsync(x => x.Code == request.Code, ct) ?? throw new ApiException("Reservation not found", 404); await reservations.CheckInAsync(r.Id, ct); return NoContent(); }

    private async Task<Activity> LockActivityAsync(Guid activityId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Activities\" SET \"Status\" = \"Status\" WHERE \"Id\" = {activityId}", ct);
        return await db.Activities.SingleOrDefaultAsync(x => x.Id == activityId, ct) ?? throw new ApiException("Activity not found", 404);
    }

    private async Task<ActivitySession> LockSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var activityId = await db.ActivitySessions.AsNoTracking().Where(x => x.Id == sessionId).Select(x => (Guid?)x.ActivityId).SingleOrDefaultAsync(ct) ?? throw new ApiException("Session not found", 404);
        await LockActivityAsync(activityId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivitySessions\" SET \"ReservedCount\" = \"ReservedCount\" WHERE \"Id\" = {sessionId}", ct);
        return await db.ActivitySessions.Include(x => x.Activity).SingleOrDefaultAsync(x => x.Id == sessionId, ct) ?? throw new ApiException("Session not found", 404);
    }

    private static Announcement ToAnnouncement(Announcement x, AnnouncementRequest r) { x.Title = r.Title.Trim(); x.Body = r.Body.Trim(); x.StartsAt = r.StartsAt; x.EndsAt = r.EndsAt; x.EventId = r.EventId; return x; }
    internal static object ToSession(ActivitySession s) => new { s.Id, s.ActivityId, s.StartsAt, s.EndsAt, s.Capacity, reserved = s.ReservedCount, s.ReservedCount, s.Status };
    internal static object ToActivity(Activity x) => new { x.Id, x.Title, x.Description, x.Location, x.Status, sessions = x.Sessions.OrderBy(s => s.StartsAt).Select(ToSession) };
    internal static object ToReservation(Reservation x) => new { x.Id, x.SessionId, status = x.Status == "Valid" ? "Reserved" : x.Status, actualStatus = x.Status, reserveCode = x.Code, x.Code, x.CheckedInAt, x.CancelledAt, createdAt = x.CreatedAt, session = x.Session is null ? null : new { x.Session.ActivityId, title = x.Session.Activity?.Title, x.Session.StartsAt, x.Session.EndsAt } };
}

[ApiController]
[Route("api/public")]
public sealed class PublicServicesController(ParkDbContext db, ReservationService reservations) : ControllerBase
{
    [AllowAnonymous, HttpGet("tags")]
    public async Task<IReadOnlyList<Tag>> Tags(CancellationToken ct) => await db.Tags.AsNoTracking().Where(x => x.IsPublic).OrderBy(x => x.Name).ToListAsync(ct);
    [AllowAnonymous, HttpGet("announcements")]
    public async Task<IReadOnlyList<object>> Announcements(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await db.Announcements.AsNoTracking()
            .Where(x => x.Status == "Published" && (x.StartsAt == null || x.StartsAt <= now) && (x.EndsAt == null || x.EndsAt >= now))
            .OrderByDescending(x => x.PublishedAt)
            .Select(x => new { x.Title, x.Body, x.StartsAt, x.EndsAt, x.PublishedAt })
            .ToListAsync(ct);
        return items.Cast<object>().ToList();
    }
    [AllowAnonymous, HttpGet("activities")]
    public async Task<IReadOnlyList<object>> Activities(CancellationToken ct) => (await db.Activities.AsNoTracking().Include(x => x.Sessions).Where(x => x.Status == "Published").OrderBy(x => x.CreatedAt).ToListAsync(ct)).Select(ServicesController.ToActivity).ToList();
    [Authorize(Roles = ParkRoles.Visitor), HttpPost("sessions/{id:guid}/reserve")]
    public async Task<IActionResult> Reserve(Guid id, CancellationToken ct) { var r = await reservations.ReserveAsync(User.Id(), id, ct); return Created($"/api/public/reservations/{r.Id}", ServicesController.ToReservation(r)); }
    [Authorize(Roles = ParkRoles.Visitor), HttpGet("reservations")]
    public async Task<PageResult<object>> MyReservations(int page = 1, int pageSize = 20, CancellationToken ct = default) { var p = await db.Reservations.AsNoTracking().Include(x => x.Session).ThenInclude(x => x!.Activity).Where(x => x.VisitorId == User.Id()).OrderByDescending(x => x.CreatedAt).ToPageAsync(page, pageSize, ct); return new PageResult<object>(p.Items.Select(ServicesController.ToReservation).ToList(), p.Total, p.Page, p.PageSize); }
    [Authorize(Roles = ParkRoles.Visitor), HttpPost("reservations/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) { await reservations.CancelAsync(User.Id(), id, false, ct); return NoContent(); }
    [AllowAnonymous, HttpGet("eco")]
    public async Task<object> Eco(CancellationToken ct) => await EcoIndexService.GetAsync(db, ct);
    [AllowAnonymous, HttpGet("toilets")]
    public async Task<IReadOnlyList<object>> Toilets(CancellationToken ct)
    {
        var toilets = await db.Devices.AsNoTracking().Include(x => x.Asset).Where(x => x.Type == "Toilet").ToListAsync(ct); var samples = await db.TelemetrySamples.AsNoTracking().Where(x => x.MetricCode == "occupancy" && toilets.Select(d => d.Id).Contains(x.DeviceId)).OrderByDescending(x => x.CollectedAt).ToListAsync(ct);
        return toilets.Select(d => { var latest = samples.FirstOrDefault(s => s.DeviceId == d.Id); var stale = latest is null || latest.CollectedAt < DateTimeOffset.UtcNow.AddMinutes(-10); return (object)new { d.AssetId, name = d.Asset?.Name, d.Asset?.Longitude, d.Asset?.Latitude, occupancy = stale ? (decimal?)null : latest!.Value, unit = latest?.Unit, collectedAt = latest?.CollectedAt, stale, openHours = "06:00-22:00" }; }).ToList();
    }
}

public sealed record TagRequest(string Name, string? Color, bool IsPublic = true);
public sealed record AnnouncementRequest(string Title, string Body, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, Guid? EventId);
public sealed record ActivityRequest(string Title, string? Description, string? Location, string? Status);
public sealed record SessionRequest(DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Capacity, string? Status);
public sealed record CheckInRequest(string Code);
