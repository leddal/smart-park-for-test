using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Overview;
using SmartPark.Api.Features.Services;

namespace SmartPark.Benchmarks;

public sealed class BenchmarkServiceHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private BenchmarkServiceHost(ServiceProvider provider) => _provider = provider;

    public IServiceProvider Services => _provider;

    public static async Task<BenchmarkServiceHost> CreateAsync(BenchmarkOptions options, DbCommandCounter commandCounter, CancellationToken ct)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ParkDb"] = options.ParkDbConnectionString,
                ["ConnectionStrings:Redis"] = options.RedisConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(commandCounter);
        services.AddHttpContextAccessor();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddDbContext<ParkDbContext>((_, builder) => builder.UseNpgsql(options.ParkDbConnectionString).AddInterceptors(commandCounter));
        services.AddIdentityCore<AppUser>(identity =>
            {
                identity.Password.RequiredLength = 12;
                identity.Password.RequireDigit = true;
                identity.Password.RequireLowercase = true;
                identity.Password.RequireUppercase = true;
                identity.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ParkDbContext>();
        services.AddStackExchangeRedisCache(redis => redis.Configuration = options.RedisConnectionString);
        services.AddMemoryCache();
        services.AddScoped<DemoSeeder>();
        services.AddScoped<AuditService>();
        services.AddScoped<OverviewQueryService>();
        services.AddScoped<OverviewCacheService>();
        services.AddScoped<ReservationService>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        var host = new BenchmarkServiceHost(provider);
        try
        {
            await host.ApplyMigrationsAndSeedAsync(ct);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task ApplyMigrationsAndSeedAsync(CancellationToken ct)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        await db.Database.MigrateAsync(ct);
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(ct);
        // Only previous synthetic paging rows use all three benchmark-owned markers.
        await db.Assets
            .Where(asset => asset.Category.StartsWith("BenchmarkPaging-")
                && asset.Code.StartsWith("BENCH-PAGE-")
                && asset.PublicCode.StartsWith("BENCH-PUBLIC-"))
            .ExecuteDeleteAsync(ct);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
