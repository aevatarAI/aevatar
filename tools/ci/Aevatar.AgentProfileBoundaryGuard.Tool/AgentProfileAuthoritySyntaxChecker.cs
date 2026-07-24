using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.AgentProfileBoundaryGuard.Tool;

internal sealed class AgentProfileAuthoritySyntaxChecker
{
    internal const string AuthorityOrderMessage =
        "The actual [EventHandler] must be the unique expected handler for the message and match the canonical pre-authority statements, exact authority call, and immediate operation parse.";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly HandlerContract[] Contracts =
    [
        new(
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs",
            "AgentProfileGAgent",
            "HandleInitializeAsync",
            "InitializeAgentProfileCommand",
            "command",
            [
                "ArgumentNullException.ThrowIfNull(command);",
                """
                var namespaceActorId = State.Identity is null
                    ? AgentProfileActorIds.Namespace
                    : AgentProfileActorInvariants.RequireActorId(
                        State.NamespaceActorId,
                        "state.namespace_actor_id");
                """,
            ],
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);",
            "var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);"),
        new(
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs",
            "AgentProfileNamespaceGAgent",
            "HandleInitializedAsync",
            "AgentProfileInitializedContinuation",
            "continuation",
            [
                "ArgumentNullException.ThrowIfNull(continuation);",
                """
                var profileActorId = AgentProfileActorInvariants.RequireActorId(
                    continuation.ProfileActorId,
                    "profile_actor_id");
                """,
            ],
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);",
            "var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);"),
        new(
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs",
            "AgentProfileNamespaceGAgent",
            "HandleInitializationRejectedAsync",
            "AgentProfileInitializationRejectedContinuation",
            "continuation",
            [
                "ArgumentNullException.ThrowIfNull(continuation);",
                """
                var profileActorId = AgentProfileActorInvariants.RequireActorId(
                    continuation.ProfileActorId,
                    "profile_actor_id");
                """,
            ],
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);",
            "var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);"),
        new(
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs",
            "AgentProfileNamespaceGAgent",
            "HandleObservePublishedSummaryAsync",
            "ObserveAgentProfilePublishedSummaryCommand",
            "command",
            [
                "ArgumentNullException.ThrowIfNull(command);",
                "AgentProfileIdentity identity;",
                "AgentProfilePublishedSummary summary;",
                """
                try
                {
                    identity = AgentProfileDeterminism.NormalizeIdentity(command.Identity);
                    summary = AgentProfileDeterminism.NormalizePublishedSummary(command.Summary);
                }
                catch (AgentProfileContractValidationException)
                {
                    throw AgentProfileActorInvariants.Error(
                        "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                        "The published summary identity is invalid.");
                }
                """,
                "var entry = FindProfile(identity.ProfileId);",
                """
                if (entry is null ||
                    entry.Status != AgentProfileProvisioningStatus.Active ||
                    !AgentProfileActorInvariants.SameIdentity(entry.Identity, identity) ||
                    !AgentProfileActorInvariants.SameReference(
                        summary.Reference,
                        entry.Identity.Reference))
                {
                    throw AgentProfileActorInvariants.Error(
                        "PROFILE_PUBLISHED_SUMMARY_MISMATCH",
                        "The published summary does not belong to the mapped Profile.");
                }
                """,
            ],
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, entry.ProfileActorId);",
            "var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);"),
    ];

    internal AgentProfileAuthorityCheckResult Check(string scanRoot)
    {
        var root = GetReadableRoot(scanRoot);
        var violations = new List<AgentProfileAuthorityViolation>();

        foreach (var contractsByFile in Contracts.GroupBy(contract => contract.RelativeFile, StringComparer.Ordinal))
        {
            var relativeFile = contractsByFile.Key;
            var source = ReadSource(root, relativeFile);
            var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions, relativeFile);
            var compilationUnit = syntaxTree.GetCompilationUnitRoot();
            var contracts = contractsByFile.ToArray();

            if (syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                violations.AddRange(contracts.Select(CreateViolation));
                continue;
            }

            foreach (var contract in contracts)
            {
                if (!Matches(compilationUnit, contract))
                    violations.Add(CreateViolation(contract));
            }
        }

        return new AgentProfileAuthorityCheckResult(violations);
    }

    private static string GetReadableRoot(string scanRoot)
    {
        try
        {
            var root = Path.GetFullPath(scanRoot);
            if (!Directory.Exists(root))
                throw new AgentProfileAuthorityInputException();
            return root;
        }
        catch (AgentProfileAuthorityInputException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentProfileAuthorityInputException();
        }
    }

    private static string ReadSource(string root, string relativeFile)
    {
        try
        {
            var path = Path.Combine(root, relativeFile);
            if (!File.Exists(path))
                throw new AgentProfileAuthorityInputException();
            return File.ReadAllText(path);
        }
        catch (AgentProfileAuthorityInputException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AgentProfileAuthorityInputException();
        }
    }

    private static bool Matches(CompilationUnitSyntax compilationUnit, HandlerContract contract)
    {
        var targetClasses = compilationUnit
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, contract.ClassName, StringComparison.Ordinal) &&
                IsTopLevel(candidate))
            .ToArray();
        if (targetClasses.Length != 1)
            return false;

        var directMethods = targetClasses[0]
            .Members
            .OfType<MethodDeclarationSyntax>()
            .ToArray();
        var registeredMethods = directMethods
            .Where(candidate =>
                HasEventHandlerAttribute(candidate) &&
                HasExactMessageParameter(candidate, contract.ParameterType))
            .ToArray();
        if (registeredMethods.Length != 1)
            return false;

        var targetMethods = directMethods
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, contract.MethodName, StringComparison.Ordinal))
            .ToArray();
        if (targetMethods.Length != 1 ||
            !ReferenceEquals(registeredMethods[0], targetMethods[0]) ||
            !MatchesSignature(targetMethods[0], contract))
            return false;

        var statements = targetMethods[0].Body!.Statements;
        var canonicalPrefix = contract.CanonicalPrefix
            .Select(ParseCanonicalStatement)
            .ToArray();
        if (statements.Count < canonicalPrefix.Length + 2)
            return false;

        for (var index = 0; index < canonicalPrefix.Length; index++)
        {
            if (!SyntaxFactory.AreEquivalent(statements[index], canonicalPrefix[index]))
                return false;
        }

        return SyntaxFactory.AreEquivalent(
                   statements[canonicalPrefix.Length],
                   ParseCanonicalStatement(contract.AuthorityStatement)) &&
               SyntaxFactory.AreEquivalent(
                   statements[canonicalPrefix.Length + 1],
                   ParseCanonicalStatement(contract.OperationStatement));
    }

    private static bool IsTopLevel(ClassDeclarationSyntax candidate) =>
        candidate.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax;

    private static bool HasEventHandlerAttribute(MethodDeclarationSyntax method) =>
        method.AttributeLists.Any(attributeList =>
            (attributeList.Target is null ||
             string.Equals(attributeList.Target.Identifier.ValueText, "method", StringComparison.Ordinal)) &&
            attributeList.Attributes.Any(static attribute =>
                attribute.Name.GetLastToken().ValueText is "EventHandler" or "EventHandlerAttribute"));

    private static bool HasExactMessageParameter(MethodDeclarationSyntax method, string parameterType)
    {
        if (method.ParameterList.Parameters.Count != 1)
            return false;

        var type = method.ParameterList.Parameters[0].Type;
        return type is NameSyntax &&
               string.Equals(type.GetLastToken().ValueText, parameterType, StringComparison.Ordinal);
    }

    private static bool MatchesSignature(MethodDeclarationSyntax method, HandlerContract contract)
    {
        if (method.Body is null || method.ExpressionBody is not null || method.SemicolonToken.RawKind != 0)
            return false;
        if (method.TypeParameterList is not null || method.ExplicitInterfaceSpecifier is not null)
            return false;
        if (method.Modifiers.Count != 2 ||
            !method.Modifiers[0].IsKind(SyntaxKind.PublicKeyword) ||
            !method.Modifiers[1].IsKind(SyntaxKind.AsyncKeyword))
        {
            return false;
        }
        if (method.ReturnType is not IdentifierNameSyntax returnType ||
            !string.Equals(returnType.Identifier.ValueText, "Task", StringComparison.Ordinal))
        {
            return false;
        }
        if (method.ParameterList.Parameters.Count != 1)
            return false;

        var parameter = method.ParameterList.Parameters[0];
        return parameter.Type is IdentifierNameSyntax parameterType &&
               string.Equals(parameterType.Identifier.ValueText, contract.ParameterType, StringComparison.Ordinal) &&
               string.Equals(parameter.Identifier.ValueText, contract.ParameterName, StringComparison.Ordinal) &&
               parameter.Modifiers.Count == 0 &&
               parameter.Default is null;
    }

    private static StatementSyntax ParseCanonicalStatement(string source)
    {
        var statement = SyntaxFactory.ParseStatement(source, options: ParseOptions);
        if (statement.ContainsDiagnostics)
            throw new InvalidOperationException("The authority checker contains an invalid canonical statement.");
        return statement;
    }

    private static AgentProfileAuthorityViolation CreateViolation(HandlerContract contract) =>
        new(contract.RelativeFile, contract.MethodName, AuthorityOrderMessage);

    private sealed record HandlerContract(
        string RelativeFile,
        string ClassName,
        string MethodName,
        string ParameterType,
        string ParameterName,
        IReadOnlyList<string> CanonicalPrefix,
        string AuthorityStatement,
        string OperationStatement);
}

internal sealed record AgentProfileAuthorityCheckResult(
    IReadOnlyList<AgentProfileAuthorityViolation> Violations);

internal sealed record AgentProfileAuthorityViolation(
    string RelativeFile,
    string MethodName,
    string Message)
{
    internal string Location => $"{RelativeFile}:{MethodName}.authority-order";
}

internal sealed class AgentProfileAuthorityInputException : Exception;
