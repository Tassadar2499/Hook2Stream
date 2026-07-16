using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

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
    .WithVolume(VolumeNameGenerator.Generate(minio, "data"), "/data");

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
    .WithEnvironment("Clerk__Issuer", builder.Configuration["Clerk:Issuer"] ?? "")
    .WaitForCompletion(bootstrapper);

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

builder
    .AddJavaScriptApp("web", "../web", "dev")
    .WithHttpEndpoint(targetPort: 3000, port: 3000, env: "PORT", isProxied: false)
    .WithEnvironment("NEXT_PUBLIC_API_BASE_URL", api.GetEndpoint("http"))
    .WithEnvironment(
        "NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY",
        builder.Configuration["Clerk:PublishableKey"] ?? "")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
