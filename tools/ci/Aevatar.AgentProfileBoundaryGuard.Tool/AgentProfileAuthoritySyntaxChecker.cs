using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.AgentProfileBoundaryGuard.Tool;

internal sealed class AgentProfileAuthoritySyntaxChecker
{
    internal const string AuthorityOrderMessage =
        "The actual [EventHandler] must be the unique expected handler for the message and match the canonical pre-authority statements, exact authority call, and immediate operation parse.";
    internal const string AdmissionArtifactOnlyMessage =
        "The Mainnet rollout artifact owns admission pins only and must not implement INyxIdChatAgentProfileBindingSource.";
    internal const string BinderReadModelOnlyMessage =
        "The Mainnet Profile binder may read only the namespace and execution read-model ports; event-store and projection-activation dependencies are forbidden.";
    internal const string SealedTurnOnlyMessage =
        "Agent Profile turns must execute from the immutable conversation binding and must not depend on IRemoteSkillFetcher.";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private const string RolloutSelectorFile =
        "src/Aevatar.Mainnet.Host.Api/Profiles/MainnetAgentProfileRolloutSelector.cs";
    private const string RolloutSelectorType = "MainnetAgentProfileRolloutSelector";
    private const string RuntimeBindingSourceMetadataName =
        "Aevatar.GAgents.NyxidChat.AgentProfiles.INyxIdChatAgentProfileBindingSource";
    private const string MainnetBinderFile =
        "src/Aevatar.Mainnet.Host.Api/AgentProfiles/MainnetNyxIdChatAgentProfileBindingSource.cs";
    private const string MainnetBinderType = "MainnetNyxIdChatAgentProfileBindingSource";
    private const string TurnMaterializerFile =
        "agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentProfileTurnCatalogMaterializer.cs";
    private const string TurnMaterializerType = "AgentProfileTurnCatalogMaterializer";
    private const string EventStoreMetadataName =
        "Aevatar.Foundation.Abstractions.Persistence.IEventStore";
    private const string ProjectionActivationMetadataName =
        "Aevatar.CQRS.Projection.Core.Abstractions.IProjectionScopeActivationService`1";
    private const string RemoteSkillFetcherMetadataName =
        "Aevatar.AI.ToolProviders.Skills.IRemoteSkillFetcher";

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

        CheckRolloutArtifactOwnership(root, violations);
        CheckBinderDependencies(root, violations);
        CheckTurnDependencies(root, violations);

        return new AgentProfileAuthorityCheckResult(violations);
    }

    private static void CheckRolloutArtifactOwnership(
        string root,
        ICollection<AgentProfileAuthorityViolation> violations)
    {
        var path = Path.Combine(root, RolloutSelectorFile);
        if (!File.Exists(path))
            return;

        var target = CreateSemanticTarget(root, RolloutSelectorFile, RolloutSelectorType);
        var runtimeSource = target.Compilation.GetTypeByMetadataName(RuntimeBindingSourceMetadataName)
            ?? throw new InvalidOperationException("The authority checker runtime source symbol is unavailable.");
        var selector = target.Declaration is null
            ? null
            : target.SemanticModel.GetDeclaredSymbol(target.Declaration);
        if (selector is null || selector.AllInterfaces.Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate, runtimeSource)))
        {
            violations.Add(new AgentProfileAuthorityViolation(
                RolloutSelectorFile,
                RolloutSelectorType,
                AdmissionArtifactOnlyMessage,
                "admission-artifact-only"));
        }
    }

    private static void CheckBinderDependencies(
        string root,
        ICollection<AgentProfileAuthorityViolation> violations)
    {
        if (!File.Exists(Path.Combine(root, MainnetBinderFile)))
            return;

        var target = CreateSemanticTarget(root, MainnetBinderFile, MainnetBinderType);
        if (target.Declaration is null || ReferencesAnyExactType(
                target,
                EventStoreMetadataName,
                ProjectionActivationMetadataName))
        {
            violations.Add(new AgentProfileAuthorityViolation(
                MainnetBinderFile,
                MainnetBinderType,
                BinderReadModelOnlyMessage,
                "read-model-only"));
        }
    }

    private static void CheckTurnDependencies(
        string root,
        ICollection<AgentProfileAuthorityViolation> violations)
    {
        if (!File.Exists(Path.Combine(root, TurnMaterializerFile)))
            return;

        var target = CreateSemanticTarget(root, TurnMaterializerFile, TurnMaterializerType);
        if (target.Declaration is null || ReferencesAnyExactType(
                target,
                RemoteSkillFetcherMetadataName))
        {
            violations.Add(new AgentProfileAuthorityViolation(
                TurnMaterializerFile,
                TurnMaterializerType,
                SealedTurnOnlyMessage,
                "sealed-turn-only"));
        }
    }

    private static SemanticTarget CreateSemanticTarget(
        string root,
        string relativeFile,
        string className)
    {
        var source = ReadSource(root, relativeFile);
        var targetTree = CSharpSyntaxTree.ParseText(source, ParseOptions, relativeFile);
        var contractTree = CSharpSyntaxTree.ParseText(
            SemanticContractStubs,
            ParseOptions,
            "AgentProfileAuthoritySemanticContracts.cs");
        var compilation = CSharpCompilation.Create(
            "AgentProfileAuthoritySemanticCheck",
            [targetTree, contractTree],
            TrustedPlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var declarations = targetTree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(candidate =>
                string.Equals(candidate.Identifier.ValueText, className, StringComparison.Ordinal) &&
                IsTopLevel(candidate))
            .Take(2)
            .ToArray();
        return new SemanticTarget(
            compilation,
            compilation.GetSemanticModel(targetTree),
            declarations.Length == 1 ? declarations[0] : null);
    }

    private static bool ReferencesAnyExactType(
        SemanticTarget target,
        params string[] metadataNames)
    {
        if (target.Declaration is null)
            return false;

        var forbidden = metadataNames
            .Select(metadataName => target.Compilation.GetTypeByMetadataName(metadataName)
                ?? throw new InvalidOperationException(
                    $"The authority checker symbol '{metadataName}' is unavailable."))
            .ToArray();
        foreach (var name in target.Declaration.DescendantNodes().OfType<NameSyntax>())
        {
            var symbol = target.SemanticModel.GetSymbolInfo(name).Symbol;
            if (symbol is IAliasSymbol alias)
                symbol = alias.Target;
            if (symbol is not INamedTypeSymbol namedType)
                continue;

            var definition = namedType.OriginalDefinition;
            if (forbidden.Any(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate, definition)))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> TrustedPlatformReferences =
        new(() =>
        {
            var paths = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (string.IsNullOrWhiteSpace(paths))
                return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
            return paths
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => MetadataReference.CreateFromFile(path))
                .ToArray();
        });

    private const string SemanticContractStubs = """
        namespace Aevatar.GAgents.NyxidChat.AgentProfiles
        {
            public interface INyxIdChatAgentProfileBindingSource;
        }

        namespace Aevatar.GAgentService.Abstractions.Ports
        {
            public interface IAgentProfileNamespaceQueryPort;
            public interface IAgentProfileExecutionSnapshotQueryPort;
        }

        namespace Aevatar.Foundation.Abstractions.Persistence
        {
            public interface IEventStore;
        }

        namespace Aevatar.CQRS.Projection.Core.Abstractions
        {
            public interface IProjectionRuntimeLease;

            public interface IProjectionScopeActivationService<TLease>
                where TLease : class, IProjectionRuntimeLease;
        }

        namespace Aevatar.AI.ToolProviders.Skills
        {
            public interface IRemoteSkillFetcher;
        }

        namespace Fixture.Decoys
        {
            public interface IEventStore;
            public interface IProjectionScopeActivationService<T>;
            public interface IRemoteSkillFetcher;
        }
        """;

    private sealed record SemanticTarget(
        CSharpCompilation Compilation,
        SemanticModel SemanticModel,
        ClassDeclarationSyntax? Declaration);

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
    string Message,
    string Rule = "authority-order")
{
    internal string Location => $"{RelativeFile}:{MethodName}.{Rule}";
}

internal sealed class AgentProfileAuthorityInputException : Exception;
