using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartPark.Api.Data;

public sealed class ParkDbContextFactory : IDesignTimeDbContextFactory<ParkDbContext>
{
    public ParkDbContext CreateDbContext(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var configuration = new ConfigurationBuilder().SetBasePath(root).AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        var connection = configuration.GetConnectionString("ParkDb") ?? "Host=localhost;Port=5432;Database=smartpark;Username=smartpark;Password=smartpark";
        return new ParkDbContext(new DbContextOptionsBuilder<ParkDbContext>().UseNpgsql(connection).Options);
    }
}
