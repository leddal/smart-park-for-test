using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SmartPark.Benchmarks;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(BenchmarkRunResult result, string runDirectory, CancellationToken ct)
    {
        var resultsPath = Path.Combine(runDirectory, "results.json");
        await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(result, JsonOptions), ct);

        var markdown = new StringBuilder();
        markdown.AppendLine("# Benchmark measured results");
        markdown.AppendLine();
        markdown.AppendLine($"- Run UTC: `{result.StartedAtUtc:O}` to `{result.CompletedAtUtc:O}`");
        markdown.AppendLine($"- Run id: `{result.RunId}`");
        markdown.AppendLine($"- Seed: `{result.Seed}`");
        markdown.AppendLine($"- Source hash: `{result.SourceHash}`");
        markdown.AppendLine("- Raw results: `results.json`");
        markdown.AppendLine();
        markdown.AppendLine("## Environment");
        markdown.AppendLine();
        markdown.AppendLine("| Field | Value |");
        markdown.AppendLine("| --- | --- |");
        foreach (var item in new Dictionary<string, string>
        {
            ["OS"] = result.Environment.OsDescription,
            [".NET"] = result.Environment.FrameworkDescription,
            ["Process architecture"] = result.Environment.ProcessArchitecture,
            ["Logical processors"] = result.Environment.LogicalProcessors.ToString(CultureInfo.InvariantCulture),
            ["Available memory bytes"] = result.Environment.AvailableMemoryBytes.ToString(CultureInfo.InvariantCulture),
            ["PostgreSQL"] = result.Environment.PostgreSqlVersion,
            ["Redis"] = result.Environment.RedisVersion,
            ["Container memory limit"] = result.Environment.ContainerMemoryLimit
        })
            markdown.AppendLine($"| {Escape(item.Key)} | {Escape(item.Value)} |");
        markdown.AppendLine();
        markdown.AppendLine("## Dependency versions");
        markdown.AppendLine();
        markdown.AppendLine("| Dependency | Version |");
        markdown.AppendLine("| --- | --- |");
        foreach (var dependency in result.DependencyVersions.OrderBy(x => x.Key, StringComparer.Ordinal))
            markdown.AppendLine($"| {Escape(dependency.Key)} | {Escape(dependency.Value)} |");

        foreach (var scenario in result.Scenarios)
        {
            markdown.AppendLine();
            markdown.AppendLine($"## {scenario.Name}");
            markdown.AppendLine();
            markdown.AppendLine(scenario.Description);
            markdown.AppendLine();
            markdown.AppendLine("### Metrics");
            markdown.AppendLine();
            markdown.AppendLine("| Metric | Value |");
            markdown.AppendLine("| --- | --- |");
            foreach (var metric in scenario.Metrics.OrderBy(x => x.Key, StringComparer.Ordinal))
                markdown.AppendLine($"| {Escape(metric.Key)} | {FormatMetric(metric.Value)} |");
            markdown.AppendLine();
            markdown.AppendLine("### Verification");
            markdown.AppendLine();
            markdown.AppendLine("```json");
            markdown.AppendLine(JsonSerializer.Serialize(scenario.Verification, JsonOptions));
            markdown.AppendLine("```");
            markdown.AppendLine();
            markdown.AppendLine("### Parameters");
            markdown.AppendLine();
            markdown.AppendLine("```json");
            markdown.AppendLine(JsonSerializer.Serialize(scenario.Parameters, JsonOptions));
            markdown.AppendLine("```");
            if (scenario.Artifacts.Count > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine("### Artifacts");
                markdown.AppendLine();
                foreach (var artifact in scenario.Artifacts) markdown.AppendLine($"- `{artifact}`");
            }
            if (scenario.Notes.Count > 0)
            {
                markdown.AppendLine();
                markdown.AppendLine("### Notes");
                markdown.AppendLine();
                foreach (var note in scenario.Notes) markdown.AppendLine($"- {note}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(runDirectory, "measured-report.md"), markdown.ToString(), ct);
    }

    private static string FormatMetric(double? value) => value is null ? "not calculated (zero denominator)" : value.Value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
