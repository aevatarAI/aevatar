using System.Security.Cryptography;
using System.Text;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Infrastructure.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ServiceExposureReconcileHookTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldReconcileProjectedExposureIntent_WhenGlobalRegistrationAndPoliciesDoNotMatch()
    {
        var identity = Identity();
        var catalog = ServiceSnapshot(
            identity,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Pending,
                DesiredSpecHash: "previous-hash",
                ExposureDesired: true));
        var commandPort = new RecordingServiceCommandPort();
        var hook = CreateHook(catalog, commandPort);

        await hook.BeforePublishAsync(CreateContext(new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        commandPort.ReconcileCommands.Should().ContainSingle();
        var command = commandPort.ReconcileCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.OpenapiUrl.Should().Be(ExpectedOpenApiUrl(identity));
        command.DesiredSpecHash.Should().Be(ExpectedSpecHash(catalog, command.OpenapiUrl));
        command.CredentialKid.Should().Be("kid-1");
        commandPort.RetireCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldNotReconcile_WhenGlobalRegistrationAndPoliciesDoNotMatchAndExposureIsNotDesired()
    {
        var identity = Identity();
        var commandPort = new RecordingServiceCommandPort();
        var hook = CreateHook(
            ServiceSnapshot(
                identity,
                new ServiceExternalExposureSnapshot(
                    string.Empty,
                    null,
                    ServiceRegistrationStatus.Unspecified,
                    ExposureDesired: false)),
            commandPort);

        await hook.BeforePublishAsync(CreateContext(new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        commandPort.ReconcileCommands.Should().BeEmpty();
        commandPort.RetireCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldReconcile_WhenGlobalRegistrationIsEnabled()
    {
        var identity = Identity();
        var catalog = ServiceSnapshot(
            identity,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Unspecified,
                ExposureDesired: false));
        var commandPort = new RecordingServiceCommandPort();
        var hook = CreateHook(
            catalog,
            commandPort,
            options => options.RegisterAllPublishedServices = true);

        await hook.BeforePublishAsync(CreateContext(new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        commandPort.ReconcileCommands.Should().ContainSingle();
        var command = commandPort.ReconcileCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.OpenapiUrl.Should().Be(ExpectedOpenApiUrl(identity));
        command.DesiredSpecHash.Should().Be(ExpectedSpecHash(catalog, command.OpenapiUrl));
        command.CredentialKid.Should().Be("kid-1");
        commandPort.RetireCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldReconcile_WhenPolicyMatchesOptInPolicy()
    {
        var identity = Identity();
        var catalog = ServiceSnapshot(
            identity,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Unspecified,
                ExposureDesired: false),
            ["external-policy"]);
        var commandPort = new RecordingServiceCommandPort();
        var hook = CreateHook(catalog, commandPort);

        await hook.BeforePublishAsync(CreateContext(new ServiceDeploymentActivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            Status = ServiceDeploymentStatus.Active,
            ActivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        commandPort.ReconcileCommands.Should().ContainSingle();
        var command = commandPort.ReconcileCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.OpenapiUrl.Should().Be(ExpectedOpenApiUrl(identity));
        command.DesiredSpecHash.Should().Be(ExpectedSpecHash(catalog, command.OpenapiUrl));
        command.CredentialKid.Should().Be("kid-1");
        commandPort.RetireCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_ShouldRetireExistingExposure_WhenIntentIsOptOut()
    {
        var identity = Identity();
        var catalog = ServiceSnapshot(
            identity,
            new ServiceExternalExposureSnapshot(
                "orders-live",
                DateTimeOffset.Parse("2026-06-23T00:00:00+00:00"),
                ServiceRegistrationStatus.Registered,
                NyxidServiceId: "nyxid-service-1",
                DesiredSpecHash: "desired-hash-1",
                ExposureDesired: true));
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateIntentService(catalog, commandPort);

        await service.ApplyAsync(new ServiceExternalExposureIntentRequest(
            identity,
            ExposureDesired: false,
            ExistingService: catalog));

        commandPort.RetireCommands.Should().ContainSingle();
        var command = commandPort.RetireCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.Identity.Should().NotBeSameAs(identity);
        command.DesiredSpecHash.Should().Be("desired-hash-1");
        commandPort.ReconcileCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_ShouldReconcileDesiredDefinition_WhenIntentIsOptIn()
    {
        var identity = Identity();
        var existingCatalog = ServiceSnapshot(
            identity,
            new ServiceExternalExposureSnapshot(
                string.Empty,
                null,
                ServiceRegistrationStatus.Unspecified,
                DesiredSpecHash: "old-hash",
                ExposureDesired: false));
        var desiredDefinition = new ServiceDefinitionSpec
        {
            Identity = identity.Clone(),
            DisplayName = "Orders Public",
            ExternalExposure = new ExternalExposure
            {
                ExposureDesired = true,
            },
        };
        desiredDefinition.Endpoints.Add(new ServiceEndpointSpec
        {
            EndpointId = "submit",
            DisplayName = "Submit",
            Kind = ServiceEndpointKind.Command,
            RequestTypeUrl = "type.googleapis.com/aevatar.SubmitOrderRequest",
            ResponseTypeUrl = "type.googleapis.com/aevatar.SubmitOrderResponse",
            Description = "Submit an order.",
        });
        var commandPort = new RecordingServiceCommandPort();
        var service = CreateIntentService(existingCatalog, commandPort);

        await service.ApplyAsync(new ServiceExternalExposureIntentRequest(
            identity,
            ExposureDesired: true,
            DesiredDefinition: desiredDefinition,
            ExistingService: existingCatalog));

        commandPort.ReconcileCommands.Should().ContainSingle();
        var command = commandPort.ReconcileCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.Identity.Should().NotBeSameAs(identity);
        command.OpenapiUrl.Should().Be(ExpectedOpenApiUrl(identity));
        command.DesiredSpecHash.Should().Be(ExpectedSpecHash(
            DesiredServiceSnapshot(identity, desiredDefinition, existingCatalog),
            command.OpenapiUrl));
        command.DesiredSpecHash.Should().NotBe(ExpectedSpecHash(existingCatalog, command.OpenapiUrl));
        command.CredentialKid.Should().Be("kid-1");
        commandPort.RetireCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldRetireExposure_WhenDeploymentIsDeactivated()
    {
        var identity = Identity();
        var commandPort = new RecordingServiceCommandPort();
        var hook = CreateHook(ServiceSnapshot(identity, null), commandPort);

        await hook.BeforePublishAsync(CreateContext(new ServiceDeploymentDeactivatedEvent
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            DeactivatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }), CancellationToken.None);

        commandPort.RetireCommands.Should().ContainSingle();
        var command = commandPort.RetireCommands.Single();
        command.Identity.Should().BeEquivalentTo(identity);
        command.Identity.Should().NotBeSameAs(identity);
        command.DesiredSpecHash.Should().BeEmpty();
        commandPort.ReconcileCommands.Should().BeEmpty();
    }

    private static ServiceExposureReconcileHook CreateHook(
        ServiceCatalogSnapshot catalog,
        RecordingServiceCommandPort commandPort,
        Action<ServiceExternalExposureOptions>? configureOptions = null)
    {
        var catalogReader = new FakeServiceCatalogQueryReader(catalog);
        var exposureOptions = new ServiceExternalExposureOptions
        {
            Enabled = true,
            PublicBaseUrl = "https://public.example.test/root/",
            RegisterAllPublishedServices = false,
            OptInPolicyIds = ["external-policy"],
        };
        configureOptions?.Invoke(exposureOptions);
        var options = Options.Create(exposureOptions);
        return new ServiceExposureReconcileHook(
            catalogReader,
            CreateIntentService(catalog, commandPort, exposureOptions),
            options);
    }

    private static ServiceExternalExposureIntentService CreateIntentService(
        ServiceCatalogSnapshot catalog,
        RecordingServiceCommandPort commandPort,
        ServiceExternalExposureOptions? exposureOptions = null)
    {
        var catalogReader = new FakeServiceCatalogQueryReader(catalog);
        exposureOptions ??= new ServiceExternalExposureOptions
        {
            Enabled = true,
            PublicBaseUrl = "https://public.example.test/root/",
            RegisterAllPublishedServices = false,
            OptInPolicyIds = ["external-policy"],
        };
        var options = Options.Create(exposureOptions);
        var keyProvider = new StaticScopeServiceTokenKeyProvider("kid-1");
        return new ServiceExternalExposureIntentService(catalogReader, commandPort, options, keyProvider);
    }

    private static CommittedStatePublicationContext CreateContext<TEvent>(TEvent evt)
        where TEvent : class, IMessage<TEvent> =>
        new()
        {
            ActorId = "deployment-actor",
            ActorType = typeof(object),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    EventData = Any.Pack(evt),
                },
            },
        };

    private static ServiceCatalogSnapshot ServiceSnapshot(
        ServiceIdentity identity,
        ServiceExternalExposureSnapshot? externalExposure,
        IReadOnlyList<string>? policyIds = null) =>
        new(
            "tenant-a:app-a:orders:svc alpha",
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Orders",
            "rev-1",
            "rev-1",
            "dep-1",
            "actor-1",
            ServiceDeploymentStatus.Active.ToString(),
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "Chat",
                    ServiceEndpointKind.Chat.ToString(),
                    "type.googleapis.com/aevatar.ChatRequest",
                    "type.googleapis.com/aevatar.ChatResponse",
                    "Chat endpoint."),
                new ServiceEndpointSnapshot(
                    "command",
                    "Command",
                    ServiceEndpointKind.Command.ToString(),
                    "type.googleapis.com/aevatar.CommandRequest",
                    "type.googleapis.com/aevatar.CommandResponse",
                    "Command endpoint."),
            ],
            policyIds ?? ["internal-policy"],
            DateTimeOffset.Parse("2026-06-23T00:00:00+00:00"),
            externalExposure);

    private static ServiceCatalogSnapshot DesiredServiceSnapshot(
        ServiceIdentity identity,
        ServiceDefinitionSpec desiredDefinition,
        ServiceCatalogSnapshot existing) =>
        new(
            existing.ServiceKey,
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            desiredDefinition.DisplayName,
            existing.DefaultServingRevisionId,
            existing.ActiveServingRevisionId,
            existing.DeploymentId,
            existing.PrimaryActorId,
            existing.DeploymentStatus,
            desiredDefinition.Endpoints.Select(x => new ServiceEndpointSnapshot(
                x.EndpointId,
                x.DisplayName,
                x.Kind.ToString(),
                x.RequestTypeUrl,
                x.ResponseTypeUrl,
                x.Description)).ToArray(),
            desiredDefinition.PolicyIds.ToArray(),
            existing.UpdatedAt,
            new ServiceExternalExposureSnapshot(
                desiredDefinition.ExternalExposure.NyxidSlug,
                desiredDefinition.ExternalExposure.RegisteredAt?.ToDateTimeOffset(),
                desiredDefinition.ExternalExposure.Status,
                desiredDefinition.ExternalExposure.NyxidServiceId,
                desiredDefinition.ExternalExposure.DesiredSpecHash,
                desiredDefinition.ExternalExposure.RegisteredSpecHash,
                desiredDefinition.ExternalExposure.LastError,
                desiredDefinition.ExternalExposure.Attempt,
                desiredDefinition.ExternalExposure.NextAttemptAt?.ToDateTimeOffset(),
                desiredDefinition.ExternalExposure.CredentialKid,
                desiredDefinition.ExternalExposure.ExposureDesired));

    private static ServiceIdentity Identity() =>
        new()
        {
            TenantId = "tenant-a",
            AppId = "app-a",
            Namespace = "orders",
            ServiceId = "svc alpha",
        };

    private static string ExpectedOpenApiUrl(ServiceIdentity identity) =>
        string.Concat(
            "https://public.example.test/root/api/services/",
            Uri.EscapeDataString(identity.ServiceId),
            "/openapi.json?tenantId=",
            Uri.EscapeDataString(identity.TenantId),
            "&appId=",
            Uri.EscapeDataString(identity.AppId),
            "&namespace=",
            Uri.EscapeDataString(identity.Namespace));

    private static string ExpectedSpecHash(ServiceCatalogSnapshot service, string openApiUrl)
    {
        using var sha = SHA256.Create();
        var buffer = new StringBuilder();
        buffer.Append(service.ServiceKey).Append('|')
            .Append(service.DisplayName).Append('|')
            .Append(openApiUrl).Append('|');

        foreach (var endpoint in service.Endpoints.OrderBy(x => x.EndpointId, StringComparer.Ordinal))
        {
            buffer.Append(endpoint.EndpointId).Append(':')
                .Append(endpoint.Kind).Append(':')
                .Append(endpoint.RequestTypeUrl).Append(':')
                .Append(endpoint.ResponseTypeUrl).Append(';');
        }

        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(buffer.ToString()))).ToLowerInvariant();
    }

    private sealed class FakeServiceCatalogQueryReader(ServiceCatalogSnapshot? snapshot) : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(snapshot);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt Receipt = new("actor", "cmd", "corr");

        public List<ReconcileExternalExposureCommand> ReconcileCommands { get; } = [];

        public List<RetireExternalExposureCommand> RetireCommands { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> ReconcileExternalExposureAsync(
            ReconcileExternalExposureCommand command,
            CancellationToken ct = default)
        {
            ReconcileCommands.Add(command);
            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> RetireExternalExposureAsync(
            RetireExternalExposureCommand command,
            CancellationToken ct = default)
        {
            RetireCommands.Add(command);
            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);
    }

    private sealed class StaticScopeServiceTokenKeyProvider : IScopeServiceTokenKeyProvider
    {
        public StaticScopeServiceTokenKeyProvider(string kid)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"))
            {
                KeyId = kid,
            };
            CurrentSigningKey = new ScopeServiceSigningKey(kid, SecurityAlgorithms.HmacSha256, key, key);
            ValidationKeys = [CurrentSigningKey];
        }

        public string Issuer => "issuer";

        public string? Audience => null;

        public TimeSpan ClockSkew => TimeSpan.Zero;

        public ScopeServiceSigningKey CurrentSigningKey { get; }

        public IReadOnlyList<ScopeServiceSigningKey> ValidationKeys { get; }
    }
}
