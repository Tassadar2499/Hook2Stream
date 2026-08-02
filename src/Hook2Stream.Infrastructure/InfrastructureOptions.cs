namespace Hook2Stream.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public string PublicServiceUrl { get; set; } = "http://localhost:9000";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "hook2stream-media";
    public StorageCredentialMode CredentialMode { get; set; } = StorageCredentialMode.Auto;
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
    public bool RequireCredentials { get; set; }
    public bool ConfigureBucketCors { get; set; }
    public bool ConfigureBucketLifecycle { get; set; }
    public bool ConfigureMultipartAbortLifecycle { get; set; } = true;
    public string[] BrowserUploadOrigins { get; set; } =
        ["http://localhost:3000", "http://127.0.0.1:3000"];
}

public enum StorageCredentialMode
{
    Auto,
    Static,
    DefaultChain
}

internal sealed class DatabaseConnectionOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class OperationalPolicyOptions
{
    public const string SectionName = "OperationalPolicy";

    public int UploadUrlMinutes { get; set; } = 10;
    public int UploadSessionHours { get; set; } = 24;
    public int StagingHours { get; set; } = 24;
    public int SupersededArtworkDays { get; set; } = 30;
    public int UnpaidProjectDays { get; set; } = 30;
    public int PaidSourceDays { get; set; } = 90;
    public int PaidOutputDays { get; set; } = 365;
    public int ExplicitDeletionDays { get; set; } = 7;
    public int DeletionFenceMinutes { get; set; } = 15;
    public int IdempotencyDays { get; set; } = 7;
    public int RetentionSweepMinutes { get; set; } = 60;
    public bool RetentionSweepEnabled { get; set; }
}

public sealed class MediaToolsOptions
{
    public const string SectionName = "MediaTools";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string ProcessorVersion { get; set; } = "ingest-v1";
    public int ProcessTimeoutSeconds { get; set; } = 180;
    public string WorkRoot { get; set; } = "";
}
