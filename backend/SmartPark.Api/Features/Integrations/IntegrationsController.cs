using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;

namespace SmartPark.Api.Features.Integrations;

[ApiController]
[Route("api/integrations")]
[Authorize(Policy = Policies.Manager)]
public sealed class IntegrationsController(ParkDbContext db, AuditService audit) : ControllerBase
{
    [HttpGet("platforms")]
    public async Task<IReadOnlyList<object>> Platforms(CancellationToken ct) => await db.IntegrationPlatforms.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Enabled, x.ForceFailure }).Cast<object>().ToListAsync(ct);
    [Authorize(Policy = Policies.Administrator), HttpPut("platforms/{id:guid}")]
    public async Task<object> Update(Guid id, PlatformRequest request, CancellationToken ct) { var item = await db.IntegrationPlatforms.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Platform not found", 404); item.Enabled = request.Enabled; item.ForceFailure = request.ForceFailure; await db.SaveChangesAsync(ct); await audit.WriteAsync("Update", "IntegrationPlatform", id, new { item.Enabled, item.ForceFailure }, ct); return new { item.Id, item.Name, item.Enabled, item.ForceFailure }; }
    [HttpGet("messages")]
    public async Task<PageResult<object>> Messages(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var p = await db.OutboxMessages.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToPageAsync(page, pageSize, ct); var ids = p.Items.Select(x => x.Id).ToArray(); var attempts = await db.IntegrationAttempts.AsNoTracking().Where(x => ids.Contains(x.OutboxMessageId)).OrderBy(x => x.Attempt).ToListAsync(ct); var platformIds = p.Items.Where(x => x.PlatformId is not null).Select(x => x.PlatformId!.Value).Distinct().ToArray(); var platformNames = await db.IntegrationPlatforms.AsNoTracking().Where(x => platformIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return new PageResult<object>(p.Items.Select(x => (object)new { x.Id, x.Kind, x.PlatformId, platformName = x.PlatformId is null ? "通用事件模拟" : platformNames.GetValueOrDefault(x.PlatformId.Value, "平台已不存在"), x.Status, x.Attempts, x.NextAttemptAt, x.LastError, x.CreatedAt, detail = attempts.Where(a => a.OutboxMessageId == x.Id).OrderByDescending(a => a.AttemptedAt).Select(a => a.Message).FirstOrDefault(), attemptLogs = attempts.Where(a => a.OutboxMessageId == x.Id).Select(a => new { a.Attempt, a.AttemptedAt, a.Success, a.Message }) }).ToList(), p.Total, p.Page, p.PageSize);
    }
    [Authorize(Policy = Policies.Manager), HttpPost("sync")]
    public async Task<IActionResult> Sync(SyncRequest request, CancellationToken ct)
    {
        if (request.Kind is not ("Assets" or "VisitorCount" or "VideoMetadata" or "PublicCodes")) throw new ApiException("Validation failed", 400, "Unsupported simulation sync kind.");
        var platform = await db.IntegrationPlatforms.SingleOrDefaultAsync(x => x.Id == request.PlatformId, ct) ?? throw new ApiException("Platform not found", 404);
        if (!platform.Enabled) throw new ApiException("Platform disabled", 409);
        var message = new OutboxMessage { PlatformId = platform.Id, Kind = request.Kind, PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { request.Kind, createdBy = User.Id() }), Status = "Pending" }; db.OutboxMessages.Add(message); await db.SaveChangesAsync(ct); await audit.WriteAsync("QueueSync", "OutboxMessage", message.Id, new { platform.Name, request.Kind }, ct); return Accepted($"/api/integrations/messages/{message.Id}", new { message.Id, message.Status, message.Kind, message.PlatformId });
    }
    [Authorize(Policy = Policies.Manager), HttpPost("messages/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) { var message = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ApiException("Message not found", 404); message.Status = "Pending"; message.Attempts = 0; message.LastError = null; message.NextAttemptAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); await audit.WriteAsync("Retry", "OutboxMessage", id, null, ct); return Accepted($"/api/integrations/messages/{id}", new { message.Id, message.Status }); }
}
public sealed record PlatformRequest(bool Enabled, bool ForceFailure);
public sealed record SyncRequest(Guid PlatformId, string Kind);
