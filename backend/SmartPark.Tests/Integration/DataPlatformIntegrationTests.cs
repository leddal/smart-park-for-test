using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class DataPlatformIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task GeoJson_preview_reports_invalid_payload_without_confirming_layer()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var content = Import("GeoJSON", "Road", "invalid.geojson", "{not-json}");
        using var response = await SendMultipartAsync(admin, "/api/platform/imports/preview", content);

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.True(json.RootElement.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Csv_confirm_is_transactional_when_a_duplicate_code_is_detected()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        var code = $"CSV-DUP-{Guid.NewGuid():N}";
        var csv = $"code,name,category\n{code},first,Facility\n{code},second,Facility\n";
        using var previewContent = Import("CSV", "POI", "duplicate.csv", csv);
        using var preview = await SendMultipartAsync(admin, "/api/platform/imports/preview", previewContent);
        preview.EnsureSuccessStatusCode();
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var batchId = previewJson.RootElement.GetProperty("id").GetGuid();

        using var confirmation = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/platform/imports/{batchId}/confirm", null);
        Assert.Equal(HttpStatusCode.BadRequest, confirmation.StatusCode);

        var imported = await factory.InDatabaseAsync(db => db.Assets.AnyAsync(x => x.Code == code));
        Assert.False(imported);
    }

    [Fact]
    public async Task Valid_csv_confirm_persists_an_isolated_asset()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        var code = $"CSV-VALID-{Guid.NewGuid():N}";
        var csv = $"code,name,category,longitude,latitude,publicDescription\n{code},Valid import,Facility,121.4380,31.1900,Public import detail\n";
        using var previewContent = Import("CSV", "POI", "valid.csv", csv);
        using var preview = await SendMultipartAsync(admin, "/api/platform/imports/preview", previewContent);
        preview.EnsureSuccessStatusCode();
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean());
        var batchId = previewJson.RootElement.GetProperty("id").GetGuid();

        using var confirmation = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/platform/imports/{batchId}/confirm", null);
        confirmation.EnsureSuccessStatusCode();

        var asset = await factory.InDatabaseAsync(db => db.Assets.SingleAsync(x => x.Code == code));
        Assert.Equal("Facility", asset.Category);
        Assert.Equal("Active", asset.Status);
        Assert.Equal("Public import detail", asset.PublicDescription);
    }

    [Fact]
    public async Task Fake_png_dom_upload_is_rejected_by_server_side_file_signature_check()
    {
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var content = Import("DOM", "Boundary", "not-an-image.png", "<svg><script>alert(1)</script></svg>", "image/png", "{\"southWest\":{\"lng\":121.4,\"lat\":31.1},\"northEast\":{\"lng\":121.5,\"lat\":31.2}}");
        using var response = await SendMultipartAsync(admin, "/api/platform/imports/preview", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GeoJSON", "Road")]
    [InlineData("DOM", "Boundary")]
    public async Task Valid_map_import_confirms_persistently_and_is_available_through_public_map(string type, string layerKind)
    {
        var name = $"valid-map-{Guid.NewGuid():N}";
        using var admin = await factory.CreateAuthenticatedClientAsync("admin");
        using var content = new MultipartFormDataContent
        {
            { new StringContent(type), "type" },
            { new StringContent(layerKind), "layerKind" },
        };
        var payload = type == "DOM"
            ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j0ioAAAAASUVORK5CYII=")
            : Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"id\":\"test-road\",\"properties\":{\"layer\":\"Road\"},\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[121.438,31.19],[121.439,31.191]]}}]}");
        var upload = new ByteArrayContent(payload);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type == "DOM" ? "image/png" : "application/geo+json");
        content.Add(upload, "file", name + (type == "DOM" ? ".png" : ".geojson"));
        if (type == "DOM") content.Add(new StringContent("{\"southWest\":{\"lng\":121.4,\"lat\":31.1},\"northEast\":{\"lng\":121.5,\"lat\":31.2}}"), "bounds");
        using var preview = await SendMultipartAsync(admin, "/api/platform/imports/preview", content);
        preview.EnsureSuccessStatusCode();
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewJson.RootElement.GetProperty("valid").GetBoolean());
        var batchId = previewJson.RootElement.GetProperty("id").GetGuid();
        using var confirmation = await SmartParkWebApplicationFactory.SendJsonAsync(admin, HttpMethod.Post, $"/api/platform/imports/{batchId}/confirm", null);
        confirmation.EnsureSuccessStatusCode();
        var layer = await factory.InDatabaseAsync(db => db.MapLayers.AsNoTracking().SingleAsync(x => x.Name == name));
        Assert.True(layer.IsPublic);
        Assert.Equal(type == "DOM" ? "DOM" : layerKind, layer.Kind);
        using var anonymous = factory.CreateCookieClient();
        using var mapResponse = await anonymous.GetAsync("/api/public/map");
        mapResponse.EnsureSuccessStatusCode();
        using var map = JsonDocument.Parse(await mapResponse.Content.ReadAsStringAsync());
        Assert.Contains(map.RootElement.GetProperty("layers").EnumerateArray(), x => x.GetProperty("id").GetGuid() == layer.Id);
        if (type == "DOM")
        {
            using var image = await anonymous.GetAsync($"/api/files/{layer.FileId}");
            image.EnsureSuccessStatusCode();
            Assert.Equal(payload, await image.Content.ReadAsByteArrayAsync());
        }
    }

    private static MultipartFormDataContent Import(string type, string layerKind, string fileName, string body, string contentType = "text/plain", string? bounds = null)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(type), "type" },
            { new StringContent(layerKind), "layerKind" },
        };
        if (bounds is not null) content.Add(new StringContent(bounds, Encoding.UTF8, "application/json"), "bounds");
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(file, "file", fileName);
        return content;
    }

    private static async Task<HttpResponseMessage> SendMultipartAsync(HttpClient client, string uri, MultipartFormDataContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", await SmartParkWebApplicationFactory.GetCsrfAsync(client));
        return await client.SendAsync(request);
    }
}
