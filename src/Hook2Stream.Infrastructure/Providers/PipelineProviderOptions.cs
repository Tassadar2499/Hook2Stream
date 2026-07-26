namespace Hook2Stream.Infrastructure.Providers;

public enum ProviderAdapterMode
{
    Fixture = 1,
    ExternalProcess = 2,
    OpenRouter = 3,
    Deterministic = 4
}

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";
    public string ApiKey { get; set; } = "";
    public string AppTitle { get; set; } = "Hook2Stream";
    public string HttpReferer { get; set; } = "";
    public string TranscriptionModel { get; set; } = "openai/whisper-large-v3";
    public string ImageModel { get; set; } = "bytedance-seed/seedream-4.5";
    public string CampaignModel { get; set; } = "openai/gpt-oss-120b";
    public int TranscriptionTimeoutSeconds { get; set; } = 120;
    public int ImageTimeoutSeconds { get; set; } = 300;
    public int CampaignTimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 2;
    public int TranscriptionChunkSeconds { get; set; } = 50;
    public int TranscriptionOverlapSeconds { get; set; } = 2;
    public bool RequireZeroDataRetention { get; set; } = true;
    public bool DenyDataCollection { get; set; } = true;
    public bool RequireParameters { get; set; } = true;
    public bool AccountOrGuardrailZdrEnforced { get; set; }
}

public sealed class PipelineProviderOptions
{
    public const string SectionName = "PipelineProviders";

    public string WorkRoot { get; set; } = "";
    public ProviderProcessOptions AudioAnalysis { get; set; } = new();
    public ProviderProcessOptions Transcription { get; set; } = new();
    public ProviderProcessOptions Artwork { get; set; } = new();
    public ProviderProcessOptions CampaignPlanning { get; set; } = new();
    public ProviderProcessOptions VideoRendering { get; set; } = new();
}

public sealed class ProviderProcessOptions
{
    public ProviderAdapterMode Mode { get; set; } = ProviderAdapterMode.Fixture;
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
    public string Provider { get; set; } = "external-process";
    public string Model { get; set; } = "configured";
    public string Version { get; set; } = "unknown";
}
