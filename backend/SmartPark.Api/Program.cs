using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Background;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Emergency;
using SmartPark.Api.Features.IoT;
using SmartPark.Api.Features.Overview;
using SmartPark.Api.Features.Services;
using SmartPark.Api.Storage;

var builder = WebApplication.CreateBuilder(args);
var keysPath = builder.Configuration["Storage:KeysPath"] ?? "/keys";
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("SmartPark");
builder.Services.AddDbContext<ParkDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("ParkDb"), npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.GetName().Name)));
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-CSRF-TOKEN"; options.Cookie.Name = "smartpark.xsrf"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(options => { options.Password.RequiredLength = 10; options.Password.RequireNonAlphanumeric = true; options.Lockout.MaxFailedAccessAttempts = 5; options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); options.User.RequireUniqueEmail = false; })
    .AddEntityFrameworkStores<ParkDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "smartpark.auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Lax; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = context => WriteAuthProblemAsync(context.Response, StatusCodes.Status401Unauthorized, "Unauthenticated"),
        OnRedirectToAccessDenied = context => WriteAuthProblemAsync(context.Response, StatusCodes.Status403Forbidden, "Forbidden"),
        OnValidatePrincipal = async context =>
        {
            await SecurityStampValidator.ValidatePrincipalAsync(context);
            if (context.Principal?.Identity?.IsAuthenticated != true) return;
            var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.GetUserAsync(context.Principal);
            if (user is null || user.Disabled)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            }
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Internal, p => p.RequireRole(ParkRoles.Administrator, ParkRoles.Dispatcher, ParkRoles.Worker));
    options.AddPolicy(Policies.Manager, p => p.RequireRole(ParkRoles.Administrator, ParkRoles.Dispatcher));
    options.AddPolicy(Policies.Administrator, p => p.RequireRole(ParkRoles.Administrator));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    }));
});
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute())).AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis"))) builder.Services.AddStackExchangeRedisCache(options => options.Configuration = builder.Configuration.GetConnectionString("Redis")); else builder.Services.AddDistributedMemoryCache();
builder.Services.AddScoped<AuditService>(); builder.Services.AddScoped<DemoSeeder>(); builder.Services.AddScoped<LocalFileStore>(); builder.Services.AddScoped<ReservationService>(); builder.Services.AddScoped<OverviewQueryService>(); builder.Services.AddScoped<OverviewCacheService>(); builder.Services.AddScoped<TelemetryIngestService>(); builder.Services.AddScoped<VisitorCountIngestService>(); builder.Services.AddScoped<EmergencyService>();
builder.Services.AddSingleton<SimulationGate>();
builder.Services.AddHostedService<SimulationHostedService>(); builder.Services.AddHostedService<CommandHostedService>(); builder.Services.AddHostedService<OutboxHostedService>();

var app = builder.Build();
if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>(); await db.Database.MigrateAsync();
    if (args.Contains("--seed", StringComparer.OrdinalIgnoreCase)) await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
    return;
}
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapOpenApi();
app.MapControllers();
app.Run();

static async Task WriteAuthProblemAsync(HttpResponse response, int status, string title)
{
    response.StatusCode = status; response.ContentType = "application/problem+json"; await response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = title == "Unauthenticated" ? "Authentication is required." : "You do not have permission for this resource." });
}

public partial class Program { }
