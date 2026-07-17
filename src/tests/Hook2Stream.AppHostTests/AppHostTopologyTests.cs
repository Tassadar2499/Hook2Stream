using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hook2Stream.AppHostTests;

public sealed class AppHostTopologyTests
{
    [Fact]
    public async Task AppHost_declares_the_complete_local_topology()
    {
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.Hook2Stream_AppHost>();

        var resources = builder.Resources.ToDictionary(resource => resource.Name);

        Assert.Contains("postgres", resources.Keys);
        Assert.Contains("hook2stream", resources.Keys);
        Assert.Contains("minio", resources.Keys);
        Assert.Contains("bootstrapper", resources.Keys);
        Assert.Contains("api", resources.Keys);
        Assert.Contains("worker", resources.Keys);
        Assert.Contains("web", resources.Keys);

        var postgresPassword = Assert.IsType<ParameterResource>(resources["postgres-password"]);
        var minioPassword = Assert.IsType<ParameterResource>(resources["minio-password"]);
        var localAuthToken = Assert.IsType<ParameterResource>(resources["local-auth-token"]);
        Assert.True(postgresPassword.Secret);
        Assert.True(minioPassword.Secret);
        Assert.True(localAuthToken.Secret);
        Assert.Equal("UserSecretsParameterDefault", postgresPassword.Default!.GetType().Name);
        Assert.Equal("UserSecretsParameterDefault", minioPassword.Default!.GetType().Name);
        Assert.IsType<GenerateParameterDefault>(localAuthToken.Default);

        var executionContext = new DistributedApplicationExecutionContext(
            DistributedApplicationOperation.Publish);
        var apiConfiguration = await ExecutionConfigurationBuilder
            .Create(resources["api"])
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);
        var webConfiguration = await ExecutionConfigurationBuilder
            .Create(resources["web"])
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);
        var apiEnvironment = apiConfiguration.EnvironmentVariables.ToDictionary();
        var webEnvironment = webConfiguration.EnvironmentVariables.ToDictionary();

        Assert.Equal("Local", apiEnvironment["Auth__Mode"]);
        Assert.Equal("local", webEnvironment["NEXT_PUBLIC_AUTH_MODE"]);
        Assert.Equal(
            apiEnvironment["Auth__LocalToken"],
            webEnvironment["NEXT_PUBLIC_LOCAL_AUTH_TOKEN"]);

        var postgresVolume = GetDataVolume(resources["postgres"]);
        var minioVolume = GetDataVolume(resources["minio"]);

        Assert.NotEqual("hook2stream-postgres-data", postgresVolume.Source);
        Assert.NotEqual("hook2stream-minio-data", minioVolume.Source);
        Assert.NotEqual(postgresVolume.Source, minioVolume.Source);
        Assert.EndsWith("-postgres-data", postgresVolume.Source, StringComparison.Ordinal);
        Assert.EndsWith("-minio-data", minioVolume.Source, StringComparison.Ordinal);

        Assert.Single(resources["minio"].Annotations.OfType<HealthCheckAnnotation>());

        var minioWait = Assert.Single(
            resources["bootstrapper"].Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, resources["minio"]));
        Assert.Equal(WaitType.WaitUntilHealthy, minioWait.WaitType);
    }

    private static ContainerMountAnnotation GetDataVolume(IResource resource)
    {
        Assert.True(resource.TryGetContainerMounts(out var mounts));
        return Assert.Single(mounts, mount => mount.Type == ContainerMountType.Volume);
    }
}
