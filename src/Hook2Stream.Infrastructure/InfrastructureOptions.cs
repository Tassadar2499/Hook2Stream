namespace Hook2Stream.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public string PublicServiceUrl { get; set; } = "http://localhost:9000";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "hook2stream-media";
    public string AccessKey { get; set; } = "hook2stream";
    public string SecretKey { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
    public bool RequireCredentials { get; set; }
    public bool ConfigureBucketCors { get; set; }
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
