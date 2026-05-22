using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using Google.Protobuf.Collections;
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
            Metadata = ToNullableDictionary(state.Metadata),
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
            Metadata = ToNullableDictionary(artifact.Metadata),
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
                    Value = ToValue(entry.Value),
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
            Metadata = ToNullableDictionary(message.Metadata),
        };

    private static Part ToDto(A2ATaskPart part) =>
        part.Type switch
        {
            "text" => new TextPart
            {
                Text = part.Text,
                Metadata = ToNullableDictionary(part.Metadata),
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
                Metadata = ToNullableDictionary(part.Metadata),
            },
            "data" => new DataPart
            {
                Data = part.DataEntries.ToDictionary(
                    entry => entry.Key,
                    entry => ToObject(entry.Value)),
                Metadata = ToNullableDictionary(part.Metadata),
            },
            _ => new TextPart
            {
                Text = part.Text,
                Metadata = ToNullableDictionary(part.Metadata),
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

    private static Dictionary<string, string>? ToNullableDictionary(MapField<string, string> metadata) =>
        metadata.Count == 0 ? null : new Dictionary<string, string>(metadata);

    private static Value ToValue(object? value) =>
        value switch
        {
            null => Value.ForNull(),
            Value protobufValue => protobufValue.Clone(),
            string text => Value.ForString(text),
            bool boolean => Value.ForBool(boolean),
            int number => Value.ForNumber(number),
            long number => Value.ForNumber(number),
            float number => Value.ForNumber(number),
            double number => Value.ForNumber(number),
            decimal number => Value.ForNumber((double)number),
            IReadOnlyDictionary<string, object?> map => Value.ForStruct(ToStruct(map)),
            IDictionary<string, object?> map => Value.ForStruct(ToStruct(map.AsReadOnly())),
            IEnumerable<object?> list => Value.ForList(list.Select(ToValue).ToArray()),
            _ => Value.ForString(value.ToString() ?? string.Empty),
        };

    private static Struct ToStruct(IReadOnlyDictionary<string, object?> map)
    {
        var result = new Struct();
        foreach (var (key, value) in map)
            result.Fields[key] = ToValue(value);
        return result;
    }

    private static object? ToObject(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => value.NumberValue,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(
                entry => entry.Key,
                entry => ToObject(entry.Value)),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ToObject).ToList(),
            _ => null,
        };
}
