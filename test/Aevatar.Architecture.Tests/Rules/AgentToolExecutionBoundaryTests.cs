using System.Collections.Concurrent;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class AgentToolExecutionBoundaryTests
{
    private const string ToolTypeName = "Aevatar.AI.Abstractions.ToolProviders.IAgentTool";
    private const string PortTypeName = "Aevatar.AI.Abstractions.ToolProviders.IAgentToolExecutionPort";
    private const string GrantTypeName = "Aevatar.AI.Abstractions.ToolProviders.AgentToolApprovalGrant";
    private const string ExecutorTypeName = "Aevatar.AI.Core.Tools.AdmittedAgentToolExecutor";
    private const string AgentBaseTypeName = "Aevatar.AI.Core.AIGAgentBase<TState>";
    private const string RoleTypeName = "Aevatar.AI.Core.RoleGAgent";
    private const string ChannelTurnRunnerTypeName =
        "Aevatar.GAgents.NyxidChat.ChannelConversationTurnRunner";
    private const string WorkflowRoleTypeName = "Aevatar.Workflow.Integration.AI.WorkflowRoleGAgent";
    private const string WorkflowAdapterTypeName =
        "Aevatar.Workflow.Integration.AI.AgentWorkflowToolSourceAdapter.AgentWorkflowToolAdapter";
    private const string ConversationReplyGeneratorTypeName =
        "Aevatar.GAgents.NyxidChat.NyxIdConversationReplyGenerator";
    private const string AgentRunAuthorizedToolStepTypeName =
        "Aevatar.GAgents.NyxidChat.AgentRunAuthorizedToolStep";
    private const string ToolCallLoopTypeName = "Aevatar.AI.Core.Tools.ToolCallLoop";
    private const string InventoryReaderTypeName =
        "Aevatar.AI.ToolProviders.NyxId.ConnectedServices.NyxIdConnectedServiceInventoryReader";
    private const int ExpectedExecutionSurfaceCount = 12;

    private static readonly string[] IgnorableNuGetWorkspaceDiagnosticCodes =
    [
        "NU1507",
        "NU1510",
        "NU1903",
    ];

    private static readonly string[] DirectPortSurfaces =
    [
        "Aevatar.GAgentService.Application.Responses.LlmRunCore",
        "Aevatar.AI.Core.Tools.StreamingToolExecutor",
        RoleTypeName,
        "Aevatar.Workflow.Integration.AI.AgentWorkflowToolSourceAdapter",
        "Aevatar.Workflow.Integration.AI.SkillBackedHumanInteractionPort",
        "Aevatar.AI.Core.Voice.AgentToolVoiceInvoker",
        "Aevatar.AI.LLMProviders.MEAI.AgentToolAIFunction",
        "Aevatar.AI.ToolProviders.MCP.MCPConnector",
        "Aevatar.GAgents.NyxidChat.ChannelConversationTurnRunner",
        "Aevatar.GAgents.NyxidChat.ChannelNyxIdConnectedServiceInventoryToolSource",
    ];

    private static readonly string[] InventoryReaderSurfaces =
    [
        "Aevatar.AI.ToolProviders.NyxId.NyxIdConnectedServiceInventoryToolSource",
        "Aevatar.GAgents.NyxidChat.ChannelNyxIdConnectedServiceInventoryToolSource",
    ];

    [Fact]
    public async Task ServerOwnedAgentTools_ShouldHaveOneAdmittedTerminalAndAllKnownSurfaces()
    {
        EnsureMSBuildRegistered();
        var loadFailures = new ConcurrentQueue<string>();
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["NoWarn"] = string.Join(';', IgnorableNuGetWorkspaceDiagnosticCodes),
        });
        workspace.WorkspaceFailed += (_, evt) =>
        {
            if (evt.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure &&
                !IsIgnorableNuGetWorkspaceDiagnostic(evt.Diagnostic.Message))
            {
                loadFailures.Enqueue(evt.Diagnostic.Message);
            }
        };

        var solutionPath = Path.Combine(FindRepoRoot(), "aevatar.slnx");
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        Assert.True(loadFailures.IsEmpty,
            "MSBuildWorkspace must load the solution without failures:\n" + string.Join("\n", loadFailures));

        var productionProjects = solution.Projects
            .Where(project => project.FilePath is not null && !IsTestProject(project.FilePath))
            .ToArray();
        Assert.NotEmpty(productionProjects);

        var rawTerminals = new List<InvocationSite>();
        var unresolvedRawTerminals = new List<InvocationSite>();
        foreach (var project in productionProjects)
        {
            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            var toolType = compilation!.GetTypeByMetadataName(ToolTypeName);
            if (toolType is null)
                continue;

            foreach (var document in project.Documents.Where(document => document.SupportsSyntaxTree))
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                Assert.NotNull(root);
                Assert.NotNull(model);

                foreach (var invocation in root!.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                        !IsRawTerminalMethod(member.Name.Identifier.ValueText))
                    {
                        continue;
                    }

                    var receiverType = model!.GetTypeInfo(member.Expression).Type;
                    if (!IsOrImplements(receiverType, toolType))
                        continue;

                    var site = InvocationSite.Create(document.FilePath, invocation);
                    var symbolInfo = model.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol method)
                    {
                        unresolvedRawTerminals.Add(site with { CandidateCount = symbolInfo.CandidateSymbols.Length });
                        continue;
                    }

                    if (IsRawTerminalMethod(method.Name))
                        rawTerminals.Add(site with { EnclosingType = EnclosingTypeName(model, invocation) });
                }
            }
        }

        Assert.True(unresolvedRawTerminals.Count == 0,
            "Every IAgentTool raw terminal invocation must bind to exactly one symbol; candidate=0 also fails:\n" +
            string.Join("\n", unresolvedRawTerminals));
        Assert.Single(rawTerminals);
        Assert.Equal(ExecutorTypeName, rawTerminals[0].EnclosingType);

        var typeIndex = await BuildTypeIndexAsync(productionProjects);
        AssertRequiredExecutionPortConstructor(RequireType(typeIndex, AgentBaseTypeName).Symbol);
        AssertRequiredExecutionPortConstructor(RequireType(typeIndex, RoleTypeName).Symbol);
        AssertRequiredExecutionPortConstructor(RequireType(typeIndex, ChannelTurnRunnerTypeName).Symbol);
        foreach (var typeName in DirectPortSurfaces)
        {
            var type = RequireType(typeIndex, typeName);
            Assert.True(await TypeInvokesPortAsync(type, PortTypeName),
                $"Known server-owned execution surface '{typeName}' must invoke IAgentToolExecutionPort.");
        }

        var workflowRole = RequireType(typeIndex, WorkflowRoleTypeName);
        Assert.Equal(RoleTypeName, workflowRole.Symbol.BaseType?.ToDisplayString());
        var role = RequireType(typeIndex, RoleTypeName);
        Assert.True(await TypeInvokesPortAsync(role, PortTypeName),
            "WorkflowRoleGAgent must inherit the actor-owned approval continuation that invokes the port.");

        var conversationReplyGenerator = RequireType(typeIndex, ConversationReplyGeneratorTypeName);
        Assert.True(await TypeForwardsPortToConstructorAsync(
                conversationReplyGenerator,
                ToolCallLoopTypeName,
                "toolExecutionPort",
                PortTypeName),
            "NyxIdConversationReplyGenerator must pass IAgentToolExecutionPort to every ToolCallLoop it creates.");

        Assert.Equal(ExpectedExecutionSurfaceCount, DirectPortSurfaces.Length + 2);

        var reader = RequireType(typeIndex, InventoryReaderTypeName);
        var references = await SymbolFinder.FindReferencesAsync(reader.Symbol, solution);
        var referencingTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in references.SelectMany(reference => reference.Locations))
        {
            var root = await location.Document.GetSyntaxRootAsync();
            var node = root?.FindNode(location.Location.SourceSpan);
            var declaration = node?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (declaration is null)
                continue;
            var model = await location.Document.GetSemanticModelAsync();
            if (model?.GetDeclaredSymbol(declaration) is INamedTypeSymbol containingType)
                referencingTypes.Add(containingType.ToDisplayString());
        }

        foreach (var typeName in InventoryReaderSurfaces)
        {
            RequireType(typeIndex, typeName);
            Assert.Contains(typeName, referencingTypes);
        }

        var grantCreators = await FindObjectCreatorsAsync(productionProjects, GrantTypeName);
        Assert.NotEmpty(grantCreators);
        Assert.All(grantCreators, creator => Assert.Contains(
            creator.EnclosingType,
            new[] { RoleTypeName, WorkflowAdapterTypeName, AgentRunAuthorizedToolStepTypeName }));
    }

    private static bool IsRawTerminalMethod(string methodName) =>
        methodName is "ExecuteAsync" or "ExecuteWithOutcomeAsync";

    [Theory]
    [InlineData("warning NU1507: multiple package sources", true)]
    [InlineData("warning NU1510: redundant package reference", true)]
    [InlineData("warning NU1903: vulnerable package", true)]
    [InlineData("warning NU19030: unrelated diagnostic", false)]
    [InlineData("error CS0001: compiler failure", false)]
    public void WorkspaceDiagnostic_ShouldIgnoreOnlyKnownNuGetLoadDiagnostics(
        string message,
        bool expected)
    {
        Assert.Equal(expected, IsIgnorableNuGetWorkspaceDiagnostic(message));
    }

    private static async Task<Dictionary<string, TypeEntry>> BuildTypeIndexAsync(IEnumerable<Project> projects)
    {
        var result = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            CollectTypes(compilation!.Assembly.GlobalNamespace, compilation, result);
        }
        return result;
    }

    private static void CollectTypes(
        INamespaceSymbol namespaceSymbol,
        Compilation compilation,
        IDictionary<string, TypeEntry> result)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            CollectType(type, compilation, result);
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
            CollectTypes(child, compilation, result);
    }

    private static void CollectType(
        INamedTypeSymbol type,
        Compilation compilation,
        IDictionary<string, TypeEntry> result)
    {
        result.TryAdd(type.ToDisplayString(), new TypeEntry(type, compilation));
        foreach (var nested in type.GetTypeMembers())
            CollectType(nested, compilation, result);
    }

    private static async Task<bool> TypeInvokesPortAsync(TypeEntry type, string portTypeName)
    {
        foreach (var syntaxReference in type.Symbol.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxReference.GetSyntaxAsync();
            var model = type.Compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method &&
                    method.ContainingType.ToDisplayString() == portTypeName)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task<bool> TypeForwardsPortToConstructorAsync(
        TypeEntry type,
        string constructedTypeName,
        string parameterName,
        string portTypeName)
    {
        var portType = type.Compilation.GetTypeByMetadataName(portTypeName);
        Assert.NotNull(portType);
        var constructorCount = 0;
        var forwardedCount = 0;
        foreach (var syntaxReference in type.Symbol.DeclaringSyntaxReferences)
        {
            var declaration = await syntaxReference.GetSyntaxAsync();
            var model = type.Compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor ||
                    constructor.ContainingType.ToDisplayString() != constructedTypeName)
                {
                    continue;
                }

                constructorCount++;
                var argument = creation.ArgumentList?.Arguments.SingleOrDefault(item =>
                    item.NameColon?.Name.Identifier.ValueText == parameterName);
                if (argument is not null && IsOrImplements(model.GetTypeInfo(argument.Expression).Type, portType!))
                    forwardedCount++;
            }
        }

        return constructorCount > 0 && forwardedCount == constructorCount;
    }

    private static async Task<List<ObjectCreationSite>> FindObjectCreatorsAsync(
        IEnumerable<Project> projects,
        string targetTypeName)
    {
        var result = new List<ObjectCreationSite>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            if (compilation!.GetTypeByMetadataName(targetTypeName) is null)
                continue;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync();
                foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor ||
                        constructor.ContainingType.ToDisplayString() != targetTypeName)
                    {
                        continue;
                    }
                    result.Add(new ObjectCreationSite(EnclosingTypeName(model, creation)));
                }
            }
        }
        return result;
    }

    private static TypeEntry RequireType(
        IReadOnlyDictionary<string, TypeEntry> typeIndex,
        string typeName)
    {
        Assert.True(typeIndex.TryGetValue(typeName, out var type),
            $"Required execution surface type was not loaded from the solution: {typeName}");
        return type!;
    }

    private static bool IsOrImplements(ITypeSymbol? candidate, INamedTypeSymbol interfaceType) =>
        candidate is not null &&
        (SymbolEqualityComparer.Default.Equals(candidate, interfaceType) ||
         candidate.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, interfaceType)));

    private static void AssertRequiredExecutionPortConstructor(INamedTypeSymbol type)
    {
        var constructors = type.InstanceConstructors
            .Where(static constructor => !constructor.IsImplicitlyDeclared)
            .ToArray();
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.Contains(
            constructor.Parameters,
            parameter => parameter.Type.ToDisplayString() == PortTypeName &&
                         !parameter.HasExplicitDefaultValue));
    }

    private static string EnclosingTypeName(SemanticModel model, SyntaxNode node) =>
        model.GetEnclosingSymbol(node.SpanStart)?.ContainingType?.ToDisplayString() ?? "<unknown>";

    private static bool IsTestProject(string projectPath)
    {
        var normalized = projectPath.Replace('\\', '/');
        return normalized.Contains("/test/", StringComparison.Ordinal) ||
               normalized.Contains("/tests/", StringComparison.Ordinal);
    }

    private static bool IsIgnorableNuGetWorkspaceDiagnostic(string message) =>
        IgnorableNuGetWorkspaceDiagnosticCodes.Any(code => ContainsDiagnosticCode(message, code));

    private static bool ContainsDiagnosticCode(string message, string code)
    {
        var index = message.IndexOf(code, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var startIsBoundary = index == 0 || !char.IsLetterOrDigit(message[index - 1]);
            var end = index + code.Length;
            var endIsBoundary = end == message.Length || !char.IsLetterOrDigit(message[end]);
            if (startIsBoundary && endIsBoundary)
                return true;

            index = message.IndexOf(code, index + code.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static void EnsureMSBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    private sealed record InvocationSite(
        string FilePath,
        int Line,
        string EnclosingType = "<unresolved>",
        int CandidateCount = 1)
    {
        public static InvocationSite Create(string? filePath, SyntaxNode node) =>
            new(filePath ?? "<unknown>",
                node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1);

        public override string ToString() =>
            $"{FilePath}:{Line} enclosing={EnclosingType} candidates={CandidateCount}";
    }

    private sealed record ObjectCreationSite(string EnclosingType);

    private sealed record TypeEntry(INamedTypeSymbol Symbol, Compilation Compilation);
}
