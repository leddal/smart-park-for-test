using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace SmartPark.Benchmarks;

public sealed record BenchmarkOptions(
    string ParkDbConnectionString,
    string RedisConnectionString,
    string OutputRoot,
    string RunDirectory,
    string RunId)
{
    public const int Seed = 20261002;
    public const int WarmupIterations = 10;
    public const int SampleIterations = 100;
    public const int TelemetryRowCount = 100_000;
    public const int PagingAssetCount = 10_000;
    public const int ReservationCapacity = 10;
    public const int ReservationVisitorCount = 100;

    public static BenchmarkOptions FromEnvironment()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BENCHMARK_ALLOWED"), "true", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to run: BENCHMARK_ALLOWED must equal 'true'.");

        var parkDb = Environment.GetEnvironmentVariable("ConnectionStrings__ParkDb");
        if (string.IsNullOrWhiteSpace(parkDb))
            throw new InvalidOperationException("Refusing to run: ConnectionStrings__ParkDb is required.");

        var builder = new NpgsqlConnectionStringBuilder(parkDb);
        if (!string.Equals(builder.Database, "smartpark_benchmark", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to run: database must be exactly 'smartpark_benchmark', but was '{builder.Database ?? "<missing>"}'.");

        var redis = Environment.GetEnvironmentVariable("ConnectionStrings__Redis");
        if (string.IsNullOrWhiteSpace(redis))
            throw new InvalidOperationException("Refusing to run: ConnectionStrings__Redis is required for the actual cache scenario.");

        var outputRoot = Environment.GetEnvironmentVariable("BENCHMARK_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputRoot)) outputRoot = "/artifacts/benchmarks";
        outputRoot = Path.GetFullPath(outputRoot);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var runDirectory = Path.Combine(outputRoot, runId);
        return new BenchmarkOptions(parkDb, redis, outputRoot, runDirectory, runId);
    }
}

public static class BenchmarkEnvironmentCapture
{
    public static async Task<BenchmarkEnvironment> CaptureAsync(string parkDbConnectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(parkDbConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SHOW server_version;", connection);
        var postgresVersion = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "unknown";

        return new BenchmarkEnvironment
        {
            OsDescription = RuntimeInformation.OSDescription,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            PostgreSqlVersion = postgresVersion,
            RedisVersion = "unknown (the benchmark uses IDistributedCache; Redis INFO is not exposed)",
            ContainerMemoryLimit = DetectContainerMemoryLimit()
        };
    }

    public static string ComputeSourceHash()
    {
        var root = FindRepositoryRoot();
        if (root is null) return "unknown";

        var separator = Path.DirectorySeparatorChar;
        var binSegment = $"{separator}bin{separator}";
        var objSegment = $"{separator}obj{separator}";
        var inputs = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => (path.Contains("SmartPark.Benchmarks", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("SmartPark.Api", StringComparison.OrdinalIgnoreCase))
                && !path.Contains(binSegment, StringComparison.OrdinalIgnoreCase)
                && !path.Contains(objSegment, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (inputs.Length == 0) return "unknown";

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in inputs)
        {
            var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string DetectContainerMemoryLimit()
    {
        var hardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit");
        if (!string.IsNullOrWhiteSpace(hardLimit)) return $"DOTNET_GCHeapHardLimit={hardLimit}";
        foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } value && !string.Equals(value, "max", StringComparison.OrdinalIgnoreCase))
                    return value;
            }
            catch (IOException)
            {
                // A cgroup limit unavailable to this process is explicitly reported as unknown below.
            }
            catch (UnauthorizedAccessException)
            {
                // A cgroup limit unavailable to this process is explicitly reported as unknown below.
            }
        }
        return "unknown";
    }

    private static string? FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "backend")) && File.Exists(Path.Combine(current.FullName, "SmartPark.sln")))
                return current.FullName;
        return null;
    }
}
