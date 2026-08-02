using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Aevatar.Workflow.Core.Primitives;

public enum WorkflowYamlResourceLimitKind
{
    Utf8Bytes = 1,
    Nodes = 2,
    NestingDepth = 3,
}

public sealed class WorkflowYamlResourceLimitException : InvalidOperationException
{
    public WorkflowYamlResourceLimitException(
        WorkflowYamlResourceLimitKind limitKind,
        int actual,
        int maximum)
        : base($"Workflow YAML {Format(limitKind)} limit exceeded: {actual} > {maximum}.")
    {
        LimitKind = limitKind;
        Actual = actual;
        Maximum = maximum;
    }

    public WorkflowYamlResourceLimitKind LimitKind { get; }

    public int Actual { get; }

    public int Maximum { get; }

    private static string Format(WorkflowYamlResourceLimitKind kind) => kind switch
    {
        WorkflowYamlResourceLimitKind.Utf8Bytes => "UTF-8 byte",
        WorkflowYamlResourceLimitKind.Nodes => "node count",
        WorkflowYamlResourceLimitKind.NestingDepth => "nesting depth",
        _ => "resource",
    };
}

public static class WorkflowYamlResourceGuard
{
    public const int MaxUtf8Bytes = 1024 * 1024;
    public const int MaxNodes = 10_000;
    public const int MaxNestingDepth = 64;

    public static void Validate(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var utf8Bytes = Encoding.UTF8.GetByteCount(yaml);
        ThrowIfExceeded(WorkflowYamlResourceLimitKind.Utf8Bytes, utf8Bytes, MaxUtf8Bytes);

        var parser = new Parser(new StringReader(yaml));
        var nodes = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart:
                case SequenceStart:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.NestingDepth, ++depth, MaxNestingDepth);
                    break;
                case Scalar:
                case AnchorAlias:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    break;
                case MappingEnd:
                case SequenceEnd:
                    depth--;
                    break;
            }
        }
    }

    private static void ThrowIfExceeded(WorkflowYamlResourceLimitKind kind, int actual, int maximum)
    {
        if (actual > maximum)
            throw new WorkflowYamlResourceLimitException(kind, actual, maximum);
    }
}
