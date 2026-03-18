using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tools.Cli.Hosting;

public static class AppScriptProtocol
{
    public const string InputField = "input";
    public const string OutputField = "output";
    public const string StatusField = "status";
    public const string LastCommandIdField = "last_command_id";
    public const string NotesField = "notes";

    public static Struct CreateState(
        string input,
        string output,
        string status,
        string? lastCommandId = null,
        IEnumerable<string>? notes = null)
    {
        var state = new Struct();
        SetString(state, InputField, input);
        SetString(state, OutputField, output);
        SetString(state, StatusField, status);
        SetString(state, LastCommandIdField, lastCommandId);
        SetStringList(state, NotesField, notes);
        return state;
    }

    public static string GetString(Struct? payload, string fieldName)
    {
        if (payload == null ||
            string.IsNullOrWhiteSpace(fieldName) ||
            !payload.Fields.TryGetValue(fieldName, out var value))
        {
            return string.Empty;
        }

        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue ?? string.Empty,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => string.Empty,
        };
    }

    public static IReadOnlyList<string> GetStringList(Struct? payload, string fieldName)
    {
        if (payload == null ||
            string.IsNullOrWhiteSpace(fieldName) ||
            !payload.Fields.TryGetValue(fieldName, out var value) ||
            value.KindCase != Value.KindOneofCase.ListValue)
        {
            return [];
        }

        return value.ListValue.Values
            .Where(static item => item.KindCase == Value.KindOneofCase.StringValue)
            .Select(static item => item.StringValue ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static void SetString(Struct target, string fieldName, string? value)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(fieldName))
            return;

        target.Fields[fieldName] = new Value
        {
            StringValue = value ?? string.Empty,
        };
    }

    private static void SetStringList(Struct target, string fieldName, IEnumerable<string>? values)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(fieldName))
            return;

        var list = new ListValue();
        if (values != null)
        {
            foreach (var item in values)
            {
                if (string.IsNullOrWhiteSpace(item))
                    continue;

                list.Values.Add(new Value
                {
                    StringValue = item,
                });
            }
        }

        target.Fields[fieldName] = new Value
        {
            ListValue = list,
        };
    }
}
