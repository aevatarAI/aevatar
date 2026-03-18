namespace Aevatar.Tools.Cli.Hosting;

internal static class AppWorkflowYamlFiles
{
    public static string NormalizeSaveFilename(string? requestedFilename, string workflowName)
    {
        var candidate = string.IsNullOrWhiteSpace(requestedFilename)
            ? workflowName
            : requestedFilename.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("workflow filename is required");

        var fileNameOnly = Path.GetFileName(candidate);
        if (!string.Equals(fileNameOnly, candidate, StringComparison.Ordinal))
            throw new InvalidOperationException("workflow filename must not include directory segments");

        var stem = Path.GetFileNameWithoutExtension(fileNameOnly);
        if (string.IsNullOrWhiteSpace(stem))
            throw new InvalidOperationException("workflow filename is invalid");

        var sanitizedChars = stem
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray();
        var sanitizedStem = new string(sanitizedChars)
            .Trim('_');
        while (sanitizedStem.Contains("__", StringComparison.Ordinal))
            sanitizedStem = sanitizedStem.Replace("__", "_", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(sanitizedStem))
            throw new InvalidOperationException("workflow filename must contain letters or digits");

        return sanitizedStem + ".yaml";
    }

    public static string NormalizeContentForSave(string yaml) =>
        (yaml ?? string.Empty).Trim() + Environment.NewLine;
}
