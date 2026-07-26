using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hook2Stream.UnitTests;

public sealed class TranscriptionConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_commit_retries_concurrency_but_terminalizes_other_post_provider_failures(
        bool failResultCommit)
    {
        var concurrency = new OneShotConcurrencyInterceptor(throwIoFailure: failResultCommit);
        var databaseName = $"transcription-commit-race-{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();
        var plainOptions = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
            .Options;
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(databaseName, databaseRoot)
            .AddInterceptors(concurrency)
            .Options;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var artworkId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string fingerprint = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        await using var db = new Hook2StreamDbContext(options);
        var project = new ReleaseProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            ProjectLabel = "Commit race",
            ArtistName = "Artist",
            TrackTitle = "Track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            CurrentTranscriptRevisionId = revisionId
        };
        var asset = new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "track.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 1_024,
            ActualBytes = 1_024,
            ObjectKey = "tests/track.mp3",
            IsActive = true,
            Sha256 = fingerprint,
            DurationMilliseconds = 30_000
        };
        var automatic = new TranscriptRevision
        {
            Id = revisionId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Processing,
            Language = "en",
            SourceFingerprint = fingerprint
        };
        var job = new Job
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = assetId,
            Type = JobType.Transcription,
            State = JobState.Running,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "openrouter-stt-v1",
            InputFingerprint = fingerprint,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                projectId,
                assetId,
                transcriptRevisionId = revisionId
            }),
            AttemptCount = 1,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        db.AddRange(project, asset, automatic, job, new RightsAttestation
        {
            ProjectId = projectId,
            ActorSubject = "owner",
            PolicyVersion = "external-ai-zdr-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = assetId,
            AudioFingerprint = fingerprint,
            AcceptedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new CallbackTranscriptionProvider(async () =>
        {
            await using var concurrent = new Hook2StreamDbContext(plainOptions);
            var concurrentlyUpdated = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
            concurrentlyUpdated.CurrentArtworkPackRevisionId = artworkId;
            await concurrent.SaveChangesAsync();
        });
        var invocationWriter = new RecordingInvocationWriter();
        var leased = new LeasedJob(
            jobId,
            workspaceId,
            projectId,
            assetId,
            JobType.Transcription,
            job.PayloadJson,
            1,
            3,
            JobRoutingRegistry.Control,
            job.HandlerVersion,
            fingerprint,
            1,
            "worker-1",
            DateTimeOffset.UtcNow.AddMinutes(1),
            leaseToken);

        concurrency.Arm();
        var exception = await Record.ExceptionAsync(() =>
            new TranscriptionJobHandler(db, provider, invocationWriter)
                .ProcessAsync(leased, CancellationToken.None));

        await using var verify = new Hook2StreamDbContext(plainOptions);
        var savedProject = await verify.Projects.SingleAsync(value => value.Id == projectId);
        var savedRevision = await verify.TranscriptRevisions.SingleAsync(value => value.Id == revisionId);
        Assert.Equal(artworkId, savedProject.CurrentArtworkPackRevisionId);
        if (failResultCommit)
        {
            var handlerException = Assert.IsType<JobHandlerException>(exception);
            Assert.Equal("provider.result_processing_failed", handlerException.Code);
            Assert.False(handlerException.Retryable);
            Assert.Equal(RevisionState.Processing, savedRevision.State);
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal(RevisionState.ReadyForReview, savedRevision.State);
        }

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(failResultCommit ? 0 : 1, invocationWriter.CallCount);
        Assert.Equal(failResultCommit ? 1 : 2, concurrency.SaveCallCount);
    }

    [Fact]
    public async Task Confirmed_instrumental_job_rebinds_transcript_without_calling_provider()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"instrumental-race-{Guid.NewGuid():N}")
            .Options;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string fingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        await using var db = new Hook2StreamDbContext(options);
        var project = new ReleaseProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            ProjectLabel = "Instrumental race",
            ArtistName = "Artist",
            TrackTitle = "Track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            IsInstrumental = true,
            IsInstrumentalConfirmed = true,
            CurrentTranscriptRevisionId = revisionId
        };
        var asset = new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "instrumental.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 1_024,
            ActualBytes = 1_024,
            ObjectKey = "tests/instrumental.mp3",
            IsActive = true,
            Sha256 = fingerprint,
            DurationMilliseconds = 180_000
        };
        var automatic = new TranscriptRevision
        {
            Id = revisionId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Processing,
            Language = "en",
            SourceFingerprint = fingerprint
        };
        var job = new Job
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = assetId,
            Type = JobType.Transcription,
            State = JobState.Running,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "openrouter-stt-v1",
            InputFingerprint = fingerprint,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                projectId,
                assetId,
                transcriptRevisionId = revisionId
            }),
            AttemptCount = 1,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        db.AddRange(project, asset, automatic, job);
        await db.SaveChangesAsync();

        var provider = new RecordingTranscriptionProvider();
        var handler = new TranscriptionJobHandler(db, provider, new RecordingInvocationWriter());
        var leased = new LeasedJob(
            jobId,
            workspaceId,
            projectId,
            assetId,
            JobType.Transcription,
            job.PayloadJson,
            1,
            3,
            JobRoutingRegistry.Control,
            job.HandlerVersion,
            fingerprint,
            1,
            "worker-1",
            DateTimeOffset.UtcNow.AddMinutes(1),
            leaseToken);

        await handler.ProcessAsync(leased, CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(RevisionState.Superseded, automatic.State);
        var rebound = await db.TranscriptRevisions.SingleAsync(
            value => value.Id == project.CurrentTranscriptRevisionId);
        Assert.Equal(TranscriptSource.Instrumental, rebound.Source);
        Assert.Equal(RevisionState.Approved, rebound.State);
        Assert.Equal("[]", rebound.PhrasesJson);
        Assert.Equal(fingerprint, rebound.SourceFingerprint);
    }

    [Fact]
    public async Task Late_provider_result_cannot_replace_manual_revision()
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase($"transcription-race-{Guid.NewGuid():N}")
            .Options;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var assetId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        await using var db = new Hook2StreamDbContext(options);
        var project = new ReleaseProject
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            ProjectLabel = "Race",
            ArtistName = "Artist",
            TrackTitle = "Track",
            Language = "en",
            FlowKind = FlowKind.Mp3First,
            CurrentTranscriptRevisionId = revisionId
        };
        var asset = new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Kind = AssetKind.Audio,
            Origin = AssetOrigin.Uploaded,
            Purpose = AssetPurpose.AudioMaster,
            State = AssetState.Ready,
            OriginalFileName = "track.mp3",
            DeclaredContentType = "audio/mpeg",
            DetectedContentType = "audio/mpeg",
            DeclaredBytes = 1_024,
            ActualBytes = 1_024,
            ObjectKey = "tests/track.mp3",
            IsActive = true,
            Sha256 = fingerprint,
            DurationMilliseconds = 180_000
        };
        var automatic = new TranscriptRevision
        {
            Id = revisionId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Processing,
            Language = "en",
            SourceFingerprint = fingerprint
        };
        var job = new Job
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            AssetId = assetId,
            Type = JobType.Transcription,
            State = JobState.Running,
            RequiredCapability = JobRoutingRegistry.Control,
            HandlerVersion = "openrouter-stt-v1",
            InputFingerprint = fingerprint,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                projectId,
                assetId,
                transcriptRevisionId = revisionId
            }),
            AttemptCount = 1,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        db.AddRange(project, asset, automatic, job, new RightsAttestation
        {
            ProjectId = projectId,
            ActorSubject = "owner",
            PolicyVersion = "external-ai-zdr-v1",
            OwnsAudioRights = true,
            OwnsLyricsRights = true,
            AllowsExternalAiProcessing = true,
            AudioAssetId = assetId,
            AudioFingerprint = fingerprint,
            AcceptedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new CallbackTranscriptionProvider(async () =>
        {
            await using var concurrent = new Hook2StreamDbContext(options);
            var concurrentProject = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
            var concurrentAutomatic = await concurrent.TranscriptRevisions.SingleAsync(value => value.Id == revisionId);
            var concurrentJob = await concurrent.Jobs.SingleAsync(value => value.Id == jobId);
            var manual = new TranscriptRevision
            {
                WorkspaceId = workspaceId,
                ProjectId = projectId,
                Number = 2,
                Source = TranscriptSource.Manual,
                State = RevisionState.ReadyForReview,
                Language = "en",
                PhrasesJson = "[]",
                SourceFingerprint = fingerprint,
                SupersedesRevisionId = revisionId
            };
            concurrentAutomatic.State = RevisionState.Superseded;
            concurrentProject.CurrentTranscriptRevisionId = manual.Id;
            concurrentJob.State = JobState.Cancelled;
            concurrentJob.LeaseOwner = null;
            concurrentJob.LeaseToken = null;
            concurrentJob.LeaseExpiresAt = null;
            concurrent.TranscriptRevisions.Add(manual);
            await concurrent.SaveChangesAsync();
        });
        var invocationWriter = new RecordingInvocationWriter();
        var handler = new TranscriptionJobHandler(db, provider, invocationWriter);
        var leased = new LeasedJob(
            jobId,
            workspaceId,
            projectId,
            assetId,
            JobType.Transcription,
            job.PayloadJson,
            1,
            3,
            JobRoutingRegistry.Control,
            job.HandlerVersion,
            fingerprint,
            1,
            "worker-1",
            DateTimeOffset.UtcNow.AddMinutes(1),
            leaseToken);

        await handler.ProcessAsync(leased, CancellationToken.None);

        await using var verify = new Hook2StreamDbContext(options);
        var currentId = await verify.Projects.Where(value => value.Id == projectId)
            .Select(value => value.CurrentTranscriptRevisionId)
            .SingleAsync();
        var current = await verify.TranscriptRevisions.SingleAsync(value => value.Id == currentId);
        Assert.Equal(TranscriptSource.Manual, current.Source);
        Assert.Equal(RevisionState.ReadyForReview, current.State);
        Assert.Equal(AiProviderInvocationLedger.DiscardedStaleInput, invocationWriter.Status);
    }

    private sealed class CallbackTranscriptionProvider(Func<Task> callback) : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public async Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await callback();
            return await new FixtureTranscriptionProvider(TimeProvider.System)
                .TranscribeAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingTranscriptionProvider : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The provider must not be called for a confirmed instrumental release.");
        }
    }

    private sealed class RecordingInvocationWriter : IAiProviderInvocationWriter
    {
        public int CallCount { get; private set; }
        public string? Status { get; private set; }

        public Task RecordAsync(
            LeasedJob job,
            string stage,
            ProviderExecutionContext context,
            ProviderProvenance provenance,
            ProviderFailure? failure,
            string? status,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Status = status;
            return Task.CompletedTask;
        }
    }

    private sealed class OneShotConcurrencyInterceptor(bool throwIoFailure = false) : SaveChangesInterceptor
    {
        private int _remainingFailures;

        public int SaveCallCount { get; private set; }

        public void Arm()
        {
            SaveCallCount = 0;
            Volatile.Write(ref _remainingFailures, 1);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                if (throwIoFailure)
                {
                    throw new IOException("Injected provider-result commit failure.");
                }

                throw new DbUpdateConcurrencyException("Injected result-commit conflict.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
