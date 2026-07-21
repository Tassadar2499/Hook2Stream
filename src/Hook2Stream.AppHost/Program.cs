using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

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
if (useLocalAuthentication && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Google:ClientId and Google:ClientSecret are required outside the Development environment.");
}

IResourceBuilder<ParameterResource>? localAuthenticationToken = null;
IResourceBuilder<ParameterResource>? jwtSigningKey = null;
if (useLocalAuthentication)
{
    localAuthenticationToken = builder.AddParameter(
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
else
{
    jwtSigningKey = builder.AddParameter(
        "jwt-signing-key",
        new GenerateParameterDefault
        {
            MinLength = 64,
            Lower = true,
            Upper = true,
            Numeric = true,
            Special = false,
            MinLower = 8,
            MinUpper = 8,
            MinNumeric = 8
        },
        secret: true,
        persist: true);
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
    persist: true);

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
    persist: true);

var postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume();
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
        "http://localhost:3000,http://127.0.0.1:3000")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithVolume(VolumeNameGenerator.Generate(minio, "data"), "/data")
    .WithHttpHealthCheck("/minio/health/ready", endpointName: "s3");

var bootstrapper = builder
    .AddProject<Projects.Hook2Stream_Bootstrapper>("bootstrapper")
    .WithReference(database)
    .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__AccessKey", "hook2stream")
    .WithEnvironment("Storage__SecretKey", minioPassword)
    .WithEnvironment("Storage__RequireCredentials", "true")
    .WithEnvironment("Storage__ConfigureBucketCors", "false")
    .WaitFor(database)
    .WaitFor(minio);

var api = builder
    .AddProject<Projects.Hook2Stream_Api>("api")
    .WithReference(database)
    .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__AccessKey", "hook2stream")
    .WithEnvironment("Storage__SecretKey", minioPassword)
    .WithEnvironment("Storage__RequireCredentials", "true")
    .WithEnvironment("Storage__ConfigureBucketCors", "false")
    .WithEnvironment("Auth__Mode", useLocalAuthentication ? "Local" : "OAuth")
    .WithEnvironment("Google__ClientId", googleClientId)
    .WithEnvironment("Google__ClientSecret", googleClientSecret)
    .WaitForCompletion(bootstrapper);

if (useLocalAuthentication)
{
    api.WithEnvironment("Auth__LocalToken", localAuthenticationToken!);
}
else
{
    api.WithEnvironment("Jwt__SigningKey", jwtSigningKey!);
}

builder
    .AddProject<Projects.Hook2Stream_Worker>("worker")
    .WithReference(database)
    .WithEnvironment("Storage__ServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__PublicServiceUrl", minio.GetEndpoint("s3"))
    .WithEnvironment("Storage__AccessKey", "hook2stream")
    .WithEnvironment("Storage__SecretKey", minioPassword)
    .WithEnvironment("Storage__RequireCredentials", "true")
    .WithEnvironment("Storage__ConfigureBucketCors", "false")
    .WaitForCompletion(bootstrapper);

var web = builder
    .AddJavaScriptApp("web", "../web", "dev")
    .WithHttpEndpoint(targetPort: 3000, port: 3000, env: "PORT", isProxied: false)
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
