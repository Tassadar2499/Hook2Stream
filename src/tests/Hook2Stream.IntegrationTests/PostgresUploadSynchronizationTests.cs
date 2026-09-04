using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresUploadSynchronizationTests
{
    [Fact]
    public async Task Same_part_is_idempotent_and_completed_upload_cannot_be_aborted()
    {
        await using var factory = new Hook2StreamApiFactory();
        using var client = factory.CreateClient();
        await Onboard(client);
        var projectId = await CreateRelease(client);
        var sessionId = await Reserve(client, projectId, sizeBytes: 1);

        using (var first = await client.SendAsync(PartRequest(sessionId, 1, [0x41])))
        using (var replay = await client.SendAsync(PartRequest(sessionId, 1, [0x41])))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
            var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                firstBody.GetProperty("sha256").GetString(),
                replayBody.GetProperty("sha256").GetString());
        }

        using var complete = await client.PostAsJsonAsync(
            $"/api/v1/uploads/{sessionId}/complete",
            new { });
        using var abort = await client.PostAsync(
            $"/api/v1/uploads/{sessionId}/abort",
            content: null);
        Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, abort.StatusCode);
    }

    [Fact]
    public async Task Parts_complete_and_abort_are_serialized_without_precommit_deletes()
    {
        var adminConnectionString =
            Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(
                    Environment.GetEnvironmentVariable("CI"),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
                "CI must provide HOOK2STREAM_TEST_POSTGRES for upload synchronization tests.");
            return;
        }

        var databaseName = $"hook2stream_upload_sync_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand(
                         $"CREATE DATABASE \"{databaseName}\"",
                         admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var migrationDb = CreateDb(connectionString))
            {
                await migrationDb.Database.MigrateAsync();
            }

            var storage = new CoordinatedObjectStorage(connectionString);
            await using var factory = new PostgresUploadApiFactory(
                connectionString,
                storage);
            using var client = factory.CreateClient();
            await Onboard(client);
            var projectId = await CreateRelease(client);

            var abortSession = await Reserve(client, projectId, sizeBytes: 1);
            storage.BlockPartUploads(expectedEntrants: 1);
            var partTask = client.SendAsync(PartRequest(abortSession, 1, [0x41]));
            await storage.WaitForBlockedPartsAsync(TimeSpan.FromSeconds(10));
            var abortTask = client.PostAsync(
                $"/api/v1/uploads/{abortSession}/abort",
                content: null);
            Assert.NotSame(
                abortTask,
                await Task.WhenAny(abortTask, Task.Delay(250)));

            storage.ReleasePartUploads();
            using (var partResponse = await partTask.WaitAsync(TimeSpan.FromSeconds(20)))
            using (var abortResponse = await abortTask.WaitAsync(TimeSpan.FromSeconds(20)))
            {
                Assert.Equal(HttpStatusCode.OK, partResponse.StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, abortResponse.StatusCode);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                Assert.Equal(
                    UploadState.Aborted,
                    await db.UploadSessions
                        .Where(value => value.Id == abortSession)
                        .Select(value => value.State)
                        .SingleAsync());
                Assert.Equal(
                    UploadPartState.Deleted,
                    await db.UploadParts
                        .Where(value => value.UploadSessionId == abortSession)
                        .Select(value => value.State)
                        .SingleAsync());
            }
            Assert.Contains(
                storage.PartDeleteObservations,
                value =>
                    value.SessionState == UploadState.Aborted &&
                    value.PartState == UploadPartState.Deleted);

            var parallelSession = await Reserve(client, projectId, sizeBytes: 2);
            await SetPartSize(factory, parallelSession, partSizeBytes: 1);
            storage.BlockPartUploads(expectedEntrants: 2);
            var firstPartTask = client.SendAsync(PartRequest(parallelSession, 1, [0x42]));
            var secondPartTask = client.SendAsync(PartRequest(parallelSession, 2, [0x43]));
            await storage.WaitForBlockedPartsAsync(TimeSpan.FromSeconds(10));
            storage.ReleasePartUploads();
            var parallelResponses = await Task.WhenAll(firstPartTask, secondPartTask)
                .WaitAsync(TimeSpan.FromSeconds(20));
            using (parallelResponses[0])
            using (parallelResponses[1])
            {
                Assert.All(
                    parallelResponses,
                    response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            }

            var completedSession = await Reserve(client, projectId, sizeBytes: 1);
            using (var partResponse = await client.SendAsync(
                       PartRequest(completedSession, 1, [0x44])))
            {
                Assert.Equal(HttpStatusCode.OK, partResponse.StatusCode);
            }
            storage.BlockFinalUpload();
            var completeTask = client.PostAsJsonAsync(
                $"/api/v1/uploads/{completedSession}/complete",
                new { });
            await storage.WaitForFinalUploadAsync(TimeSpan.FromSeconds(10));
            var losingAbortTask = client.PostAsync(
                $"/api/v1/uploads/{completedSession}/abort",
                content: null);
            Assert.NotSame(
                losingAbortTask,
                await Task.WhenAny(losingAbortTask, Task.Delay(250)));

            storage.ReleaseFinalUpload();
            using var completeResponse = await completeTask.WaitAsync(TimeSpan.FromSeconds(20));
            using var losingAbortResponse = await losingAbortTask.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(HttpStatusCode.Accepted, completeResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, losingAbortResponse.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                var completed = await db.UploadSessions
                    .AsNoTracking()
                    .SingleAsync(value => value.Id == completedSession);
                Assert.Equal(UploadState.Completed, completed.State);
                Assert.Equal(
                    UploadPartState.Committed,
                    await db.UploadParts
                        .Where(value => value.UploadSessionId == completedSession)
                        .Select(value => value.State)
                        .SingleAsync());
                Assert.True(storage.Contains(completed.ObjectKey));
            }
            Assert.Contains(
                storage.PartDeleteObservations,
                value =>
                    value.SessionState == UploadState.Completed &&
                    value.PartState == UploadPartState.Committed);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.EnableRetryOnFailure())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static async Task Onboard(HttpClient client)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            new
            {
                workspaceName = "Upload synchronization tests",
                acceptTerms = true,
                acceptPrivacy = true,
                termsVersion = "2026-09-04",
                privacyVersion = "2026-09-04",
                displayName = "Test artist"
            });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateRelease(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/releases",
            new
            {
                projectLabel = "Upload synchronization",
                artistName = "Test artist",
                trackTitle = "Test track",
                language = "en",
                internalNotes = (string?)null,
                lyricsText = "Test lyrics",
                isInstrumental = false,
                mode = "unscheduled",
                releaseDate = (DateOnly?)null,
                campaignStartDate = (DateOnly?)null
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> Reserve(
        HttpClient client,
        Guid projectId,
        long sizeBytes)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/releases/{projectId}/uploads",
            new
            {
                kind = "audio",
                fileName = $"upload-{Guid.NewGuid():N}.mp3",
                contentType = "audio/mpeg",
                sizeBytes,
                replacesAssetId = (Guid?)null
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("sessionId").GetGuid();
    }

    private static HttpRequestMessage PartRequest(
        Guid sessionId,
        int partNumber,
        byte[] bytes) =>
        new(HttpMethod.Put, $"/api/v1/uploads/{sessionId}/parts/{partNumber}")
        {
            Content = new ByteArrayContent(bytes)
        };

    private static async Task SetPartSize(
        PostgresUploadApiFactory factory,
        Guid sessionId,
        long partSizeBytes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
        var session = await db.UploadSessions.SingleAsync(value => value.Id == sessionId);
        session.PartSizeBytes = partSizeBytes;
        await db.SaveChangesAsync();
    }

    private sealed class PostgresUploadApiFactory(
        string connectionString,
        CoordinatedObjectStorage storage) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:Mode", "OAuth");
            builder.UseSetting("Storage:AccessKey", "test-access-key");
            builder.UseSetting("Storage:SecretKey", "test-secret-key");
            builder.UseSetting("StorageEncryption:Mode", "Plaintext");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
                services.RemoveAll<Hook2StreamDbContext>();
                services.AddDbContext<Hook2StreamDbContext>(options =>
                    options.UseNpgsql(
                            connectionString,
                            npgsql => npgsql.EnableRetryOnFailure())
                        .UseSnakeCaseNamingConvention());

                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(storage);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class CoordinatedObjectStorage(string connectionString) : IObjectStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new();
        private readonly ConcurrentQueue<PartDeleteObservation> _partDeleteObservations = new();
        private readonly object _barrierSync = new();
        private TaskCompletionSource _blockedPartsEntered = NewSignal();
        private TaskCompletionSource _releaseParts = CompletedSignal();
        private int _expectedPartEntrants;
        private int _partEntrants;
        private TaskCompletionSource _finalUploadEntered = NewSignal();
        private TaskCompletionSource _releaseFinalUpload = CompletedSignal();

        public IReadOnlyCollection<PartDeleteObservation> PartDeleteObservations =>
            _partDeleteObservations.ToArray();

        public bool Contains(string objectKey) => _objects.ContainsKey(objectKey);

        public void BlockPartUploads(int expectedEntrants)
        {
            lock (_barrierSync)
            {
                _expectedPartEntrants = expectedEntrants;
                _partEntrants = 0;
                _blockedPartsEntered = NewSignal();
                _releaseParts = NewSignal();
            }
        }

        public Task WaitForBlockedPartsAsync(TimeSpan timeout) =>
            _blockedPartsEntered.Task.WaitAsync(timeout);

        public void ReleasePartUploads() => _releaseParts.TrySetResult();

        public void BlockFinalUpload()
        {
            _finalUploadEntered = NewSignal();
            _releaseFinalUpload = NewSignal();
        }

        public Task WaitForFinalUploadAsync(TimeSpan timeout) =>
            _finalUploadEntered.Task.WaitAsync(timeout);

        public void ReleaseFinalUpload() => _releaseFinalUpload.TrySetResult();

        public Task EnsureBucketAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Uri> CreateUploadUrlAsync(
            string objectKey,
            string contentType,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw Disabled();

        public Task<Uri> CreateReadUrlAsync(
            string objectKey,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw Disabled();

        public Task<MultipartUpload> CreateMultipartUploadAsync(
            string objectKey,
            string contentType,
            CancellationToken cancellationToken) => throw Disabled();

        public Task<Uri> CreateMultipartPartUploadUrlAsync(
            string objectKey,
            string uploadId,
            int partNumber,
            TimeSpan lifetime,
            CancellationToken cancellationToken) => throw Disabled();

        public Task CompleteMultipartUploadAsync(
            string objectKey,
            string uploadId,
            IReadOnlyList<MultipartPart> parts,
            CancellationToken cancellationToken) => throw Disabled();

        public Task AbortMultipartUploadAsync(
            string objectKey,
            string uploadId,
            CancellationToken cancellationToken) => throw Disabled();

        public Task<StorageObjectInfo?> HeadAsync(
            string objectKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<StorageObjectInfo?>(_objects.TryGetValue(objectKey, out var bytes)
                ? new StorageObjectInfo(
                    bytes.LongLength,
                    Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(bytes))
                        .ToLowerInvariant(),
                    "application/octet-stream")
                : null);

        public Task DownloadAsync(
            string objectKey,
            string destinationPath,
            CancellationToken cancellationToken) =>
            File.WriteAllBytesAsync(
                destinationPath,
                _objects[objectKey],
                cancellationToken);

        public async Task UploadAsync(
            string objectKey,
            string sourcePath,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (objectKey.Contains("/parts/", StringComparison.Ordinal))
            {
                Task release;
                lock (_barrierSync)
                {
                    _partEntrants++;
                    if (_expectedPartEntrants > 0 && _partEntrants >= _expectedPartEntrants)
                    {
                        _blockedPartsEntered.TrySetResult();
                    }
                    release = _releaseParts.Task;
                }
                await release.WaitAsync(cancellationToken);
            }
            else if (!_releaseFinalUpload.Task.IsCompleted)
            {
                _finalUploadEntered.TrySetResult();
                await _releaseFinalUpload.Task.WaitAsync(cancellationToken);
            }

            _objects[objectKey] = await File.ReadAllBytesAsync(
                sourcePath,
                cancellationToken);
        }

        public async Task DeleteAsync(
            string objectKey,
            CancellationToken cancellationToken)
        {
            if (objectKey.Contains("/parts/", StringComparison.Ordinal))
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    """
                    SELECT s.state, p.state
                    FROM upload_parts AS p
                    INNER JOIN upload_sessions AS s ON s.id = p.upload_session_id
                    WHERE p.object_key = @object_key
                    """,
                    connection);
                command.Parameters.AddWithValue("object_key", objectKey);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                _partDeleteObservations.Enqueue(new PartDeleteObservation(
                    (UploadState)reader.GetInt32(0),
                    (UploadPartState)reader.GetInt32(1)));
            }
            _objects.TryRemove(objectKey, out _);
        }

        public Task DeleteProjectObjectsAsync(
            ProjectStorageScope scope,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAssetObjectsAsync(
            AssetStorageScope scope,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CopyToAsync(
            string objectKey,
            Stream destination,
            long offset,
            long? length,
            CancellationToken cancellationToken)
        {
            var bytes = _objects[objectKey];
            var count = checked((int)(length ?? bytes.LongLength - offset));
            return destination.WriteAsync(
                bytes.AsMemory(checked((int)offset), count),
                cancellationToken).AsTask();
        }

        private static NotSupportedException Disabled() =>
            new("Presigned and raw multipart storage are disabled in this test.");

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = NewSignal();
            signal.SetResult();
            return signal;
        }
    }

    private sealed record PartDeleteObservation(
        UploadState SessionState,
        UploadPartState PartState);
}
