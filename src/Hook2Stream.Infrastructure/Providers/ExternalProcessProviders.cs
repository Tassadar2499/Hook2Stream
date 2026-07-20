using System.Text.Json;
using System.Text.Json.Serialization;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public sealed class ExternalAudioAnalysisProvider(
    IProcessRunner processRunner,
    IOptions<PipelineProviderOptions> options,
    TimeProvider timeProvider) : IAudioAnalysisProvider
{
    public Task<ProviderResult<AudioAnalysisResult>> AnalyzeAsync(
        AudioAnalysisRequest request,
        CancellationToken cancellationToken) =>
        ExternalProviderProcess.ExecuteAsync<AudioAnalysisRequest, AudioAnalysisResult>(
            request,
            request.Context,
            options.Value,
            options.Value.AudioAnalysis,
            processRunner,
            timeProvider,
            cancellationToken);
}

public sealed class ExternalTranscriptionProvider(
    IProcessRunner processRunner,
    IOptions<PipelineProviderOptions> options,
    TimeProvider timeProvider) : ITranscriptionProvider
{
    public Task<ProviderResult<TranscriptionResult>> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken) =>
        ExternalProviderProcess.ExecuteAsync<TranscriptionRequest, TranscriptionResult>(
            request,
            request.Context,
            options.Value,
            options.Value.Transcription,
            processRunner,
            timeProvider,
            cancellationToken);
}

public sealed class ExternalArtworkProvider(
    IProcessRunner processRunner,
    IOptions<PipelineProviderOptions> options,
    TimeProvider timeProvider) : IArtworkProvider
{
    public Task<ProviderResult<ArtworkGenerationResult>> GenerateAsync(
        ArtworkGenerationRequest request,
        CancellationToken cancellationToken) =>
        ExternalProviderProcess.ExecuteAsync<ArtworkGenerationRequest, ArtworkGenerationResult>(
            request,
            request.Context,
            options.Value,
            options.Value.Artwork,
            processRunner,
            timeProvider,
            cancellationToken);
}

public sealed class ExternalCampaignPlanner(
    IProcessRunner processRunner,
    IOptions<PipelineProviderOptions> options,
    TimeProvider timeProvider) : ICampaignPlanner
{
    public Task<ProviderResult<CampaignPlanningResult>> PlanAsync(
        CampaignPlanningRequest request,
        CancellationToken cancellationToken) =>
        ExternalProviderProcess.ExecuteAsync<CampaignPlanningRequest, CampaignPlanningResult>(
            request,
            request.Context,
            options.Value,
            options.Value.CampaignPlanning,
            processRunner,
            timeProvider,
            cancellationToken);
}

public sealed class ExternalVideoRenderer(
    IProcessRunner processRunner,
    IOptions<PipelineProviderOptions> options,
    TimeProvider timeProvider) : IVideoRenderer
{
    public Task<ProviderResult<VideoRenderResult>> RenderAsync(
        VideoRenderRequest request,
        CancellationToken cancellationToken) =>
        ExternalProviderProcess.ExecuteAsync<VideoRenderRequest, VideoRenderResult>(
            request,
            request.Context,
            options.Value,
            options.Value.VideoRendering,
            processRunner,
            timeProvider,
            cancellationToken);
}

internal static class ExternalProviderProcess
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<ProviderResult<TResult>> ExecuteAsync<TRequest, TResult>(
        TRequest request,
        ProviderExecutionContext context,
        PipelineProviderOptions rootOptions,
        ProviderProcessOptions providerOptions,
        IProcessRunner processRunner,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var startedAt = timeProvider.GetUtcNow();
        if (providerOptions.Mode != ProviderAdapterMode.ExternalProcess ||
            string.IsNullOrWhiteSpace(providerOptions.Executable))
        {
            return ProviderResult<TResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "provider.not_configured",
                    "The external provider is not configured."),
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }

        var workRoot = string.IsNullOrWhiteSpace(rootOptions.WorkRoot)
            ? Path.Combine(Path.GetTempPath(), "hook2stream-providers")
            : Path.GetFullPath(rootOptions.WorkRoot);
        var workDirectory = Path.Combine(
            workRoot,
            context.OperationId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(workDirectory, "request.json");
        var responsePath = Path.Combine(workDirectory, "response.json");

        try
        {
            Directory.CreateDirectory(workDirectory);
            RestrictDirectory(workDirectory);
            await File.WriteAllTextAsync(
                requestPath,
                JsonSerializer.Serialize(request, JsonOptions),
                cancellationToken);
            RestrictFile(requestPath);

            var arguments = providerOptions.Arguments.ToList();
            arguments.Add("--request");
            arguments.Add(requestPath);
            arguments.Add("--response");
            arguments.Add(responsePath);
            var execution = await processRunner.RunAsync(
                providerOptions.Executable,
                arguments,
                TimeSpan.FromSeconds(providerOptions.TimeoutSeconds),
                workDirectory,
                cancellationToken);

            if (File.Exists(responsePath))
            {
                var responseJson = await File.ReadAllTextAsync(responsePath, cancellationToken);
                var envelope = JsonSerializer.Deserialize<ExternalProviderEnvelope<TResult>>(
                    responseJson,
                    JsonOptions);
                if (envelope is not null)
                {
                    var provenance = CreateProvenance(
                        context,
                        providerOptions,
                        envelope.Provenance?.RequestId,
                        envelope.Provenance?.StartedAt ?? startedAt,
                        envelope.Provenance?.CompletedAt ?? timeProvider.GetUtcNow());
                    if (envelope.Value is not null && envelope.Failure is null && execution.ExitCode == 0)
                    {
                        return ProviderResult<TResult>.Succeeded(envelope.Value, provenance);
                    }

                    if (envelope.Failure is not null)
                    {
                        return ProviderResult<TResult>.Failed(Sanitize(envelope.Failure), provenance);
                    }
                }
            }

            var failure = execution.ExitCode == 0
                ? new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "provider.invalid_response",
                    "The provider returned an invalid response.")
                : new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "provider.process_failed",
                    "The provider process failed before producing a result.");
            return ProviderResult<TResult>.Failed(
                failure,
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return ProviderResult<TResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "provider.timeout",
                    "The provider timed out before producing a result."),
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }
        catch (IOException)
        {
            return ProviderResult<TResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Transient,
                    "provider.io_failure",
                    "The provider could not exchange its result safely."),
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }
        catch (UnauthorizedAccessException)
        {
            return ProviderResult<TResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "provider.work_directory_denied",
                    "The provider work directory is unavailable."),
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<TResult>.Failed(
                new ProviderFailure(
                    ProviderFailureKind.Unknown,
                    "provider.unexpected_failure",
                    "The provider failed without a confirmed result."),
                CreateProvenance(context, providerOptions, null, startedAt, timeProvider.GetUtcNow()));
        }
        finally
        {
            TryDeleteWorkDirectory(workDirectory);
        }
    }

    private static ProviderProvenance CreateProvenance(
        ProviderExecutionContext context,
        ProviderProcessOptions options,
        string? requestId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new(
            options.Provider,
            options.Model,
            options.Version,
            requestId,
            context.InputHash,
            context.ParameterHash,
            startedAt,
            completedAt);

    private static ProviderFailure Sanitize(ProviderFailure failure)
    {
        var code = failure.Code.Length <= 80 && failure.Code.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? failure.Code
            : "provider.external_failure";
        var safeMessage = string.Join(
            ' ',
            failure.SafeMessage.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (safeMessage.Length > 240)
        {
            safeMessage = safeMessage[..240];
        }

        if (string.IsNullOrWhiteSpace(safeMessage))
        {
            safeMessage = "The provider could not complete the request.";
        }

        return failure with { Code = code, SafeMessage = safeMessage };
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDeleteWorkDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A later staging cleanup can remove a work directory held by a terminated provider.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not turn a completed provider operation into a failure during best-effort cleanup.
        }
    }

    private sealed record ExternalProviderEnvelope<T>(
        T? Value,
        ProviderFailure? Failure,
        ProviderProvenance? Provenance)
        where T : class;
}
