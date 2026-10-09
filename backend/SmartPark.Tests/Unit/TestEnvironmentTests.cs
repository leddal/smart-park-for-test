namespace SmartPark.Tests.Unit;

public sealed class TestEnvironmentTests
{
    [Fact]
    public void Allows_only_explicit_test_database_when_test_mode_enabled()
    {
        var result = TestEnvironment.RequireIsolatedServices(
            "Host=postgres;Database=smartpark_test;Username=park;Password=secret",
            "redis:6379,abortConnect=false",
            "true");

        Assert.Contains("Database=smartpark_test", result.ParkDb, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("smartpark")]
    [InlineData("smartpark_e2e")]
    [InlineData("postgres")]
    public void Rejects_any_database_except_smartpark_test(string database)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestEnvironment.RequireIsolatedServices(
            $"Host=postgres;Database={database};Username=park;Password=secret",
            "redis:6379",
            "true"));

        Assert.Contains("smartpark_test", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smartpark.integrations")]
    [InlineData("smartpark.integrations.e2e")]
    [InlineData("")]
    public void Rejects_messaging_queues_outside_backend_verification(string queue)
    {
        Assert.Throws<InvalidOperationException>(() => TestEnvironment.RequireIsolatedMessaging("rabbitmq", queue));
    }

    [Fact]
    public void Allows_explicit_backend_test_broker_and_queue()
    {
        TestEnvironment.RequireIsolatedMessaging("rabbitmq", "smartpark.integrations.test");
        Assert.Throws<InvalidOperationException>(() => TestEnvironment.RequireIsolatedMessaging("", "smartpark.integrations.test"));
    }

    [Fact]
    public void Requires_explicit_TEST_ALLOWED_true()
    {
        var error = Assert.Throws<InvalidOperationException>(() => TestEnvironment.RequireIsolatedServices(
            "Host=postgres;Database=smartpark_test;Username=park;Password=secret",
            "redis:6379",
            "false"));

        Assert.Contains("TEST_ALLOWED=true", error.Message, StringComparison.Ordinal);
    }
}
