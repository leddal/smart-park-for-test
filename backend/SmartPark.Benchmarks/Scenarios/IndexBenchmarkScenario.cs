using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace SmartPark.Benchmarks.Scenarios;

public sealed class IndexBenchmarkScenario(BenchmarkOptions options)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] MetricCodes = Enumerable.Range(0, 10).Select(x => $"metric-{x:D2}").ToArray();

    public async Task<BenchmarkScenarioResult> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(options.RunDirectory, "plans"));
        var deviceIds = Enumerable.Range(0, 10).Select(DeterministicGuid).ToArray();
        var queryDevice = deviceIds[3];
        const string queryMetric = "metric-07";
        var rangeStart = Start.AddSeconds(100);
        var rangeEnd = Start.AddSeconds(900);

        await using var connection = new NpgsqlConnection(options.ParkDbConnectionString);
        await connection.OpenAsync(ct);
        await CreateAndSeedAsync(connection, deviceIds, ct);

        var baselineRows = await QueryAsync(connection, "benchmark.telemetry_baseline", queryDevice, queryMetric, rangeStart, rangeEnd, ct);
        var optimizedRows = await QueryAsync(connection, "benchmark.telemetry_optimized", queryDevice, queryMetric, rangeStart, rangeEnd, ct);
        if (!baselineRows.SequenceEqual(optimizedRows))
            throw new InvalidOperationException("Index benchmark inconsistency: baseline and optimized queries did not return identical ordered rows.");
        if (baselineRows.Count != 500)
            throw new InvalidOperationException($"Index benchmark inconsistency: expected 500 rows but observed {baselineRows.Count}.");

        var baselinePlanFile = Path.Combine("plans", "index-baseline-explain-analyze-buffers.txt");
        var optimizedPlanFile = Path.Combine("plans", "index-optimized-explain-analyze-buffers.txt");
        await File.WriteAllTextAsync(Path.Combine(options.RunDirectory, baselinePlanFile), await ExplainAsync(connection, "benchmark.telemetry_baseline", queryDevice, queryMetric, rangeStart, rangeEnd, ct), ct);
        await File.WriteAllTextAsync(Path.Combine(options.RunDirectory, optimizedPlanFile), await ExplainAsync(connection, "benchmark.telemetry_optimized", queryDevice, queryMetric, rangeStart, rangeEnd, ct), ct);

        var samples = new List<BenchmarkSample>();
        for (var iteration = 0; iteration < BenchmarkOptions.WarmupIterations; iteration++)
        {
            samples.Add(await MeasureAsync(connection, "baselinePrimaryKeyOnly", "warmup", iteration, "benchmark.telemetry_baseline", queryDevice, queryMetric, rangeStart, rangeEnd, ct));
            samples.Add(await MeasureAsync(connection, "optimizedCompoundIndex", "warmup", iteration, "benchmark.telemetry_optimized", queryDevice, queryMetric, rangeStart, rangeEnd, ct));
        }
        for (var iteration = 0; iteration < BenchmarkOptions.SampleIterations; iteration++)
        {
            samples.Add(await MeasureAsync(connection, "baselinePrimaryKeyOnly", "measured", iteration, "benchmark.telemetry_baseline", queryDevice, queryMetric, rangeStart, rangeEnd, ct));
            samples.Add(await MeasureAsync(connection, "optimizedCompoundIndex", "measured", iteration, "benchmark.telemetry_optimized", queryDevice, queryMetric, rangeStart, rangeEnd, ct));
        }

        var baseline = samples.Where(x => x.Phase == "measured" && x.Variant == "baselinePrimaryKeyOnly").Select(x => x.ElapsedMilliseconds).ToArray();
        var optimized = samples.Where(x => x.Phase == "measured" && x.Variant == "optimizedCompoundIndex").Select(x => x.ElapsedMilliseconds).ToArray();
        var baselineMedian = BenchmarkMetrics.Median(baseline);
        var optimizedMedian = BenchmarkMetrics.Median(optimized);
        var baselineP95 = BenchmarkMetrics.Percentile95(baseline);
        var optimizedP95 = BenchmarkMetrics.Percentile95(optimized);

        return new BenchmarkScenarioResult
        {
            Name = "telemetry_compound_index",
            Description = "Uses identical deterministic telemetry data in benchmark-only PostgreSQL tables: a primary-key-only baseline and an actual (device_id, metric_code, collected_at) compound index.",
            Parameters = new Dictionary<string, object?>
            {
                ["telemetryRowsPerTable"] = BenchmarkOptions.TelemetryRowCount,
                ["seed"] = BenchmarkOptions.Seed,
                ["deviceId"] = queryDevice,
                ["metricCode"] = queryMetric,
                ["collectedAtStartUtc"] = rangeStart,
                ["collectedAtEndUtc"] = rangeEnd,
                ["resultLimit"] = 500,
                ["ordering"] = "collected_at descending, id ascending",
                ["baselineIndex"] = "primary key only",
                ["optimizedIndex"] = "CREATE INDEX ix_telemetry_optimized_device_metric_collected_at ON benchmark.telemetry_optimized (device_id, metric_code, collected_at)"
            },
            Verification = new Dictionary<string, object?>
            {
                ["identicalOrderedResults"] = true,
                ["resultRowCount"] = baselineRows.Count,
                ["baselineTable"] = "benchmark.telemetry_baseline",
                ["optimizedTable"] = "benchmark.telemetry_optimized"
            },
            Metrics = new Dictionary<string, double?>
            {
                ["baselineMedianMilliseconds"] = baselineMedian,
                ["optimizedMedianMilliseconds"] = optimizedMedian,
                ["baselineP95Milliseconds"] = baselineP95,
                ["optimizedP95Milliseconds"] = optimizedP95,
                ["baselineSpreadMilliseconds"] = BenchmarkMetrics.Spread(baseline),
                ["optimizedSpreadMilliseconds"] = BenchmarkMetrics.Spread(optimized),
                ["medianReductionPercent"] = BenchmarkMetrics.Reduction(baselineMedian, optimizedMedian),
                ["medianFactor"] = BenchmarkMetrics.Factor(baselineMedian, optimizedMedian)
            },
            Samples = samples,
            Artifacts = [baselinePlanFile.Replace('\\', '/'), optimizedPlanFile.Replace('\\', '/')],
            Notes = ["The runner does not alter production application indexes or planner settings. PostgreSQL selected its own plan; EXPLAIN (ANALYZE, BUFFERS) output is retained as an artifact."]
        };
    }

    private static async Task CreateAndSeedAsync(NpgsqlConnection connection, Guid[] deviceIds, CancellationToken ct)
    {
        const string ddl = """
            CREATE SCHEMA IF NOT EXISTS benchmark;
            DROP TABLE IF EXISTS benchmark.telemetry_baseline;
            DROP TABLE IF EXISTS benchmark.telemetry_optimized;
            CREATE TABLE benchmark.telemetry_baseline (
                id uuid PRIMARY KEY,
                device_id uuid NOT NULL,
                metric_code text NOT NULL,
                collected_at timestamptz NOT NULL,
                value numeric(18,6) NOT NULL,
                unit text NOT NULL
            );
            CREATE TABLE benchmark.telemetry_optimized (
                id uuid PRIMARY KEY,
                device_id uuid NOT NULL,
                metric_code text NOT NULL,
                collected_at timestamptz NOT NULL,
                value numeric(18,6) NOT NULL,
                unit text NOT NULL
            );
            CREATE INDEX ix_telemetry_optimized_device_metric_collected_at
                ON benchmark.telemetry_optimized (device_id, metric_code, collected_at);
            """;
        await using (var command = new NpgsqlCommand(ddl, connection))
            await command.ExecuteNonQueryAsync(ct);

        await CopyRowsAsync(connection, "benchmark.telemetry_baseline", deviceIds, ct);
        await CopyRowsAsync(connection, "benchmark.telemetry_optimized", deviceIds, ct);
        await using var analyze = new NpgsqlCommand("ANALYZE benchmark.telemetry_baseline; ANALYZE benchmark.telemetry_optimized;", connection);
        await analyze.ExecuteNonQueryAsync(ct);
    }

    private static async Task CopyRowsAsync(NpgsqlConnection connection, string table, Guid[] deviceIds, CancellationToken ct)
    {
        await using var importer = await connection.BeginBinaryImportAsync($"COPY {table} (id, device_id, metric_code, collected_at, value, unit) FROM STDIN (FORMAT BINARY)", ct);
        var random = new Random(BenchmarkOptions.Seed);
        for (var row = 0; row < BenchmarkOptions.TelemetryRowCount; row++)
        {
            var combination = row % 100;
            var deviceId = deviceIds[combination % deviceIds.Length];
            var metricCode = MetricCodes[combination / deviceIds.Length];
            var collectedAt = Start.AddSeconds(row / 100);
            await importer.StartRowAsync(ct);
            await importer.WriteAsync(DeterministicGuid(row + 10_000), NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(deviceId, NpgsqlDbType.Uuid, ct);
            await importer.WriteAsync(metricCode, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(collectedAt, NpgsqlDbType.TimestampTz, ct);
            await importer.WriteAsync(Math.Round((decimal)random.NextDouble() * 100m, 6), NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync("unit", NpgsqlDbType.Text, ct);
        }
        await importer.CompleteAsync(ct);
    }

    private static async Task<List<TelemetryRow>> QueryAsync(NpgsqlConnection connection, string table, Guid deviceId, string metricCode, DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken ct)
    {
        var sql = $"SELECT id, device_id, metric_code, collected_at, value, unit FROM {table} WHERE device_id = @deviceId AND metric_code = @metricCode AND collected_at >= @rangeStart AND collected_at <= @rangeEnd ORDER BY collected_at DESC, id ASC LIMIT 500;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("deviceId", NpgsqlDbType.Uuid, deviceId);
        command.Parameters.AddWithValue("metricCode", NpgsqlDbType.Text, metricCode);
        command.Parameters.AddWithValue("rangeStart", NpgsqlDbType.TimestampTz, rangeStart);
        command.Parameters.AddWithValue("rangeEnd", NpgsqlDbType.TimestampTz, rangeEnd);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TelemetryRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new TelemetryRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetDecimal(4), reader.GetString(5)));
        return rows;
    }

    private static async Task<BenchmarkSample> MeasureAsync(NpgsqlConnection connection, string variant, string phase, int sequence, string table, Guid deviceId, string metricCode, DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = await QueryAsync(connection, table, deviceId, metricCode, rangeStart, rangeEnd, ct);
        stopwatch.Stop();
        if (rows.Count != 500) throw new InvalidOperationException($"Index benchmark inconsistency: {variant} returned {rows.Count} rows during {phase} sample {sequence}.");
        return new BenchmarkSample(variant, phase, sequence, stopwatch.Elapsed.TotalMilliseconds, rows.Count);
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, string table, Guid deviceId, string metricCode, DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken ct)
    {
        var sql = $"EXPLAIN (ANALYZE, BUFFERS) SELECT id, device_id, metric_code, collected_at, value, unit FROM {table} WHERE device_id = '{deviceId:D}'::uuid AND metric_code = '{metricCode}' AND collected_at >= '{rangeStart:O}'::timestamptz AND collected_at <= '{rangeEnd:O}'::timestamptz ORDER BY collected_at DESC, id ASC LIMIT 500;";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(ct)) lines.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static Guid DeterministicGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes[..4], value);
        BitConverter.TryWriteBytes(bytes.Slice(4, 4), BenchmarkOptions.Seed);
        return new Guid(bytes);
    }

    private sealed record TelemetryRow(Guid Id, Guid DeviceId, string MetricCode, DateTimeOffset CollectedAt, decimal Value, string Unit);
}
