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
        //
        // Receiver tracking (addresses review feedback): within each type we compute the set of
        // identifiers bound to an IChannelOutboundPort — fields, properties, constructor / method
        // parameters — and then close the set under local-variable aliasing (var port = _outbound;)
        // and assignment aliasing (port = this._outbound;). Any SendAsync call whose innermost
        // receiver identifier (`x.SendAsync(...)`, `this._outbound.SendAsync(...)`,
        // `wrapper.Port.SendAsync(...)`) resolves to one of those names is flagged.

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!text.Contains(".SendAsync", System.StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = ChannelSourceIndex.NormalizePath(sourceFile);
            if (IsAllowedOutboundSendCaller(normalized))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeReceivers = ChannelReceiverTracker.CollectTypeReceiverNames(typeDecl, "IChannelOutboundPort");

                foreach (var methodBody in ChannelReceiverTracker.EnumerateMethodBodies(typeDecl))
                {
                    var callableReceivers = ChannelReceiverTracker.ExpandLocalAliases(
                        methodBody,
                        typeReceivers,
                        "IChannelOutboundPort");

                    foreach (var invocation in methodBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var (methodName, receiverExpression) =
                            ChannelReceiverTracker.GetMemberInvocationParts(invocation);

                        if (methodName != "SendAsync" || receiverExpression is null)
                        {
                            continue;
                        }

                        var receiverName = ChannelReceiverTracker.ExtractLeafName(receiverExpression);
                        if (receiverName is null)
                        {
                            continue;
                        }

                        // Flag when the receiver name matches either the enclosing type's set or
                        // the repo-global set of IChannelOutboundPort-typed members. The global
                        // set catches wrapper-property forwarding (`wrapper.Port.SendAsync(...)`)
                        // where the property returning IChannelOutboundPort is declared in a
                        // different file.
                        if (!callableReceivers.Contains(receiverName)
                            && !ChannelSourceIndex.GlobalOutboundMemberNames.Contains(receiverName))
                        {
                            continue;
                        }

                        var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                        violators.Add(
                            $"{normalized}:{typeDecl.Identifier.ValueText}:line {line}: {invocation.ToString().Trim()}");
                    }
                }
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
        // Rule: proactive actor-to-actor paths (SkillExecutionGAgent, WorkflowAgentGAgent, admin endpoint
        // controllers, workflow triggers) must dispatch through ConversationGAgent command envelopes
        // rather than directly invoking IChannelOutboundPort.ContinueConversationAsync.
        //
        // Match by declared type identifier via Roslyn syntax walk, not by filename, so partial
        // classes split across arbitrarily-named files (for example `WorkflowAgentGAgent.Schedule.cs`)
        // still fail the guard.

        var proactiveCallerPatterns = new[]
        {
            "SkillExecutionGAgent",
            "WorkflowAgentGAgent",
            "AdminBroadcast",
            "ChannelBroadcast",
        };

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            // Cheap short-circuit that also accepts `?.ContinueConversationAsync(` null-
            // conditional forms by matching on the method-name substring without the leading dot.
            if (!text.Contains("ContinueConversationAsync(", System.StringComparison.Ordinal))
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
                    var (methodName, _) = ChannelReceiverTracker.GetMemberInvocationParts(invocation);
                    if (methodName != "ContinueConversationAsync")
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
        // Rule: any argument handed to IBlobStore.WriteAsync whose provenance looks like raw
        // channel payload bytes must be the result of IPayloadRedactor.Redact / RedactAsync. This
        // test performs local, intra-method data-flow:
        //   * collect identifiers typed as IBlobStore in the enclosing type (receivers for WriteAsync);
        //   * collect identifiers in the enclosing method whose initializer / latest assignment
        //     traces back to `.Redact(` or `.RedactAsync(` (possibly through chained aliasing);
        //   * for each WriteAsync argument that looks like raw payload (identifier / parameter
        //     containing "raw" in its name, or arguments named RawPayload*), require the provenance
        //     set to contain the identifier. Inline `redactor.Redact(...)` / `await redactor
        //     .RedactAsync(...)` arguments are accepted directly.
        //
        // Until IBlobStore lands in the repo the guard is vacuously satisfied; once the type is
        // introduced, any call site that stores non-redacted raw payload bytes trips the rule.

        var violators = new List<string>();

        foreach (var sourceFile in ChannelSourceIndex.EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);

            // Cheap short-circuit: only files that invoke a member named WriteAsync can possibly
            // violate. The file-level `IBlobStore` filter is intentionally absent — a consumer
            // that reaches the store through a wrapper (`_holder.Store.WriteAsync(...)`) never
            // mentions `IBlobStore` in its own text but still needs to be analyzed.
            if (!text.Contains(".WriteAsync", System.StringComparison.Ordinal))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
            var root = tree.GetRoot();
            var normalized = ChannelSourceIndex.NormalizePath(sourceFile);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var blobStoreReceivers = ChannelReceiverTracker.CollectTypeReceiverNames(typeDecl, "IBlobStore");

                foreach (var methodBody in ChannelReceiverTracker.EnumerateMethodBodies(typeDecl))
                {
                    var callableBlobStores = ChannelReceiverTracker.ExpandLocalAliases(
                        methodBody,
                        blobStoreReceivers,
                        "IBlobStore");

                    var redactedProvenance = ChannelReceiverTracker.CollectRedactedProvenance(methodBody);

                    foreach (var invocation in methodBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var (methodName, receiverExpression) =
                            ChannelReceiverTracker.GetMemberInvocationParts(invocation);

                        if (methodName != "WriteAsync" || receiverExpression is null)
                        {
                            continue;
                        }

                        var receiverName = ChannelReceiverTracker.ExtractLeafName(receiverExpression);
                        if (receiverName is null)
                        {
                            continue;
                        }

                        // Consult the per-method set plus the repo-global index of members
                        // declared as IBlobStore — the global lookup catches cross-file wrapper
                        // forwarding (`_holder.Store.WriteAsync(...)` where `Store` is declared
                        // elsewhere as `IBlobStore`).
                        if (!callableBlobStores.Contains(receiverName)
                            && !ChannelSourceIndex.GlobalBlobStoreMemberNames.Contains(receiverName))
                        {
                            continue;
                        }

                        foreach (var argument in invocation.ArgumentList.Arguments)
                        {
                            if (!ChannelReceiverTracker.LooksLikeRawPayloadArgument(argument))
                            {
                                continue;
                            }

                            if (ChannelReceiverTracker.ArgumentTracesToRedaction(argument, redactedProvenance))
                            {
                                continue;
                            }

                            var line = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                            violators.Add(
                                $"{normalized}:{typeDecl.Identifier.ValueText}:line {line}: "
                                + $"argument '{argument.ToString().Trim()}' passed to IBlobStore.WriteAsync "
                                + "does not trace back to IPayloadRedactor.Redact");
                        }
                    }
                }
            }
        }

        Assert.True(
            violators.Count == 0,
            "Raw payload arguments to IBlobStore.WriteAsync must come from IPayloadRedactor.Redact. "
            + "Forbidden call sites:\n" + string.Join("\n", violators));
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

}

internal static class ChannelReceiverTracker
{
    public static HashSet<string> CollectTypeReceiverNames(TypeDeclarationSyntax type, string typeHint)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!TypeTextMatches(field.Declaration.Type, typeHint))
            {
                continue;
            }

            foreach (var variable in field.Declaration.Variables)
            {
                names.Add(variable.Identifier.ValueText);
            }
        }

        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (TypeTextMatches(property.Type, typeHint))
            {
                names.Add(property.Identifier.ValueText);
            }
        }

        foreach (var parameter in type.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Type != null && TypeTextMatches(parameter.Type, typeHint))
            {
                names.Add(parameter.Identifier.ValueText);
            }
        }

        // Methods whose return type matches the hint are also receivers — call sites like
        // `ResolvePort().SendAsync(...)` or `this.ResolveStore().WriteAsync(...)` need the method
        // identifier in the set so they are flagged.
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            if (MethodReturnTypeMatches(method, typeHint))
            {
                names.Add(method.Identifier.ValueText);
            }
        }

        return names;
    }

    public static IEnumerable<SyntaxNode> EnumerateMethodBodies(TypeDeclarationSyntax type)
    {
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case BaseMethodDeclarationSyntax method:
                    if (method.Body is not null)
                    {
                        yield return method.Body;
                    }

                    if (method.ExpressionBody is not null)
                    {
                        yield return method.ExpressionBody;
                    }

                    break;

                case PropertyDeclarationSyntax property:
                    if (property.ExpressionBody is not null)
                    {
                        yield return property.ExpressionBody;
                    }

                    if (property.AccessorList is not null)
                    {
                        foreach (var accessor in property.AccessorList.Accessors)
                        {
                            if (accessor.Body is not null)
                            {
                                yield return accessor.Body;
                            }

                            if (accessor.ExpressionBody is not null)
                            {
                                yield return accessor.ExpressionBody;
                            }
                        }
                    }

                    break;
            }
        }
    }

    public static HashSet<string> ExpandLocalAliases(SyntaxNode methodBody, HashSet<string> seed, string typeHint)
    {
        var set = new HashSet<string>(seed, System.StringComparer.Ordinal);

        // Seed with local declarations whose declared type mentions the hint. This closes the
        // delegate-based bypass (`Func<IChannelOutboundPort> resolve = ...; resolve().SendAsync(...)`)
        // as well as `Lazy<IFoo>`, `Task<IFoo>`, `ValueTask<IFoo>`, `IEnumerable<IFoo>`, and plain
        // `IFoo local = ...` declarations — anything whose type text contains the hint identifier
        // as a whole-word segment.
        foreach (var local in methodBody.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            var declarationType = local.Declaration.Type;
            var declarationMatches = TypeTextMatches(declarationType, typeHint);
            var declaredAsVar = declarationType.IsVar || declarationType.ToString() == "var";

            foreach (var declarator in local.Declaration.Variables)
            {
                if (declarationMatches)
                {
                    set.Add(declarator.Identifier.ValueText);
                    continue;
                }

                // `var` hides the declared type, so fall back to the initializer expression.
                // Covers `var resolve = new Func<IFoo>(...)`, `var x = (IFoo)something`,
                // `var x = default(Func<IFoo>)`, and parenthesized / awaited variants.
                if (declaredAsVar
                    && declarator.Initializer?.Value is { } init
                    && InitializerExpressionMatchesTypeHint(init, typeHint))
                {
                    set.Add(declarator.Identifier.ValueText);
                }
            }
        }

        // Local functions whose return type matches the hint are also receivers.
        foreach (var localFn in methodBody.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (TypeTextMatches(localFn.ReturnType, typeHint))
            {
                set.Add(localFn.Identifier.ValueText);
            }
        }

        // Lambdas and anonymous methods bound to `var` have no declared type, but we can still
        // infer their return type when the body references a receiver that is already known. For
        // example `var resolve = () => _outbound;` — once `_outbound` lives in the set, invoking
        // `resolve()` also produces a known receiver, so the next fixed-point iteration adds
        // `resolve`.

        bool changed;
        do
        {
            changed = false;

            foreach (var declarator in methodBody.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer?.Value is not { } init)
                {
                    continue;
                }

                // `ReturnExpressionMatches` subsumes the plain leaf case plus `??` / `?:` /
                // `switch` branch enumeration, so `var alias = port ?? throw ...;` joins the set
                // when `port` is already known.
                if (ReturnExpressionMatches(init, set)
                    && set.Add(declarator.Identifier.ValueText))
                {
                    changed = true;
                    continue;
                }

                if (IsLambdaReturningKnownReceiver(init, set)
                    && set.Add(declarator.Identifier.ValueText))
                {
                    changed = true;
                }
            }

            foreach (var assignment in methodBody.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not IdentifierNameSyntax lhs)
                {
                    continue;
                }

                if (ReturnExpressionMatches(assignment.Right, set)
                    && set.Add(lhs.Identifier.ValueText))
                {
                    changed = true;
                    continue;
                }

                if (IsLambdaReturningKnownReceiver(assignment.Right, set)
                    && set.Add(lhs.Identifier.ValueText))
                {
                    changed = true;
                }
            }
        }
        while (changed);

        return set;
    }

    public static HashSet<string> CollectRedactedProvenance(SyntaxNode methodBody)
    {
        var set = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var declarator in methodBody.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is { } init && ExpressionProducesRedactedBytes(init))
            {
                set.Add(declarator.Identifier.ValueText);
            }
        }

        foreach (var assignment in methodBody.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is IdentifierNameSyntax lhs
                && ExpressionProducesRedactedBytes(assignment.Right))
            {
                set.Add(lhs.Identifier.ValueText);
            }
        }

        bool changed;
        do
        {
            changed = false;

            foreach (var declarator in methodBody.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer?.Value is { } init
                    && ExtractLeafName(init) is { } leaf
                    && set.Contains(leaf)
                    && set.Add(declarator.Identifier.ValueText))
                {
                    changed = true;
                }
            }

            foreach (var assignment in methodBody.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is IdentifierNameSyntax lhs
                    && ExtractLeafName(assignment.Right) is { } rhsLeaf
                    && set.Contains(rhsLeaf)
                    && set.Add(lhs.Identifier.ValueText))
                {
                    changed = true;
                }
            }
        }
        while (changed);

        return set;
    }

    public static bool LooksLikeRawPayloadArgument(ArgumentSyntax argument)
    {
        var text = argument.Expression.ToString();
        if (text.Contains("RawPayload", System.StringComparison.Ordinal)
            || text.Contains("rawPayload", System.StringComparison.Ordinal)
            || text.Contains("rawBytes", System.StringComparison.Ordinal)
            || text.Contains("RawBytes", System.StringComparison.Ordinal))
        {
            return true;
        }

        if (argument.Expression is IdentifierNameSyntax id)
        {
            var name = id.Identifier.ValueText;
            if (name.StartsWith("raw", System.StringComparison.OrdinalIgnoreCase)
                || name.Contains("Raw", System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ArgumentTracesToRedaction(ArgumentSyntax argument, HashSet<string> redactedProvenance)
    {
        if (ExpressionProducesRedactedBytes(argument.Expression))
        {
            return true;
        }

        if (ExtractLeafName(argument.Expression) is { } leaf && redactedProvenance.Contains(leaf))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the method name and receiver expression from a member-invocation syntax form,
    /// normalising <see cref="MemberAccessExpressionSyntax"/> (`x.SendAsync(...)`) and
    /// <see cref="MemberBindingExpressionSyntax"/> inside a <see cref="ConditionalAccessExpressionSyntax"/>
    /// (`x?.SendAsync(...)`) into the same shape. Returns <c>(null, null)</c> for invocations that
    /// aren't plain member dispatches (for example `F(x)` or `del()`).
    /// </summary>
    public static (string? MethodName, ExpressionSyntax? Receiver) GetMemberInvocationParts(InvocationExpressionSyntax invocation)
    {
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax member:
                return (member.Name.Identifier.ValueText, member.Expression);

            case MemberBindingExpressionSyntax binding:
            {
                var methodName = binding.Name.Identifier.ValueText;

                // Null-conditional invocations come through as the `WhenNotNull` of the nearest
                // enclosing ConditionalAccessExpression. Its `Expression` is the receiver that
                // precedes the `?.` operator — everything from plain `_outbound?.SendAsync(...)`
                // to chained `wrapper?.Port?.SendAsync(...)` surfaces here.
                var nearestConditional = invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
                return (methodName, nearestConditional?.Expression);
            }

            default:
                return (null, null);
        }
    }

    public static string? ExtractLeafName(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            // A method-return receiver such as `ResolvePort().SendAsync(...)` or
            // `this.ResolveStore().WriteAsync(...)` exposes the callee's name: recurse into the
            // invocation's callee expression so the leaf resolves to the method identifier.
            InvocationExpressionSyntax invocation => ExtractLeafName(invocation.Expression),
            ConditionalAccessExpressionSyntax conditional => ExtractLeafName(conditional.WhenNotNull) ?? ExtractLeafName(conditional.Expression),
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            ParenthesizedExpressionSyntax parens => ExtractLeafName(parens.Expression),
            AwaitExpressionSyntax awaitExpr => ExtractLeafName(awaitExpr.Expression),
            CastExpressionSyntax cast => ExtractLeafName(cast.Expression),
            PostfixUnaryExpressionSyntax postfix => ExtractLeafName(postfix.Operand),
            _ => null,
        };
    }

    private static bool IsLambdaReturningKnownReceiver(ExpressionSyntax expression, HashSet<string> set)
    {
        expression = UnwrapExpression(expression);

        return expression switch
        {
            LambdaExpressionSyntax lambda => AnyLambdaReturnInSet(lambda.Body, set),
            AnonymousMethodExpressionSyntax anon => AnyLambdaReturnInSet(anon.Body, set),
            _ => false,
        };
    }

    private static ExpressionSyntax UnwrapExpression(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parens:
                    expression = parens.Expression;
                    break;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static bool AnyLambdaReturnInSet(Microsoft.CodeAnalysis.SyntaxNode body, HashSet<string> set)
    {
        if (body is ExpressionSyntax expression)
        {
            return ReturnExpressionMatches(expression, set);
        }

        if (body is BlockSyntax block)
        {
            foreach (var ret in block.DescendantNodes(descendIntoChildren: n =>
                n is not LambdaExpressionSyntax
                && n is not AnonymousMethodExpressionSyntax
                && n is not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>())
            {
                if (ret.Expression is { } returnExpr && ReturnExpressionMatches(returnExpr, set))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when at least one branch of <paramref name="expression"/> evaluates to a name
    /// already present in <paramref name="set"/>. Splits `a ?? b`, `cond ? a : b`, and
    /// `switch { ... }` so an attacker cannot wrap a known receiver in an innocuous-looking
    /// expression (`_outbound ?? throw ...`, `_flag ? _store : throw ...`, etc.) and slip past
    /// the leaf-name check. `throw` expressions contribute no receiver and are skipped.
    /// </summary>
    private static bool ReturnExpressionMatches(ExpressionSyntax expression, HashSet<string> set)
    {
        foreach (var candidate in EnumerateReceiverBranches(expression))
        {
            if (ExtractLeafName(candidate) is { } leaf && set.Contains(leaf))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<ExpressionSyntax> EnumerateReceiverBranches(ExpressionSyntax expression)
    {
        expression = UnwrapExpression(expression);

        switch (expression)
        {
            case ThrowExpressionSyntax:
                // `throw ...` produces no receiver — do not yield a candidate.
                yield break;

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var branch in EnumerateReceiverBranches(binary.Left))
                {
                    yield return branch;
                }
                foreach (var branch in EnumerateReceiverBranches(binary.Right))
                {
                    yield return branch;
                }
                yield break;

            case ConditionalExpressionSyntax conditional:
                foreach (var branch in EnumerateReceiverBranches(conditional.WhenTrue))
                {
                    yield return branch;
                }
                foreach (var branch in EnumerateReceiverBranches(conditional.WhenFalse))
                {
                    yield return branch;
                }
                yield break;

            case SwitchExpressionSyntax switchExpr:
                foreach (var arm in switchExpr.Arms)
                {
                    foreach (var branch in EnumerateReceiverBranches(arm.Expression))
                    {
                        yield return branch;
                    }
                }
                yield break;

            case AwaitExpressionSyntax awaitExpr:
                foreach (var branch in EnumerateReceiverBranches(awaitExpr.Expression))
                {
                    yield return branch;
                }
                yield break;

            default:
                yield return expression;
                yield break;
        }
    }

    private static bool InitializerExpressionMatchesTypeHint(ExpressionSyntax expression, string typeHint)
    {
        switch (expression)
        {
            case ObjectCreationExpressionSyntax objectCreation:
                return TypeTextMatches(objectCreation.Type, typeHint);

            case CastExpressionSyntax cast:
                return TypeTextMatches(cast.Type, typeHint)
                    || InitializerExpressionMatchesTypeHint(cast.Expression, typeHint);

            case DefaultExpressionSyntax @default:
                return TypeTextMatches(@default.Type, typeHint);

            case ParenthesizedExpressionSyntax parens:
                return InitializerExpressionMatchesTypeHint(parens.Expression, typeHint);

            case AwaitExpressionSyntax awaitExpr:
                return InitializerExpressionMatchesTypeHint(awaitExpr.Expression, typeHint);

            case ConditionalExpressionSyntax conditional:
                return InitializerExpressionMatchesTypeHint(conditional.WhenTrue, typeHint)
                    || InitializerExpressionMatchesTypeHint(conditional.WhenFalse, typeHint);

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.CoalesceExpression):
                return InitializerExpressionMatchesTypeHint(binary.Left, typeHint)
                    || InitializerExpressionMatchesTypeHint(binary.Right, typeHint);

            default:
                return false;
        }
    }

    private static bool MethodReturnTypeMatches(MethodDeclarationSyntax method, string typeHint)
    {
        if (TypeTextMatches(method.ReturnType, typeHint))
        {
            return true;
        }

        // Unwrap Task<IFoo> / ValueTask<IFoo> / async iterator wrappers — `ResolvePortAsync()`
        // returning `Task<IChannelOutboundPort>` should still count as a receiver source.
        if (method.ReturnType is GenericNameSyntax generic)
        {
            foreach (var argument in generic.TypeArgumentList.Arguments)
            {
                if (TypeTextMatches(argument, typeHint))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TypeTextMatches(TypeSyntax type, string typeHint)
    {
        var text = type.ToString();
        // Match as a whole identifier segment to avoid matching embedded substrings
        // (e.g. hint "IBlobStore" would otherwise match "IBlobStoreFactory").
        if (text.Equals(typeHint, System.StringComparison.Ordinal))
        {
            return true;
        }

        return Regex.IsMatch(text, $@"(^|[^A-Za-z0-9_]){Regex.Escape(typeHint)}([^A-Za-z0-9_]|$)");
    }

    private static bool ExpressionProducesRedactedBytes(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case AwaitExpressionSyntax awaitExpr:
                return ExpressionProducesRedactedBytes(awaitExpr.Expression);

            case ParenthesizedExpressionSyntax parens:
                return ExpressionProducesRedactedBytes(parens.Expression);

            case InvocationExpressionSyntax invocation:
                if (invocation.Expression is MemberAccessExpressionSyntax member
                    && (member.Name.Identifier.ValueText == "Redact"
                        || member.Name.Identifier.ValueText == "RedactAsync"))
                {
                    return true;
                }

                if (invocation.Expression is IdentifierNameSyntax id
                    && (id.Identifier.ValueText == "Redact" || id.Identifier.ValueText == "RedactAsync"))
                {
                    return true;
                }

                // Handle `await x.RedactAsync(...).ConfigureAwait(false)` and similar chains.
                if (invocation.Expression is MemberAccessExpressionSyntax chained
                    && chained.Expression is InvocationExpressionSyntax inner)
                {
                    return ExpressionProducesRedactedBytes(inner);
                }

                break;

            case MemberAccessExpressionSyntax propertyAccess:
                if (propertyAccess.Name.Identifier.ValueText is "Redacted" or "Sanitized"
                    && propertyAccess.Expression is InvocationExpressionSyntax invokeBefore)
                {
                    return ExpressionProducesRedactedBytes(invokeBefore);
                }

                break;
        }

        return false;
    }
}

internal static class ChannelSourceIndex
{
    private static readonly Lazy<string> LazyRepoRoot = new(FindRepoRoot);

    private static readonly Lazy<HashSet<string>> LazyOutboundMemberNames =
        new(() => CollectGlobalMemberNamesByType("IChannelOutboundPort"));

    private static readonly Lazy<HashSet<string>> LazyBlobStoreMemberNames =
        new(() => CollectGlobalMemberNamesByType("IBlobStore"));

    public static string RepoRoot => LazyRepoRoot.Value;

    public static HashSet<string> GlobalOutboundMemberNames => LazyOutboundMemberNames.Value;

    public static HashSet<string> GlobalBlobStoreMemberNames => LazyBlobStoreMemberNames.Value;

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

    private static HashSet<string> CollectGlobalMemberNamesByType(string typeHint)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var sourceFile in EnumerateProductionSourceFiles())
        {
            var text = File.ReadAllText(sourceFile);
            if (!text.Contains(typeHint, System.StringComparison.Ordinal))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: sourceFile);
            var root = tree.GetRoot();

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                if (field.Declaration.Type.ToString().Contains(typeHint, System.StringComparison.Ordinal))
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        names.Add(variable.Identifier.ValueText);
                    }
                }
            }

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (property.Type.ToString().Contains(typeHint, System.StringComparison.Ordinal))
                {
                    names.Add(property.Identifier.ValueText);
                }
            }

            foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (parameter.Type?.ToString().Contains(typeHint, System.StringComparison.Ordinal) == true)
                {
                    names.Add(parameter.Identifier.ValueText);
                }
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var returnText = method.ReturnType.ToString();
                if (returnText.Contains(typeHint, System.StringComparison.Ordinal))
                {
                    names.Add(method.Identifier.ValueText);
                }
            }

            foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                var returnText = localFunction.ReturnType.ToString();
                if (returnText.Contains(typeHint, System.StringComparison.Ordinal))
                {
                    names.Add(localFunction.Identifier.ValueText);
                }
            }
        }

        return names;
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
