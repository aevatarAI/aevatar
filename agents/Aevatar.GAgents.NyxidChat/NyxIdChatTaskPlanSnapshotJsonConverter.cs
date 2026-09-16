using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatStateJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new NyxIdChatTaskPlanSnapshotJsonConverter());
        return options;
    }
}

internal sealed class NyxIdChatTaskPlanSnapshotJsonConverter
    : JsonConverter<NyxIdChatConversationTaskSnapshot>
{
    public override NyxIdChatConversationTaskSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("TaskPlan current-state JSON is write-only.");

    public override void Write(
        Utf8JsonWriter writer,
        NyxIdChatConversationTaskSnapshot value,
        JsonSerializerOptions options)
    {
        var taskPlan = NyxIdChatTaskPlanWireMapper.FromSnapshot(value);
        NyxIdChatTaskPlanJsonFormatter.FormatTaskPlan(taskPlan).WriteTo(writer, options);
    }
}
