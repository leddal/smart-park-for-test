using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartPark.Api.Common;

public static class ParkRoles
{
    public const string Administrator = "Administrator";
    public const string Dispatcher = "Dispatcher";
    public const string Worker = "Worker";
    public const string Visitor = "Visitor";
    public const string Internal = Administrator + "," + Dispatcher + "," + Worker;
    public const string Managers = Administrator + "," + Dispatcher;
}

public static class Policies
{
    public const string Internal = "internal";
    public const string Manager = "manager";
    public const string Administrator = "administrator";
}

public sealed class ApiException(string title, int statusCode, string? detail = null) : Exception(detail ?? title)
{
    public string Title { get; } = title;
    public int StatusCode { get; } = statusCode;
}

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail, unexpected) = exception switch
        {
            ApiException api => (api.StatusCode, api.Title, api.Message, false),
            BadHttpRequestException bad => (StatusCodes.Status400BadRequest, "Invalid request", bad.Message, false),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict", "The resource was modified by another request.", false),
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => (StatusCodes.Status409Conflict, "Conflict", "The resource already exists.", false),
            PostgresException { SqlState: "23505" } => (StatusCodes.Status409Conflict, "Conflict", "The resource already exists.", false),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected server error occurred.", true)
        };
        if (unexpected) logger.LogError(exception, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}

public static class CurrentUser
{
    public static Guid Id(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : throw new ApiException("Unauthenticated", StatusCodes.Status401Unauthorized);
    }

    public static bool IsManager(this ClaimsPrincipal user) => user.IsInRole(ParkRoles.Administrator) || user.IsInRole(ParkRoles.Dispatcher);
    public static bool IsInternal(this ClaimsPrincipal user) => user.IsManager() || user.IsInRole(ParkRoles.Worker);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public static (int Page, int Size) Normalize(int page, int pageSize) => (Math.Max(1, page), Math.Clamp(pageSize == 0 ? 20 : pageSize, 1, 100));

    public static async Task<PageResult<T>> ToPageAsync<T>(this IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        var (current, size) = Normalize(page, pageSize);
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query.Skip((current - 1) * size).Take(size), ct);
        return new PageResult<T>(items, total, current, size);
    }
}

public sealed class AuditService(Data.ParkDbContext db, IHttpContextAccessor accessor)
{
    public async Task WriteAsync(string action, string entityType, Guid? entityId, object? data, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new Data.AuditLog
        {
            Id = Guid.NewGuid(), Action = action, EntityType = entityType, EntityId = entityId,
            UserId = accessor.HttpContext?.User.Identity?.IsAuthenticated == true ? accessor.HttpContext.User.Id() : null,
            OccurredAt = DateTimeOffset.UtcNow,
            DataJson = data is null ? null : System.Text.Json.JsonSerializer.Serialize(data)
        });
        await db.SaveChangesAsync(ct);
    }
}
