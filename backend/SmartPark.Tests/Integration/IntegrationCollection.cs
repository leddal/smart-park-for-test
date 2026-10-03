using Xunit;

namespace SmartPark.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationCollection : ICollectionFixture<SmartParkWebApplicationFactory>
{
    public const string Name = "PostgreSQL and Redis integration tests";
}
