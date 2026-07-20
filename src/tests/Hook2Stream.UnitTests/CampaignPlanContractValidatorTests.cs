using Hook2Stream.Application;
using Hook2Stream.Infrastructure.Providers;

namespace Hook2Stream.UnitTests;

public sealed class CampaignPlanContractValidatorTests
{
    private static readonly ProviderExecutionContext Context = new(
        Guid.Parse("01900000-0000-7000-8000-000000000091"),
        new string('a', 64),
        new string('b', 64),
        "staging/campaign-contract-test");

    [Fact]
    public async Task Released_fixture_uses_post_release_recipe_without_countdown_copy()
    {
        var (request, items) = await Fixture(isAlreadyReleased: true);

        Assert.True(CampaignPlanContractValidator.Validate(request, items).IsValid);
        Assert.Equal(
            [0, 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 18, 20],
            items.Select(item => item.RelativeDay));
        Assert.Equal(2, items.Count(item => item.TemplateKey == "out-now" && item.RelativeDay == 0));
        Assert.Equal(2, items.Count(item => item.TemplateKey == "post-release-cta"));
        Assert.DoesNotContain(items, item => item.TemplateKey == "countdown");
        Assert.DoesNotContain(
            items,
            item => item.Headline.Contains("countdown", StringComparison.OrdinalIgnoreCase) ||
                    item.Caption.Contains("countdown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Fixture_is_deterministic_for_the_same_provider_context()
    {
        var (request, first) = await Fixture(isAlreadyReleased: false);
        var result = await new FixtureCampaignPlanner(TimeProvider.System)
            .PlanAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(first, result.Value!.Items);
    }

    [Fact]
    public async Task Validator_rejects_wrong_item_count()
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);

        var validation = CampaignPlanContractValidator.Validate(request, items[..^1]);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.item_count");
    }

    [Fact]
    public async Task Validator_rejects_duplicate_item_ids()
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);
        items[1] = items[1] with { ItemId = items[0].ItemId };

        var validation = CampaignPlanContractValidator.Validate(request, items);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.item_ids");
    }

    [Fact]
    public async Task Validator_rejects_an_incomplete_hook_template_matrix()
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);
        var index = Array.FindIndex(items, item => item.TemplateKey == "visual-loop-b");
        items[index] = items[index] with { TemplateKey = "visual-loop-a" };

        var validation = CampaignPlanContractValidator.Validate(request, items);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.hook_matrix");
    }

    [Fact]
    public async Task Validator_rejects_a_noncanonical_schedule()
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);
        items[4] = items[4] with { RelativeDay = -4 };

        var validation = CampaignPlanContractValidator.Validate(request, items);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.schedule");
    }

    [Theory]
    [InlineData(9_999)]
    [InlineData(30_001)]
    public async Task Validator_rejects_duration_outside_inclusive_bounds(long durationMilliseconds)
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);
        items[0] = items[0] with { DurationMilliseconds = durationMilliseconds };

        var validation = CampaignPlanContractValidator.Validate(request, items);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.duration");
    }

    [Fact]
    public async Task Validator_rejects_missing_destination_copy_snapshot()
    {
        var (request, items) = await Fixture(isAlreadyReleased: false);
        items[0] = items[0] with { CompositionJson = "{\"durationMilliseconds\":15000}" };

        var validation = CampaignPlanContractValidator.Validate(request, items);

        Assert.Contains(validation.Errors, error => error.Code == "campaign.composition");
    }

    private static async Task<(CampaignPlanningRequest Request, CampaignItemPlan[] Items)> Fixture(
        bool isAlreadyReleased)
    {
        var hooks = Enumerable.Range(0, 3)
            .Select(index => new CampaignHookInput(
                Guid.Parse($"01900000-0000-7000-8000-{index + 1:000000000000}"),
                $"Hook {index + 1}",
                10_000 + index * 20_000,
                25_000 + index * 20_000,
                $"Excerpt {index + 1}"))
            .ToArray();
        var request = new CampaignPlanningRequest(
            Context,
            "Signal Artist",
            "Night Track",
            isAlreadyReleased ? null : new DateOnly(2026, 8, 1),
            isAlreadyReleased,
            "direct",
            "Listen now",
            hooks,
            []);
        var result = await new FixtureCampaignPlanner(TimeProvider.System)
            .PlanAsync(request, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return (request, result.Value!.Items.ToArray());
    }
}
