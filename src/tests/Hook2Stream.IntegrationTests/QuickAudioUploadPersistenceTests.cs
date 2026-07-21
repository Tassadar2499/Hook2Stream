using System.Net;
using System.Net.Http.Json;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hook2Stream.IntegrationTests;

public sealed class QuickAudioUploadPersistenceTests
{
    public static TheoryData<Exception> PersistenceFailures => new()
    {
        new DbUpdateException("Simulated idempotency race without a winner."),
        new InvalidOperationException("Simulated persistence failure."),
        new OperationCanceledException("Simulated persistence cancellation.")
    };

    [Theory]
    [MemberData(nameof(PersistenceFailures))]
    public async Task Multipart_upload_is_aborted_without_masking_the_persistence_failure(
        Exception persistenceFailure)
    {
        var interceptor = new FailMultipartPersistenceInterceptor(persistenceFailure);
        var exceptionCapture = new CapturingExceptionHandler();
        await using var factory = new Hook2StreamApiFactory(
            services =>
            {
                services.RemoveAll<IExceptionHandler>();
                services.AddSingleton<IExceptionHandler>(exceptionCapture);
            },
            options => options.AddInterceptors(interceptor));
        using var client = factory.CreateClient();
        await Onboard(client);

        var storage = Assert.IsType<FakeObjectStorage>(
            factory.Services.GetRequiredService<IObjectStorage>());
        storage.AbortMultipartException = new IOException("Simulated cleanup failure.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/releases/audio-uploads")
        {
            Content = JsonContent.Create(new
            {
                fileName = "multipart.mp3",
                contentType = "audio/mpeg",
                sizeBytes = MediaPolicy.MultipartThresholdBytes,
                confirmsContentRights = true,
                allowsExternalAiProcessing = true
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"multipart-failure-{Guid.NewGuid():N}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Same(persistenceFailure, exceptionCapture.Exception);
        var aborted = Assert.Single(storage.AbortedMultipartUploads);
        Assert.Equal("test-upload", aborted.UploadId);
        Assert.Contains("/assets/", aborted.ObjectKey, StringComparison.Ordinal);
    }

    private static async Task Onboard(HttpClient client)
    {
        var response = await client.PutAsJsonAsync("/api/v1/account/onboarding", new
        {
            workspaceName = "Multipart persistence tests",
            acceptTerms = true,
            acceptPrivacy = true,
            termsVersion = "draft-2026-07-16",
            privacyVersion = "draft-2026-07-16",
            displayName = "Test artist"
        });
        response.EnsureSuccessStatusCode();
    }
}

internal sealed class FailMultipartPersistenceInterceptor(Exception persistenceFailure) : SaveChangesInterceptor
{
    private int _hasFailed;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var isMultipartPersistence = eventData.Context?.ChangeTracker
            .Entries<UploadSession>()
            .Any(entry => entry.State == EntityState.Added && entry.Entity.IsMultipart) == true;
        if (isMultipartPersistence && Interlocked.Exchange(ref _hasFailed, 1) == 0)
        {
            throw persistenceFailure;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

internal sealed class CapturingExceptionHandler : IExceptionHandler
{
    public Exception? Exception { get; private set; }

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        Exception = exception;
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return ValueTask.FromResult(true);
    }
}
