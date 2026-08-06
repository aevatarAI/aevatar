namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal static class NyxIdOperationHeaderPolicy
{
    private const int MaximumConditionalHeaderLength = 1024;

    public static bool IsValidWorkflowHeader(string name, string value) =>
        string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(value, "application/json", StringComparison.OrdinalIgnoreCase)
            : name is not null &&
              (string.Equals(name, "If-Match", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "If-None-Match", StringComparison.OrdinalIgnoreCase)) &&
              IsValidConditionalValue(value);

    private static bool IsValidConditionalValue(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= MaximumConditionalHeaderLength &&
        !value.Contains('\r') &&
        !value.Contains('\n');
}
