using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Hook2Stream.Infrastructure.Pipeline;
using Hook2Stream.Infrastructure.Providers;
using Hook2Stream.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class AiHandlerCommitConcurrencyTests
{
    [Fact]
    public async Task Artwork_commit_retry_and_audit_failure_do_not_repeat_provider_or_delete_committed_objects()
    {
        var concurrency = new OneShotConcurrencyInterceptor();
        var database = DatabaseOptions("artwork", concurrency);
        var options = database.Intercepted;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var audioId = Guid.CreateVersion7();
        var packId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string audioFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string packFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        await using var db = new Hook2StreamDbContext(options);
        var project = Project(workspaceId, projectId);
        project.CurrentArtworkPackRevisionId = packId;
        var audio = Audio(workspaceId, projectId, audioId, audioFingerprint);
        var pack = new ArtworkPackRevision
        {
            Id = packId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Processing,
            Prompt = "Text-free cover",
            SourceFingerprint = packFingerprint
        };
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            artworkPackRevisionId = packId,
            prompt = pack.Prompt,
            style = "test",
            count = 3
        });
        var job = RunningJob(
            jobId,
            workspaceId,
            projectId,
            JobType.ArtworkGeneration,
            payload,
            packFingerprint,
            leaseToken,
            "openrouter-image-v1");
        db.AddRange(
            project,
            audio,
            pack,
            job,
            Rights(projectId, audioId, audioFingerprint),
            new BrandKit { WorkspaceId = workspaceId });
        await db.SaveChangesAsync();

        var provider = new CallbackArtworkProvider(async () =>
        {
            await using var concurrent = new Hook2StreamDbContext(database.Plain);
            var concurrentlyUpdated = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
            concurrentlyUpdated.InternalNotes = "saved while artwork provider was running";
            await concurrent.SaveChangesAsync();
        });
        var artifactStore = new RecordingArtifactStore();
        var storage = new TestStorage();
        var invocationWriter = new ThrowingInvocationWriter();
        var handler = new ArtworkGenerationJobHandler(
            db,
            provider,
            artifactStore,
            storage,
            new MaterializingProcessRunner(),
            Options.Create(new MediaToolsOptions()),
            TimeProvider.System,
            new RejectingCoverComposer(),
            invocationWriter);

        concurrency.Arm();
        await handler.ProcessAsync(
            Lease(job, JobType.ArtworkGeneration, packFingerprint, leaseToken, attemptNumber: 3),
            CancellationToken.None);

        await using var verify = new Hook2StreamDbContext(database.Plain);
        var savedProject = await verify.Projects.SingleAsync(value => value.Id == projectId);
        var savedPack = await verify.ArtworkPackRevisions.SingleAsync(value => value.Id == packId);
        Assert.Equal("saved while artwork provider was running", savedProject.InternalNotes);
        Assert.Equal(RevisionState.ReadyForReview, savedPack.State);
        Assert.Equal(3, JsonSerializer.Deserialize<Guid[]>(savedPack.CandidateAssetIdsJson)!.Length);
        Assert.Equal(3, await verify.MediaAssets.CountAsync(value => value.Purpose == AssetPurpose.CoverCandidate));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(3, artifactStore.PromoteCount);
        Assert.Equal(1, invocationWriter.CallCount);
        Assert.Equal(2, concurrency.SaveCallCount);
        Assert.Empty(storage.Deleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Artwork_rejected_prepare_or_precommit_failure_deletes_every_owned_object(
        int failureStage)
    {
        var failDuringPrepare = failureStage == 1;
        var rejectProviderResult = failureStage == 2;
        var revokeSchedule = failureStage == 3;
        var ambiguousCommittedSave = failureStage == 4;
        var failure = new OneShotIoFailureInterceptor(throwAfterSave: ambiguousCommittedSave);
        var database = DatabaseOptions("artwork-precommit-failure", failure);
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var audioId = Guid.CreateVersion7();
        var packId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string audioFingerprint = "1111111111111111111111111111111111111111111111111111111111111111";
        const string packFingerprint = "2222222222222222222222222222222222222222222222222222222222222222";

        await using var db = new Hook2StreamDbContext(database.Intercepted);
        var project = Project(workspaceId, projectId);
        project.CurrentArtworkPackRevisionId = packId;
        var pack = new ArtworkPackRevision
        {
            Id = packId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Processing,
            Prompt = "Text-free cover",
            SourceFingerprint = packFingerprint
        };
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            artworkPackRevisionId = packId,
            prompt = pack.Prompt,
            style = "test",
            count = 3
        });
        var job = RunningJob(
            jobId,
            workspaceId,
            projectId,
            JobType.ArtworkGeneration,
            payload,
            packFingerprint,
            leaseToken,
            "openrouter-image-v1");
        db.AddRange(
            project,
            Audio(workspaceId, projectId, audioId, audioFingerprint),
            pack,
            job,
            Rights(projectId, audioId, audioFingerprint),
            new BrandKit { WorkspaceId = workspaceId });
        await db.SaveChangesAsync();

        var successfulProvider = new CallbackArtworkProvider(async () =>
        {
            if (!revokeSchedule) return;
            await using var concurrent = new Hook2StreamDbContext(database.Plain);
            var currentProject = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
            currentProject.ReleaseDate = null;
            await concurrent.SaveChangesAsync();
        });
        var rejectedProvider = new IncompleteArtworkProvider();
        IArtworkProvider provider = rejectProviderResult ? rejectedProvider : successfulProvider;
        var artifactStore = new RecordingArtifactStore(failDuringPrepare ? 2 : null);
        var storage = new TestStorage();
        var invocationWriter = new RecordingInvocationWriter();
        var handler = new ArtworkGenerationJobHandler(
            db,
            provider,
            artifactStore,
            storage,
            new MaterializingProcessRunner(),
            Options.Create(new MediaToolsOptions()),
            TimeProvider.System,
            new RejectingCoverComposer(),
            invocationWriter);

        if (failureStage is 0 or 4)
        {
            failure.Arm();
        }

        var exception = await Record.ExceptionAsync(() => handler.ProcessAsync(
            Lease(job, JobType.ArtworkGeneration, packFingerprint, leaseToken),
            CancellationToken.None));

        await using var verify = new Hook2StreamDbContext(database.Plain);
        if (ambiguousCommittedSave)
        {
            Assert.Null(exception);
            Assert.Equal(RevisionState.ReadyForReview, (await verify.ArtworkPackRevisions.SingleAsync()).State);
            Assert.Equal(3, await verify.MediaAssets.CountAsync(value => value.Purpose == AssetPurpose.CoverCandidate));
            Assert.Equal(0, invocationWriter.CallCount);
        }
        else if (revokeSchedule)
        {
            Assert.Equal("release.schedule_required", Assert.IsType<JobBlockedException>(exception).ReasonCode);
            Assert.Equal(RevisionState.Processing, (await verify.ArtworkPackRevisions.SingleAsync()).State);
        }
        else
        {
            var handlerException = Assert.IsType<JobHandlerException>(exception);
            Assert.Equal(
                rejectProviderResult ? "artwork.candidate_batch_incomplete" : "provider.result_processing_failed",
                handlerException.Code);
            Assert.False(handlerException.Retryable);
            Assert.Equal(RevisionState.Failed, (await verify.ArtworkPackRevisions.SingleAsync()).State);
        }

        if (!ambiguousCommittedSave)
        {
            Assert.Empty(await verify.MediaAssets.Where(value => value.Purpose == AssetPurpose.CoverCandidate).ToListAsync());
        }

        Assert.Equal(1, successfulProvider.CallCount + rejectedProvider.CallCount);
        var expectedOwnedObjects = revokeSchedule || ambiguousCommittedSave
            ? 0
            : rejectProviderResult ? 2 : failDuringPrepare ? 4 : 9;
        Assert.Equal(
            revokeSchedule || rejectProviderResult ? 0 : failDuringPrepare ? 2 : 3,
            artifactStore.PromoteCount);
        Assert.Equal(expectedOwnedObjects, storage.Deleted.Count);
        Assert.Equal(expectedOwnedObjects, storage.Deleted.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Campaign_commit_retries_locally_and_rejects_stale_or_uncommittable_provider_results(
        int scenario)
    {
        var invalidateArtworkDependency = scenario == 1;
        var failResultCommit = scenario == 2;
        var invalidateMutableProjectInput = scenario == 3;
        var concurrency = new OneShotConcurrencyInterceptor(throwIoFailure: failResultCommit);
        var database = DatabaseOptions("campaign", concurrency);
        var options = database.Intercepted;
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var audioId = Guid.CreateVersion7();
        var transcriptId = Guid.CreateVersion7();
        var hookSetId = Guid.CreateVersion7();
        var artworkId = Guid.CreateVersion7();
        var campaignId = Guid.CreateVersion7();
        var coverId = Guid.CreateVersion7();
        var backgroundIds = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var jobId = Guid.CreateVersion7();
        var leaseToken = Guid.CreateVersion7();
        const string audioFingerprint = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        await using var db = new Hook2StreamDbContext(options);
        var project = Project(workspaceId, projectId);
        project.CurrentTranscriptRevisionId = transcriptId;
        project.CurrentHookSetRevisionId = hookSetId;
        project.CurrentArtworkPackRevisionId = artworkId;
        project.CurrentCampaignPlanRevisionId = campaignId;
        var audio = Audio(workspaceId, projectId, audioId, audioFingerprint);
        var hooks = new[]
        {
            new HookRequest("verse", "verse", 0, 10_000, "Verse"),
            new HookRequest("chorus", "chorus", 10_000, 20_000, "Chorus"),
            new HookRequest("bridge", "bridge", 20_000, 30_000, "Bridge")
        };
        var hookSet = new HookSetRevision
        {
            Id = hookSetId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            State = RevisionState.Approved,
            TranscriptRevisionId = transcriptId,
            HooksJson = JsonSerializer.Serialize(hooks),
            SourceFingerprint = audioFingerprint
        };
        var transcript = new TranscriptRevision
        {
            Id = transcriptId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            Source = TranscriptSource.Automatic,
            State = RevisionState.Approved,
            Language = "en",
            PhrasesJson = "[]",
            SourceFingerprint = audioFingerprint
        };
        var artwork = new ArtworkPackRevision
        {
            Id = artworkId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            OperationNumber = 1,
            State = RevisionState.Approved,
            SelectedAssetId = coverId,
            CandidateAssetIdsJson = JsonSerializer.Serialize(new[] { coverId }),
            BackgroundAssetIdsJson = JsonSerializer.Serialize(backgroundIds),
            SourceFingerprint = audioFingerprint,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        var campaignFingerprint = PipelineHandlerData.CampaignFingerprint(
            project,
            transcript,
            artwork,
            hookSet,
            brandVersion: 1);
        var campaign = new CampaignPlanRevision
        {
            Id = campaignId,
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Number = 1,
            State = RevisionState.Processing,
            TranscriptRevisionId = transcriptId,
            ArtworkPackRevisionId = artworkId,
            HookSetRevisionId = hookSetId,
            SourceFingerprint = campaignFingerprint
        };
        var brand = new BrandKit { WorkspaceId = workspaceId };
        var payload = JsonSerializer.Serialize(new
        {
            projectId,
            campaignRevisionId = campaignId,
            brandKitVersion = 1
        });
        var job = RunningJob(
            jobId,
            workspaceId,
            projectId,
            JobType.CampaignGeneration,
            payload,
            campaignFingerprint,
            leaseToken,
            "openrouter-campaign-v1");
        db.AddRange(
            project,
            audio,
            transcript,
            hookSet,
            artwork,
            campaign,
            brand,
            job,
            Rights(projectId, audioId, audioFingerprint),
            Visual(workspaceId, projectId, coverId, AssetPurpose.ApprovedCover, 0));
        db.AddRange(backgroundIds.Select((id, index) =>
            Visual(workspaceId, projectId, id, AssetPurpose.CampaignBackground, index + 1)));
        await db.SaveChangesAsync();

        var provider = new CallbackCampaignPlanner(async () =>
        {
            await using var concurrent = new Hook2StreamDbContext(database.Plain);
            if (invalidateArtworkDependency)
            {
                var concurrentlyUpdated = await concurrent.ArtworkPackRevisions.SingleAsync(value => value.Id == artworkId);
                concurrentlyUpdated.SelectedAssetId = null;
            }
            else if (invalidateMutableProjectInput)
            {
                var concurrentlyUpdated = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
                concurrentlyUpdated.TrackTitle = "Changed while campaign provider was running";
            }
            else
            {
                var concurrentlyUpdated = await concurrent.Projects.SingleAsync(value => value.Id == projectId);
                concurrentlyUpdated.InternalNotes = "saved while campaign provider was running";
            }

            await concurrent.SaveChangesAsync();
        });
        var invocationWriter = new RecordingInvocationWriter();

        concurrency.Arm();
        var exception = await Record.ExceptionAsync(() =>
            new CampaignGenerationJobHandler(db, provider, invocationWriter).ProcessAsync(
            Lease(job, JobType.CampaignGeneration, campaignFingerprint, leaseToken),
            CancellationToken.None));

        await using var verify = new Hook2StreamDbContext(database.Plain);
        var savedProject = await verify.Projects.SingleAsync(value => value.Id == projectId);
        var savedCampaign = await verify.CampaignPlanRevisions.SingleAsync(value => value.Id == campaignId);
        if (failResultCommit)
        {
            var handlerException = Assert.IsType<JobHandlerException>(exception);
            Assert.Equal("provider.result_processing_failed", handlerException.Code);
            Assert.False(handlerException.Retryable);
            Assert.NotEqual(ProjectState.CampaignReady, savedProject.State);
            Assert.Equal(RevisionState.Processing, savedCampaign.State);
            Assert.Equal(0, invocationWriter.CallCount);
            Assert.Equal(1, concurrency.SaveCallCount);
        }
        else if (invalidateArtworkDependency || invalidateMutableProjectInput)
        {
            Assert.Null(exception);
            Assert.NotEqual(ProjectState.CampaignReady, savedProject.State);
            Assert.Equal(RevisionState.Processing, savedCampaign.State);
            Assert.Equal(AiProviderInvocationLedger.DiscardedStaleInput, invocationWriter.Status);
            Assert.Equal(0, concurrency.SaveCallCount);
        }
        else
        {
            Assert.Null(exception);
            Assert.Equal("saved while campaign provider was running", savedProject.InternalNotes);
            Assert.Equal(ProjectState.CampaignReady, savedProject.State);
            Assert.Equal(RevisionState.ReadyForReview, savedCampaign.State);
            Assert.Equal(18, JsonSerializer.Deserialize<CampaignItemRequest[]>(savedCampaign.ItemsJson)!.Length);
            Assert.Equal(2, concurrency.SaveCallCount);
        }

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(failResultCommit ? 0 : 1, invocationWriter.CallCount);
    }

    private static TestDatabase DatabaseOptions(
        string stage,
        SaveChangesInterceptor interceptor)
    {
        var name = $"{stage}-commit-race-{Guid.NewGuid():N}";
        var root = new InMemoryDatabaseRoot();
        var plain = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(name, root)
            .Options;
        var intercepted = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseInMemoryDatabase(name, root)
            .AddInterceptors(interceptor)
            .Options;
        return new TestDatabase(plain, intercepted);
    }

    private sealed record TestDatabase(
        DbContextOptions<Hook2StreamDbContext> Plain,
        DbContextOptions<Hook2StreamDbContext> Intercepted);

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

    private sealed class OneShotIoFailureInterceptor(bool throwAfterSave = false) : SaveChangesInterceptor
    {
        private int _remainingFailures;

        public void Arm() => Volatile.Write(ref _remainingFailures, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!throwAfterSave && Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                throw new IOException("Injected retryable result-commit failure.");
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (throwAfterSave && Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            {
                throw new IOException("Injected ambiguous post-commit failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private static ReleaseProject Project(Guid workspaceId, Guid projectId) => new()
    {
        Id = projectId,
        WorkspaceId = workspaceId,
        ProjectLabel = "AI commit race",
        ArtistName = "Artist",
        TrackTitle = "Track",
        Language = "en",
        FlowKind = FlowKind.Mp3First,
        SetupCompletedAt = DateTimeOffset.UtcNow,
        Mode = ReleaseMode.Upcoming,
        ReleaseDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7)
    };

    private static MediaAsset Audio(
        Guid workspaceId,
        Guid projectId,
        Guid assetId,
        string fingerprint) => new()
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

    private static MediaAsset Visual(
        Guid workspaceId,
        Guid projectId,
        Guid assetId,
        AssetPurpose purpose,
        int sortOrder) => new()
    {
        Id = assetId,
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        Kind = purpose == AssetPurpose.CampaignBackground ? AssetKind.Visual : AssetKind.Cover,
        Origin = AssetOrigin.Generated,
        Purpose = purpose,
        State = AssetState.Ready,
        OriginalFileName = $"{assetId:N}.png",
        DeclaredContentType = "image/png",
        DetectedContentType = "image/png",
        DeclaredBytes = 1_024,
        ActualBytes = 1_024,
        ObjectKey = $"tests/{assetId:N}.png",
        IsActive = true,
        SortOrder = sortOrder,
        Sha256 = new string((char)('e' + sortOrder), 64),
        Width = 1088,
        Height = 1920
    };

    private static RightsAttestation Rights(Guid projectId, Guid audioId, string fingerprint) => new()
    {
        ProjectId = projectId,
        ActorSubject = "owner",
        PolicyVersion = "external-ai-zdr-v1",
        OwnsAudioRights = true,
        OwnsLyricsRights = true,
        OwnsVisualRights = true,
        AllowsExternalAiProcessing = true,
        AudioAssetId = audioId,
        AudioFingerprint = fingerprint,
        AcceptedAt = DateTimeOffset.UtcNow
    };

    private static Job RunningJob(
        Guid jobId,
        Guid workspaceId,
        Guid projectId,
        JobType type,
        string payload,
        string fingerprint,
        Guid leaseToken,
        string handlerVersion) => new()
    {
        Id = jobId,
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        Type = type,
        State = JobState.Running,
        RequiredCapability = JobRoutingRegistry.Control,
        HandlerVersion = handlerVersion,
        InputFingerprint = fingerprint,
        PayloadJson = payload,
        AttemptCount = 1,
        LeaseOwner = "worker-1",
        LeaseToken = leaseToken,
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2)
    };

    private static LeasedJob Lease(
        Job job,
        JobType type,
        string fingerprint,
        Guid leaseToken,
        int attemptNumber = 1) => new(
        job.Id,
        job.WorkspaceId,
        job.ProjectId,
        job.AssetId,
        type,
        job.PayloadJson,
        attemptNumber,
        3,
        JobRoutingRegistry.Control,
        job.HandlerVersion,
        fingerprint,
        1,
        "worker-1",
        DateTimeOffset.UtcNow.AddMinutes(2),
        leaseToken);

    private sealed class CallbackArtworkProvider(Func<Task> callback) : IArtworkProvider
    {
        public int CallCount { get; private set; }

        public async Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
            ArtworkGenerationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await callback();
            return await new FixtureArtworkProvider(TimeProvider.System)
                .GenerateAsync(request, cancellationToken);
        }
    }

    private sealed class IncompleteArtworkProvider : IArtworkProvider
    {
        public int CallCount { get; private set; }

        public async Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
            ArtworkGenerationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var fixture = await new FixtureArtworkProvider(TimeProvider.System).GenerateAsync(
                request with { CandidateCount = 2 },
                cancellationToken);
            var candidates = fixture.Value!.Candidates
                .Select(value => value with { Artwork = value.Artwork with { Materialized = true } })
                .ToArray();
            return ProviderResult<ArtworkGenerationResult>.Succeeded(
                new ArtworkGenerationResult(
                    candidates,
                    candidates.Select(value => value.Artwork).ToArray()),
                fixture.Provenance);
        }
    }

    private sealed class CallbackCampaignPlanner(Func<Task> callback) : ICampaignPlanner
    {
        public int CallCount { get; private set; }

        public async Task<ProviderResult<CampaignPlanningResult>> PlanAsync(
            CampaignPlanningRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await callback();
            return await new FixtureCampaignPlanner(TimeProvider.System)
                .PlanAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingArtifactStore(int? failOnPromotion = null) : IPipelineArtifactStore
    {
        public int PromoteCount { get; private set; }

        public Task<PromotedArtifact> PromoteAsync(
            ProviderArtifactManifest manifest,
            string canonicalObjectKey,
            CancellationToken cancellationToken)
        {
            PromoteCount++;
            if (PromoteCount == failOnPromotion)
            {
                throw new IOException("Injected canonical promotion failure.");
            }

            return Task.FromResult(new PromotedArtifact(
                canonicalObjectKey,
                manifest.ContentType,
                manifest.SizeBytes,
                manifest.Sha256,
                manifest.DurationMilliseconds,
                manifest.Width,
                manifest.Height));
        }

        public Task<PromotedArtifact> StoreLocalAsync(
            string sourcePath,
            string canonicalObjectKey,
            string contentType,
            long? durationMilliseconds,
            int? width,
            int? height,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MaterializingProcessRunner : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            File.WriteAllBytes(arguments[^1], "generated-proxy"u8.ToArray());
            return Task.FromResult(new ProcessExecutionResult(0, "", "", TimeSpan.Zero));
        }
    }

    private sealed class RejectingCoverComposer : ICleanCoverComposer
    {
        public Task<MediaAsset> EnsureAsync(
            ReleaseProject project,
            ArtworkPackRevision artworkPack,
            CancellationToken cancellationToken,
            string? artistNameSnapshot = null,
            string? trackTitleSnapshot = null) =>
            throw new InvalidOperationException("Cover composition is not used for cover candidates.");
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

    private sealed class ThrowingInvocationWriter : IAiProviderInvocationWriter
    {
        public int CallCount { get; private set; }

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
            throw new InvalidOperationException("Invocation ledger is unavailable.");
        }
    }

    private sealed class TestStorage : IObjectStorage
    {
        public List<string> Deleted { get; } = [];

        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            Task.FromResult<StorageObjectInfo?>(null);
        public Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken)
        {
            File.WriteAllBytes(destinationPath, "source-image"u8.ToArray());
            return Task.CompletedTask;
        }
        public Task UploadAsync(string objectKey, string sourcePath, string contentType, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            Deleted.Add(objectKey);
            return Task.CompletedTask;
        }
        public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
