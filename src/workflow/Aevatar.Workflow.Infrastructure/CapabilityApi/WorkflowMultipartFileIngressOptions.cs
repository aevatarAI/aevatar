namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class WorkflowMultipartFileIngressOptions
{
    public const string SectionName = "WorkflowMultipartFileIngress";

    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    public List<string> AllowedMediaTypes { get; } =
    [
        "image/png",
        "image/jpeg",
        "image/webp",
        "audio/mpeg",
        "audio/wav",
        "audio/wave",
        "audio/x-wav",
        "video/mp4",
    ];
}
