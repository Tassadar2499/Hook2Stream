using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Providers;

public sealed class OpenRouterCampaignPlanner(
    OpenRouterClient client,
    IOptions<OpenRouterOptions> options,
    TimeProvider timeProvider) : ICampaignPlanner
{
    private readonly OpenRouterOptions _options = options.Value;

    public async Task<ProviderResult<CampaignPlanningResult>> PlanAsync(
        CampaignPlanningRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        if (request.Hooks.Count != 3)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.UserInput,
                    "provider.insufficient_hooks",
                    "Exactly three approved hooks are required to plan a campaign."));
        }

        var slots = CanonicalSlots(request);
        var requestIds = new List<string>();
        var generationIds = new List<string>();
        var usages = new List<ProviderUsage>();
        string? resolvedModel = null;
        string? resolvedProvider = null;
        string? validationHint = null;
        byte[]? acceptedCopy = null;
        IReadOnlyList<CampaignItemPlan>? acceptedItems = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await client.PostJsonAsync(
                "chat/completions",
                BuildPayload(request, slots, validationHint),
                $"{request.Context.OperationId:N}:campaign:{attempt}",
                _options.CampaignTimeoutSeconds,
                outcomeCanBeRetried: true,
                cancellationToken);
            if (!response.IsSuccess)
            {
                return Failed(
                    request.Context,
                    startedAt,
                    response.Failure!,
                    requestIds,
                    generationIds,
                    resolvedModel,
                    resolvedProvider,
                    usages);
            }

            if (response.RequestId is not null) requestIds.Add(response.RequestId);
            if (response.GenerationId is not null) generationIds.Add(response.GenerationId);
            try
            {
                using var responseJson = JsonDocument.Parse(response.Body);
                resolvedModel ??= OpenRouterProviderData.String(responseJson.RootElement, "model");
                resolvedProvider ??= OpenRouterProviderData.String(responseJson.RootElement, "provider");
                var responseGeneration = OpenRouterProviderData.String(responseJson.RootElement, "id");
                if (responseGeneration is not null && !generationIds.Contains(responseGeneration, StringComparer.Ordinal))
                {
                    generationIds.Add(responseGeneration);
                }

                usages.Add(OpenRouterProviderData.Usage(responseJson.RootElement));
                var content = ReadContent(responseJson.RootElement);
                acceptedCopy = Encoding.UTF8.GetBytes(content);
                using var copy = JsonDocument.Parse(content);
                var parsed = ParseItems(request, slots, copy.RootElement);
                var validation = CampaignPlanContractValidator.Validate(request, parsed);
                if (validation.IsValid)
                {
                    acceptedItems = parsed;
                    break;
                }

                validationHint = string.Join(
                    "; ",
                    validation.Errors.Take(6).Select(value => value.Code));
            }
            catch (JsonException)
            {
                validationHint = "The previous response was not valid JSON matching the supplied schema.";
            }
            catch (InvalidDataException exception)
            {
                validationHint = exception.Message;
            }
        }

        if (acceptedItems is null || acceptedCopy is null)
        {
            return Failed(
                request.Context,
                startedAt,
                new ProviderFailure(
                    ProviderFailureKind.Permanent,
                    "openrouter.campaign_response_invalid",
                    "OpenRouter returned invalid campaign copy twice."),
                requestIds,
                generationIds,
                resolvedModel,
                resolvedProvider,
                usages);
        }

        var artifact = new ProviderArtifactManifest(
            OpenRouterProviderData.StableId(request.Context.OperationId, "campaign-plan"),
            "campaign-plan",
            $"{request.Context.StagingPrefix.Trim().Trim('/')}/campaign-plan.json",
            OpenRouterProviderData.Sha256(acceptedCopy),
            "application/json",
            acceptedCopy.LongLength,
            Materialized: false);
        return ProviderResult<CampaignPlanningResult>.Succeeded(
            new CampaignPlanningResult(acceptedItems, [artifact]),
            Provenance(
                request.Context,
                startedAt,
                requestIds,
                generationIds,
                resolvedModel,
                resolvedProvider,
                usages));
    }

    private object BuildPayload(
        CampaignPlanningRequest request,
        IReadOnlyList<CanonicalSlot> slots,
        string? validationHint) => new
        {
            model = _options.CampaignModel,
            messages = new object[]
        {
            new
            {
                role = "system",
                content = "You write concise music-release social copy. Return only the strict JSON schema. " +
                          "Do not change, omit, reorder, or invent slots. Do not claim awards, chart positions, reviews, or facts absent from the input."
            },
            new
            {
                role = "user",
                content = JsonSerializer.Serialize(new
                {
                    task = "Write copy for every canonical campaign slot.",
                    repair = validationHint,
                    artistName = request.ArtistName,
                    trackTitle = request.TrackTitle,
                    releaseDate = request.ReleaseDate,
                    request.IsAlreadyReleased,
                    request.Tone,
                    callToAction = request.CallToAction,
                    slots = slots.Select(slot => new
                    {
                        slot.Sequence,
                        slot.RelativeDay,
                        slot.TemplateKey,
                        slot.Variant,
                        hook = slot.Hook is null
                            ? null
                            : new { slot.Hook.Label, slot.Hook.Excerpt }
                    })
                }, OpenRouterProviderData.Json)
            }
        },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "hook2stream_campaign_copy_v1",
                    strict = true,
                    schema = CampaignSchema()
                }
            },
            temperature = 0.4,
            max_tokens = 8_000,
            provider = new
            {
                zdr = _options.RequireZeroDataRetention,
                data_collection = _options.DenyDataCollection ? "deny" : "allow",
                require_parameters = _options.RequireParameters,
                allow_fallbacks = true
            }
        };

    private static object CampaignSchema() => new
    {
        type = "object",
        properties = new
        {
            items = new
            {
                type = "array",
                minItems = CampaignPlanContractValidator.ExpectedItemCount,
                maxItems = CampaignPlanContractValidator.ExpectedItemCount,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        sequence = new { type = "integer", minimum = 1, maximum = 18 },
                        headline = new { type = "string", minLength = 1, maxLength = 120 },
                        caption = new { type = "string", minLength = 1, maxLength = 600 },
                        hashtags = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = 8,
                            items = new { type = "string", minLength = 1, maxLength = 80 }
                        },
                        neutral = new { type = "string", minLength = 1, maxLength = 800 },
                        emotional = new { type = "string", minLength = 1, maxLength = 800 },
                        destinations = new
                        {
                            type = "object",
                            properties = new
                            {
                                tiktok = new { type = "string", minLength = 1, maxLength = 1_000 },
                                youtubeShorts = new { type = "string", minLength = 1, maxLength = 1_000 },
                                instagramReels = new { type = "string", minLength = 1, maxLength = 1_000 },
                                vkClips = new { type = "string", minLength = 1, maxLength = 1_000 }
                            },
                            required = new[] { "tiktok", "youtubeShorts", "instagramReels", "vkClips" },
                            additionalProperties = false
                        }
                    },
                    required = new[] { "sequence", "headline", "caption", "hashtags", "neutral", "emotional", "destinations" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "items" },
        additionalProperties = false
    };

    private static string ReadContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidDataException("The response did not contain campaign copy.");
        }

        if (content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()))
        {
            return content.GetString()!;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var text = content.EnumerateArray()
                .Where(value => OpenRouterProviderData.String(value, "type") is "text" or "output_text")
                .Select(value => OpenRouterProviderData.String(value, "text"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (text is not null) return text;
        }

        throw new InvalidDataException("The response did not contain campaign copy.");
    }

    private static IReadOnlyList<CampaignItemPlan> ParseItems(
        CampaignPlanningRequest request,
        IReadOnlyList<CanonicalSlot> slots,
        JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() != CampaignPlanContractValidator.ExpectedItemCount)
        {
            throw new InvalidDataException("The response must contain exactly eighteen copy items.");
        }

        var bySequence = new Dictionary<int, JsonElement>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("sequence", out var sequenceElement) ||
                !sequenceElement.TryGetInt32(out var sequence) ||
                !bySequence.TryAdd(sequence, item))
            {
                throw new InvalidDataException("Every copy item needs a unique canonical sequence.");
            }
        }

        return slots.Select(slot =>
        {
            if (!bySequence.TryGetValue(slot.Sequence, out var copy))
            {
                throw new InvalidDataException("Copy is missing for a canonical slot.");
            }

            var headline = RequiredText(copy, "headline", 120);
            var caption = RequiredText(copy, "caption", 600);
            var hashtags = Hashtags(copy);
            var neutral = RequiredText(copy, "neutral", 800);
            var emotional = RequiredText(copy, "emotional", 800);
            if (!copy.TryGetProperty("destinations", out var destinations) ||
                destinations.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Destination copy is missing.");
            }

            var artworkId = request.Artwork.Count == 0
                ? (Guid?)null
                : request.Artwork[(slot.Sequence - 1) % request.Artwork.Count].AssetId;
            var composition = JsonSerializer.Serialize(new
            {
                durationMilliseconds = slot.DurationMilliseconds,
                opening = slot.Variant == 1 ? "title-card" : "cold-open",
                hashtags,
                copyVariants = new
                {
                    neutral,
                    emotional,
                    destinations = new
                    {
                        tiktok = RequiredText(destinations, "tiktok", 1_000),
                        youtubeShorts = RequiredText(destinations, "youtubeShorts", 1_000),
                        instagramReels = RequiredText(destinations, "instagramReels", 1_000),
                        vkClips = RequiredText(destinations, "vkClips", 1_000)
                    }
                }
            }, OpenRouterProviderData.Json);
            return new CampaignItemPlan(
                OpenRouterProviderData.StableId(
                    request.Context.OperationId,
                    $"campaign-{slot.Sequence}-{slot.TemplateKey}-{slot.Variant}"),
                slot.Sequence,
                slot.RelativeDay,
                slot.TemplateKey,
                slot.Hook?.HookId,
                headline,
                caption,
                request.CallToAction,
                artworkId,
                slot.DurationMilliseconds,
                composition);
        }).ToArray();
    }

    private static string RequiredText(JsonElement root, string property, int maximumLength)
    {
        var value = OpenRouterProviderData.String(root, property)?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Campaign field {property} is invalid.");
        }

        return value;
    }

    private static IReadOnlyList<string> Hashtags(JsonElement root)
    {
        if (!root.TryGetProperty("hashtags", out var hashtags) || hashtags.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Campaign hashtags are missing.");
        }

        var normalized = hashtags.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? NormalizeHashtag(value.GetString()) : null)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        if (normalized.Length == 0) throw new InvalidDataException("Campaign hashtags are invalid.");
        return normalized;
    }

    private static string? NormalizeHashtag(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var normalized = new string(source
            .Trim()
            .TrimStart('#')
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .Take(40)
            .ToArray());
        return normalized.Length == 0 ? null : $"#{normalized}";
    }

    private static IReadOnlyList<CanonicalSlot> CanonicalSlots(CampaignPlanningRequest request) =>
        CampaignPlanContractValidator.CanonicalSlots(request.IsAlreadyReleased)
            .Select((slot, index) =>
            {
                var hook = slot.HookIndex is { } hookIndex ? request.Hooks[hookIndex] : null;
                var duration = hook is null
                    ? 15_000
                    : Math.Clamp(
                        hook.EndMilliseconds - hook.StartMilliseconds,
                        CampaignPlanContractValidator.MinimumDurationMilliseconds,
                        CampaignPlanContractValidator.MaximumDurationMilliseconds);
                return new CanonicalSlot(
                    index + 1,
                    slot.RelativeDay,
                    slot.TemplateKey,
                    slot.Variant,
                    hook,
                    duration);
            })
            .ToArray();

    private ProviderResult<CampaignPlanningResult> Failed(
        ProviderExecutionContext context,
        DateTimeOffset startedAt,
        ProviderFailure failure,
        IReadOnlyCollection<string>? requestIds = null,
        IReadOnlyCollection<string>? generationIds = null,
        string? resolvedModel = null,
        string? resolvedProvider = null,
        IReadOnlyCollection<ProviderUsage>? usages = null) =>
        ProviderResult<CampaignPlanningResult>.Failed(
            failure,
            Provenance(
                context,
                startedAt,
                requestIds ?? [],
                generationIds ?? [],
                resolvedModel,
                resolvedProvider,
                usages ?? []));

    private ProviderProvenance Provenance(
        ProviderExecutionContext context,
        DateTimeOffset startedAt,
        IReadOnlyCollection<string> requestIds,
        IReadOnlyCollection<string> generationIds,
        string? resolvedModel,
        string? resolvedProvider,
        IReadOnlyCollection<ProviderUsage> usages) =>
        new(
            "openrouter",
            resolvedModel ?? _options.CampaignModel,
            "chat-completions-v1",
            requestIds.Count == 0 ? null : string.Join(',', requestIds),
            context.InputHash,
            context.ParameterHash,
            startedAt,
            timeProvider.GetUtcNow(),
            _options.CampaignModel,
            resolvedProvider,
            generationIds.Count == 0 ? null : string.Join(',', generationIds),
            OpenRouterProviderData.Sum(usages));

    private sealed record CanonicalSlot(
        int Sequence,
        int RelativeDay,
        string TemplateKey,
        int Variant,
        CampaignHookInput? Hook,
        long DurationMilliseconds);
}
