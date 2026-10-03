using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartPark.Api.Common;
using SmartPark.Api.Data;
using SmartPark.Api.Features.Services;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class ReservationIntegrationTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Reservation_is_owned_by_visitor_and_duplicate_cancel_does_not_release_capacity_twice()
    {
        var sessionId = await CreateSessionAsync(2);
        using var owner = await RegisterVisitorAsync();
        using var stranger = await RegisterVisitorAsync();

        var reservationId = await ReserveAsync(owner, sessionId);
        using var duplicate = await SmartParkWebApplicationFactory.SendJsonAsync(owner, HttpMethod.Post, $"/api/public/sessions/{sessionId}/reserve", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var prohibitedCancel = await SmartParkWebApplicationFactory.SendJsonAsync(stranger, HttpMethod.Post, $"/api/public/reservations/{reservationId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, prohibitedCancel.StatusCode);

        using var firstCancel = await SmartParkWebApplicationFactory.SendJsonAsync(owner, HttpMethod.Post, $"/api/public/reservations/{reservationId}/cancel", null);
        using var repeatedCancel = await SmartParkWebApplicationFactory.SendJsonAsync(owner, HttpMethod.Post, $"/api/public/reservations/{reservationId}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, firstCancel.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeatedCancel.StatusCode);

        var state = await factory.InDatabaseAsync(async db => new
        {
            Session = await db.ActivitySessions.SingleAsync(x => x.Id == sessionId),
            Reservation = await db.Reservations.SingleAsync(x => x.Id == reservationId),
        });
        Assert.Equal(0, state.Session.ReservedCount);
        Assert.Equal("Cancelled", state.Reservation.Status);
    }

    [Fact]
    public async Task Checked_in_reservation_cannot_be_cancelled()
    {
        var sessionId = await CreateSessionAsync(1);
        using var visitor = await RegisterVisitorAsync();
        using var dispatcher = await factory.CreateAuthenticatedClientAsync("dispatcher");
        var reservationId = await ReserveAsync(visitor, sessionId);

        using var checkIn = await SmartParkWebApplicationFactory.SendJsonAsync(dispatcher, HttpMethod.Post, $"/api/services/reservations/{reservationId}/check-in", null);
        checkIn.EnsureSuccessStatusCode();
        using var cancel = await SmartParkWebApplicationFactory.SendJsonAsync(visitor, HttpMethod.Post, $"/api/public/reservations/{reservationId}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        var state = await factory.InDatabaseAsync(db => db.Reservations.SingleAsync(x => x.Id == reservationId));
        Assert.Equal("CheckedIn", state.Status);
    }

    [Fact]
    public async Task One_hundred_distinct_visitors_concurrently_compete_for_ten_places_without_oversell()
    {
        var sessionId = await CreateSessionAsync(10);
        var visitorIds = await CreateVisitorIdentitiesAsync(100);

        var outcomes = await Task.WhenAll(visitorIds.Select(visitorId => AttemptReservationAsync(visitorId, sessionId)));

        Assert.Equal(10, outcomes.Count(x => x is null));
        Assert.All(outcomes.Where(x => x is not null), status => Assert.Equal(409, status));
        var persisted = await factory.InDatabaseAsync(async db => new
        {
            Session = await db.ActivitySessions.SingleAsync(x => x.Id == sessionId),
            ValidReservations = await db.Reservations.CountAsync(x => x.SessionId == sessionId && x.Status == "Valid"),
        });
        Assert.Equal(10, persisted.Session.ReservedCount);
        Assert.Equal(10, persisted.ValidReservations);
    }

    private async Task<Guid> CreateSessionAsync(int capacity)
    {
        return await factory.InDatabaseAsync(async db =>
        {
            var activity = new Activity
            {
                Title = $"Reservation test {Guid.NewGuid():N}",
                Description = "Isolated integration-test activity",
                Location = "Test location",
                Status = "Published",
            };
            var session = new ActivitySession
            {
                Activity = activity,
                StartsAt = DateTimeOffset.UtcNow.AddDays(2),
                EndsAt = DateTimeOffset.UtcNow.AddDays(2).AddHours(1),
                Capacity = capacity,
                Status = "Published",
            };
            db.ActivitySessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });
    }

    private async Task<HttpClient> RegisterVisitorAsync()
    {
        var client = factory.CreateCookieClient();
        var csrf = await SmartParkWebApplicationFactory.GetCsrfAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                userName = $"reservation-{Guid.NewGuid():N}",
                password = "Visitor!2026",
                displayName = "预约测试游客",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<Guid> ReserveAsync(HttpClient client, Guid sessionId)
    {
        using var response = await SmartParkWebApplicationFactory.SendJsonAsync(client, HttpMethod.Post, $"/api/public/sessions/{sessionId}/reserve", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid[]> CreateVisitorIdentitiesAsync(int number)
    {
        return await factory.InDatabaseAsync(async db =>
        {
            var users = Enumerable.Range(0, number).Select(index => new AppUser
            {
                UserName = $"concurrent-{Guid.NewGuid():N}-{index}",
                NormalizedUserName = $"CONCURRENT-{Guid.NewGuid():N}-{index}",
                DisplayName = "并发预约测试游客",
                SecurityStamp = Guid.NewGuid().ToString("N"),
            }).ToArray();
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            return users.Select(x => x.Id).ToArray();
        });
    }

    private async Task<int?> AttemptReservationAsync(Guid visitorId, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ReservationService>();
        try
        {
            await service.ReserveAsync(visitorId, sessionId);
            return null;
        }
        catch (ApiException exception)
        {
            return exception.StatusCode;
        }
        catch
        {
            return 500;
        }
    }
}
