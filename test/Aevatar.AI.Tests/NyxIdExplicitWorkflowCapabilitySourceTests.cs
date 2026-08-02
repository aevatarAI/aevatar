using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExplicitWorkflowCapabilitySourceTests
{
    [Fact]
    public void AddNyxIdTools_ShouldRegisterExplicitRequestCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExplicitWorkflowCapabilitySource));
    }

    [Fact]
    public async Task InspectAsync_ShouldBuildExplicitProofFromExactUserServiceWithoutMcpRead()
    {
        var handler = new InventoryHandler(UserServiceKeys(Service()));
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedSelector.Should().BeEquivalentTo(Selector());
        result.SelectedCapability.CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest);
        var proof = result.SelectedCapability.NyxIdUserRequest;
        proof.Request.Should().BeEquivalentTo(Selector().NyxIdRequest);
        proof.ServiceSlugSnapshot.Should().Be("shared-slug");
        proof.ContractDigest.Should().NotBeNullOrWhiteSpace();
        proof.ExecutionPolicy.Risk.Should().Be(NyxIdOperationRisk.ReadOnly);
        proof.ExecutionPolicy.Approval.Should().Be(NyxIdOperationApproval.None);
        proof.ExecutionPolicy.EnforcementOwner.Should().Be(NyxIdOperationEnforcementOwner.Aevatar);
        proof.ExecutionPolicy.AllowedExecutionModes.Should().Equal(
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        result.Sources.Should().ContainSingle().Which.SourceKind.Should()
            .Be(ExternalCapabilitySourceKind.NyxIdUserServices);
        handler.Requests.Should().Equal(new RequestRecord("/api/v1/keys", "caller-credential"));
    }

    [Fact]
    public async Task InspectAsync_WithoutSourceReadableCredential_ShouldFailBeforeInventoryRead()
    {
        var handler = new InventoryHandler(UserServiceKeys(Service()));
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            new ExternalWorkflowCapabilityAccessContext("scope-alpha", "nyx-user-alpha"),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.ServiceAccessDenied);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_ADMISSION_SOURCE_CREDENTIAL_REQUIRED");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Get)]
    [InlineData(NyxIdRequestMethod.Head)]
    [InlineData(NyxIdRequestMethod.Options)]
    public async Task InspectAsync_ShouldAdmitDurableSafeRequestFromExactCatalogGrant(
        NyxIdRequestMethod method)
    {
        var handler = new InventoryHandler(UserServiceKeys(Service()));
        var catalog = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(handler, catalog);

        var result = await source.InspectAsync(
            Access(),
            Selector(method),
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedCapability.NyxIdUserRequest.Request.UserServiceId.Should().Be("usvc-alpha");
        result.Sources.Select(static source => source.SourceKind).Should().BeEquivalentTo(new[]
        {
            ExternalCapabilitySourceKind.NyxIdUserServices,
            ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
        });
        var expectedOwner = Owner();
        catalog.Owners.Should().ContainSingle().Which.Should().BeEquivalentTo(expectedOwner);
        result.Sources.Single(static source =>
                source.SourceKind == ExternalCapabilitySourceKind.DurableAuthorizationCatalog)
            .SourceId.Should().Be(NyxIdAuthorizationCatalogActorIds.Build(expectedOwner));
        handler.Requests.Should().Equal(new RequestRecord("/api/v1/keys", "caller-credential"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("owner_mismatch")]
    [InlineData("exact_id_missing")]
    [InlineData("slug_only")]
    [InlineData("same_slug_different_id")]
    public async Task InspectAsync_ShouldFailClosedWhenDurableCatalogDoesNotProveExactService(
        string scenario)
    {
        var snapshot = scenario switch
        {
            "missing" => null,
            "owner_mismatch" => ReadyCatalogSnapshot(ownerSubject: "nyx-user-beta"),
            "exact_id_missing" => ReadyCatalogSnapshot("usvc-beta", "different-slug"),
            "slug_only" => ReadyCatalogSnapshot(string.Empty),
            "same_slug_different_id" => ReadyCatalogSnapshot("usvc-beta"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var source = CreateSource(
            new InventoryHandler(UserServiceKeys(Service())),
            new RecordingCatalogQueryPort(snapshot));

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
    }

    [Fact]
    public async Task InspectAsync_WhenDurableCatalogQueryThrows_ShouldFailClosedAndLogSanitizedWarning()
    {
        const string sensitiveExceptionMessage = "catalog secret-token-alpha";
        var logger = new RecordingLogger<NyxIdExplicitWorkflowCapabilitySource>();
        var source = CreateSource(
            new InventoryHandler(UserServiceKeys(Service())),
            new ThrowingCatalogQueryPort(new InvalidOperationException(sensitiveExceptionMessage)),
            logger);

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
        var warning = logger.Entries.Should().ContainSingle(static entry =>
            entry.Level == LogLevel.Warning).Subject;
        warning.Exception.Should().BeNull();
        warning.Message.Should().NotContain(sensitiveExceptionMessage);
        warning.Properties.Should().Contain("OwnerAuthority", NyxIdAuthorizationAuthorities.NyxId);
        warning.Properties.Should().Contain("OwnerKind", AuthorizationOwnerKind.Personal);
        warning.Properties.Should().Contain("FailureType", nameof(InvalidOperationException));
    }

    [Theory]
    [InlineData("inactive", ExternalCapabilityReadinessStatus.CredentialConnectionRequired, "USER_SERVICE_INACTIVE")]
    [InlineData("inaccessible", ExternalCapabilityReadinessStatus.ServiceAccessDenied, "USER_SERVICE_ACCESS_DENIED")]
    public async Task InspectAsync_ShouldRejectUnusableServiceBeforeDurableCatalogRead(
        string scenario,
        ExternalCapabilityReadinessStatus expectedStatus,
        string expectedCode)
    {
        var service = scenario == "inactive"
            ? Service(active: false)
            : Service(credentialSource: "org", allowed: false);
        var catalog = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(new InventoryHandler(UserServiceKeys(service)), catalog);

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.Blockers.Should().ContainSingle().Which.Code.Should().Be(expectedCode);
        catalog.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsync_ShouldNotReadDurableCatalogForInteractiveRequest()
    {
        var catalog = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(new InventoryHandler(UserServiceKeys(Service())), catalog);

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Interactive, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        catalog.ReadCount.Should().Be(0);
        result.Sources.Should().ContainSingle().Which.SourceKind.Should()
            .Be(ExternalCapabilitySourceKind.NyxIdUserServices);
    }

    [Theory]
    [InlineData(NyxIdRequestMethod.Post, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Put, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Patch, NyxIdOperationRisk.Write)]
    [InlineData(NyxIdRequestMethod.Delete, NyxIdOperationRisk.Destructive)]
    public async Task InspectAsync_ShouldKeepMutatingDurableRequestsInteractiveOnly(
        NyxIdRequestMethod method,
        NyxIdOperationRisk expectedRisk)
    {
        var catalog = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(new InventoryHandler(UserServiceKeys(Service())), catalog);

        var result = await source.InspectAsync(
            Access(), Selector(method), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED");
        result.SelectedCapability.NyxIdUserRequest.ExecutionPolicy.Risk.Should().Be(expectedRisk);
        result.SelectedCapability.NyxIdUserRequest.ExecutionPolicy.Approval.Should()
            .Be(NyxIdOperationApproval.Required);
        result.SelectedCapability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Should()
            .Equal(ExternalCapabilityExecutionMode.Interactive);
        catalog.ReadCount.Should().Be(0);
    }

    [Theory]
    [InlineData("missing", ExternalCapabilityReadinessStatus.ServiceRegistrationRequired)]
    [InlineData("inactive", ExternalCapabilityReadinessStatus.CredentialConnectionRequired)]
    [InlineData("inaccessible", ExternalCapabilityReadinessStatus.ServiceAccessDenied)]
    [InlineData("ambiguous", ExternalCapabilityReadinessStatus.SourceStale)]
    public async Task InspectAsync_ShouldFailClosedForUnusableExactService(
        string scenario,
        ExternalCapabilityReadinessStatus expectedStatus)
    {
        var response = scenario switch
        {
            "missing" => UserServiceKeys(Service(id: "usvc-beta")),
            "inactive" => UserServiceKeys(Service(active: false)),
            "inaccessible" => UserServiceKeys(Service(credentialSource: "org", allowed: false)),
            "ambiguous" => UserServiceKeys(Service(), Service(slug: "other-slug")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var source = CreateSource(new InventoryHandler(response));

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.SelectedCapability.Should().BeNull();
        result.Blockers.Should().ContainSingle().Which.Code.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("pending_auth")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("failed")]
    [InlineData("refresh_failed")]
    public async Task InspectAsync_ShouldRejectDirectServiceWithoutActiveCredential(string status)
    {
        var catalog = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(
            new InventoryHandler(UserServiceKeys(Service(status: status))),
            catalog);

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.CredentialConnectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("USER_SERVICE_CREDENTIAL_NOT_READY");
        result.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.ConnectCredential);
        catalog.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsync_ShouldAdmitOnlineNodeManagedServiceWithoutActiveServerCredential()
    {
        var source = CreateSource(new InventoryHandler(UserServiceKeys(
            Service(status: "pending_auth", nodeId: "node-alpha", nodeStatus: "online"))));

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Interactive, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("draining")]
    [InlineData("unknown")]
    public async Task InspectAsync_ShouldRejectUnavailableExplicitNodeRoute(string nodeStatus)
    {
        var source = CreateSource(new InventoryHandler(UserServiceKeys(
            Service(nodeId: "node-alpha", nodeStatus: nodeStatus))));

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Interactive, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.NodeUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("USER_SERVICE_NODE_UNAVAILABLE");
        result.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.RestoreNode);
    }

    [Fact]
    public async Task InspectAsync_ShouldRejectInaccessibleExplicitNodeRoute()
    {
        var source = CreateSource(new InventoryHandler(UserServiceKeys(
            Service(nodeId: "node-alpha", nodeStatus: "inaccessible"))));

        var result = await source.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Interactive, CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.ServiceAccessDenied);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("USER_SERVICE_NODE_ACCESS_DENIED");
        result.Remediations.Should().ContainSingle().Which.ActionKind.Should()
            .Be(ExternalCapabilityRemediationActionKind.RequestAccess);
    }

    private static NyxIdExplicitWorkflowCapabilitySource CreateSource(
        InventoryHandler handler,
        INyxIdAuthorizationCatalogQueryPort? catalog = null,
        ILogger<NyxIdExplicitWorkflowCapabilitySource>? logger = null)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" };
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton(new NyxIdApiClient(options, new HttpClient(handler)))
            .AddSingleton<TimeProvider>(new FixedTimeProvider());
        if (catalog is not null)
            services.AddSingleton(catalog);
        if (logger is not null)
            services.AddSingleton(logger);
        using var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<NyxIdExplicitWorkflowCapabilitySource>(provider);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-alpha",
            "nyx-user-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("caller-credential"));

    private static ExternalWorkflowCapabilitySelector Selector(
        NyxIdRequestMethod method = NyxIdRequestMethod.Get)
    {
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = method,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = method is NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch
                ? NyxIdRequestBodyMode.Json
                : NyxIdRequestBodyMode.None,
            BodyRequired = method is NyxIdRequestMethod.Post or NyxIdRequestMethod.Put or NyxIdRequestMethod.Patch,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        request.QueryParameters.Add("page_size");
        request.HeaderParameters.Add("If-Match");
        return new ExternalWorkflowCapabilitySelector { NyxIdRequest = request };
    }

    private static string UserServiceKeys(params string[] services) =>
        $"{{\"keys\":[{string.Join(',', services)}]}}";

    private static string Service(
        string id = "usvc-alpha",
        string slug = "shared-slug",
        bool active = true,
        string status = "active",
        string? nodeId = null,
        string? nodeStatus = null,
        string credentialSource = "personal",
        bool allowed = true)
    {
        var source = credentialSource == "personal"
            ? "{\"type\":\"personal\"}"
            : $"{{\"type\":\"org\",\"org_id\":\"org-alpha\",\"org_name\":\"Org Alpha\",\"role\":\"member\",\"allowed\":{allowed.ToString().ToLowerInvariant()}}}";
        var node = nodeId is null
            ? string.Empty
            : $",\"node_id\":\"{nodeId}\",\"node_status\":\"{nodeStatus ?? "online"}\"";
        return $"{{\"id\":\"{id}\",\"slug\":\"{slug}\",\"label\":\"Example service\",\"catalog_service_name\":null,\"status\":\"{status}\",\"is_active\":{active.ToString().ToLowerInvariant()},\"credential_source\":{source},\"endpoint_id\":\"endpoint-alpha\",\"endpoint_url\":\"https://example.invalid\",\"connected\":true{node}}}";
    }

    private static AuthorizationOwnerIdentity Owner(string subject = "nyx-user-alpha") =>
        new()
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = subject,
        };

    private static NyxIdAuthorizationCatalogSnapshot ReadyCatalogSnapshot(
        string userServiceId = "usvc-alpha",
        string serviceSlug = "shared-slug",
        string ownerSubject = "nyx-user-alpha")
    {
        var owner = Owner(ownerSubject);
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = userServiceId,
            ServiceSlug = serviceSlug,
            DisplayName = "Example service",
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
            ResourceOwner = Owner(ownerSubject),
        };
        NyxIdAuthorizationServiceEvidence[] services = [service];
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            17,
            new DateTimeOffset(2026, 7, 30, 7, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 30, 8, 5, 0, TimeSpan.Zero),
            "scope-plan-contract/v1",
            "scope-plan-policy/v1",
            new DateTimeOffset(2026, 7, 30, 7, 59, 0, TimeSpan.Zero),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            services,
            Activated: true);
    }

    private sealed class InventoryHandler(string response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var record = new RequestRecord(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Parameter ?? string.Empty);
            Requests.Add(record);
            if (record.Path != "/api/v1/keys")
                throw new InvalidOperationException($"Unexpected explicit admission request: {record.Path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public int ReadCount => Owners.Count;

        public List<AuthorizationOwnerIdentity> Owners { get; } = [];

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            Owners.Add(owner.Clone());
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ThrowingCatalogQueryPort(Exception exception)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default) =>
            Task.FromException<NyxIdAuthorizationCatalogSnapshot?>(exception);
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values
                    .Where(static value => value.Key != "{OriginalFormat}")
                    .ToDictionary(static value => value.Key, static value => value.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed record RequestRecord(string Path, string BearerToken);
}
