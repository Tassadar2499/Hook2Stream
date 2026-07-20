using Hook2Stream.Domain;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class WorkerRegistrationTests
{
    [Fact]
    public void Worker_registers_distinct_preview_final_export_and_clean_cover_handlers()
    {
        var services = new ServiceCollection();

        services.AddHook2StreamJobHandlers();

        var handlers = services.Where(value => value.ServiceType == typeof(IJobHandler)).ToArray();
        Assert.Equal(9, handlers.Length);
        Assert.Equal(2, handlers.Count(value => value.ImplementationFactory is not null));
        Assert.Contains(handlers, value => value.ImplementationType == typeof(ExportBundleJobHandler));
        Assert.Contains(handlers, value => value.ImplementationType == typeof(CleanCoverRenderJobHandler));

        var preview = new VideoRenderJobHandler(
            JobType.PreviewRender, null!, null!, null!, null!);
        var final = new VideoRenderJobHandler(
            JobType.FinalRender, null!, null!, null!, null!);
        Assert.Equal(JobType.PreviewRender, preview.Type);
        Assert.Equal(JobType.FinalRender, final.Type);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoRenderJobHandler(
            JobType.ExportBundle, null!, null!, null!, null!));
    }

    [Fact]
    public void Manual_campaign_controls_change_the_canonical_render_fingerprint()
    {
        var itemId = Guid.NewGuid();
        var backgroundId = Guid.NewGuid();
        var baseline = new CampaignItemRequest(
            itemId,
            1,
            "kinetic-lyrics",
            "chorus",
            backgroundId,
            "Approved text",
            """{"durationMilliseconds":15000,"cta":"Listen","caption":"One","fit":"fill","focalX":0.5,"focalY":0.5,"opening":"fade","textLayout":"center"}""");
        var edited = baseline with
        {
            CompositionJson =
                """{"durationMilliseconds":22000,"cta":"Save now","caption":"Two","fit":"fit","focalX":0.2,"focalY":0.8,"opening":"punch","textLayout":"lowerThird"}"""
        };
        var hook = new HookRequest("chorus", "chorus", 1_000, 25_000, "Chorus");
        var audio = Asset(AssetKind.Audio, "a");
        var cover = Asset(AssetKind.Cover, "b");
        var background = Asset(AssetKind.Visual, "c");

        var first = VideoCompositionControls.Parse(baseline, hook, 60_000)
            .BindSources(audio, cover, background, 1);
        var second = VideoCompositionControls.Parse(edited, hook, 60_000)
            .BindSources(audio, cover, background, 1);

        Assert.NotEqual(first.CompositionHash, second.CompositionHash);
        Assert.Equal("fill", first.Fit);
        Assert.Equal("fit", second.Fit);
        Assert.Equal(22_000, second.DurationMilliseconds);
        Assert.Equal("Save now", second.CallToAction);
        Assert.Equal("lowerThird", second.TextLayout);
    }

    [Fact]
    public void Legacy_campaign_call_to_action_is_preserved_during_render()
    {
        var item = new CampaignItemRequest(
            Guid.NewGuid(),
            1,
            "kinetic-lyrics",
            "chorus",
            Guid.NewGuid(),
            "Approved text",
            """{"callToAction":"Pre-save now"}""");
        var hook = new HookRequest("chorus", "chorus", 1_000, 20_000, "Chorus");

        var controls = VideoCompositionControls.Parse(item, hook, 60_000);

        Assert.Equal("Pre-save now", controls.CallToAction);
    }

    private static MediaAsset Asset(AssetKind kind, string hashSeed) => new()
    {
        WorkspaceId = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Kind = kind,
        OriginalFileName = "asset",
        DeclaredContentType = kind == AssetKind.Audio ? "audio/mpeg" : "image/png",
        DeclaredBytes = 1,
        ObjectKey = "asset/object",
        Sha256 = new string(hashSeed[0], 64)
    };
}
