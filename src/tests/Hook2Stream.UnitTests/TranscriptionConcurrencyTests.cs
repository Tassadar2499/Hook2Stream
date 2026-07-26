using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;

namespace Hook2Stream.UnitTests;

public sealed class TranscriptionConcurrencyTests
{
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
        public async Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken)
        {
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
            Status = status;
            return Task.CompletedTask;
        }
    }
}
