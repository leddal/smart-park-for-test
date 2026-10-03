using System.Net;
using System.Text.Json;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class RedisFallbackIntegrationTests
{
    [Fact]
    public async Task Dead_dedicated_redis_connection_falls_back_to_database_without_interrupting_api()
    {
        var offlineRedis = new SmartParkWebApplicationFactory("127.0.0.1:1,abortConnect=false,connectTimeout=100,syncTimeout=100");
        try
        {
            await offlineRedis.InitializeAsync();
            using var dispatcher = await offlineRedis.CreateAuthenticatedClientAsync("dispatcher");
            using var summary = await dispatcher.GetAsync("/api/overview/summary");
            summary.EnsureSuccessStatusCode();
            using var snapshot = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
            Assert.Equal("MemoryFallback", snapshot.RootElement.GetProperty("cacheStatus").GetString());

            using var ready = await dispatcher.GetAsync("/api/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using var readiness = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
            Assert.Equal("degraded", readiness.RootElement.GetProperty("cache").GetString());
        }
        finally
        {
            await offlineRedis.DisposeAsync();
        }
    }
}
