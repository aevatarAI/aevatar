using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

internal static class ResponsesProtoPayloads
{
    // refactor helper, no behavior change
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
