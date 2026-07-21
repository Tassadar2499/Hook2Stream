using System.Text.Json;

namespace Hook2Stream.Application;

public sealed record CampaignRecipeSlot(
    int RelativeDay,
    string TemplateKey,
    int? HookIndex,
    int Variant);

public sealed record CampaignPlanContractError(string Code, string Message);

public sealed record CampaignPlanContractValidation(
    IReadOnlyList<CampaignPlanContractError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The versioned, provider-independent campaign contract. Keeping the canonical recipe here lets
/// fixture and external provider results pass through the same boundary before persistence.
/// </summary>
public static class CampaignPlanContractValidator
{
    public const int ExpectedItemCount = 18;
    public const long MinimumDurationMilliseconds = 10_000;
    public const long MaximumDurationMilliseconds = 30_000;

    private static readonly string[] HookTemplates =
        ["kinetic-lyrics", "animated-cover", "visual-loop-a", "visual-loop-b"];

    private static readonly IReadOnlyList<CampaignRecipeSlot> UpcomingSlots = Array.AsReadOnly(
        new[]
        {
            new CampaignRecipeSlot(-10, "teaser", null, 1),
            new CampaignRecipeSlot(-9, "animated-cover", 0, 1),
            new CampaignRecipeSlot(-8, "kinetic-lyrics", 1, 1),
            new CampaignRecipeSlot(-6, "visual-loop-a", 2, 1),
            new CampaignRecipeSlot(-5, "teaser", null, 2),
            new CampaignRecipeSlot(-3, "visual-loop-a", 0, 1),
            new CampaignRecipeSlot(-2, "countdown", null, 1),
            new CampaignRecipeSlot(-1, "countdown", null, 2),
            new CampaignRecipeSlot(0, "out-now", null, 1),
            new CampaignRecipeSlot(0, "out-now", null, 2),
            new CampaignRecipeSlot(1, "kinetic-lyrics", 0, 1),
            new CampaignRecipeSlot(2, "animated-cover", 1, 1),
            new CampaignRecipeSlot(3, "animated-cover", 2, 1),
            new CampaignRecipeSlot(5, "visual-loop-a", 1, 1),
            new CampaignRecipeSlot(6, "kinetic-lyrics", 2, 1),
            new CampaignRecipeSlot(7, "visual-loop-b", 0, 1),
            new CampaignRecipeSlot(9, "visual-loop-b", 1, 1),
            new CampaignRecipeSlot(10, "visual-loop-b", 2, 1)
        });

    private static readonly IReadOnlyList<CampaignRecipeSlot> ReleasedSlots = Array.AsReadOnly(
        new[]
        {
            new CampaignRecipeSlot(0, "out-now", null, 1),
            new CampaignRecipeSlot(0, "out-now", null, 2),
            new CampaignRecipeSlot(1, "teaser", null, 1),
            new CampaignRecipeSlot(2, "animated-cover", 0, 1),
            new CampaignRecipeSlot(3, "kinetic-lyrics", 1, 1),
            new CampaignRecipeSlot(5, "visual-loop-a", 2, 1),
            new CampaignRecipeSlot(6, "teaser", null, 2),
            new CampaignRecipeSlot(7, "visual-loop-a", 0, 1),
            new CampaignRecipeSlot(8, "post-release-cta", null, 1),
            new CampaignRecipeSlot(9, "post-release-cta", null, 2),
            new CampaignRecipeSlot(10, "kinetic-lyrics", 0, 1),
            new CampaignRecipeSlot(11, "animated-cover", 1, 1),
            new CampaignRecipeSlot(12, "animated-cover", 2, 1),
            new CampaignRecipeSlot(13, "visual-loop-a", 1, 1),
            new CampaignRecipeSlot(15, "kinetic-lyrics", 2, 1),
            new CampaignRecipeSlot(16, "visual-loop-b", 0, 1),
            new CampaignRecipeSlot(18, "visual-loop-b", 1, 1),
            new CampaignRecipeSlot(20, "visual-loop-b", 2, 1)
        });

    public static IReadOnlyList<CampaignRecipeSlot> CanonicalSlots(bool isAlreadyReleased) =>
        isAlreadyReleased ? ReleasedSlots : UpcomingSlots;

    public static CampaignPlanContractValidation Validate(
        CampaignPlanningRequest request,
        IReadOnlyList<CampaignItemPlan> items)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Validate(request.IsAlreadyReleased, request.Hooks.Select(value => value.HookId).ToArray(), items);
    }

    /// <summary>
    /// Validates an already materialized campaign revision without requiring a provider execution
    /// context. Manual edits use this overload so they cross the same canonical boundary as
    /// fixture and external-provider output.
    /// </summary>
    public static CampaignPlanContractValidation Validate(
        bool isAlreadyReleased,
        IReadOnlyList<Guid> hookIds,
        IReadOnlyList<CampaignItemPlan> items)
    {
        ArgumentNullException.ThrowIfNull(hookIds);
        ArgumentNullException.ThrowIfNull(items);

        var errors = new List<CampaignPlanContractError>();
        if (hookIds.Count != 3)
        {
            Add(errors, "campaign.hook_count", "The campaign input must contain exactly three hooks.");
        }

        if (items.Count != ExpectedItemCount)
        {
            Add(errors, "campaign.item_count", $"The campaign must contain exactly {ExpectedItemCount} items.");
        }

        if (items.Select(item => item.ItemId).Distinct().Count() != items.Count)
        {
            Add(errors, "campaign.item_ids", "Campaign item IDs must be unique.");
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Sequence != index + 1)
            {
                Add(errors, "campaign.sequence", "Campaign items must be ordered with contiguous one-based sequence numbers.");
                break;
            }

            if (item.DurationMilliseconds is < MinimumDurationMilliseconds or > MaximumDurationMilliseconds)
            {
                Add(
                    errors,
                    "campaign.duration",
                    $"Item {item.ItemId} duration must be between 10 and 30 seconds inclusive.");
            }

            ValidateComposition(item, errors);
        }

        ValidateHookMatrix(hookIds, items, errors);
        ValidateSupportingTypes(isAlreadyReleased, items, errors);
        ValidateSchedule(isAlreadyReleased, hookIds, items, errors);
        return new CampaignPlanContractValidation(errors);
    }

    private static void ValidateHookMatrix(
        IReadOnlyList<Guid> hookIds,
        IReadOnlyList<CampaignItemPlan> items,
        ICollection<CampaignPlanContractError> errors)
    {
        if (hookIds.Count != 3) return;

        var expectedHookIds = hookIds.ToHashSet();
        var hookItems = items.Where(item => item.HookId is not null).ToArray();
        if (hookItems.Length != 12 || hookItems.Any(item => !expectedHookIds.Contains(item.HookId!.Value)))
        {
            Add(errors, "campaign.hook_matrix", "The campaign must contain twelve items bound to the three input hooks.");
            return;
        }

        foreach (var hookId in hookIds)
        {
            foreach (var template in HookTemplates)
            {
                if (hookItems.Count(item => item.HookId == hookId && item.TemplateKey == template) != 1)
                {
                    Add(
                        errors,
                        "campaign.hook_matrix",
                        $"Hook {hookId} must have exactly one {template} item.");
                }
            }
        }
    }

    private static void ValidateSupportingTypes(
        bool isAlreadyReleased,
        IReadOnlyList<CampaignItemPlan> items,
        ICollection<CampaignPlanContractError> errors)
    {
        var expected = isAlreadyReleased
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["teaser"] = 2,
                ["post-release-cta"] = 2,
                ["out-now"] = 2
            }
            : new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["teaser"] = 2,
                ["countdown"] = 2,
                ["out-now"] = 2
            };

        var supporting = items.Where(item => item.HookId is null).ToArray();
        if (supporting.Length != 6 || expected.Any(pair => supporting.Count(item => item.TemplateKey == pair.Key) != pair.Value))
        {
            Add(
                errors,
                "campaign.supporting_types",
                isAlreadyReleased
                    ? "Released campaigns require two teasers, two post-release CTAs, and two out-now items."
                    : "Upcoming campaigns require two teasers, two countdowns, and two out-now items.");
        }

        if (supporting.Any(item => !expected.ContainsKey(item.TemplateKey)))
        {
            Add(errors, "campaign.supporting_types", "The campaign contains an unsupported non-hook item type.");
        }
    }

    private static void ValidateSchedule(
        bool isAlreadyReleased,
        IReadOnlyList<Guid> hookIds,
        IReadOnlyList<CampaignItemPlan> items,
        ICollection<CampaignPlanContractError> errors)
    {
        if (hookIds.Count != 3 || items.Count != ExpectedItemCount) return;

        var expected = CanonicalSlots(isAlreadyReleased);
        for (var index = 0; index < expected.Count; index++)
        {
            var slot = expected[index];
            var item = items[index];
            var expectedHookId = slot.HookIndex is { } hookIndex
                ? hookIds[hookIndex]
                : (Guid?)null;
            if (item.RelativeDay != slot.RelativeDay ||
                item.TemplateKey != slot.TemplateKey ||
                item.HookId != expectedHookId)
            {
                Add(
                    errors,
                    "campaign.schedule",
                    $"Slot {index + 1} does not match the canonical {(isAlreadyReleased ? "Released" : "Upcoming")} recipe.");
            }
        }
    }

    private static void ValidateComposition(
        CampaignItemPlan item,
        ICollection<CampaignPlanContractError> errors)
    {
        if (string.IsNullOrWhiteSpace(item.CompositionJson))
        {
            Add(errors, "campaign.composition", $"Item {item.ItemId} has no composition snapshot.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(item.CompositionJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("durationMilliseconds", out var duration) ||
                duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetInt64(out var compositionDuration) ||
                compositionDuration != item.DurationMilliseconds ||
                !root.TryGetProperty("hashtags", out var hashtags) ||
                hashtags.ValueKind != JsonValueKind.Array ||
                hashtags.GetArrayLength() == 0 ||
                !root.TryGetProperty("copyVariants", out var copy) ||
                !HasText(copy, "neutral") ||
                !HasText(copy, "emotional") ||
                !copy.TryGetProperty("destinations", out var destinations) ||
                !HasText(destinations, "tiktok") ||
                !HasText(destinations, "youtubeShorts") ||
                !HasText(destinations, "instagramReels") ||
                !HasText(destinations, "vkClips"))
            {
                Add(
                    errors,
                    "campaign.composition",
                    $"Item {item.ItemId} is missing its duration, hashtags, or destination copy snapshot.");
            }
        }
        catch (JsonException)
        {
            Add(errors, "campaign.composition", $"Item {item.ItemId} has invalid composition JSON.");
        }
        catch (InvalidOperationException)
        {
            Add(errors, "campaign.composition", $"Item {item.ItemId} has invalid composition value types.");
        }
    }

    private static bool HasText(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString());

    private static void Add(
        ICollection<CampaignPlanContractError> errors,
        string code,
        string message)
    {
        if (errors.Any(error => error.Code == code && error.Message == message)) return;
        errors.Add(new CampaignPlanContractError(code, message));
    }
}
