namespace Aevatar.Tools.Cli.Hosting;

public static class AppScriptProtocol
{
    public const string InputField = "input";
    public const string OutputField = "output";
    public const string StatusField = "status";
    public const string LastCommandIdField = "last_command_id";
    public const string NotesField = "notes";

    public static AppScriptCommand CreateCommand(
        string input,
        string? commandId = null) =>
        new()
        {
            Input = input ?? string.Empty,
            CommandId = commandId ?? string.Empty,
        };

    public static AppScriptReadModel CreateState(
        string input,
        string output,
        string status,
        string? lastCommandId = null,
        IEnumerable<string>? notes = null)
    {
        var state = new AppScriptReadModel
        {
            Input = input ?? string.Empty,
            Output = output ?? string.Empty,
            Status = status ?? string.Empty,
            LastCommandId = lastCommandId ?? string.Empty,
        };
        if (notes != null)
        {
            state.Notes.AddRange(notes.Where(static item => !string.IsNullOrWhiteSpace(item)));
        }

        return state;
    }

    public static string GetString(AppScriptReadModel? payload, string fieldName)
    {
        if (payload == null || string.IsNullOrWhiteSpace(fieldName))
            return string.Empty;

        return fieldName switch
        {
            InputField => payload.Input ?? string.Empty,
            OutputField => payload.Output ?? string.Empty,
            StatusField => payload.Status ?? string.Empty,
            LastCommandIdField => payload.LastCommandId ?? string.Empty,
            _ => string.Empty,
        };
    }

    public static IReadOnlyList<string> GetStringList(AppScriptReadModel? payload, string fieldName)
    {
        if (payload == null ||
            string.IsNullOrWhiteSpace(fieldName) ||
            !string.Equals(fieldName, NotesField, StringComparison.Ordinal))
        {
            return [];
        }

        return payload.Notes
            .Select(static item => item ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
}
