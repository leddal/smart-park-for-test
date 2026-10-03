using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Services;

namespace SmartPark.Benchmarks.Scenarios;

public sealed class ReservationCorrectnessScenario(BenchmarkServiceHost host, BenchmarkOptions options)
{
    public async Task<BenchmarkScenarioResult> RunAsync(CancellationToken ct)
    {
        var visitors = await CreateVisitorsAndProductionSessionAsync(ct);
        var baselineSessionId = await CreateUnsafeBaselineTablesAsync(ct);
        var baseline = await RunUnsafeBaselineAsync(baselineSessionId, visitors, ct);
        var production = await RunProductionServiceAsync(visitors, ct);

        if (baseline.OversellCount <= 0)
            throw new InvalidOperationException("Unsafe reservation baseline did not oversell despite the injected read barrier.");
        if (production.OversellCount != 0 || production.DuplicateCount != 0 || production.FinalReservedCount != production.ValidReservationCount)
            throw new InvalidOperationException("ReservationService inconsistency: production reservation invariants failed.");
        if (production.ValidReservationCount != BenchmarkOptions.ReservationCapacity || production.Successes != BenchmarkOptions.ReservationCapacity || production.Rejected != BenchmarkOptions.ReservationVisitorCount - BenchmarkOptions.ReservationCapacity)
            throw new InvalidOperationException("ReservationService inconsistency: the available capacity was not filled exactly once.");

        return new BenchmarkScenarioResult
        {
            Name = "reservation_concurrency_correctness",
            Description = "Uses 100 distinct Identity visitors and capacity 10. The benchmark-only baseline deliberately separates read and write with a barrier; the production contrast calls ReservationService.ReserveAsync for the same concurrency shape.",
            Parameters = new Dictionary<string, object?>
            {
                ["capacity"] = BenchmarkOptions.ReservationCapacity,
                ["distinctVisitors"] = BenchmarkOptions.ReservationVisitorCount,
                ["baselineTables"] = "benchmark.reservation_baseline_sessions, benchmark.reservation_baseline_reservations",
                ["productionService"] = "SmartPark.Api.Features.Services.ReservationService.ReserveAsync(Guid, Guid, CancellationToken)",
                ["faultInjection"] = "All baseline participants read capacity before the barrier releases insert/update. This demonstrates an unsafe pattern and is not a natural-occurrence probability."
            },
            Verification = new Dictionary<string, object?>
            {
                ["baseline"] = baseline.ToDictionary(),
                ["production"] = production.ToDictionary(),
                ["productionCapacityInvariant"] = production.ValidReservationCount <= BenchmarkOptions.ReservationCapacity,
                ["productionUniqueVisitorInvariant"] = production.DuplicateCount == 0,
                ["productionSessionCounterInvariant"] = production.FinalReservedCount == production.ValidReservationCount
            },
            Metrics = new Dictionary<string, double?>
            {
                ["baselineSuccesses"] = baseline.Successes,
                ["baselineRejected"] = baseline.Rejected,
                ["baselineOversellCount"] = baseline.OversellCount,
                ["baselineDuplicateCount"] = baseline.DuplicateCount,
                ["productionSuccesses"] = production.Successes,
                ["productionRejected"] = production.Rejected,
                ["productionOversellCount"] = production.OversellCount,
                ["productionDuplicateCount"] = production.DuplicateCount,
                ["productionSerializationFailureRejections"] = production.SerializationFailureRejections
            },
            Samples = [],
            Artifacts = [],
            Notes = ["This is a correctness experiment. It intentionally makes no latency or throughput claim.", "Unexpected service exceptions are not swallowed; they make the benchmark command fail."]
        };
    }

    private async Task<IReadOnlyList<Guid>> CreateVisitorsAndProductionSessionAsync(CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var db = services.GetRequiredService<ParkDbContext>();
        var visitorIds = new List<Guid>(BenchmarkOptions.ReservationVisitorCount);
        const string password = "Benchmark!20261002";
        for (var index = 0; index < BenchmarkOptions.ReservationVisitorCount; index++)
        {
            var user = new AppUser
            {
                Id = Guid.NewGuid(),
                UserName = $"bench-reservation-{options.RunId}-{index:D3}",
                DisplayName = $"Benchmark visitor {index:D3}",
                EmailConfirmed = true
            };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded) throw new InvalidOperationException($"Could not create benchmark Identity visitor {index}: {string.Join("; ", created.Errors.Select(x => x.Code))}");
            var role = await users.AddToRoleAsync(user, ParkRoles.Visitor);
            if (!role.Succeeded) throw new InvalidOperationException($"Could not assign Visitor role to benchmark Identity visitor {index}: {string.Join("; ", role.Errors.Select(x => x.Code))}");
            visitorIds.Add(user.Id);
        }

        var activity = new Activity
        {
            Title = $"BENCH reservation activity {options.RunId}",
            Description = "Synthetic isolated benchmark activity",
            Location = "Benchmark",
            Status = "Published"
        };
        var session = new ActivitySession
        {
            Activity = activity,
            StartsAt = DateTimeOffset.UtcNow.AddDays(7),
            EndsAt = DateTimeOffset.UtcNow.AddDays(7).AddHours(1),
            Capacity = BenchmarkOptions.ReservationCapacity,
            ReservedCount = 0,
            Status = "Published"
        };
        db.ActivitySessions.Add(session);
        await db.SaveChangesAsync(ct);
        _productionSessionId = session.Id;
        return visitorIds;
    }

    private Guid _productionSessionId;

    private async Task<Guid> CreateUnsafeBaselineTablesAsync(CancellationToken ct)
    {
        var sessionId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(options.ParkDbConnectionString);
        await connection.OpenAsync(ct);
        var ddl = """
            CREATE SCHEMA IF NOT EXISTS benchmark;
            DROP TABLE IF EXISTS benchmark.reservation_baseline_reservations;
            DROP TABLE IF EXISTS benchmark.reservation_baseline_sessions;
            CREATE TABLE benchmark.reservation_baseline_sessions (
                id uuid PRIMARY KEY,
                capacity integer NOT NULL,
                reserved_count integer NOT NULL
            );
            CREATE TABLE benchmark.reservation_baseline_reservations (
                id uuid PRIMARY KEY,
                visitor_id uuid NOT NULL,
                session_id uuid NOT NULL
            );
            """;
        await using (var command = new NpgsqlCommand(ddl, connection))
            await command.ExecuteNonQueryAsync(ct);
        await using var insert = new NpgsqlCommand("INSERT INTO benchmark.reservation_baseline_sessions (id, capacity, reserved_count) VALUES (@id, @capacity, 0);", connection);
        insert.Parameters.AddWithValue("id", sessionId);
        insert.Parameters.AddWithValue("capacity", BenchmarkOptions.ReservationCapacity);
        await insert.ExecuteNonQueryAsync(ct);
        return sessionId;
    }

    private async Task<ReservationOutcome> RunUnsafeBaselineAsync(Guid sessionId, IReadOnlyList<Guid> visitorIds, CancellationToken ct)
    {
        var allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        async Task<ReservationAttempt> AttemptAsync(Guid visitorId)
        {
            var observed = await ReadUnsafeAvailabilityAsync(sessionId, ct);
            if (Interlocked.Increment(ref readCount) == visitorIds.Count) allRead.SetResult();
            await allRead.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (observed.ReservedCount >= observed.Capacity) return ReservationAttempt.Rejected("observedFull");
            await InsertUnsafeReservationAsync(sessionId, visitorId, observed.ReservedCount + 1, ct);
            return ReservationAttempt.Succeeded();
        }
        var tasks = visitorIds.Select(AttemptAsync).ToArray();
        var attempts = await Task.WhenAll(tasks);
        var outcome = await ReadUnsafeOutcomeAsync(sessionId, attempts, ct);
        if (outcome.Successes != visitorIds.Count)
            throw new InvalidOperationException($"Unsafe baseline inconsistency: expected every participant to use the stale capacity observation, but only {outcome.Successes} inserted.");
        return outcome;
    }

    private async Task<(int Capacity, int ReservedCount)> ReadUnsafeAvailabilityAsync(Guid sessionId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(options.ParkDbConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT capacity, reserved_count FROM benchmark.reservation_baseline_sessions WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Unsafe baseline session unexpectedly missing.");
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task InsertUnsafeReservationAsync(Guid sessionId, Guid visitorId, int staleReservedCount, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(options.ParkDbConnectionString);
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand("INSERT INTO benchmark.reservation_baseline_reservations (id, visitor_id, session_id) VALUES (@id, @visitorId, @sessionId);", connection);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("visitorId", visitorId);
        insert.Parameters.AddWithValue("sessionId", sessionId);
        await insert.ExecuteNonQueryAsync(ct);
        await using var update = new NpgsqlCommand("UPDATE benchmark.reservation_baseline_sessions SET reserved_count = @reservedCount WHERE id = @id;", connection);
        update.Parameters.AddWithValue("reservedCount", staleReservedCount);
        update.Parameters.AddWithValue("id", sessionId);
        await update.ExecuteNonQueryAsync(ct);
    }

    private async Task<ReservationOutcome> RunProductionServiceAsync(IReadOnlyList<Guid> visitorIds, CancellationToken ct)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ready = new CountdownEvent(visitorIds.Count);
        var tasks = visitorIds.Select(visitorId => Task.Run(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ReservationService>();
            ready.Signal();
            await start.Task.WaitAsync(ct);
            try
            {
                await service.ReserveAsync(visitorId, _productionSessionId, ct);
                return ReservationAttempt.Succeeded();
            }
            catch (ApiException api) when (api.StatusCode == 409)
            {
                return ReservationAttempt.Rejected("api409");
            }
            catch (PostgresException postgres) when (postgres.SqlState == "40001")
            {
                return ReservationAttempt.Rejected("serializationFailure");
            }
            catch (Exception exception)
            {
                return ReservationAttempt.Fatal(exception);
            }
        }, ct)).ToArray();
        if (!ready.Wait(TimeSpan.FromSeconds(30), ct))
            throw new TimeoutException("Timed out preparing concurrent ReservationService calls.");
        start.SetResult();
        var attempts = await Task.WhenAll(tasks);
        var fatalAttempt = attempts.FirstOrDefault(x => x.Exception is not null);
        if (fatalAttempt?.Exception is { } fatal)
            throw new InvalidOperationException("ReservationService threw an unexpected exception during the correctness experiment.", fatal);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ParkDbContext>();
        var session = await db.ActivitySessions.AsNoTracking().SingleAsync(x => x.Id == _productionSessionId, ct);
        var reservations = await db.Reservations.AsNoTracking().Where(x => x.SessionId == _productionSessionId && x.Status == "Valid").ToListAsync(ct);
        return ReservationOutcome.FromAttempts(attempts, session.Capacity, session.ReservedCount, reservations.Count, reservations.Select(x => x.VisitorId).Distinct().Count());
    }

    private async Task<ReservationOutcome> ReadUnsafeOutcomeAsync(Guid sessionId, IReadOnlyList<ReservationAttempt> attempts, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(options.ParkDbConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT s.capacity, s.reserved_count, COUNT(r.id), COUNT(DISTINCT r.visitor_id) FROM benchmark.reservation_baseline_sessions s LEFT JOIN benchmark.reservation_baseline_reservations r ON r.session_id = s.id WHERE s.id = @id GROUP BY s.capacity, s.reserved_count;", connection);
        command.Parameters.AddWithValue("id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Unsafe baseline session unexpectedly missing after concurrent inserts.");
        return ReservationOutcome.FromAttempts(attempts, reader.GetInt32(0), reader.GetInt32(1), checked((int)reader.GetInt64(2)), checked((int)reader.GetInt64(3)));
    }

    private sealed record ReservationAttempt(bool Success, string Outcome, Exception? Exception)
    {
        public static ReservationAttempt Succeeded() => new(true, "success", null);
        public static ReservationAttempt Rejected(string outcome) => new(false, outcome, null);
        public static ReservationAttempt Fatal(Exception exception) => new(false, "fatal", exception);
    }

    private sealed record ReservationOutcome(int Successes, int Rejected, int SerializationFailureRejections, int Capacity, int FinalReservedCount, int ValidReservationCount, int DistinctVisitors)
    {
        public int OversellCount => Math.Max(0, ValidReservationCount - Capacity);
        public int DuplicateCount => ValidReservationCount - DistinctVisitors;

        public static ReservationOutcome FromAttempts(IReadOnlyList<ReservationAttempt> attempts, int capacity, int finalReservedCount, int validReservations, int distinctVisitors) => new(
            attempts.Count(x => x.Success),
            attempts.Count(x => !x.Success && x.Exception is null),
            attempts.Count(x => x.Outcome == "serializationFailure"),
            capacity,
            finalReservedCount,
            validReservations,
            distinctVisitors);

        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["successes"] = Successes,
            ["rejected"] = Rejected,
            ["serializationFailureRejections"] = SerializationFailureRejections,
            ["capacity"] = Capacity,
            ["finalReservedCount"] = FinalReservedCount,
            ["validReservationCount"] = ValidReservationCount,
            ["distinctVisitors"] = DistinctVisitors,
            ["oversellCount"] = OversellCount,
            ["duplicateCount"] = DuplicateCount
        };
    }
}
