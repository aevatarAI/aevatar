using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.Lark;

internal static partial class LarkSheetsRangeHelper
{
    [GeneratedRegex("^[A-Za-z]+[1-9][0-9]*$")]
    private static partial Regex SingleCellPattern();

    [GeneratedRegex("^[A-Za-z]+[1-9][0-9]*:[A-Za-z]+[1-9][0-9]*$")]
    private static partial Regex CellSpanPattern();

    [GeneratedRegex("^[A-Za-z]+[1-9][0-9]*:[A-Za-z]+$")]
    private static partial Regex CellToColumnPattern();

    [GeneratedRegex("^[A-Za-z]+:[A-Za-z]+$")]
    private static partial Regex ColumnSpanPattern();

    [GeneratedRegex("^[1-9][0-9]*:[1-9][0-9]*$")]
    private static partial Regex RowSpanPattern();

    public static string ExtractSpreadsheetToken(string value)
    {
        var input = value.Trim();
        foreach (var prefix in new[] { "/sheets/", "/spreadsheets/" })
        {
            var index = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var token = input[(index + prefix.Length)..];
            var end = token.IndexOfAny(['/', '?', '#']);
            return end >= 0 ? token[..end] : token;
        }

        return input;
    }

    public static bool TryResolveAppendRange(
        string? sheetId,
        string? range,
        out string? resolvedRange,
        out string? error)
    {
        resolvedRange = null;
        error = null;

        var normalizedSheetId = string.IsNullOrWhiteSpace(sheetId) ? null : sheetId.Trim();
        var normalizedRange = NormalizeSeparators(range);

        if (string.IsNullOrWhiteSpace(normalizedRange))
        {
            if (string.IsNullOrWhiteSpace(normalizedSheetId))
            {
                error = "sheet_id or range is required.";
                return false;
            }

            resolvedRange = normalizedSheetId;
            return true;
        }

        if (!normalizedRange.Contains('!') && LooksLikeRelativeRange(normalizedRange))
        {
            if (string.IsNullOrWhiteSpace(normalizedSheetId))
            {
                error = "range without a sheet prefix requires sheet_id.";
                return false;
            }

            normalizedRange = $"{normalizedSheetId}!{normalizedRange}";
        }

        resolvedRange = NormalizeSingleCellRange(normalizedRange);
        return true;
    }

    private static string NormalizeSingleCellRange(string input)
    {
        var splitIndex = input.IndexOf('!');
        if (splitIndex < 0 || splitIndex == input.Length - 1)
            return input;

        var sheetId = input[..splitIndex];
        var subRange = input[(splitIndex + 1)..];
        if (!SingleCellPattern().IsMatch(subRange))
            return input;

        return $"{sheetId}!{subRange}:{subRange}";
    }

    private static bool LooksLikeRelativeRange(string input) =>
        SingleCellPattern().IsMatch(input) ||
        CellSpanPattern().IsMatch(input) ||
        CellToColumnPattern().IsMatch(input) ||
        ColumnSpanPattern().IsMatch(input) ||
        RowSpanPattern().IsMatch(input);

    private static string NormalizeSeparators(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input
            .Trim()
            .Replace(@"\！", "!", StringComparison.Ordinal)
            .Replace(@"\!", "!", StringComparison.Ordinal)
            .Replace("！", "!", StringComparison.Ordinal);
    }
}
