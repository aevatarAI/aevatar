using Aevatar.Tools.Cli.Studio.Application.Contracts;

namespace Aevatar.Tools.Cli.Studio.Application.Services;

public sealed class TextDiffService
{
    public IReadOnlyList<DiffLine> BuildLineDiff(string? before, string? after)
    {
        var leftLines = SplitLines(before);
        var rightLines = SplitLines(after);

        var lcs = BuildLcs(leftLines, rightLines);
        var results = new List<DiffLine>();

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftLines.Length && rightIndex < rightLines.Length)
        {
            if (leftLines[leftIndex] == rightLines[rightIndex])
            {
                results.Add(new DiffLine(leftIndex + 1, rightIndex + 1, "equal", leftLines[leftIndex]));
                leftIndex++;
                rightIndex++;
            }
            else if (lcs[leftIndex + 1, rightIndex] >= lcs[leftIndex, rightIndex + 1])
            {
                results.Add(new DiffLine(leftIndex + 1, null, "removed", leftLines[leftIndex]));
                leftIndex++;
            }
            else
            {
                results.Add(new DiffLine(null, rightIndex + 1, "added", rightLines[rightIndex]));
                rightIndex++;
            }
        }

        while (leftIndex < leftLines.Length)
        {
            results.Add(new DiffLine(leftIndex + 1, null, "removed", leftLines[leftIndex]));
            leftIndex++;
        }

        while (rightIndex < rightLines.Length)
        {
            results.Add(new DiffLine(null, rightIndex + 1, "added", rightLines[rightIndex]));
            rightIndex++;
        }

        return results;
    }

    private static string[] SplitLines(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

    private static int[,] BuildLcs(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var result = new int[left.Count + 1, right.Count + 1];
        for (var i = left.Count - 1; i >= 0; i--)
        {
            for (var j = right.Count - 1; j >= 0; j--)
            {
                result[i, j] = left[i] == right[j]
                    ? result[i + 1, j + 1] + 1
                    : Math.Max(result[i + 1, j], result[i, j + 1]);
            }
        }

        return result;
    }
}
