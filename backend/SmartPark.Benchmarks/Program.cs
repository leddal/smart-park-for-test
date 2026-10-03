using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartPark.Api.Data;
using SmartPark.Benchmarks;
using SmartPark.Benchmarks.Scenarios;

return await BenchmarkCommand.RunAsync(args);

internal static class BenchmarkCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: dotnet run --project backend/SmartPark.Benchmarks -- run");
            Console.WriteLine("Required: BENCHMARK_ALLOWED=true, ConnectionStrings__ParkDb database exactly smartpark_benchmark, ConnectionStrings__Redis.");
            Console.WriteLine("Container command: docker compose -f compose.verify.yaml -p smartpark-verify run --rm benchmarks");
            return 0;
        }
        if (args.Length > 1 || (args.Length == 1 && !string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Invalid benchmark command. Use 'run' or '--help'.");
            return 64;
        }

        try
        {
            var options = BenchmarkOptions.FromEnvironment();
            Directory.CreateDirectory(options.RunDirectory);
            var started = DateTimeOffset.UtcNow;
            var environment = await BenchmarkEnvironmentCapture.CaptureAsync(options.ParkDbConnectionString, CancellationToken.None);
            var result = new BenchmarkRunResult
            {
                RunId = options.RunId,
                StartedAtUtc = started,
                CompletedAtUtc = started,
                Seed = BenchmarkOptions.Seed,
                SourceHash = BenchmarkEnvironmentCapture.ComputeSourceHash(),
                Environment = environment,
                DependencyVersions = DependencyVersions(),
                InputParameters = new Dictionary<string, object?>
                {
                    ["database"] = "smartpark_benchmark",
                    ["outputDirectory"] = options.RunDirectory,
                    ["telemetryRows"] = BenchmarkOptions.TelemetryRowCount,
                    ["pagingAssets"] = BenchmarkOptions.PagingAssetCount,
                    ["reservationCapacity"] = BenchmarkOptions.ReservationCapacity,
                    ["reservationVisitors"] = BenchmarkOptions.ReservationVisitorCount,
                    ["warmupIterations"] = BenchmarkOptions.WarmupIterations,
                    ["measuredIterations"] = BenchmarkOptions.SampleIterations,
                    ["cacheTtlSeconds"] = 10
                }
            };

            var commandCounter = new DbCommandCounter();
            await using var host = await BenchmarkServiceHost.CreateAsync(options, commandCounter, CancellationToken.None);
            result.Scenarios.Add(await new IndexBenchmarkScenario(options).RunAsync(CancellationToken.None));
            result.Scenarios.Add(await new OverviewCacheBenchmarkScenario(host, commandCounter, options).RunAsync(CancellationToken.None));
            result.Scenarios.Add(await new PagingBenchmarkScenario(host, commandCounter, options).RunAsync(CancellationToken.None));
            result.Scenarios.Add(await new ReservationCorrectnessScenario(host, options).RunAsync(CancellationToken.None));
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            await ReportWriter.WriteAsync(result, options.RunDirectory, CancellationToken.None);
            Console.WriteLine($"BENCHMARK SUCCEEDED: {options.RunDirectory}");
            Console.WriteLine("Artifacts: results.json, measured-report.md, plans/.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("BENCHMARK FAILED (nonzero exit): " + exception);
            return 1;
        }
    }

    private static Dictionary<string, string> DependencyVersions() => new(StringComparer.Ordinal)
    {
        ["Microsoft.EntityFrameworkCore"] = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "unknown",
        ["Microsoft.Extensions.Caching.StackExchangeRedis"] = "10.0.4 (declared)",
        ["Npgsql"] = typeof(NpgsqlConnection).Assembly.GetName().Version?.ToString() ?? "unknown",
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = "10.0.3 (declared)",
        ["SmartPark.Api"] = typeof(ParkDbContext).Assembly.GetName().Version?.ToString() ?? "unknown",
        ["SmartPark.Benchmarks"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
    };
}
