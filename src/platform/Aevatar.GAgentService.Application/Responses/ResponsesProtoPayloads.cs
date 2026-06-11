using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

internal static class ResponsesProtoPayloads
{
    // Refactor (iter355/issue1438-first): Old pattern: Responses command builders persisted tool payload objects only as JSON strings. New principle: typed Struct fields carry new writes; empty Struct is the legacy/invalid JSON fallback.
    public static Struct ParseStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Struct();

        try
        {
            return JsonParser.Default.Parse<Struct>(json);
        }
        catch
        {
            return new Struct();
        }
    }
}
