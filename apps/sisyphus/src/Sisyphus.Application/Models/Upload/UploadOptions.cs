namespace Sisyphus.Application.Models.Upload;

public sealed class UploadOptions
{
    public const string SectionName = "Sisyphus:Upload";

    public int ApiBatchSize { get; set; } = 50;
    public int PurgeBatchSize { get; set; } = 30;
    public int PurgeMaxRetries { get; set; } = 3;
    public int PurgeConcurrency { get; set; } = 10;
}
