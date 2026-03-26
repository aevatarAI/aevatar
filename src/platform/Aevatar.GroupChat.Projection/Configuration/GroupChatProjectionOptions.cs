using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.GroupChat.Projection.Configuration;

public sealed class GroupChatProjectionOptions : IProjectionRuntimeOptions
{
    public bool Enabled { get; set; } = true;

    public bool EnableRunQueryEndpoints { get; set; } = true;

    public bool EnableRunReportDocuments { get; set; } = false;

    public int RunProjectionCompletionWaitTimeoutMs { get; set; } = 3000;
}
