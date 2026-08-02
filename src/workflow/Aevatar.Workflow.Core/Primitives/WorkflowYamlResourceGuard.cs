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
        var resourceGraph = new WorkflowYamlResourceGraph();
        var nodes = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    resourceGraph.BeginDocument();
                    break;
                case DocumentEnd:
                    resourceGraph.EndDocument();
                    break;
                case MappingStart mapping:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.NestingDepth, ++depth, MaxNestingDepth);
                    resourceGraph.AddCollection(mapping.Anchor);
                    break;
                case SequenceStart sequence:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.NestingDepth, ++depth, MaxNestingDepth);
                    resourceGraph.AddCollection(sequence.Anchor);
                    break;
                case Scalar scalar:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    resourceGraph.AddScalar(scalar.Anchor);
                    break;
                case AnchorAlias alias:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    resourceGraph.AddAlias(alias.Value);
                    break;
                case MappingEnd:
                case SequenceEnd:
                    depth--;
                    resourceGraph.EndCollection();
                    break;
            }
        }

        resourceGraph.ValidateExpandedLimits();
    }

    private static void ThrowIfExceeded(WorkflowYamlResourceLimitKind kind, int actual, int maximum)
    {
        if (actual > maximum)
            throw new WorkflowYamlResourceLimitException(kind, actual, maximum);
    }

    private sealed class WorkflowYamlResourceGraph
    {
        private readonly List<ResourceNode> _nodes = [];
        private readonly List<int> _roots = [];
        private readonly Stack<int> _collections = new();
        private readonly Dictionary<string, int> _anchors = new(StringComparer.Ordinal);
        private readonly List<UnresolvedAlias> _unresolvedAliases = [];

        public void BeginDocument()
        {
            _anchors.Clear();
            _unresolvedAliases.Clear();
        }

        public void EndDocument()
        {
            ResolveForwardAliases();
            _unresolvedAliases.Clear();
        }

        public void AddCollection(AnchorName anchor)
        {
            var index = AddNode(ResourceNode.Collection(), anchor);
            _collections.Push(index);
        }

        public void AddScalar(AnchorName anchor)
        {
            AddNode(ResourceNode.Scalar(), anchor);
        }

        public void AddAlias(AnchorName alias)
        {
            if (_anchors.TryGetValue(alias.Value, out var target))
            {
                AddNode(ResourceNode.Alias(target), AnchorName.Empty);
                return;
            }

            var index = AddNode(ResourceNode.Alias(), AnchorName.Empty);
            _unresolvedAliases.Add(new UnresolvedAlias(index, alias.Value));
        }

        public void EndCollection()
        {
            _collections.Pop();
        }

        public void ValidateExpandedLimits()
        {
            var expandedNodes = 0;
            var activeCollections = new HashSet<int>();
            var pending = new Stack<TraversalFrame>();

            for (var index = _roots.Count - 1; index >= 0; index--)
                pending.Push(TraversalFrame.Enter(_roots[index], depth: 0));

            while (pending.TryPop(out var frame))
            {
                if (frame.IsExit)
                {
                    activeCollections.Remove(frame.NodeIndex);
                    continue;
                }

                var node = _nodes[frame.NodeIndex];
                if (node.Kind == ResourceNodeKind.Alias)
                {
                    if (node.AliasTarget >= 0)
                        pending.Push(TraversalFrame.Enter(node.AliasTarget, frame.Depth));
                    else
                        ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++expandedNodes, MaxNodes);
                    continue;
                }

                ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++expandedNodes, MaxNodes);
                if (node.Kind != ResourceNodeKind.Collection)
                    continue;

                if (activeCollections.Contains(frame.NodeIndex))
                {
                    throw new WorkflowYamlResourceLimitException(
                        WorkflowYamlResourceLimitKind.NestingDepth,
                        MaxNestingDepth + 1,
                        MaxNestingDepth);
                }

                var collectionDepth = frame.Depth + 1;
                ThrowIfExceeded(
                    WorkflowYamlResourceLimitKind.NestingDepth,
                    collectionDepth,
                    MaxNestingDepth);
                activeCollections.Add(frame.NodeIndex);
                pending.Push(TraversalFrame.Exit(frame.NodeIndex));

                var children = node.Children!;
                for (var index = children.Count - 1; index >= 0; index--)
                    pending.Push(TraversalFrame.Enter(children[index], collectionDepth));
            }
        }

        private int AddNode(ResourceNode node, AnchorName anchor)
        {
            var index = _nodes.Count;
            _nodes.Add(node);
            if (_collections.TryPeek(out var parent))
                _nodes[parent].Children!.Add(index);
            else
                _roots.Add(index);

            if (!anchor.IsEmpty)
                _anchors[anchor.Value] = index;
            return index;
        }

        private void ResolveForwardAliases()
        {
            foreach (var alias in _unresolvedAliases)
            {
                if (_anchors.TryGetValue(alias.Anchor, out var target))
                    _nodes[alias.NodeIndex].ResolveAlias(target);
            }
        }
    }

    private enum ResourceNodeKind
    {
        Scalar,
        Collection,
        Alias,
    }

    private sealed class ResourceNode
    {
        private ResourceNode(ResourceNodeKind kind, int aliasTarget = -1)
        {
            Kind = kind;
            AliasTarget = aliasTarget;
            Children = kind == ResourceNodeKind.Collection ? [] : null;
        }

        public ResourceNodeKind Kind { get; }

        public int AliasTarget { get; private set; }

        public List<int>? Children { get; }

        public static ResourceNode Scalar() => new(ResourceNodeKind.Scalar);

        public static ResourceNode Collection() => new(ResourceNodeKind.Collection);

        public static ResourceNode Alias(int target = -1) => new(ResourceNodeKind.Alias, target);

        public void ResolveAlias(int target)
        {
            AliasTarget = target;
        }
    }

    private readonly record struct UnresolvedAlias(int NodeIndex, string Anchor);

    private readonly record struct TraversalFrame(int NodeIndex, int Depth, bool IsExit)
    {
        public static TraversalFrame Enter(int nodeIndex, int depth) => new(nodeIndex, depth, IsExit: false);

        public static TraversalFrame Exit(int nodeIndex) => new(nodeIndex, Depth: 0, IsExit: true);
    }
}
