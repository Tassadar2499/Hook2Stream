using Aspire.Hosting.ApplicationModel;
using Hook2Stream.Application;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var childEnvironment = builder.Environment.EnvironmentName;
var isE2e = string.Equals(
    builder.Configuration["HOOK2STREAM_E2E"],
    "1",
    StringComparison.OrdinalIgnoreCase);
var webPort = isE2e ? 3100 : 3000;
var webBaseUrl = isE2e
    ? $"http://127.0.0.1:{webPort}"
    : "http://localhost:3000";
var minioBrowserOrigins =
    "http://localhost:" + webPort + ",http://127.0.0.1:" + webPort;

var googleClientId = builder.Configuration["Google:ClientId"]?.Trim() ?? "";
var googleClientSecret = builder.Configuration["Google:ClientSecret"]?.Trim() ?? "";
var hasGoogleClientId = !string.IsNullOrWhiteSpace(googleClientId);
var hasGoogleClientSecret = !string.IsNullOrWhiteSpace(googleClientSecret);

if (hasGoogleClientId != hasGoogleClientSecret)
{
    throw new InvalidOperationException(
        "Google configuration is incomplete. Set both Google:ClientId and Google:ClientSecret, or remove both to use local Development authentication.");
}

var useLocalAuthentication = !hasGoogleClientId;
if (useLocalAuthentication &&
    !builder.Environment.IsDevelopment() &&
    !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException(
        "Google:ClientId and Google:ClientSecret are required outside the Development environment.");
}

IResourceBuilder<ParameterResource>? localAuthenticationToken = null;
if (useLocalAuthentication)
{
    localAuthenticationToken = isE2e
        ? builder.AddParameter(
            "local-auth-token",
            builder.Configuration["HOOK2STREAM_E2E_AUTH_TOKEN"]?.Trim()
                ?? "hook2stream-e2e-local-auth-token-20260725-fixed",
            secret: true)
        : builder.AddParameter(
            "local-auth-token",
            new GenerateParameterDefault
            {
                MinLength = 48,
                Lower = true,
                Upper = true,
                Numeric = true,
                Special = false,
                MinLower = 4,
                MinUpper = 4,
                MinNumeric = 4
            },
            secret: true);
}

var minioPassword = builder.AddParameter(
    "minio-password",
    new GenerateParameterDefault
    {
        MinLength = 32,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false,
        MinLower = 2,
        MinUpper = 2,
        MinNumeric = 2
    },
    secret: true,
    persist: !isE2e);

var postgresPassword = builder.AddParameter(
    "postgres-password",
    new GenerateParameterDefault
    {
        MinLength = 32,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false,
        MinLower = 2,
        MinUpper = 2,
        MinNumeric = 2
    },
    secret: true,
    persist: !isE2e);

var postgres = builder.AddPostgres("postgres", password: postgresPassword);
if (!isE2e)
{
    postgres.WithDataVolume();
}
var database = postgres.AddDatabase("hook2stream");

var minio = builder.AddContainer(
    "minio",
    "minio/minio",
    "RELEASE.2025-04-22T22-12-26Z");

minio
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", "hook2stream")
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
    .WithEnvironment(
        "MINIO_API_CORS_ALLOW_ORIGIN",
        minioBrowserOrigins)
    .WithHttpEndpoint(port: isE2e ? 9000 : null, targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithHttpHealthCheck("/minio/health/ready", endpointName: "s3");
if (!isE2e)
{
    minio.WithVolume(VolumeNameGenerator.Generate(minio, "data"), "/data");
}

var bootstrapper = builder
    .AddProject<Projects.Hook2Stream_Bootstrapper>("bootstrapper")
    .WithReference(database)
    .WithEnvironment("DOTNET_ENVIRONMENT", childEnvironment);
if (isE2e)
{
    bootstrapper
        .WithEnvironment("Storage__ServiceUrl", "http://localhost:9000")
        .WithEnvironment("Storage__PublicServiceUrl", "http://127.0.0.1:9000");
}
else
{
    bootstrapper
        .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
        .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"));
}
bootstrapper
    .WithEnvironment("StorageEncryption__Mode", "Plaintext")
    .WithEnvironment("Storage__AccessKey", "hook2stream")
    .WithEnvironment("Storage__SecretKey", minioPassword)
    .WithEnvironment("Storage__RequireCredentials", "true")
    .WithEnvironment("Storage__ConfigureBucketCors", "false")
    .WithEnvironment("Storage__ConfigureBucketLifecycle", "true")
    .WithEnvironment("Storage__ConfigureMultipartAbortLifecycle", "false")
    .WaitFor(database)
    .WaitFor(minio);

var api = builder
    .AddProject<Projects.Hook2Stream_Api>("api")
    .WithReference(database)
    .WithEnvironment("DOTNET_ENVIRONMENT", childEnvironment);
if (isE2e)
{
    api
        .WithEndpoint("http", endpoint => endpoint.Port = 5100)
        .WithEnvironment("Storage__ServiceUrl", "http://localhost:9000")
        .WithEnvironment("Storage__PublicServiceUrl", "http://127.0.0.1:9000");
}
else
{
    api
        .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
        .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"));
}
api
    .WithEnvironment("StorageEncryption__Mode", "Plaintext")
    .WithEnvironment("Storage__AccessKey", "hook2stream")
    .WithEnvironment("Storage__SecretKey", minioPassword)
    .WithEnvironment("Storage__RequireCredentials", "true")
    .WithEnvironment("Storage__ConfigureBucketCors", "false")
    .WithEnvironment("Auth__Mode", useLocalAuthentication ? "Local" : "OAuth")
    .WithEnvironment("Google__ClientId", googleClientId)
    .WithEnvironment("Google__ClientSecret", googleClientSecret)
    .WithHttpHealthCheck("/health/ready")
    .WaitForCompletion(bootstrapper);

api.WithEnvironment("Google__PublicApiBaseUrl", api.GetEndpoint("http"));
api.WithEnvironment("Google__PublicWebReturnBaseUrl", webBaseUrl);
api.WithEnvironment("Cors__Origins__0", webBaseUrl);
if (isE2e)
{
    api
        .WithEnvironment("Stripe__Mode", "Fixture")
        .WithEnvironment("Stripe__PublicWebBaseUrl", webBaseUrl)
        .WithEnvironment("OperationalPolicy__RetentionSweepEnabled", "false");
}

if (useLocalAuthentication)
{
    api.WithEnvironment("Auth__LocalToken", localAuthenticationToken!);
}

foreach (var capability in JobRoutingRegistry.Capabilities)
{
    var worker = builder
        .AddProject<Projects.Hook2Stream_Worker>($"worker-{capability}")
        .WithReference(database)
        .WithHttpEndpoint(name: "http")
        .WithEnvironment("DOTNET_ENVIRONMENT", childEnvironment);
    worker.WithEnvironment("StorageEncryption__Mode", "Plaintext");
    if (isE2e)
    {
        worker
            .WithEnvironment("Storage__ServiceUrl", "http://localhost:9000")
            .WithEnvironment("Storage__PublicServiceUrl", "http://127.0.0.1:9000")
            .WithEnvironment("PipelineProviders__AudioAnalysis__Mode", "Deterministic")
            .WithEnvironment("PipelineProviders__Transcription__Mode", "Fixture")
            .WithEnvironment("PipelineProviders__Artwork__Mode", "Fixture")
            .WithEnvironment("PipelineProviders__CampaignPlanning__Mode", "Fixture")
            .WithEnvironment("PipelineProviders__VideoRendering__Mode", "Deterministic")
            .WithEnvironment("OperationalPolicy__RetentionSweepEnabled", "false");
    }
    else
    {
        worker
            .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
            .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"));
        if (string.Equals(
                capability,
                JobRoutingRegistry.Control,
                StringComparison.OrdinalIgnoreCase))
        {
            worker.WithEnvironment(
                "OperationalPolicy__RetentionSweepEnabled",
                "true");
        }
    }
    worker
        .WithEnvironment("Storage__AccessKey", "hook2stream")
        .WithEnvironment("Storage__SecretKey", minioPassword)
        .WithEnvironment("Storage__RequireCredentials", "true")
        .WithEnvironment("Storage__ConfigureBucketCors", "false")
        .WithEnvironment("Worker__Capabilities__0", capability)
        .WithHttpHealthCheck("/health/ready")
        .WaitForCompletion(bootstrapper);
}

var web = builder
    .AddJavaScriptApp("web", "../web", isE2e ? "start" : "dev")
    .WithNpm(install: !isE2e, installCommand: "ci", installArgs: [])
    .WithHttpEndpoint(targetPort: webPort, port: webPort, env: "PORT", isProxied: false)
    .WithHttpHealthCheck("/")
    .WithEnvironment("NEXT_PUBLIC_API_BASE_URL", api.GetEndpoint("http"))
    .WithEnvironment(
        "NEXT_PUBLIC_AUTH_MODE",
        useLocalAuthentication ? "local" : "oauth")
    .WithReference(api)
    .WaitFor(api);

if (useLocalAuthentication)
{
    web.WithEnvironment("NEXT_PUBLIC_LOCAL_AUTH_TOKEN", localAuthenticationToken!);
}

builder.Build().Run();
