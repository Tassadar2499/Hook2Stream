using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Hook2Stream.Application;
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
        var childEnvironment = builder.Environment.EnvironmentName;

        Assert.Contains("postgres", resources.Keys);
        Assert.Contains("hook2stream", resources.Keys);
        Assert.Contains("minio", resources.Keys);
        Assert.Contains("bootstrapper", resources.Keys);
        Assert.Contains("api", resources.Keys);
        var workerNames = JobRoutingRegistry.Capabilities
            .Select(capability => $"worker-{capability}")
            .ToArray();
        Assert.All(workerNames, workerName => Assert.Contains(workerName, resources.Keys));
        Assert.DoesNotContain("worker", resources.Keys);
        Assert.Contains("web-installer", resources.Keys);
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
        var bootstrapperConfiguration = await ExecutionConfigurationBuilder
            .Create(resources["bootstrapper"])
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);
        var bootstrapperEnvironment = bootstrapperConfiguration.EnvironmentVariables.ToDictionary();
        var apiEnvironment = apiConfiguration.EnvironmentVariables.ToDictionary();
        var webEnvironment = webConfiguration.EnvironmentVariables.ToDictionary();

        Assert.Equal("Local", apiEnvironment["Auth__Mode"]);
        Assert.Equal(childEnvironment, bootstrapperEnvironment["DOTNET_ENVIRONMENT"]);
        Assert.Equal(childEnvironment, apiEnvironment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("false", bootstrapperEnvironment["Storage__ConfigureBucketCors"]);
        Assert.Equal("true", bootstrapperEnvironment["Storage__ConfigureBucketLifecycle"]);
        Assert.Equal(
            "false",
            bootstrapperEnvironment["Storage__ConfigureMultipartAbortLifecycle"]);
        Assert.DoesNotContain("Storage__BrowserUploadOrigins__0", bootstrapperEnvironment.Keys);
        Assert.DoesNotContain("Storage__BrowserUploadOrigins__1", bootstrapperEnvironment.Keys);
        Assert.Equal(
            "http://localhost:3000",
            apiEnvironment["Google__PublicWebReturnBaseUrl"]);
        Assert.Equal("http://localhost:3000", apiEnvironment["Cors__Origins__0"]);
        Assert.Equal("local", webEnvironment["NEXT_PUBLIC_AUTH_MODE"]);
        Assert.Equal(
            apiEnvironment["Auth__LocalToken"],
            webEnvironment["NEXT_PUBLIC_LOCAL_AUTH_TOKEN"]);

        foreach (var capability in JobRoutingRegistry.Capabilities)
        {
            var workerConfiguration = await ExecutionConfigurationBuilder
                .Create(resources[$"worker-{capability}"])
                .WithEnvironmentVariablesConfig()
                .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);
            var workerEnvironment = workerConfiguration.EnvironmentVariables.ToDictionary();
            Assert.Equal(childEnvironment, workerEnvironment["DOTNET_ENVIRONMENT"]);
            Assert.Equal(capability, workerEnvironment["Worker__Capabilities__0"]);
        }

        var postgresVolume = GetDataVolume(resources["postgres"]);
        var minioVolume = GetDataVolume(resources["minio"]);

        Assert.NotEqual("hook2stream-postgres-data", postgresVolume.Source);
        Assert.NotEqual("hook2stream-minio-data", minioVolume.Source);
        Assert.NotEqual(postgresVolume.Source, minioVolume.Source);
        Assert.EndsWith("-postgres-data", postgresVolume.Source, StringComparison.Ordinal);
        Assert.EndsWith("-minio-data", minioVolume.Source, StringComparison.Ordinal);

        Assert.Single(resources["minio"].Annotations.OfType<HealthCheckAnnotation>());
        Assert.Single(resources["api"].Annotations.OfType<HealthCheckAnnotation>());
        Assert.Single(resources["web"].Annotations.OfType<HealthCheckAnnotation>());
        Assert.All(
            workerNames,
            workerName => Assert.Single(
                resources[workerName].Annotations.OfType<HealthCheckAnnotation>()));

        AssertWaitGraph(
            resources["bootstrapper"],
            (resources["postgres"], WaitType.WaitUntilHealthy),
            (resources["hook2stream"], WaitType.WaitUntilHealthy),
            (resources["minio"], WaitType.WaitUntilHealthy));
        AssertWaitGraph(
            resources["api"],
            (resources["bootstrapper"], WaitType.WaitForCompletion));
        foreach (var workerName in workerNames)
        {
            AssertWaitGraph(
                resources[workerName],
                (resources["bootstrapper"], WaitType.WaitForCompletion));
        }

        AssertWaitGraph(
            resources["web"],
            (resources["web-installer"], WaitType.WaitForCompletion),
            (resources["api"], WaitType.WaitUntilHealthy));
    }

    private static ContainerMountAnnotation GetDataVolume(IResource resource)
    {
        Assert.True(resource.TryGetContainerMounts(out var mounts));
        return Assert.Single(mounts, mount => mount.Type == ContainerMountType.Volume);
    }

    private static void AssertWaitGraph(
        IResource resource,
        params (IResource Dependency, WaitType WaitType)[] expected)
    {
        var actual = resource.Annotations
            .OfType<WaitAnnotation>()
            .ToDictionary(annotation => annotation.Resource.Name);

        Assert.True(
            actual.Count == expected.Length,
            $"Unexpected waits for {resource.Name}: {string.Join(", ", actual.Select(pair => $"{pair.Key}:{pair.Value.WaitType}"))}");
        foreach (var (dependency, waitType) in expected)
        {
            var annotation = Assert.Contains(dependency.Name, actual);
            Assert.Same(dependency, annotation.Resource);
            Assert.Equal(waitType, annotation.WaitType);
        }
    }
}
