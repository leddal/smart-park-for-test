using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class AuthenticationAndAuthorizationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Internal_asset_endpoint_rejects_anonymous_requests_with_401()
    {
        using var client = factory.CreateCookieClient();
        using var response = await client.GetAsync("/api/assets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Visitor_cannot_elevate_role_at_registration_or_read_internal_assets()
    {
        using var client = factory.CreateCookieClient();
        var csrf = await SmartParkWebApplicationFactory.GetCsrfAsync(client);
        var userName = $"visitor-{Guid.NewGuid():N}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                userName,
                password = "Visitor!2026",
                displayName = "权限测试游客",
                roles = new[] { "Administrator", "Dispatcher" },
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var registration = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        using var account = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var roles = account.RootElement.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "Visitor" }, roles);

        using var assets = await client.GetAsync("/api/assets");
        Assert.Equal(HttpStatusCode.Forbidden, assets.StatusCode);
    }

    [Fact]
    public async Task Authenticated_mutation_without_csrf_token_returns_400()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets")
        {
            Content = JsonContent.Create(new
            {
                code = $"CSRF-{Guid.NewGuid():N}",
                name = "CSRF guard asset",
                category = "Facility",
                publicCode = $"P-CSRF-{Guid.NewGuid():N}",
                version = 0,
            }),
        };
        using var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dispatcher_is_forbidden_from_administrator_only_asset_creation()
    {
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, "/api/assets", new
        {
            code = $"DISPATCHER-{Guid.NewGuid():N}",
            name = "Dispatcher must not create assets",
            category = "Facility",
            publicCode = $"P-DISPATCHER-{Guid.NewGuid():N}",
            version = 0,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/control/devices")]
    [InlineData("/api/control/commands")]
    [InlineData("/api/emergency/events")]
    [InlineData("/api/emergency/statistics")]
    [InlineData("/api/integrations/platforms")]
    [InlineData("/api/integrations/messages")]
    [InlineData("/api/auth/workers")]
    [InlineData("/api/operations/flood-plans")]
    [InlineData("/api/overview/summary")]
    public async Task Management_ledger_endpoints_are_limited_to_manager_roles(string path)
    {
        using var worker = await factory.CreateAuthenticatedClientAsync("worker");
        using var visitor = await factory.CreateAuthenticatedClientAsync("visitor");
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");

        using var workerResponse = await worker.GetAsync(path);
        using var visitorResponse = await visitor.GetAsync(path);
        using var dispatcherResponse = await dispatcher.GetAsync(path);
        using var adminResponse = await admin.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, workerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, visitorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dispatcherResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task Emergency_event_detail_rejects_non_manager_roles_before_lookup()
    {
        var path = $"/api/emergency/events/{Guid.NewGuid():D}";
        using var worker = await factory.CreateAuthenticatedClientAsync("worker");
        using var visitor = await factory.CreateAuthenticatedClientAsync("visitor");

        using var workerResponse = await worker.GetAsync(path);
        using var visitorResponse = await visitor.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, workerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, visitorResponse.StatusCode);
    }
}
