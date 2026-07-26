using Aevatar.AgentProfileBoundaryGuard.Tool;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Aevatar.Architecture.Tests.Rules;

public sealed class AgentProfileAuthoritySyntaxCheckerTests
{
    [Fact]
    public void RepositoryHandlersPassStructuredSyntaxCheck()
    {
        var result = new AgentProfileAuthoritySyntaxChecker().Check(FindRepositoryRoot());

        Assert.Empty(result.Violations);
    }

    [Theory]
    [InlineData("raw-string-handler-decoy", "HandleInitializeAsync")]
    [InlineData("wrong-first-authority", "HandleInitializeAsync")]
    [InlineData("executable-interpolation", "HandleInitializeAsync")]
    [InlineData("inline-replay", "HandleInitializeAsync")]
    [InlineData("state-insert", "HandleInitializedAsync")]
    [InlineData("direct-assignment", "HandleInitializationRejectedAsync")]
    [InlineData("send-effect", "HandleInitializationRejectedAsync")]
    [InlineData("persist-effect", "HandleObservePublishedSummaryAsync")]
    public void RejectsNonCanonicalWorkBeforeAuthority(string corruption, string expectedMethod)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.ApplyCorruption(corruption);
        fixture.AssertGovernedSourcesParse();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Contains(result.Violations, violation => violation.MethodName == expectedMethod);
    }

    [Fact]
    public void CanonicalHandlerInOtherTopLevelClassDoesNotHideCorruptedDirectHandler() =>
        AssertCanonicalSelectorDecoyDoesNotHideCorruptedDirectHandler(
            "other-top-level-class-canonical-decoy");

    [Fact]
    public void CanonicalHandlerInNestedClassDoesNotHideCorruptedDirectHandler() =>
        AssertCanonicalSelectorDecoyDoesNotHideCorruptedDirectHandler(
            "nested-class-canonical-decoy");

    [Fact]
    public void CanonicalLocalFunctionDoesNotHideCorruptedDirectHandler() =>
        AssertCanonicalSelectorDecoyDoesNotHideCorruptedDirectHandler(
            "local-function-canonical-decoy");

    [Theory]
    [InlineData("HandleInitializeAsync")]
    [InlineData("HandleInitializedAsync")]
    [InlineData("HandleInitializationRejectedAsync")]
    [InlineData("HandleObservePublishedSummaryAsync")]
    public void RejectsMissingAuthorityIndependentlyForEveryHandler(string methodName)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.RemoveAuthority(methodName);
        fixture.AssertGovernedSourcesParse();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Contains(result.Violations, violation => violation.MethodName == methodName);
    }

    [Theory]
    [InlineData("HandleInitializeAsync")]
    [InlineData("HandleInitializedAsync")]
    [InlineData("HandleInitializationRejectedAsync")]
    [InlineData("HandleObservePublishedSummaryAsync")]
    public void RejectsMissingEventHandlerRegistrationIndependentlyForEveryHandler(string methodName)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.RemoveEventHandler(methodName);
        fixture.AssertGovernedSourcesParse();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Contains(result.Violations, violation => violation.MethodName == methodName);
    }

    [Theory]
    [InlineData("registered-handler-moved-to-unsafe-method")]
    [InlineData("duplicate-registered-handler")]
    public void RejectsAmbiguousOrMisdirectedEventHandlerRegistration(string corruption)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.ApplyCorruption(corruption);
        fixture.AssertGovernedSourcesParse();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Contains(result.Violations, violation => violation.MethodName == "HandleInitializeAsync");
    }

    [Theory]
    [InlineData("duplicate-target-class")]
    [InlineData("duplicate-target-method")]
    [InlineData("wrong-signature")]
    [InlineData("expression-body")]
    [InlineData("parse-error")]
    public void FailsClosedForAmbiguousOrInvalidTargetSyntax(string corruption)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.ApplyCorruption(corruption);
        if (corruption != "parse-error")
            fixture.AssertGovernedSourcesParse();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Contains(result.Violations, violation => violation.MethodName == "HandleInitializeAsync");
    }

    [Fact]
    public void IgnoresCommentStringAndInactiveCodeDecoysAroundValidHandlers()
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.AddNonExecutableDecoys();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void RejectsRolloutArtifactImplementingRuntimeBindingSource()
    {
        using var fixture = AuthorityBoundarySemanticFixture.Create();
        fixture.MakeRolloutArtifactImplementRuntimeSource();
        fixture.AssertBoundarySourcesCompile();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(
            "src/Aevatar.Mainnet.Host.Api/Profiles/MainnetAgentProfileRolloutSelector.cs:MainnetAgentProfileRolloutSelector.admission-artifact-only",
            violation.Location);
        Assert.Equal(
            "The Mainnet rollout artifact owns admission pins only and must not implement INyxIdChatAgentProfileBindingSource.",
            violation.Message);
    }

    [Theory]
    [InlineData("event-store")]
    [InlineData("projection-activation")]
    public void RejectsBinderAuthoritySideReadOrPrimingDependencies(string dependency)
    {
        using var fixture = AuthorityBoundarySemanticFixture.Create();
        fixture.AddBinderDependency(dependency);
        fixture.AssertBoundarySourcesCompile();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(
            "src/Aevatar.Mainnet.Host.Api/AgentProfiles/MainnetNyxIdChatAgentProfileBindingSource.cs:MainnetNyxIdChatAgentProfileBindingSource.read-model-only",
            violation.Location);
        Assert.Equal(
            "The Mainnet Profile binder may read only the namespace and execution read-model ports; event-store and projection-activation dependencies are forbidden.",
            violation.Message);
    }

    [Fact]
    public void RejectsTurnRemoteSkillFetcherDependency()
    {
        using var fixture = AuthorityBoundarySemanticFixture.Create();
        fixture.AddTurnRemoteFetcherDependency();
        fixture.AssertBoundarySourcesCompile();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(
            "agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentProfileTurnCatalogMaterializer.cs:AgentProfileTurnCatalogMaterializer.sealed-turn-only",
            violation.Location);
        Assert.Equal(
            "Agent Profile turns must execute from the immutable conversation binding and must not depend on IRemoteSkillFetcher.",
            violation.Message);
    }

    [Fact]
    public void AcceptsReadModelBinderAndExactSymbolDecoys()
    {
        using var fixture = AuthorityBoundarySemanticFixture.Create();
        fixture.AssertBoundarySourcesCompile();

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void CliBatchScansEveryRootAndReturnsOneForViolations()
    {
        using var valid = AuthoritySyntaxFixture.Create();
        using var invalid = AuthoritySyntaxFixture.Create();
        invalid.ApplyCorruption("raw-string-handler-decoy");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = AgentProfileBoundaryGuardCli.Run(
            ["check", "--scan-root", valid.Root, "--scan-root", invalid.Root],
            stdout,
            stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains($"PASS|{valid.Root}", stdout.ToString());
        Assert.Contains(
            $"VIOLATION|{invalid.Root}|src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandleInitializeAsync.authority-order|",
            stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void CliReturnsTwoForMissingInputAndStillScansOtherRoots()
    {
        using var valid = AuthoritySyntaxFixture.Create();
        var missing = Path.Combine(valid.Root, "missing-root");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = AgentProfileBoundaryGuardCli.Run(
            ["check", "--scan-root", missing, "--scan-root", valid.Root],
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains($"ERROR|{missing}|", stderr.ToString());
        Assert.Contains($"PASS|{valid.Root}", stdout.ToString());
    }

    [Fact]
    public void CliSanitizesTabEscapeAndSeparatorsInRecordFields()
    {
        var hostileRoot = Path.Combine(Path.GetTempPath(), "missing\t\u001b|root");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = AgentProfileBoundaryGuardCli.Run(
            ["check", "--scan-root", hostileRoot],
            stdout,
            stderr);

        var expectedRoot = hostileRoot
            .Replace('\t', '_')
            .Replace('\u001b', '_')
            .Replace('|', '_');
        Assert.Equal(2, exitCode);
        Assert.Equal(
            $"ERROR|{expectedRoot}|Scan root or governed input is missing or unreadable.{Environment.NewLine}",
            stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    [Theory]
    [InlineData()]
    [InlineData("check")]
    [InlineData("scan", "--scan-root", "root")]
    [InlineData("check", "root")]
    public void CliReturnsTwoForInvalidArguments(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = AgentProfileBoundaryGuardCli.Run(args, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", stderr.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void AssertCanonicalSelectorDecoyDoesNotHideCorruptedDirectHandler(
        string corruption)
    {
        using var fixture = AuthoritySyntaxFixture.Create();
        fixture.ApplyCorruption(corruption);
        fixture.AssertGovernedSourcesParse();
        fixture.AssertSelectorDecoyMatchesCanonicalHandler(corruption);

        var result = new AgentProfileAuthoritySyntaxChecker().Check(fixture.Root);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("HandleInitializeAsync", violation.MethodName);
        Assert.Equal(
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs:HandleInitializeAsync.authority-order",
            violation.Location);
    }

    private sealed class AuthorityBoundarySemanticFixture : IDisposable
    {
        private const string SelectorRelativePath =
            "src/Aevatar.Mainnet.Host.Api/Profiles/MainnetAgentProfileRolloutSelector.cs";
        private const string BinderRelativePath =
            "src/Aevatar.Mainnet.Host.Api/AgentProfiles/MainnetNyxIdChatAgentProfileBindingSource.cs";
        private const string TurnRelativePath =
            "agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentProfileTurnCatalogMaterializer.cs";
        private const string ContractsRelativePath = "fixtures/AuthorityBoundaryContracts.cs";

        private readonly AuthoritySyntaxFixture _authorityFixture;

        private AuthorityBoundarySemanticFixture(AuthoritySyntaxFixture authorityFixture)
        {
            _authorityFixture = authorityFixture;
        }

        public string Root => _authorityFixture.Root;

        private string SelectorPath => Path.Combine(Root, SelectorRelativePath);

        private string BinderPath => Path.Combine(Root, BinderRelativePath);

        private string TurnPath => Path.Combine(Root, TurnRelativePath);

        private string ContractsPath => Path.Combine(Root, ContractsRelativePath);

        public static AuthorityBoundarySemanticFixture Create()
        {
            var fixture = new AuthorityBoundarySemanticFixture(AuthoritySyntaxFixture.Create());
            WriteSource(fixture.ContractsPath, BoundaryContracts);
            WriteSource(fixture.SelectorPath, ValidSelector);
            WriteSource(fixture.BinderPath, ValidBinder);
            WriteSource(fixture.TurnPath, ValidTurnMaterializer);
            return fixture;
        }

        public void MakeRolloutArtifactImplementRuntimeSource()
        {
            ReplaceOnce(
                SelectorPath,
                "public sealed class MainnetAgentProfileRolloutSelector",
                "public sealed class MainnetAgentProfileRolloutSelector : INyxIdChatAgentProfileBindingSource");
            ReplaceOnce(
                SelectorPath,
                "    private readonly AgentProfileRolloutReleaseSpec? _releaseSpec;",
                """
                    private readonly AgentProfileRolloutReleaseSpec? _releaseSpec;

                    public Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
                        string actorId,
                        string routeToolSetName,
                        CancellationToken ct = default) =>
                        Task.FromResult(new NyxIdChatAgentProfileBindingResult());
                """);
        }

        public void AddBinderDependency(string dependency)
        {
            var declaration = dependency switch
            {
                "event-store" =>
                    "private readonly Aevatar.Foundation.Abstractions.Persistence.IEventStore _eventStore = null!;",
                "projection-activation" =>
                    "private readonly Aevatar.CQRS.Projection.Core.Abstractions.IProjectionScopeActivationService<ProfileProjectionLease> _projectionActivation = null!;",
                _ => throw new ArgumentOutOfRangeException(nameof(dependency), dependency, null),
            };
            ReplaceOnce(
                BinderPath,
                "public sealed class MainnetNyxIdChatAgentProfileBindingSource : INyxIdChatAgentProfileBindingSource\n{",
                $$"""
                public sealed class MainnetNyxIdChatAgentProfileBindingSource : INyxIdChatAgentProfileBindingSource
                {
                    {{declaration}}
                """);
        }

        public void AddTurnRemoteFetcherDependency() =>
            ReplaceOnce(
                TurnPath,
                "public sealed class AgentProfileTurnCatalogMaterializer\n{",
                """
                public sealed class AgentProfileTurnCatalogMaterializer
                {
                    private readonly Aevatar.AI.ToolProviders.Skills.IRemoteSkillFetcher _remoteSkillFetcher = null!;
                """);

        public void AssertBoundarySourcesCompile()
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var paths = new[] { ContractsPath, SelectorPath, BinderPath, TurnPath };
            var syntaxTrees = paths
                .Select(path => CSharpSyntaxTree.ParseText(
                    File.ReadAllText(path),
                    parseOptions,
                    path))
                .ToArray();
            var trustedPlatformAssemblies = Assert.IsType<string>(
                AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
            var references = trustedPlatformAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                $"AgentProfileAuthorityBoundaryFixture_{Guid.NewGuid():N}",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
            var diagnostics = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();

            Assert.True(
                diagnostics.Length == 0,
                $"{string.Join(Environment.NewLine, diagnostics)}{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    paths.Select(path => $"--- {path} ---{Environment.NewLine}{File.ReadAllText(path)}")));
        }

        private static void ReplaceOnce(string path, string current, string replacement)
        {
            var source = File.ReadAllText(path);
            var index = source.IndexOf(current, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Fixture text was not found: {current}");
            source = source.Remove(index, current.Length).Insert(index, replacement);
            File.WriteAllText(path, source);
        }

        private static void WriteSource(string path, string source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        public void Dispose() => _authorityFixture.Dispose();

        private const string BoundaryContracts = """
            using System.Threading;
            using System.Threading.Tasks;

            namespace Aevatar.GAgents.NyxidChat.AgentProfiles
            {
                public sealed class NyxIdChatAgentProfileBindingResult;

                public interface INyxIdChatAgentProfileBindingSource
                {
                    Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
                        string actorId,
                        string routeToolSetName,
                        CancellationToken ct = default);
                }
            }

            namespace Aevatar.GAgentService.Abstractions.AgentProfiles
            {
                public sealed class AgentProfileRolloutReleaseSpec;
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

        private const string ValidSelector = """
            using Aevatar.GAgentService.Abstractions.AgentProfiles;
            using Aevatar.GAgents.NyxidChat.AgentProfiles;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Aevatar.Mainnet.Host.Api.Profiles;

            public sealed class MainnetAgentProfileRolloutSelector
            {
                private readonly AgentProfileRolloutReleaseSpec? _releaseSpec;
            }
            """;

        private const string ValidBinder = """
            using Aevatar.GAgents.NyxidChat.AgentProfiles;
            using Aevatar.GAgentService.Abstractions.Ports;
            using Aevatar.Mainnet.Host.Api.Profiles;
            using System.Threading;
            using System.Threading.Tasks;

            namespace Aevatar.Mainnet.Host.Api.AgentProfiles;

            public sealed class MainnetNyxIdChatAgentProfileBindingSource : INyxIdChatAgentProfileBindingSource
            {
                private readonly MainnetAgentProfileRolloutSelector _selector;
                private readonly IAgentProfileNamespaceQueryPort _namespaceQuery;
                private readonly IAgentProfileExecutionSnapshotQueryPort _executionQuery;

                public MainnetNyxIdChatAgentProfileBindingSource(
                    MainnetAgentProfileRolloutSelector selector,
                    IAgentProfileNamespaceQueryPort namespaceQuery,
                    IAgentProfileExecutionSnapshotQueryPort executionQuery)
                {
                    _selector = selector;
                    _namespaceQuery = namespaceQuery;
                    _executionQuery = executionQuery;
                }

                public Task<NyxIdChatAgentProfileBindingResult> ResolveForNewConversationAsync(
                    string actorId,
                    string routeToolSetName,
                    CancellationToken ct = default) =>
                    Task.FromResult(new NyxIdChatAgentProfileBindingResult());

                private static void ExactSymbolDecoys()
                {
                    // IEventStore IProjectionScopeActivationService IRemoteSkillFetcher
                    const string names = "IEventStore IProjectionScopeActivationService IRemoteSkillFetcher";
                    static Fixture.Decoys.IEventStore? IEventStore() => null;
                    static Fixture.Decoys.IProjectionScopeActivationService<object>? IProjectionScopeActivationService() => null;
                    _ = names;
                    _ = IEventStore();
                    _ = IProjectionScopeActivationService();
                }

                private sealed class ProfileProjectionLease :
                    Aevatar.CQRS.Projection.Core.Abstractions.IProjectionRuntimeLease;
            }
            """;

        private const string ValidTurnMaterializer = """
            namespace Aevatar.GAgents.NyxidChat.AgentProfiles;

            public sealed class AgentProfileTurnCatalogMaterializer
            {
                private static void ExactSymbolDecoys()
                {
                    // IRemoteSkillFetcher.FetchSkillAsync
                    const string remoteFetch = "IRemoteSkillFetcher FetchSkillAsync Ornn";
                    static Fixture.Decoys.IRemoteSkillFetcher? IRemoteSkillFetcher() => null;
                    static void FetchSkillAsync() { }
                    _ = remoteFetch;
                    _ = IRemoteSkillFetcher();
                    FetchSkillAsync();
                }
            }
            """;
    }

    private sealed class AuthoritySyntaxFixture : IDisposable
    {
        private const string ProfileRelativePath =
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs";
        private const string NamespaceRelativePath =
            "src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs";
        private const string ProfileAuthority =
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);";
        private const string InitializedAuthority =
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);";
        private const string SummaryAuthority =
            "AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, entry.ProfileActorId);";

        private AuthoritySyntaxFixture(string root)
        {
            Root = root;
        }

        public string Root { get; }

        private string ProfilePath => Path.Combine(Root, ProfileRelativePath);

        private string NamespacePath => Path.Combine(Root, NamespaceRelativePath);

        public static AuthoritySyntaxFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"agent-profile-authority-syntax-{Guid.NewGuid():N}");
            var fixture = new AuthoritySyntaxFixture(root);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProfilePath)!);
            File.WriteAllText(fixture.ProfilePath, ValidProfileActor);
            File.WriteAllText(fixture.NamespacePath, ValidNamespaceActor);
            return fixture;
        }

        public void ApplyCorruption(string corruption)
        {
            switch (corruption)
            {
                case "raw-string-handler-decoy":
                    ReplaceOnce(ProfilePath, ProfileAuthority, $"State.Operations.Insert(0, new AgentProfileOperationFact());{Environment.NewLine}        {ProfileAuthority}");
                    ReplaceOnce(
                        ProfilePath,
                        "public sealed class AgentProfileGAgent\n{",
                        $"public sealed class AgentProfileGAgent{Environment.NewLine}{{{Environment.NewLine}    private const string HandlerDecoy = \"\"\"{Environment.NewLine}{CanonicalInitializeMethod}{Environment.NewLine}\"\"\";");
                    break;
                case "other-top-level-class-canonical-decoy":
                    CorruptDirectInitializeHandler();
                    AddOtherTopLevelCanonicalHandler();
                    break;
                case "nested-class-canonical-decoy":
                    CorruptDirectInitializeHandler();
                    AddNestedCanonicalHandler();
                    break;
                case "local-function-canonical-decoy":
                    CorruptDirectInitializeHandler();
                    AddCanonicalLocalFunction();
                    break;
                case "wrong-first-authority":
                    ReplaceOnce(
                        ProfilePath,
                        ProfileAuthority,
                        $"AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, command.NamespaceActorId);{Environment.NewLine}        State.Operations.Insert(0, new AgentProfileOperationFact());{Environment.NewLine}        {ProfileAuthority}");
                    break;
                case "executable-interpolation":
                    ReplaceOnce(
                        ProfilePath,
                        "\"state.namespace_actor_id\"",
                        "$\"{State.Operations.FirstOrDefault(candidate => candidate.Operation.OperationId == command.Operation.OperationId)}\"");
                    break;
                case "inline-replay":
                    ReplaceOnce(
                        ProfilePath,
                        ProfileAuthority,
                        $"var replay = State.Operations.FirstOrDefault(candidate => candidate.Operation.OperationId == command.Operation.OperationId);{Environment.NewLine}        {ProfileAuthority}");
                    break;
                case "state-insert":
                    ReplaceOnce(
                        NamespacePath,
                        InitializedAuthority,
                        $"State.Operations.Insert(0, new AgentProfileOperationFact());{Environment.NewLine}        {InitializedAuthority}");
                    break;
                case "direct-assignment":
                    ReplaceAuthorityInMethod(
                        "HandleInitializationRejectedAsync",
                        $"State.LastPublishedRevision = continuation.DraftRevision;{Environment.NewLine}        {InitializedAuthority}");
                    break;
                case "send-effect":
                    ReplaceAuthorityInMethod(
                        "HandleInitializationRejectedAsync",
                        $"await SendToAsync(profileActorId, continuation);{Environment.NewLine}        {InitializedAuthority}");
                    break;
                case "persist-effect":
                    ReplaceOnce(
                        NamespacePath,
                        SummaryAuthority,
                        $"await PersistAsync(command.Operation);{Environment.NewLine}        {SummaryAuthority}");
                    break;
                case "registered-handler-moved-to-unsafe-method":
                    RemoveEventHandler("HandleInitializeAsync");
                    AddUnsafeRegisteredInitializeHandler();
                    break;
                case "duplicate-registered-handler":
                    AddUnsafeRegisteredInitializeHandler();
                    break;
                case "duplicate-target-class":
                    File.AppendAllText(ProfilePath, $"{Environment.NewLine}{ValidProfileActor}");
                    break;
                case "duplicate-target-method":
                    ReplaceOnce(
                        ProfilePath,
                        "public sealed class AgentProfileGAgent\n{",
                        $"public sealed class AgentProfileGAgent{Environment.NewLine}{{{Environment.NewLine}{CanonicalInitializeMethod}");
                    break;
                case "wrong-signature":
                    ReplaceOnce(
                        ProfilePath,
                        "HandleInitializeAsync(InitializeAgentProfileCommand command)",
                        "HandleInitializeAsync(object command)");
                    break;
                case "expression-body":
                    File.WriteAllText(
                        ProfilePath,
                        "public sealed class AgentProfileGAgent { public async Task HandleInitializeAsync(InitializeAgentProfileCommand command) => await PersistAsync(command); }");
                    break;
                case "parse-error":
                    File.WriteAllText(ProfilePath, "}" + File.ReadAllText(ProfilePath));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
            }
        }

        public void RemoveEventHandler(string methodName)
        {
            var path = methodName == "HandleInitializeAsync" ? ProfilePath : NamespacePath;
            ReplaceOnce(
                path,
                $"[EventHandler]{Environment.NewLine}    public async Task {methodName}",
                $"public async Task {methodName}");
        }

        public void RemoveAuthority(string methodName)
        {
            switch (methodName)
            {
                case "HandleInitializeAsync":
                    ReplaceOnce(ProfilePath, ProfileAuthority, string.Empty);
                    break;
                case "HandleInitializedAsync":
                case "HandleInitializationRejectedAsync":
                    ReplaceAuthorityInMethod(methodName, string.Empty);
                    break;
                case "HandleObservePublishedSummaryAsync":
                    ReplaceOnce(NamespacePath, SummaryAuthority, string.Empty);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null);
            }
        }

        public void AddNonExecutableDecoys()
        {
            ReplaceOnce(
                ProfilePath,
                ProfileAuthority,
                $"// State.Operations.Insert(0, new AgentProfileOperationFact());{Environment.NewLine}        {ProfileAuthority}");
            ReplaceOnce(
                ProfilePath,
                "public sealed class AgentProfileGAgent\n{",
                $$"""
                public sealed class AgentProfileGAgent
                {
                    private const string HandlerText = "HandleInitializeAsync State.Operations.Insert";
                #if false
                {{CanonicalInitializeMethod}}
                #endif
                """);
        }

        public void AssertGovernedSourcesParse()
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            foreach (var path in new[] { ProfilePath, NamespacePath })
            {
                var diagnostics = CSharpSyntaxTree
                    .ParseText(File.ReadAllText(path), parseOptions, path)
                    .GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .ToArray();
                Assert.True(
                    diagnostics.Length == 0,
                    $"{string.Join(Environment.NewLine, diagnostics)}{Environment.NewLine}{File.ReadAllText(path)}");
            }
        }

        public void AssertSelectorDecoyMatchesCanonicalHandler(string corruption)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var compilationUnit = CSharpSyntaxTree
                .ParseText(File.ReadAllText(ProfilePath), parseOptions, ProfilePath)
                .GetCompilationUnitRoot();
            var canonicalMethod = CSharpSyntaxTree
                .ParseText(
                    $"public sealed class CanonicalAgent\n{{\n{CanonicalInitializeMethod}\n}}",
                    parseOptions)
                .GetCompilationUnitRoot()
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single();

            switch (corruption)
            {
                case "other-top-level-class-canonical-decoy":
                    var otherMethod = compilationUnit
                        .DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .Single(candidate => candidate.Identifier.ValueText == "OtherAgent")
                        .Members
                        .OfType<MethodDeclarationSyntax>()
                        .Single(candidate => candidate.Identifier.ValueText == "HandleInitializeAsync");
                    Assert.True(SyntaxFactory.AreEquivalent(canonicalMethod, otherMethod));
                    break;
                case "nested-class-canonical-decoy":
                    var nestedMethod = compilationUnit
                        .DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .Single(candidate => candidate.Identifier.ValueText == "NestedAgent")
                        .Members
                        .OfType<MethodDeclarationSyntax>()
                        .Single(candidate => candidate.Identifier.ValueText == "HandleInitializeAsync");
                    Assert.True(SyntaxFactory.AreEquivalent(canonicalMethod, nestedMethod));
                    break;
                case "local-function-canonical-decoy":
                    var localFunction = compilationUnit
                        .DescendantNodes()
                        .OfType<LocalFunctionStatementSyntax>()
                        .Single(candidate => candidate.Identifier.ValueText == "HandleInitializeAsync");
                    Assert.Single(localFunction.Modifiers);
                    Assert.True(localFunction.Modifiers[0].IsKind(SyntaxKind.AsyncKeyword));
                    Assert.True(SyntaxFactory.AreEquivalent(canonicalMethod.ReturnType, localFunction.ReturnType));
                    Assert.True(SyntaxFactory.AreEquivalent(canonicalMethod.ParameterList, localFunction.ParameterList));
                    Assert.True(SyntaxFactory.AreEquivalent(canonicalMethod.Body!, localFunction.Body!));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
            }
        }

        private void CorruptDirectInitializeHandler() =>
            ReplaceOnce(
                ProfilePath,
                ProfileAuthority,
                $"State.Operations.Insert(0, new AgentProfileOperationFact());{Environment.NewLine}        {ProfileAuthority}");

        private void AddUnsafeRegisteredInitializeHandler() =>
            ReplaceOnce(
                ProfilePath,
                "public sealed class AgentProfileGAgent\n{",
                """
                public sealed class AgentProfileGAgent
                {
                    [EventHandler]
                    public async Task HandleUnsafeInitializeAsync(InitializeAgentProfileCommand command)
                    {
                        ArgumentNullException.ThrowIfNull(command);
                        State.Operations.Insert(0, new AgentProfileOperationFact());
                        var namespaceActorId = State.Identity is null
                            ? AgentProfileActorIds.Namespace
                            : AgentProfileActorInvariants.RequireActorId(
                                State.NamespaceActorId,
                                "state.namespace_actor_id");
                        AgentProfileActorInvariants.RequireProtocolPublisher(
                            ActiveInboundEnvelope,
                            namespaceActorId);
                        var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
                        await PersistAsync(operation);
                    }
                """);

        private void AddOtherTopLevelCanonicalHandler() =>
            ReplaceOnce(
                ProfilePath,
                "public sealed class AgentProfileGAgent\n{",
                $$"""
                public sealed class OtherAgent
                {
                {{CanonicalInitializeMethod}}
                }

                public sealed class AgentProfileGAgent
                {
                """);

        private void AddNestedCanonicalHandler() =>
            ReplaceOnce(
                ProfilePath,
                "public sealed class AgentProfileGAgent\n{",
                $$"""
                public sealed class AgentProfileGAgent
                {
                    private sealed class NestedAgent
                    {
                {{CanonicalInitializeMethod}}
                    }

                """);

        private void AddCanonicalLocalFunction()
        {
            var canonicalLocalFunction = CanonicalInitializeMethod.Replace(
                "public async Task",
                "async Task",
                StringComparison.Ordinal);
            ReplaceOnce(
                ProfilePath,
                "public sealed class AgentProfileGAgent\n{",
                $$"""
                public sealed class AgentProfileGAgent
                {
                    private void DeclareLocalHandler()
                    {
                {{canonicalLocalFunction}}
                    }

                """);
        }

        private void ReplaceAuthorityInMethod(string methodName, string replacement)
        {
            var source = File.ReadAllText(NamespacePath);
            var methodStart = source.IndexOf($"{methodName}(", StringComparison.Ordinal);
            Assert.True(methodStart >= 0, $"Missing method {methodName} in fixture.");
            var authority = source.IndexOf(InitializedAuthority, methodStart, StringComparison.Ordinal);
            Assert.True(authority >= 0, $"Missing authority in method {methodName}.");
            source = source.Remove(authority, InitializedAuthority.Length).Insert(authority, replacement);
            File.WriteAllText(NamespacePath, source);
        }

        private static void ReplaceOnce(string path, string current, string replacement)
        {
            var source = File.ReadAllText(path);
            var index = source.IndexOf(current, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Fixture text was not found: {current}");
            source = source.Remove(index, current.Length).Insert(index, replacement);
            File.WriteAllText(path, source);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private const string CanonicalInitializeMethod = """
            [EventHandler]
            public async Task HandleInitializeAsync(InitializeAgentProfileCommand command)
            {
                ArgumentNullException.ThrowIfNull(command);
                var namespaceActorId = State.Identity is null
                    ? AgentProfileActorIds.Namespace
                    : AgentProfileActorInvariants.RequireActorId(
                        State.NamespaceActorId,
                        "state.namespace_actor_id");
                AgentProfileActorInvariants.RequireProtocolPublisher(
                    ActiveInboundEnvelope,
                    namespaceActorId);
                var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
                await PersistAsync(operation);
            }
            """;

        private const string ValidProfileActor = """
            namespace Aevatar.GAgentService.Core.AgentProfiles;

            public sealed class AgentProfileGAgent
            {
                [EventHandler]
                public async Task HandleInitializeAsync(InitializeAgentProfileCommand command)
                {
                    ArgumentNullException.ThrowIfNull(command);
                    var namespaceActorId = State.Identity is null
                        ? AgentProfileActorIds.Namespace
                        : AgentProfileActorInvariants.RequireActorId(
                            State.NamespaceActorId,
                            "state.namespace_actor_id");
                    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, namespaceActorId);
                    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
                    await PersistAsync(operation);
                }
            }
            """;

        private const string ValidNamespaceActor = """
            namespace Aevatar.GAgentService.Core.AgentProfiles;

            public sealed class AgentProfileNamespaceGAgent
            {
                [EventHandler]
                public async Task HandleInitializedAsync(AgentProfileInitializedContinuation continuation)
                {
                    ArgumentNullException.ThrowIfNull(continuation);
                    var profileActorId = AgentProfileActorInvariants.RequireActorId(
                        continuation.ProfileActorId,
                        "profile_actor_id");
                    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);
                    var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
                    await PersistAsync(operation);
                }

                [EventHandler]
                public async Task HandleInitializationRejectedAsync(
                    AgentProfileInitializationRejectedContinuation continuation)
                {
                    ArgumentNullException.ThrowIfNull(continuation);
                    var profileActorId = AgentProfileActorInvariants.RequireActorId(
                        continuation.ProfileActorId,
                        "profile_actor_id");
                    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, profileActorId);
                    var operation = AgentProfileActorInvariants.RequireOperation(continuation.Operation);
                    await PersistAsync(operation);
                }

                [EventHandler]
                public async Task HandleObservePublishedSummaryAsync(
                    ObserveAgentProfilePublishedSummaryCommand command)
                {
                    ArgumentNullException.ThrowIfNull(command);
                    AgentProfileIdentity identity;
                    AgentProfilePublishedSummary summary;
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

                    var entry = FindProfile(identity.ProfileId);
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
                    AgentProfileActorInvariants.RequireProtocolPublisher(ActiveInboundEnvelope, entry.ProfileActorId);
                    var operation = AgentProfileActorInvariants.RequireOperation(command.Operation);
                    await PersistAsync(operation);
                }
            }
            """;
    }
}
