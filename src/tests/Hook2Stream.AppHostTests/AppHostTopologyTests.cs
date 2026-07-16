using Aspire.Hosting.Testing;

namespace Hook2Stream.AppHostTests;

public sealed class AppHostTopologyTests
{
    [Fact]
    public async Task AppHost_declares_the_complete_local_topology()
    {
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Hook2Stream_AppHost>();

        var names = builder.Resources.Select(resource => resource.Name).ToHashSet();

        Assert.Contains("postgres", names);
        Assert.Contains("hook2stream", names);
        Assert.Contains("minio", names);
        Assert.Contains("bootstrapper", names);
        Assert.Contains("api", names);
        Assert.Contains("worker", names);
        Assert.Contains("web", names);
    }
}
