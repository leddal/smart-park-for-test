using System.Text;
using Microsoft.AspNetCore.Http;
using SmartPark.Api.Common;
using SmartPark.Api.Features.Assets;
using SmartPark.Api.Features.Overview;
using SmartPark.Api.Storage;

namespace SmartPark.Tests.Unit;

public sealed class CalculationAndFileValidationTests
{
    [Fact]
    public void Carbon_estimate_uses_the_documented_demo_formula()
    {
        var estimated = CarbonEstimateService.Estimate(10m, 2m);

        Assert.Equal(110m / 3m, estimated);
        Assert.True(CarbonEstimateService.TryEstimate(10m, 2m, out var fromNullable));
        Assert.Equal(estimated, fromNullable);
    }

    public static TheoryData<decimal?, decimal?> InvalidCarbonMeasurements => new()
    {
        { null, 2m }, { 10m, null }, { 0m, 2m }, { 10m, 0m },
    };

    [Theory]
    [MemberData(nameof(InvalidCarbonMeasurements))]
    public void Carbon_estimate_marks_missing_or_nonpositive_measurements_invalid(decimal? diameter, decimal? height)
    {
        Assert.False(CarbonEstimateService.TryEstimate(diameter, height, out var estimate));
        Assert.Equal(0m, estimate);
    }

    [Fact]
    public void Eco_index_uses_documented_weights_and_refuses_missing_inputs()
    {
        var result = EcoIndexService.Calculate(30m, 45m, 6m, DateTimeOffset.UnixEpoch);
        var insufficient = EcoIndexService.Calculate(30m, null, 6m, DateTimeOffset.UnixEpoch);

        Assert.True(result.Sufficient);
        Assert.Equal(78.5m, result.Score);
        Assert.Equal(80m, result.AirScore);
        Assert.Equal(80m, result.NoiseScore);
        Assert.Equal(75m, result.WaterScore);
        Assert.False(insufficient.Sufficient);
        Assert.Null(insufficient.Score);
    }

    [Fact]
    public async Task Photo_upload_rejects_html_disguised_as_png()
    {
        var payload = Encoding.UTF8.GetBytes("<html><script>attack()</script></html>");
        await using var stream = new MemoryStream(payload);
        var file = new FormFile(stream, 0, payload.Length, "file", "unsafe.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };

        var error = await Assert.ThrowsAsync<ApiException>(() => FileValidation.ValidateAsync(file, "photo", CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Invalid file", error.Title);
    }

    [Fact]
    public async Task Upload_rejects_path_like_file_name_by_extension_policy()
    {
        var payload = Encoding.UTF8.GetBytes("binary");
        await using var stream = new MemoryStream(payload);
        var file = new FormFile(stream, 0, payload.Length, "file", "../../payload.exe") { Headers = new HeaderDictionary(), ContentType = "application/octet-stream" };

        var error = await Assert.ThrowsAsync<ApiException>(() => FileValidation.ValidateAsync(file, "photo", CancellationToken.None));
        Assert.Equal("Unsupported file", error.Title);
    }
}
