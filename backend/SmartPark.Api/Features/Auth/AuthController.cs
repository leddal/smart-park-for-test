using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;
using SmartPark.Api.Data;

namespace SmartPark.Api.Features.Auth;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(UserManager<AppUser> users, SignInManager<AppUser> signIn, ParkDbContext db, IAntiforgery antiforgery, AuditService audit) : ControllerBase
{
    [AllowAnonymous, HttpGet("csrf")]
    public object Csrf() => new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken };

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByNameAsync(request.UserName);
        if (user is null || user.Disabled) throw new ApiException("Invalid credentials", StatusCodes.Status401Unauthorized);
        var result = await signIn.PasswordSignInAsync(user, request.Password, true, lockoutOnFailure: true);
        if (!result.Succeeded) throw new ApiException(result.IsLockedOut ? "Account locked" : "Invalid credentials", StatusCodes.Status401Unauthorized);
        await audit.WriteAsync("Login", "User", user.Id, null, ct);
        return Ok(await ToMeAsync(user));
    }

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.DisplayName)) throw new ApiException("Validation failed", 400, "User name, password and display name are required.");
        var user = new AppUser { Id = Guid.NewGuid(), UserName = request.UserName.Trim(), DisplayName = request.DisplayName.Trim(), EmailConfirmed = true, LockoutEnabled = true };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded) throw new ApiException("Registration failed", 400, string.Join("; ", result.Errors.Select(x => x.Description)));
        await users.AddToRoleAsync(user, ParkRoles.Visitor);
        await signIn.SignInAsync(user, true);
        await audit.WriteAsync("Register", "User", user.Id, new { user.UserName }, ct);
        return Created("/api/auth/me", await ToMeAsync(user));
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var id = User.Id(); await signIn.SignOutAsync(); await audit.WriteAsync("Logout", "User", id, null, ct); return NoContent();
    }

    [Authorize, HttpGet("me")]
    public async Task<object> Me()
    {
        var user = await users.GetUserAsync(User) ?? throw new ApiException("Unauthenticated", 401);
        return await ToMeAsync(user);
    }

    [Authorize(Policy = Policies.Administrator), HttpGet("users")]
    public async Task<PageResult<object>> ListUsers(int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().OrderBy(x => x.UserName).Select(x => new UserListRow(x.Id, x.UserName!, x.DisplayName, x.Disabled, x.CreatedAt));
        var basePage = await query.ToPageAsync(page, pageSize, ct);
        var rows = new List<object>();
        foreach (var row in basePage.Items) rows.Add(new { row.Id, row.UserName, row.DisplayName, row.Disabled, row.CreatedAt, roles = await RolesAsync(row.Id) });
        return new PageResult<object>(rows, basePage.Total, basePage.Page, basePage.PageSize);
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("users")]
    public async Task<IActionResult> CreateUser(CreateUserRequest request, CancellationToken ct)
    {
        var requestedRoles = (request.Roles is { Count: > 0 } ? request.Roles : [ParkRoles.Worker]).Distinct(StringComparer.Ordinal).ToArray();
        if (requestedRoles.Any(x => x is not (ParkRoles.Administrator or ParkRoles.Dispatcher or ParkRoles.Worker or ParkRoles.Visitor))) throw new ApiException("Validation failed", 400, "A requested role is invalid.");
        var user = new AppUser { Id = Guid.NewGuid(), UserName = request.UserName.Trim(), DisplayName = request.DisplayName.Trim(), EmailConfirmed = true, LockoutEnabled = true };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded) throw new ApiException("Create user failed", 400, string.Join("; ", result.Errors.Select(x => x.Description)));
        result = await users.AddToRolesAsync(user, requestedRoles);
        if (!result.Succeeded) throw new ApiException("Role assignment failed", 400, string.Join("; ", result.Errors.Select(x => x.Description)));
        await audit.WriteAsync("Create", "User", user.Id, new { user.UserName, requestedRoles }, ct);
        return Created($"/api/auth/users/{user.Id}", new { user.Id, user.UserName, user.DisplayName, roles = requestedRoles });
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("users/{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new ApiException("User not found", 404);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, request.Password);
        if (!result.Succeeded) throw new ApiException("Password reset failed", 400, string.Join("; ", result.Errors.Select(x => x.Description)));
        await audit.WriteAsync("ResetPassword", "User", id, null, ct);
        return NoContent();
    }

    [Authorize(Policy = Policies.Administrator), HttpPost("users/{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, DisableUserRequest request, CancellationToken ct)
    {
        if (id == User.Id()) throw new ApiException("Validation failed", 400, "Administrators cannot disable their own account.");
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new ApiException("User not found", 404);
        user.Disabled = request.Disabled;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw new ApiException("User update failed", 400, string.Join("; ", result.Errors.Select(x => x.Description)));
        await users.UpdateSecurityStampAsync(user);
        await audit.WriteAsync(request.Disabled ? "Disable" : "Enable", "User", id, null, ct);
        return NoContent();
    }

    [Authorize(Policy = Policies.Manager), HttpGet("workers")]
    public async Task<PageResult<object>> Workers(int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var workerRole = await db.Roles.SingleAsync(x => x.Name == ParkRoles.Worker, ct);
        var query = from user in db.Users.AsNoTracking() join mapping in db.UserRoles on user.Id equals mapping.UserId where mapping.RoleId == workerRole.Id && !user.Disabled orderby user.DisplayName select new { user.Id, user.UserName, user.DisplayName };
        var result = await query.ToPageAsync(page, pageSize, ct);
        return new PageResult<object>(result.Items.Cast<object>().ToList(), result.Total, result.Page, result.PageSize);
    }

    private async Task<object> ToMeAsync(AppUser user) => new { id = user.Id, userName = user.UserName, displayName = user.DisplayName, roles = await users.GetRolesAsync(user) };
    private async Task<IList<string>> RolesAsync(Guid id) => await users.GetRolesAsync((await users.FindByIdAsync(id.ToString()))!);
    private sealed record UserListRow(Guid Id, string UserName, string DisplayName, bool Disabled, DateTimeOffset CreatedAt);
}

public sealed record LoginRequest(string UserName, string Password);
public sealed record RegisterRequest(string UserName, string Password, string DisplayName);
public sealed record CreateUserRequest(string UserName, string Password, string DisplayName, List<string>? Roles);
public sealed record ResetPasswordRequest(string Password);
public sealed record DisableUserRequest(bool Disabled = true);
