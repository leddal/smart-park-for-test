using Microsoft.EntityFrameworkCore;
using SmartPark.Api.Common;

namespace SmartPark.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class MigrationAndSeedTests(SmartParkWebApplicationFactory factory)
{
    [Fact]
    public async Task Explicit_migration_and_two_seed_runs_leave_one_complete_seed_set()
    {
        var migrations = await factory.InDatabaseAsync(db => db.Database.GetAppliedMigrationsAsync());
        Assert.NotEmpty(migrations);

        var seed = factory.SeedSnapshot;
        Assert.True(seed.Parks >= 1);
        Assert.True(seed.Devices >= 1);
        Assert.True(seed.Activities >= 1);
        var versions = await factory.InDatabaseAsync(db => db.SeedVersions.Select(x => x.Name).ToListAsync());
        Assert.Single(versions, x => x is "initial-v1" or "initial-v2");
        Assert.Equal(versions.Count, versions.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(versions.Count, seed.SeedVersions);

        var accounts = await factory.InDatabaseAsync(async db => await (
            from user in db.Users
            join map in db.UserRoles on user.Id equals map.UserId
            join role in db.Roles on map.RoleId equals role.Id
            where user.UserName == "admin" || user.UserName == "dispatcher" || user.UserName == "worker" || user.UserName == "worker2" || user.UserName == "visitor"
            select new { user.UserName, Role = role.Name }).ToListAsync());

        Assert.Contains(accounts, x => x.UserName == "admin" && x.Role == ParkRoles.Administrator);
        Assert.Contains(accounts, x => x.UserName == "dispatcher" && x.Role == ParkRoles.Dispatcher);
        Assert.Equal(2, accounts.Count(x => x.Role == ParkRoles.Worker));
        Assert.Contains(accounts, x => x.UserName == "visitor" && x.Role == ParkRoles.Visitor);
    }
}
