using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.Architecture.Tests.Rules;

/// <summary>
/// Channel RFC §14.1 Layer 2 semantic guards. These tests back the analyzer / Architecture.Tests rules that cannot
/// be expressed with simple grep-style shell guards.
/// </summary>
public sealed class ChannelArchitectureTests
{
    private static readonly string RepoRoot = ChannelSourceIndex.RepoRoot;

    private static readonly ImmutableArray<string> ForbiddenNonDeterministicMembers = ImmutableArray.Create(
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTime.Today",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "Guid.NewGuid",
        "Environment.TickCount",
        "Environment.TickCount64",
        "Random.Shared");

    [Fact]
    public void CanonicalKeyGenerator_MethodBodies_ShouldBe_Pure()
    {
        var violations = ChannelSourceIndex.CollectAttributedMethodViolations(
            attributeName: "CanonicalKeyGenerator",
            forbiddenExpressions: ForbiddenNonDeterministicMembers,
            forbiddenTypeNames: ImmutableArray.Create("Random"));

        Assert.True(
            violations.Count == 0,
            "CanonicalKey generator methods must remain deterministic for retry / replay / dedup. "
            + "Forbidden calls detected:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ActivityIdGenerator_MethodBodies_ShouldBe_RetryStable()
    {
        var violations = ChannelSourceIndex.CollectAttributedMethodViolations(
            attributeName: "ActivityIdGenerator",
            forbiddenExpressions: ForbiddenNonDeterministicMembers,
            forbiddenTypeNames: ImmutableArray.Create("Random"));

        Assert.True(
            violations.Count == 0,
            "ActivityId generator methods must derive retry-stable ids from platform delivery keys. "
            + "Forbidden non-deterministic calls detected:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void OutboundSendAsync_Callers_Must_StayInsideAdapters_Or_TurnContext()
    {
        // Rule: business source must not hit IChannelOutboundPort.SendAsync directly. Allowed call sites:
        //   1. the adapter implementations under agents/channels/** (they own the transport)
        //   2. the ITurnContext / TurnContext implementations in the abstractions (they proxy the call
        //      back through the adapter using the turn-bound credential)
        //   3. the abstraction interface itself (IChannelOutboundPort.cs)
        //   4. conformance / surface test harnesses under test/**

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            if (!File.ReadAllText(sourceFile).Contains(".SendAsync"))
            {
                continue;
            }

            var normalized = ChannelSourceIndex.NormalizePath(sourceFile);
            if (IsAllowedOutboundSendCaller(normalized))
            {
                continue;
            }

            var invocationReport = FindForbiddenInvocations(sourceFile, receiverTypeHint: "IChannelOutboundPort", methodName: "SendAsync");
            if (invocationReport.Count > 0)
            {
                violators.AddRange(invocationReport.Select(line => $"{normalized}: {line}"));
            }
        }

        Assert.True(
            violators.Count == 0,
            "Business types must go through ITurnContext / adapter internals to reach IChannelOutboundPort.SendAsync. "
            + "Forbidden call sites:\n" + string.Join("\n", violators));
    }

    [Fact]
    public void ProactiveCallers_MustNot_Invoke_ContinueConversationAsync_Directly()
    {
        // Rule: proactive actor-to-actor paths (SkillRunnerGAgent, WorkflowAgentGAgent, admin endpoint
        // controllers, workflow triggers) must dispatch through ConversationGAgent command envelopes
        // rather than directly invoking IChannelOutboundPort.ContinueConversationAsync.
        //
        // Match by declared type identifier via Roslyn syntax walk, not by filename, so partial
        // classes split across arbitrarily-named files (for example `WorkflowAgentGAgent.Schedule.cs`)
        // still fail the guard.

        var proactiveCallerPatterns = new[]
        {
            "SkillRunnerGAgent",
            "WorkflowAgentGAgent",
            "AdminBroadcast",
            "ChannelBroadcast",
        };

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!text.Contains(".ContinueConversationAsync(", System.StringComparison.Ordinal))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
            var root = tree.GetRoot();
            var normalized = ChannelSourceIndex.NormalizePath(sourceFile);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeName = typeDecl.Identifier.ValueText;
                if (!proactiveCallerPatterns.Any(pattern =>
                    typeName.Contains(pattern, System.StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var invocation in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    {
                        continue;
                    }

                    if (member.Name.Identifier.ValueText != "ContinueConversationAsync")
                    {
                        continue;
                    }

                    var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                    violators.Add($"{normalized}:{typeName}:line {line}: {invocation.ToString().Trim()}");
                }
            }
        }

        Assert.True(
            violators.Count == 0,
            "Proactive callers must dispatch through ConversationGAgent command envelopes, not "
            + "IChannelOutboundPort.ContinueConversationAsync directly. Violating call sites:\n"
            + string.Join("\n", violators));
    }

    [Fact]
    public void RawPayload_BlobWrites_Must_RouteThrough_PayloadRedactor()
    {
        // Rule: any IBlobStore.WriteAsync invocation that forwards a raw payload must receive the
        // bytes back from IPayloadRedactor.Redact first. Until IBlobStore lands in the repo this
        // guard is vacuously satisfied; once the type exists, any call site that mentions a raw
        // payload without also mentioning the redactor fails the guard.

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!text.Contains("IBlobStore"))
            {
                continue;
            }

            // Flag call sites where we hand raw payload bytes / RawPayload-typed variables to
            // IBlobStore.WriteAsync without also routing through IPayloadRedactor in the same file.
            var mentionsRawPayload = Regex.IsMatch(text, @"\bRawPayload\b")
                || text.Contains("rawPayload")
                || text.Contains("RawPayloadBlobRef");

            if (!mentionsRawPayload)
            {
                continue;
            }

            if (text.Contains("IPayloadRedactor") || text.Contains("PayloadRedactor.Redact"))
            {
                continue;
            }

            if (!Regex.IsMatch(text, @"IBlobStore\b\s*\.?\s*WriteAsync\("))
            {
                continue;
            }

            violators.Add(ChannelSourceIndex.NormalizePath(sourceFile));
        }

        Assert.True(
            violators.Count == 0,
            "Raw payload blob writes must be routed through IPayloadRedactor.Redact before IBlobStore.WriteAsync. "
            + "Files that appear to skip the redactor:\n" + string.Join("\n", violators));
    }

    [Fact]
    public void DurableInboxImplementations_Must_DependOn_AsyncStream_ChatActivity()
    {
        // Rule: any type implementing IChannelDurableInbox must depend on
        // Orleans.Streams.IAsyncStream<ChatActivity> for durability, not in-process substitutes
        // such as ConcurrentQueue or System.Threading.Channels.Channel.

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!Regex.IsMatch(text, @"\bIChannelDurableInbox\b"))
            {
                continue;
            }

            // Skip the abstraction interface declaration itself — the rule targets implementations.
            if (Regex.IsMatch(text, @"\binterface\s+IChannelDurableInbox\b"))
            {
                continue;
            }

            var dependsOnAsyncStream = Regex.IsMatch(text, @"IAsyncStream<\s*ChatActivity\s*>");
            var usesForbiddenStorage =
                Regex.IsMatch(text, @"\bConcurrentQueue\s*<\s*ChatActivity")
                || Regex.IsMatch(text, @"System\.Threading\.Channels\.Channel\s*<\s*ChatActivity")
                || Regex.IsMatch(text, @"\bChannel<\s*ChatActivity\s*>\s*\.");

            if (!dependsOnAsyncStream || usesForbiddenStorage)
            {
                violators.Add(ChannelSourceIndex.NormalizePath(sourceFile));
            }
        }

        Assert.True(
            violators.Count == 0,
            "Durable inbox implementations must use IAsyncStream<ChatActivity> and never substitute "
            + "in-process ConcurrentQueue / Channel storage. Violating files:\n"
            + string.Join("\n", violators));
    }

    private static bool IsAllowedOutboundSendCaller(string normalizedPath)
    {
        if (normalizedPath.Contains("/agents/channels/", System.StringComparison.Ordinal))
        {
            return true;
        }

        if (normalizedPath.EndsWith("/agents/Aevatar.GAgents.Channel.Abstractions/Transport/IChannelOutboundPort.cs", System.StringComparison.Ordinal))
        {
            return true;
        }

        if (normalizedPath.EndsWith("/agents/Aevatar.GAgents.Channel.Abstractions/Bots/ITurnContext.cs", System.StringComparison.Ordinal))
        {
            return true;
        }

        // TurnContext implementations are allowed to forward to the adapter's outbound surface.
        var fileName = Path.GetFileName(normalizedPath);
        if (fileName.StartsWith("TurnContext", System.StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static List<string> FindForbiddenInvocations(string sourceFile, string receiverTypeHint, string methodName)
    {
        var violations = new List<string>();
        var text = File.ReadAllText(sourceFile);

        var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            if (member.Name.Identifier.ValueText != methodName)
            {
                continue;
            }

            var receiverText = member.Expression.ToString();
            if (!receiverText.Contains(receiverTypeHint, System.StringComparison.Ordinal)
                && !TypeHintPresentInParameterList(root, receiverText, receiverTypeHint))
            {
                continue;
            }

            var position = tree.GetLineSpan(invocation.Span).StartLinePosition;
            violations.Add($"line {position.Line + 1}: {invocation.ToString().Trim()}");
        }

        return violations;
    }

    private static bool TypeHintPresentInParameterList(SyntaxNode root, string receiverText, string typeHint)
    {
        // When the call site is `_outbound.SendAsync(...)` we resolve by checking whether any field,
        // property, or parameter in the file is typed as IChannelOutboundPort.
        if (string.IsNullOrEmpty(receiverText))
        {
            return false;
        }

        var simpleReceiver = receiverText.Trim().TrimStart('_');
        foreach (var declaration in root.DescendantNodes())
        {
            switch (declaration)
            {
                case FieldDeclarationSyntax field when field.Declaration.Type.ToString().Contains(typeHint, System.StringComparison.Ordinal):
                    if (field.Declaration.Variables.Any(variable =>
                        variable.Identifier.ValueText.Equals(simpleReceiver, System.StringComparison.OrdinalIgnoreCase)
                        || variable.Identifier.ValueText.Equals(receiverText, System.StringComparison.Ordinal)))
                    {
                        return true;
                    }
                    break;
                case PropertyDeclarationSyntax property when property.Type.ToString().Contains(typeHint, System.StringComparison.Ordinal):
                    if (property.Identifier.ValueText.Equals(simpleReceiver, System.StringComparison.OrdinalIgnoreCase)
                        || property.Identifier.ValueText.Equals(receiverText, System.StringComparison.Ordinal))
                    {
                        return true;
                    }
                    break;
                case ParameterSyntax parameter when parameter.Type?.ToString().Contains(typeHint, System.StringComparison.Ordinal) == true:
                    if (parameter.Identifier.ValueText.Equals(simpleReceiver, System.StringComparison.OrdinalIgnoreCase)
                        || parameter.Identifier.ValueText.Equals(receiverText, System.StringComparison.Ordinal))
                    {
                        return true;
                    }
                    break;
            }
        }

        return false;
    }
}

internal static class ChannelSourceIndex
{
    private static readonly Lazy<string> LazyRepoRoot = new(FindRepoRoot);

    public static string RepoRoot => LazyRepoRoot.Value;

    public static string NormalizePath(string path) => path.Replace('\\', '/');

    public static IEnumerable<string> EnumerateProductionSourceFiles()
    {
        var roots = new[] { Path.Combine(RepoRoot, "src"), Path.Combine(RepoRoot, "agents") };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = NormalizePath(file);
                if (normalized.Contains("/bin/", System.StringComparison.Ordinal)
                    || normalized.Contains("/obj/", System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (normalized.EndsWith(".g.cs", System.StringComparison.Ordinal)
                    || normalized.EndsWith(".designer.cs", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    public static List<string> CollectAttributedMethodViolations(
        string attributeName,
        ImmutableArray<string> forbiddenExpressions,
        ImmutableArray<string> forbiddenTypeNames)
    {
        var violations = new List<string>();

        foreach (var sourceFile in EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!text.Contains("[" + attributeName, System.StringComparison.Ordinal))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
            var root = tree.GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!HasAttribute(method.AttributeLists, attributeName))
                {
                    continue;
                }

                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                foreach (var memberAccess in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    var expression = memberAccess.ToString();
                    foreach (var forbidden in forbiddenExpressions)
                    {
                        if (expression.EndsWith(forbidden, System.StringComparison.Ordinal)
                            || expression.Contains(forbidden, System.StringComparison.Ordinal))
                        {
                            violations.Add($"{NormalizePath(sourceFile)}:{method.Identifier.ValueText}: forbidden '{forbidden}'");
                            break;
                        }
                    }
                }

                foreach (var creation in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    var typeText = creation.Type.ToString();
                    foreach (var forbiddenType in forbiddenTypeNames)
                    {
                        if (typeText == forbiddenType || typeText.EndsWith("." + forbiddenType, System.StringComparison.Ordinal))
                        {
                            violations.Add($"{NormalizePath(sourceFile)}:{method.Identifier.ValueText}: forbidden 'new {forbiddenType}'");
                        }
                    }
                }

                foreach (var identifier in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    foreach (var forbiddenType in forbiddenTypeNames)
                    {
                        if (identifier.Identifier.ValueText == forbiddenType
                            && identifier.Parent is not ObjectCreationExpressionSyntax
                            && identifier.Parent is MemberAccessExpressionSyntax parentAccess
                            && parentAccess.Name is not null)
                        {
                            // Flag static access like Random.Shared.Next()
                            violations.Add($"{NormalizePath(sourceFile)}:{method.Identifier.ValueText}: forbidden '{forbiddenType}.{parentAccess.Name.Identifier.ValueText}'");
                        }
                    }
                }
            }
        }

        return violations;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string attributeName)
    {
        foreach (var list in lists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name == attributeName
                    || name == attributeName + "Attribute"
                    || name.EndsWith("." + attributeName, System.StringComparison.Ordinal)
                    || name.EndsWith("." + attributeName + "Attribute", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(typeof(ChannelArchitectureTests).Assembly.Location)!);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root (aevatar.slnx) from test assembly location.");
    }
}
