using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SmartPark.Api.Data;

namespace SmartPark.Tests;

/// <summary>Uses only the isolated verification database selected by the test environment.</summary>
public sealed class SmartParkWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _parkDbConnection;
    private readonly string _redisConnection;

    public SmartParkWebApplicationFactory() : this(null) { }

    internal SmartParkWebApplicationFactory(string? redisOverride)
    {
        (_parkDbConnection, _redisConnection) = TestEnvironment.RequireIsolatedServices();
        _redisConnection = redisOverride ?? _redisConnection;
    }

    public SeedSnapshot SeedSnapshot { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ParkDb"] = _parkDbConnection,
                ["ConnectionStrings:Redis"] = _redisConnection,
                ["Storage:Root"] = "/tmp/smartpark-test-files",
                ["Storage:KeysPath"] = "/tmp/smartpark-test-keys",
                ["Demo:Password"] = "ParkDemo!2026",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestClientAddressFilter>();
            // Hosted queues/simulators are removed only from this test host. Production startup has no test switch.
            foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService)).ToArray())
                services.Remove(registration);
        });
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory("/tmp/smartpark-test-files");
        Directory.CreateDirectory("/tmp/smartpark-test-keys");

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length == 0)
            throw new InvalidOperationException("Integration tests require a real EF Core migration; EnsureCreated and EF InMemory are not permitted.");

        await db.Database.MigrateAsync();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
        await seeder.SeedAsync();
        var afterFirstSeed = await CaptureSeedSnapshotAsync(db);
        await seeder.SeedAsync();
        SeedSnapshot = await CaptureSeedSnapshotAsync(db);
        if (SeedSnapshot != afterFirstSeed)
            throw new InvalidOperationException("Demo seeding is not idempotent: a second invocation changed persisted counts.");
    }

    private static async Task<SeedSnapshot> CaptureSeedSnapshotAsync(ParkDbContext db) => new(
        await db.Parks.CountAsync(),
        await db.Users.CountAsync(),
        await db.Roles.CountAsync(),
        await db.Devices.CountAsync(),
        await db.Activities.CountAsync(),
        await db.SeedVersions.CountAsync());

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    private static int clientNumber;

    public HttpClient CreateCookieClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var number = Interlocked.Increment(ref clientNumber);
        client.DefaultRequestHeaders.Add("X-Verification-IP", $"10.127.{(number >> 8) & 255}.{number & 255}");
        return client;
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string userName, string password = "ParkDemo!2026")
    {
        var client = CreateCookieClient();
        var csrf = await GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { userName, password }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return client;
    }

    public static async Task<string> GetCsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("The CSRF endpoint returned no token.");
    }

    public static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string uri, object? body, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (csrf && method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
            request.Headers.Add("X-CSRF-TOKEN", await GetCsrfAsync(client));
        return await client.SendAsync(request);
    }

    public async Task<T> InDatabaseAsync<T>(Func<ParkDbContext, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ParkDbContext>());
    }

    public async Task InDatabaseAsync(Func<ParkDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ParkDbContext>());
    }
}

// TestServer clients have no remote address by default. Model distinct visitors without disabling rate limiting.
internal sealed class TestClientAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["X-Verification-IP"].ToString(), out var address))
                context.Connection.RemoteIpAddress = address;
            await pipeline(context);
        });
        next(app);
    };
}

public readonly record struct SeedSnapshot(int Parks, int Users, int Roles, int Devices, int Activities, int SeedVersions);

public static class TestEnvironment
{
    public static (string ParkDb, string Redis) RequireIsolatedServices(
        string? parkDb = null,
        string? redis = null,
        string? allowed = null)
    {
        if (!bool.TryParse(allowed ?? Environment.GetEnvironmentVariable("TEST_ALLOWED"), out var testAllowed) || !testAllowed)
            throw new InvalidOperationException("Refusing integration tests: set TEST_ALLOWED=true explicitly.");

        var connectionString = parkDb ?? Environment.GetEnvironmentVariable("ConnectionStrings__ParkDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Refusing integration tests: ConnectionStrings__ParkDb is required.");

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (!string.Equals(database, "smartpark_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing integration tests: ConnectionStrings__ParkDb must target database smartpark_test.");

        var redisConnection = redis ?? Environment.GetEnvironmentVariable("ConnectionStrings__Redis");
        if (string.IsNullOrWhiteSpace(redisConnection))
            throw new InvalidOperationException("Refusing integration tests: ConnectionStrings__Redis is required for real Redis integration coverage.");

        return (connectionString, redisConnection);
    }
}
