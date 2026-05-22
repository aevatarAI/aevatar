using System.Text.Json;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Application;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: JSON A2A DTOs were stored directly in a process-local ledger.
//   New principle: Host DTOs map to typed protobuf before entering actor/readmodel lifecycle.
public static class A2ATaskModelMapper
{
    public static A2ATaskMessage ToProto(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new A2ATaskMessage
        {
            Role = message.Role,
        };

        result.Parts.AddRange(message.Parts.Select(ToProto));
        CopyMap(message.Metadata, result.Metadata);
        return result;
    }

    public static A2ATask ToDto(A2ATaskState state, int? historyLength = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var history = state.History.Select(ToDto).ToList();
        if (historyLength is >= 0 and var len && len < history.Count)
            history = history.GetRange(history.Count - len, len);

        var result = new A2ATask
        {
            Id = state.TaskId,
            SessionId = string.IsNullOrWhiteSpace(state.SessionId) ? null : state.SessionId,
            Status = ToDto(state.Status),
            History = history,
            Artifacts = state.Artifacts.Select(ToDto).ToList(),
            Metadata = state.Metadata.Count == 0 ? null : new Dictionary<string, string>(state.Metadata),
        };

        return result;
    }

    public static A2ATaskStatus ToDto(A2ATaskStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new A2ATaskStatus
        {
            State = ToDto(status.State),
            Message = status.Message is { Parts.Count: > 0 } ? ToDto(status.Message) : null,
            Timestamp = status.Timestamp?.ToDateTime().ToString("O"),
        };
    }

    public static Artifact ToDto(A2ATaskArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new Artifact
        {
            Name = string.IsNullOrWhiteSpace(artifact.Name) ? null : artifact.Name,
            Description = string.IsNullOrWhiteSpace(artifact.Description) ? null : artifact.Description,
            Parts = artifact.Parts.Select(ToDto).ToArray(),
            Index = artifact.Index,
            Metadata = artifact.Metadata.Count == 0 ? null : new Dictionary<string, string>(artifact.Metadata),
        };
    }

    public static A2ATaskStatusSnapshot BuildStatus(
        A2ATaskLifecycleState state,
        Timestamp timestamp,
        A2ATaskMessage? message = null)
    {
        var result = new A2ATaskStatusSnapshot
        {
            State = state,
            Timestamp = timestamp,
        };
        if (message != null)
            result.Message = message;
        return result;
    }

    public static A2ATaskPart ToProto(Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var result = new A2ATaskPart
        {
            Type = part.Type,
        };
        CopyMap(part.Metadata, result.Metadata);

        switch (part)
        {
            case TextPart text:
                result.Text = text.Text;
                break;
            case FilePart file:
                result.FileName = file.File.Name ?? string.Empty;
                result.FileMimeType = file.File.MimeType ?? string.Empty;
                result.FileBytes = file.File.Bytes ?? string.Empty;
                result.FileUri = file.File.Uri ?? string.Empty;
                break;
            case DataPart data:
                result.DataEntries.AddRange(data.Data.Select(entry => new A2ATaskPartDataEntry
                {
                    Key = entry.Key,
                    ValueJson = JsonSerializer.Serialize(entry.Value),
                }));
                break;
        }

        return result;
    }

    private static Message ToDto(A2ATaskMessage message) =>
        new()
        {
            Role = message.Role,
            Parts = message.Parts.Select(ToDto).ToArray(),
            Metadata = message.Metadata.Count == 0 ? null : new Dictionary<string, string>(message.Metadata),
        };

    private static Part ToDto(A2ATaskPart part) =>
        part.Type switch
        {
            "text" => new TextPart
            {
                Text = part.Text,
                Metadata = part.Metadata.Count == 0 ? null : new Dictionary<string, string>(part.Metadata),
            },
            "file" => new FilePart
            {
                File = new FileContent
                {
                    Name = string.IsNullOrWhiteSpace(part.FileName) ? null : part.FileName,
                    MimeType = string.IsNullOrWhiteSpace(part.FileMimeType) ? null : part.FileMimeType,
                    Bytes = string.IsNullOrWhiteSpace(part.FileBytes) ? null : part.FileBytes,
                    Uri = string.IsNullOrWhiteSpace(part.FileUri) ? null : part.FileUri,
                },
                Metadata = part.Metadata.Count == 0 ? null : new Dictionary<string, string>(part.Metadata),
            },
            "data" => new DataPart
            {
                Data = part.DataEntries.ToDictionary(
                    entry => entry.Key,
                    entry => JsonSerializer.Deserialize<object?>(entry.ValueJson)),
                Metadata = part.Metadata.Count == 0 ? null : new Dictionary<string, string>(part.Metadata),
            },
            _ => new TextPart
            {
                Text = part.Text,
                Metadata = part.Metadata.Count == 0 ? null : new Dictionary<string, string>(part.Metadata),
            },
        };

    private static TaskState ToDto(A2ATaskLifecycleState state) =>
        state switch
        {
            A2ATaskLifecycleState.Submitted => TaskState.Submitted,
            A2ATaskLifecycleState.Working => TaskState.Working,
            A2ATaskLifecycleState.InputRequired => TaskState.InputRequired,
            A2ATaskLifecycleState.Completed => TaskState.Completed,
            A2ATaskLifecycleState.Canceled => TaskState.Canceled,
            A2ATaskLifecycleState.Failed => TaskState.Failed,
            A2ATaskLifecycleState.Unknown => TaskState.Unknown,
            _ => TaskState.Unknown,
        };

    private static void CopyMap(IReadOnlyDictionary<string, string>? source, IDictionary<string, string> target)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
            target[key] = value;
    }
}
