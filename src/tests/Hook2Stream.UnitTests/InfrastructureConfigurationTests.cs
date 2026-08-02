using Hook2Stream.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class InfrastructureConfigurationTests
{
    [Fact]
    public void Production_requires_an_explicit_database_connection_string()
    {
        using var provider = Services(new Dictionary<string, string?>(), Environments.Production);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<DatabaseConnectionOptions>>().Value);

        Assert.Contains("ConnectionStrings:hook2stream", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_accepts_the_legacy_database_connection_string_name()
    {
        const string connectionString =
            "Host=postgres;Database=hook2stream;Username=app;Password=secret";
        using var provider = Services(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString
            },
            Environments.Production);

        var options = provider.GetRequiredService<IOptions<DatabaseConnectionOptions>>().Value;

        Assert.Equal(connectionString, options.ConnectionString);
    }

    [Fact]
    public void Development_preserves_the_local_database_fallback()
    {
        using var provider = Services(new Dictionary<string, string?>(), Environments.Development);

        var options = provider.GetRequiredService<IOptions<DatabaseConnectionOptions>>().Value;

        Assert.Contains("Host=localhost", options.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("Database=hook2stream", options.ConnectionString, StringComparison.Ordinal);
    }

    private static ServiceProvider Services(
        Dictionary<string, string?> values,
        string environment)
    {
        var services = new ServiceCollection();
        services.AddHook2StreamInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new ConfigurationHostEnvironment(environment),
            includeBilling: false);
        return services.BuildServiceProvider();
    }

    private sealed class ConfigurationHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Hook2Stream.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
