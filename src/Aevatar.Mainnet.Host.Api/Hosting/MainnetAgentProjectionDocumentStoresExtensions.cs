using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Stores;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.ProjectionRecovery;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Mainnet.Host.Api.Status;
using Aevatar.Mainnet.Host.Api.ProjectionRecovery;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.Hosting;

public static class MainnetAgentProjectionDocumentStoresExtensions
{
    // Engine labels surfaced by the read-model inventory (GET /api/cqrs/readmodels). The document
    // store/engine choice is a per-type branch in this file (ES vs InMemory), so the inventory
    // descriptors are declared right next to the store registrations that pick the engine.
    private const string ElasticsearchEngineLabel = "Elasticsearch";
    private const string InMemoryEngineLabel = "dev/InMemory";

    public static IServiceCollection AddMainnetAgentProjectionDocumentStores(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(
            configuration,
            "MainnetAgentProjectionDocumentStores");

        if (documentProvider.ElasticsearchEnabled)
        {
            AddElasticsearchStores(services, configuration);
            // Keep the operator-bound enforcement mode. The conservative built-in
            // HTTP probe cannot prove that every other Elasticsearch identity is
            // denied, so forcing Strict here would make the stock Mainnet
            // composition impossible to start. Warn remains the deployable default;
            // Strict is an explicit deployment policy paired with a stronger probe.
            // Replace only the identity module's Unavailable fallback. A deployment may
            // pre-register a stronger verifier that can positively prove the effective
            // grant; Mainnet must not overwrite it with the conservative built-in probe.
            RegisterDefaultOAuthClientEsAclProbe(services, configuration);
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, AevatarOAuthClientEsAclStartupGuard>());
            // Self-heal projection-index schema drift at startup (reindex + atomic alias swap)
            // so a deploy that bumps a read-model schema doesn't 500 reads (e.g. /ws/voice).
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ElasticsearchProjectionIndexReconcileHostedService>());
            AddReadModelInventoryDescriptors(services, ElasticsearchEngineLabel);
        }
        else
        {
            AddInMemoryStores(services);
            AddReadModelInventoryDescriptors(services, InMemoryEngineLabel);
        }

        // Assembles the read-model inventory from the opt-in descriptors registered above; reads the
        // materialized read-model stores only (the read-write-separation invariant).
        services.TryAddSingleton<IProjectionReadModelInventoryQueryPort, ProjectionReadModelInventoryQueryPort>();

        return services;
    }

    private static void RegisterDefaultOAuthClientEsAclProbe(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var registrations = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IOAuthClientEsAclProbe))
            .ToArray();
        var customRegistrations = registrations
            .Where(static descriptor =>
                descriptor.ImplementationType != typeof(UnavailableOAuthClientEsAclProbe))
            .ToArray();
        if (customRegistrations.Length > 1)
        {
            throw new InvalidOperationException(
                "Mainnet requires exactly one custom IOAuthClientEsAclProbe registration.");
        }

        foreach (var fallback in registrations.Except(customRegistrations))
            services.Remove(fallback);

        if (customRegistrations.Length == 1)
            return;

        services.AddSingleton<IOAuthClientEsAclProbe>(sp =>
            new HttpOAuthClientEsAclProbe(
                ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
                sp.GetService<ILogger<HttpOAuthClientEsAclProbe>>()));
    }

    private static void AddElasticsearchStores(IServiceCollection services, IConfiguration configuration)
    {
        RegisterElasticsearchAuditTrailArtifactStore(services, configuration);
        RegisterElasticsearchHealthProbeOperationalSnapshotStore(services, configuration);
        TryAddElasticsearchStore<ChannelBotRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ConversationDeliveryCurrentStateDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ProjectionScopeStatusDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ExternalIdentityBindingDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<AevatarOAuthClientDocument>(services, configuration, static document => document.Id);
        AddAevatarOAuthClientVersionRegressionRepair(services);
        TryAddElasticsearchStore<ManagedCodexCredentialDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<ChatRoutePolicyCurrentStateDocument>(services, configuration, static document => document.ActorId);
        TryAddElasticsearchStore<DeviceRegistrationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentCatalogNyxCredentialDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<UserAgentApiKeyRevocationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<WorkflowExternalApprovalContinuationDocument>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyChatSessionTerminalSnapshot>(services, configuration, static document => document.Id);
        TryAddElasticsearchStore<StreamingProxyRoomParticipantsSnapshot>(services, configuration, static document => document.Id);
    }

    private static void AddAevatarOAuthClientVersionRegressionRepair(IServiceCollection services)
    {
        if (!services.Any(static descriptor =>
                descriptor.ServiceType == typeof(IElasticsearchProjectionDocumentRepairStore<
                    AevatarOAuthClientDocument,
                    string>)))
        {
            services.AddElasticsearchDocumentProjectionRepairStore<
                AevatarOAuthClientDocument,
                string>();
        }

        services.TryAddSingleton<
            IAevatarOAuthClientVersionRegressionStorePort,
            ElasticsearchAevatarOAuthClientVersionRegressionStorePort>();
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(IAevatarOAuthClientProjectionRepublishPort)) &&
            services.Any(static descriptor => descriptor.ServiceType == typeof(IActorRuntime)))
        {
            services.TryAddSingleton<
                IAevatarOAuthClientVersionRegressionRepairService,
                AevatarOAuthClientVersionRegressionRepairService>();
        }
    }

    private static void AddInMemoryStores(IServiceCollection services)
    {
        RegisterInMemoryAuditTrailArtifactStore(services);
        services.Replace(ServiceDescriptor.Singleton<
            IHealthProbeOperationalSnapshotStore,
            InMemoryHealthProbeOperationalSnapshotStore>());
        TryAddInMemoryStore<ChannelBotRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ConversationDeliveryCurrentStateDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ProjectionScopeStatusDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ExternalIdentityBindingDocument>(services, static document => document.Id);
        TryAddInMemoryStore<AevatarOAuthClientDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ManagedCodexCredentialDocument>(services, static document => document.Id);
        TryAddInMemoryStore<ChatRoutePolicyCurrentStateDocument>(services, static document => document.ActorId);
        TryAddInMemoryStore<DeviceRegistrationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentCatalogNyxCredentialDocument>(services, static document => document.Id);
        TryAddInMemoryStore<UserAgentApiKeyRevocationDocument>(services, static document => document.Id);
        TryAddInMemoryStore<WorkflowExternalApprovalContinuationDocument>(services, static document => document.Id);
        TryAddInMemoryStore(
            services,
            static (StreamingProxyChatSessionTerminalSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
        TryAddInMemoryStore(
            services,
            static (StreamingProxyRoomParticipantsSnapshot document) => document.Id,
            static document => document.UpdatedAt.ToDateTimeOffset());
    }

    // Opt-in read-model inventory descriptors, one per materialized document read-model registered above.
    // shape = Document when backed by Elasticsearch, Memory when backed by the InMemory dev store; the
    // engine label is supplied by the caller (the branch that picked the provider). actorKind is the
    // best-available GAgent/actor kind whose current state each read-model replicates.
    private static void AddReadModelInventoryDescriptors(IServiceCollection services, string engineLabel)
    {
        var shape = ReferenceEquals(engineLabel, ElasticsearchEngineLabel)
            ? ProjectionReadModelSinkShape.Document
            : ProjectionReadModelSinkShape.Memory;

        TryAddReadModelDescriptor<ChannelBotRegistrationDocument>(services, "channel-bot-registration", "ChannelBotGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ConversationDeliveryCurrentStateDocument>(services, "conversation-delivery-current-state", "ConversationGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ProjectionScopeStatusDocument>(services, "projection-scope-status", "ProjectionMaterializationScopeGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ExternalIdentityBindingDocument>(services, "external-identity-binding", "ExternalIdentityBindingGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<AevatarOAuthClientDocument>(services, "aevatar-oauth-client", "AevatarOAuthClientGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ManagedCodexCredentialDocument>(services, "managed-codex-credential", "ManagedCodexCredentialGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<ChatRoutePolicyCurrentStateDocument>(services, "chat-route-policy-current-state", "ChatRoutePolicyGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<DeviceRegistrationDocument>(services, "device-registration", "DeviceGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<UserAgentCatalogDocument>(services, "user-agent-catalog", "UserAgentCatalogGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<UserAgentCatalogNyxCredentialDocument>(services, "user-agent-catalog-nyx-credential", "UserAgentCatalogGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<UserAgentApiKeyRevocationDocument>(services, "user-agent-api-key-revocation", "UserAgentCatalogGAgent", engineLabel, shape);
        // WorkflowExternalApprovalContinuationDocument is inventoried at the workflow store-registration
        // site (it owns that read-model); registering it here too would double-count it in the inventory.
        TryAddReadModelDescriptor<StreamingProxyChatSessionTerminalSnapshot>(services, "streaming-proxy-chat-session", "StreamingProxyChatSessionGAgent", engineLabel, shape);
        TryAddReadModelDescriptor<StreamingProxyRoomParticipantsSnapshot>(services, "streaming-proxy-room-participants", "StreamingProxyRoomGAgent", engineLabel, shape);
    }

    private static void RegisterElasticsearchAuditTrailArtifactStore(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton<AuditTrailDocumentMetadataProvider>();
        services.TryAddSingleton<ElasticsearchAuditTrailArtifactStore>(sp =>
            new ElasticsearchAuditTrailArtifactStore(
                ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
                sp.GetRequiredService<AuditTrailDocumentMetadataProvider>().Metadata,
                logger: sp.GetRequiredService<ILogger<ElasticsearchAuditTrailArtifactStore>>()));
        services.TryAddSingleton<IAuditTrailArtifactStore>(static sp =>
            sp.GetRequiredService<ElasticsearchAuditTrailArtifactStore>());
        services.TryAddSingleton<IAuditTrailQueryPort>(static sp =>
            sp.GetRequiredService<ElasticsearchAuditTrailArtifactStore>());
        services.AddSingleton<IProjectionIndexReconcileTarget>(static sp =>
            sp.GetRequiredService<ElasticsearchAuditTrailArtifactStore>());
    }

    private static void RegisterInMemoryAuditTrailArtifactStore(IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryAuditTrailStore>();
        services.TryAddSingleton<IAuditTrailArtifactStore>(static sp => sp.GetRequiredService<InMemoryAuditTrailStore>());
        services.TryAddSingleton<IAuditTrailQueryPort>(static sp => sp.GetRequiredService<InMemoryAuditTrailStore>());
    }

    private static void RegisterElasticsearchHealthProbeOperationalSnapshotStore(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton<ElasticsearchHealthProbeOperationalSnapshotStore>(sp =>
        {
            var options = ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration);
            return new ElasticsearchHealthProbeOperationalSnapshotStore(
                options.Endpoints,
                options.IndexPrefix,
                options.RequestTimeoutMs,
                options.Username,
                options.Password,
                logger: sp.GetRequiredService<ILogger<ElasticsearchHealthProbeOperationalSnapshotStore>>());
        });
        services.Replace(ServiceDescriptor.Singleton<IHealthProbeOperationalSnapshotStore>(static sp =>
            sp.GetRequiredService<ElasticsearchHealthProbeOperationalSnapshotStore>()));
        services.AddSingleton<IProjectionIndexReconcileTarget>(static sp =>
            sp.GetRequiredService<ElasticsearchHealthProbeOperationalSnapshotStore>());
    }

    // Registers a single inventory descriptor that delegates to the read-model's already-registered
    // document reader. The closed concrete descriptor type is the idempotence key; TryAddEnumerable
    // cannot be used for the interface factory because factory descriptors have no distinct
    // implementation type and are indistinguishable when more than one read-model is registered.
    private static void TryAddReadModelDescriptor<TReadModel>(
        IServiceCollection services,
        string name,
        string actorKind,
        string engineLabel,
        ProjectionReadModelSinkShape shape)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ProjectionDocumentReadModelDescriptor<TReadModel>)))
            return;

        services.AddSingleton<ProjectionDocumentReadModelDescriptor<TReadModel>>(sp =>
            new ProjectionDocumentReadModelDescriptor<TReadModel>(
                name,
                shape,
                engineLabel,
                actorKind,
                sp.GetRequiredService<IProjectionDocumentReader<TReadModel, string>>()));
        services.AddSingleton<IProjectionReadModelDescriptor>(sp =>
            sp.GetRequiredService<ProjectionDocumentReadModelDescriptor<TReadModel>>());
    }

    private static void TryAddElasticsearchStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TReadModel, string> keySelector)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key);
    }

    private static void TryAddInMemoryStore<TReadModel>(
        IServiceCollection services,
        Func<TReadModel, string> keySelector,
        Func<TReadModel, object?>? defaultSortSelector = null)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TReadModel>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: defaultSortSelector);
    }

    private static void EnsureCompatibleDocumentReaderProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        if (!HasAnyDocumentReader<TReadModel>(services))
            return;
        if (HasDocumentReaderForProvider<TReadModel>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TReadModel).Name} is already registered with a different provider.");
    }

    private static bool HasAnyDocumentReader<TReadModel>(IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentReader<TReadModel, string>));
    }

    private static bool HasDocumentReaderForProvider<TReadModel>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TReadModel, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(static descriptor =>
                descriptor.ServiceType == typeof(InMemoryProjectionDocumentStore<TReadModel, string>)),
            _ => false,
        };
    }

    internal sealed class ElasticsearchAuditTrailArtifactStore :
        IAuditTrailArtifactStore,
        IAuditTrailQueryPort,
        IProjectionIndexReconcileTarget,
        IDisposable
    {
        private const int DefaultAuditQueryTake = 100;
        private const int MaxAuditQueryTake = 500;

        private readonly JsonFormatter _formatter = new(
            JsonFormatter.Settings.Default
                .WithPreserveProtoFieldNames(true)
                .WithFormatDefaultValues(true));
        private readonly JsonParser _parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        private readonly HttpClient _httpClient;
        private readonly ElasticsearchProjectionDocumentStoreOptions _options;
        private readonly DocumentIndexMetadata _metadata;
        private readonly ElasticsearchIndexLifecycleManager _indexManager;
        private readonly ILogger<ElasticsearchAuditTrailArtifactStore> _logger;
        private readonly string _legacyIndexName;
        private readonly string _indexName;

        public ElasticsearchAuditTrailArtifactStore(
            ElasticsearchProjectionDocumentStoreOptions options,
            DocumentIndexMetadata metadata,
            HttpMessageHandler? httpMessageHandler = null,
            ILogger<ElasticsearchAuditTrailArtifactStore>? logger = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(metadata);

            _options = options;
            _legacyIndexName = BuildIndexName(options.IndexPrefix, metadata.IndexName);
            _indexName = $"{_legacyIndexName}-current";
            _metadata = metadata with { IndexName = _indexName };
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ElasticsearchAuditTrailArtifactStore>.Instance;
            _httpClient = httpMessageHandler == null
                ? new HttpClient()
                : new HttpClient(httpMessageHandler, disposeHandler: true);
            _httpClient.BaseAddress = ResolvePrimaryEndpoint(options.Endpoints);
            _httpClient.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, options.RequestTimeoutMs));

            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                var raw = $"{options.Username}:{options.Password}";
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            // Startup reconciliation is an explicit, governed provisioning operation. It remains
            // enabled when request-path AutoCreateIndex is false.
            _indexManager = new ElasticsearchIndexLifecycleManager(_httpClient, autoCreate: true, _logger);
        }

        public string IndexAlias => _indexName;

        public Task ReconcileIndexAsync(CancellationToken ct = default) =>
            _indexManager.ReconcileArtifactWithReindexAsync(
                _indexName,
                _legacyIndexName,
                _metadata,
                ct);

        public async Task<Audit.AuditTrailDocument?> GetAsync(string auditId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(auditId);

            var storageDocument = await GetStorageDocumentAsync(auditId, ct);
            return storageDocument?.Artifact.Clone();
        }

        public async Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            cancellationToken.ThrowIfCancellationRequested();

            var boundedTake = ClampAuditQueryTake(query.Take);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_indexName}/_search")
            {
                Content = new StringContent(
                    BuildAuditQueryPayload(query, boundedTake),
                    Encoding.UTF8,
                    "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var notFoundPayload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsIndexNotFoundPayload(notFoundPayload) &&
                    _options.MissingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw &&
                    !_options.AutoCreateIndex)
                {
                    throw new InvalidOperationException(
                        $"Elasticsearch audit artifact index '{_indexName}' was not found.");
                }

                return new AuditTrailPage(
                    [],
                    null,
                    DateTimeOffset.UtcNow,
                    AuditQueryCoverage.Create(
                        query,
                        truncated: false,
                        ingestionWatermark: null,
                        completeThrough: null,
                        schemaCompatibility: AuditSchemaCompatibility.Current));
            }

            await EnsureSuccessAsync(response, "audit artifact query", cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var jsonDocument = JsonDocument.Parse(payload);
            var ingestionWatermark = ParseAuditIngestionWatermark(jsonDocument.RootElement);
            var schemaCompatibility = ParseAuditSchemaCompatibility(jsonDocument.RootElement);
            if (!jsonDocument.RootElement.TryGetProperty("hits", out var hitsNode) ||
                !hitsNode.TryGetProperty("hits", out var hitItems))
            {
                return new AuditTrailPage(
                    [],
                    null,
                    DateTimeOffset.UtcNow,
                    AuditQueryCoverage.Create(
                        query,
                        truncated: false,
                        ingestionWatermark: ingestionWatermark,
                        completeThrough: null,
                        schemaCompatibility: schemaCompatibility));
            }

            var recordsWithCursors = new List<(AuditRecord Record, string? Cursor)>();
            foreach (var hit in hitItems.EnumerateArray())
            {
                if (!hit.TryGetProperty("_source", out var sourceNode))
                    continue;

                var storageDocument = _parser.Parse<AuditTrailArtifactStorageDocument>(sourceNode.GetRawText());
                if (storageDocument.Artifact?.Record is not { } record)
                    continue;

                recordsWithCursors.Add((record.Clone(), BuildSearchAfterCursor(hit)));
            }

            var truncated = recordsWithCursors.Count > boundedTake;
            var pageItems = recordsWithCursors.Take(boundedTake).ToArray();
            var records = pageItems.Select(static item => item.Record).ToArray();
            var nextCursor = truncated ? pageItems.LastOrDefault().Cursor : null;

            return new AuditTrailPage(
                records,
                nextCursor,
                DateTimeOffset.UtcNow,
                AuditQueryCoverage.Create(
                    query,
                    truncated,
                    ingestionWatermark,
                    completeThrough: null,
                    schemaCompatibility: schemaCompatibility));
        }

        public async Task<AuditTrailArtifactWriteResult> UpsertAsync(
            Audit.AuditTrailDocument document,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            var storageDocument = AuditTrailArtifactStorageDocument.FromArtifact(document);
            if (string.IsNullOrWhiteSpace(storageDocument.Id))
                return AuditTrailArtifactWriteResult.Conflict();

            var existing = await GetStorageDocumentAsync(storageDocument.Id, ct);
            if (existing != null)
                return EvaluateExisting(existing.Artifact, document);

            await EnsureIndexAsync(ct);
            using var createRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"{_indexName}/_create/{Uri.EscapeDataString(storageDocument.Id)}")
            {
                Content = new StringContent(
                    _formatter.Format(storageDocument),
                    Encoding.UTF8,
                    "application/json"),
            };

            using var createResponse = await _httpClient.SendAsync(createRequest, ct);
            if (createResponse.IsSuccessStatusCode)
                return AuditTrailArtifactWriteResult.Applied();

            if (createResponse.StatusCode == HttpStatusCode.Conflict)
            {
                var reconciled = await GetStorageDocumentAsync(storageDocument.Id, ct);
                return reconciled == null
                    ? AuditTrailArtifactWriteResult.Conflict()
                    : EvaluateExisting(reconciled.Artifact, document);
            }

            await EnsureSuccessAsync(createResponse, "audit artifact create", ct);
            return AuditTrailArtifactWriteResult.Conflict();
        }

        private async Task<AuditTrailArtifactStorageDocument?> GetStorageDocumentAsync(
            string auditId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var trimmedAuditId = auditId.Trim();
            if (trimmedAuditId.Length == 0)
                return null;

            using var response = await _httpClient.GetAsync(
                $"{_indexName}/_doc/{Uri.EscapeDataString(trimmedAuditId)}",
                ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var notFoundPayload = await response.Content.ReadAsStringAsync(ct);
                if (IsIndexNotFoundPayload(notFoundPayload) &&
                    _options.MissingIndexBehavior == ElasticsearchMissingIndexBehavior.Throw &&
                    !_options.AutoCreateIndex)
                {
                    throw new InvalidOperationException(
                        $"Elasticsearch audit artifact index '{_indexName}' was not found.");
                }

                return null;
            }

            await EnsureSuccessAsync(response, "audit artifact get", ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            using var jsonDocument = JsonDocument.Parse(payload);
            if (!jsonDocument.RootElement.TryGetProperty("_source", out var sourceNode))
                return null;

            return _parser.Parse<AuditTrailArtifactStorageDocument>(sourceNode.GetRawText());
        }

        private async Task EnsureIndexAsync(CancellationToken ct)
        {
            if (!_options.AutoCreateIndex)
                return;

            await ReconcileIndexAsync(ct);
        }

        private static string BuildAuditQueryPayload(AuditTrailQuery query, int boundedTake)
        {
            var filters = BuildAuditQueryFilters(query);
            var root = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["size"] = boundedTake + 1,
                ["sort"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["artifact.occurred_at"] = new Dictionary<string, object?>
                        {
                            ["order"] = "desc",
                            ["unmapped_type"] = "date",
                        },
                    },
                    new Dictionary<string, object?>
                    {
                        ["id.keyword"] = new Dictionary<string, object?>
                        {
                            ["order"] = "asc",
                            ["unmapped_type"] = "keyword",
                        },
                    },
                },
                ["aggs"] = new Dictionary<string, object?>
                {
                    ["ingestion"] = new Dictionary<string, object?>
                    {
                        ["global"] = new Dictionary<string, object?>(),
                        ["aggs"] = new Dictionary<string, object?>
                        {
                            ["watermark"] = new Dictionary<string, object?>
                            {
                                ["max"] = new Dictionary<string, object?>
                                {
                                    ["field"] = "artifact.recorded_at",
                                },
                            },
                        },
                    },
                    ["incompatible_schema_records"] = new Dictionary<string, object?>
                    {
                        ["filter"] = new Dictionary<string, object?>
                        {
                            ["bool"] = new Dictionary<string, object?>
                            {
                                ["filter"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["exists"] = new Dictionary<string, object?>
                                        {
                                            ["field"] = "artifact.schema_version",
                                        },
                                    },
                                },
                                ["must_not"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["term"] = new Dictionary<string, object?>
                                        {
                                            ["artifact.schema_version"] =
                                                AuditContractSemantics.CurrentSchemaVersion,
                                        },
                                    },
                                },
                            },
                        },
                    },
                    ["legacy_schema_records"] = new Dictionary<string, object?>
                    {
                        ["missing"] = new Dictionary<string, object?>
                        {
                            ["field"] = "artifact.schema_version",
                        },
                    },
                },
                ["query"] = filters.Count == 0
                    ? new Dictionary<string, object?>
                    {
                        ["match_all"] = new Dictionary<string, object?>(),
                    }
                    : new Dictionary<string, object?>
                    {
                        ["bool"] = new Dictionary<string, object?>
                        {
                            ["filter"] = filters,
                        },
                    },
            };

            var searchAfter = DecodeSearchAfterCursor(query.Cursor);
            if (searchAfter is not null)
                root["search_after"] = searchAfter;

            return JsonSerializer.Serialize(root);
        }

        private static List<object> BuildAuditQueryFilters(AuditTrailQuery query)
        {
            var filters = new List<object>();

            AddTimestampRange(filters, "artifact.occurred_at", query.OccurredFrom, query.OccurredTo);
            AddTerm(filters, "artifact.scope_id.keyword", query.ScopeId);
            AddTerm(filters, "artifact.audit_actor_id.keyword", query.AuditActorId);
            AddTerms(filters, "artifact.audit_actor_id.keyword", query.AuditActorIds);
            AddTerm(filters, "artifact.record.identity_key_id.keyword", query.IdentityKeyId);
            AddTerm(filters, "artifact.operation_name.keyword", query.OperationName);
            AddTerm(filters, "artifact.target_kind.keyword", query.TargetKind);
            AddTerm(filters, "artifact.target_id.keyword", query.TargetId);
            AddTerm(filters, "artifact.trace_id.keyword", query.TraceId);
            AddTerm(filters, "artifact.correlation_id.keyword", query.CorrelationId);
            AddTerm(filters, "artifact.record.correlation.causation_id.keyword", query.CausationId);
            AddTerm(filters, "artifact.request_id.keyword", query.RequestId);
            AddTerm(filters, "artifact.command_id.keyword", query.CommandId);
            AddTerm(filters, "artifact.record.correlation.call_id.keyword", query.CallId);
            AddTerm(filters, "artifact.session_id.keyword", query.SessionId);
            AddTerm(filters, "artifact.workflow_run_id.keyword", query.WorkflowRunId);
            AddTerm(filters, "artifact.record.correlation.approval_id.keyword", query.ApprovalId);
            AddTerm(filters, "artifact.committed_event_id.keyword", query.CommittedEventId);
            AddTerm(filters, "artifact.committed_actor_id.keyword", query.CommittedActorId);
            AddTerm(filters, "artifact.committed_actor_type.keyword", query.CommittedActorType);
            AddTerm(filters, "artifact.committed_event_type_url.keyword", query.CommittedEventTypeUrl);
            AddEnumTerm(filters, "artifact.record.actor_kind.keyword", query.ActorKind);
            AddEnumTerm(filters, "artifact.record.operation_kind.keyword", query.OperationKind);
            AddEnumTerm(filters, "artifact.outcome.keyword", query.Outcome);
            AddEnumTerm(filters, "artifact.lifecycle_phase.keyword", query.LifecyclePhase);
            AddEnumTerm(filters, "artifact.terminal_outcome.keyword", query.TerminalOutcome);
            AddEnumTerm(filters, "artifact.sensitivity_level.keyword", query.SensitivityLevel);
            AddEnumTerm(filters, "artifact.record.capture_plane.keyword", query.CapturePlane);
            if (query.RequireChatProvenance)
            {
                filters.Add(new Dictionary<string, object?>
                {
                    ["exists"] = new Dictionary<string, object?>
                    {
                        ["field"] = "artifact.record.provenance.chat.surface",
                    },
                });
            }

            AddEnumTerm(filters, "artifact.record.provenance.chat.surface", query.ChatSurface);
            AddTerm(filters, "artifact.record.provenance.chat.conversation_id", query.ChatConversationId);
            if (query.CommittedStateVersion.HasValue)
                AddTerm(filters, "artifact.committed_state_version", query.CommittedStateVersion.Value);

            return filters;
        }

        private static void AddTimestampRange(
            List<object> filters,
            string fieldPath,
            DateTimeOffset? from,
            DateTimeOffset? to)
        {
            if (!from.HasValue && !to.HasValue)
                return;

            var range = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (from.HasValue)
                range["gte"] = from.Value.UtcDateTime.ToString("O");
            if (to.HasValue)
                range["lte"] = to.Value.UtcDateTime.ToString("O");

            filters.Add(new Dictionary<string, object?>
            {
                ["range"] = new Dictionary<string, object?>
                {
                    [fieldPath] = range,
                },
            });
        }

        private static void AddTerm(List<object> filters, string fieldPath, string? value)
        {
            if (value is null)
                return;

            var normalized = value.Trim();
            if (normalized.Length == 0)
                return;

            AddTermValue(filters, fieldPath, normalized);
        }

        private static void AddTerm(List<object> filters, string fieldPath, long value)
        {
            AddTermValue(filters, fieldPath, value);
        }

        private static void AddTerms(
            List<object> filters,
            string fieldPath,
            IReadOnlyList<string>? values)
        {
            if (values is null)
                return;

            var normalized = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            filters.Add(normalized.Length == 0
                ? new Dictionary<string, object?>
                {
                    ["match_none"] = new Dictionary<string, object?>(),
                }
                : new Dictionary<string, object?>
                {
                    ["terms"] = new Dictionary<string, object?>
                    {
                        [fieldPath] = normalized,
                    },
                });
        }

        private static void AddEnumTerm<TEnum>(List<object> filters, string fieldPath, TEnum? value)
            where TEnum : struct, Enum
        {
            if (!value.HasValue)
                return;

            AddTerm(filters, fieldPath, GetProtoEnumName(value.Value));
        }

        private static void AddTermValue(List<object> filters, string fieldPath, object value)
        {
            filters.Add(new Dictionary<string, object?>
            {
                ["term"] = new Dictionary<string, object?>
                {
                    [fieldPath] = value,
                },
            });
        }

        private static string GetProtoEnumName<TEnum>(TEnum value)
            where TEnum : struct, Enum
        {
            var member = typeof(TEnum).GetMember(value.ToString()).FirstOrDefault();
            return member?.GetCustomAttribute<Google.Protobuf.Reflection.OriginalNameAttribute>()?.Name
                   ?? value.ToString();
        }

        private static int ClampAuditQueryTake(int take)
        {
            return take <= 0 ? DefaultAuditQueryTake : Math.Min(take, MaxAuditQueryTake);
        }

        private static object[]? DecodeSearchAfterCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor))
                return null;

            try
            {
                var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Trim()));
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    return null;

                return json.RootElement.EnumerateArray()
                    .Select(ToSearchAfterValue)
                    .ToArray();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new ArgumentException("Audit query cursor is invalid.", nameof(cursor), ex);
            }
        }

        private static object? ToSearchAfterValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value.GetRawText(),
            };
        }

        private static string? BuildSearchAfterCursor(JsonElement hit)
        {
            if (!hit.TryGetProperty("sort", out var sortNode) || sortNode.ValueKind != JsonValueKind.Array)
                return null;

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sortNode.GetRawText()));
        }

        private static DateTimeOffset? ParseAuditIngestionWatermark(JsonElement root)
        {
            if (!root.TryGetProperty("aggregations", out var aggregations) ||
                !aggregations.TryGetProperty("ingestion", out var ingestion) ||
                !ingestion.TryGetProperty("watermark", out var watermark) ||
                !watermark.TryGetProperty("value_as_string", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        private static AuditSchemaCompatibility ParseAuditSchemaCompatibility(JsonElement root)
        {
            if (!root.TryGetProperty("aggregations", out var aggregations))
                return AuditSchemaCompatibility.Incompatible;

            if (!aggregations.TryGetProperty("incompatible_schema_records", out var incompatible) ||
                !incompatible.TryGetProperty("doc_count", out var incompatibleCountNode) ||
                !incompatibleCountNode.TryGetInt64(out var incompatibleCount))
            {
                return AuditSchemaCompatibility.Incompatible;
            }

            if (incompatibleCount > 0)
                return AuditSchemaCompatibility.Incompatible;

            if (!aggregations.TryGetProperty("legacy_schema_records", out var legacy) ||
                !legacy.TryGetProperty("doc_count", out var count) ||
                !count.TryGetInt64(out var legacyCount))
            {
                return AuditSchemaCompatibility.Incompatible;
            }

            if (legacyCount > 0)
            {
                return AuditSchemaCompatibility.ContainsLegacyRecords;
            }

            return AuditSchemaCompatibility.Current;
        }

        private static AuditTrailArtifactWriteResult EvaluateExisting(
            Audit.AuditTrailDocument existing,
            Audit.AuditTrailDocument incoming)
        {
            var isDuplicate = string.Equals(existing.ContentHash, incoming.ContentHash, StringComparison.Ordinal) ||
                              existing.Record is not null &&
                              incoming.Record is not null &&
                              AuditRecordSemanticComparer.AreEquivalent(existing.Record, incoming.Record);
            return isDuplicate
                ? AuditTrailArtifactWriteResult.Duplicate()
                : AuditTrailArtifactWriteResult.Conflict();
        }

        private static Uri ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
        {
            if (endpoints == null || endpoints.Count == 0)
                throw new InvalidOperationException("Elasticsearch provider requires at least one endpoint.");

            var endpoint = endpoints[0].Trim();
            if (endpoint.Length == 0)
                throw new InvalidOperationException("Elasticsearch endpoint cannot be empty.");
            if (!endpoint.Contains("://", StringComparison.Ordinal))
                endpoint = "http://" + endpoint;

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Invalid Elasticsearch endpoint '{endpoints[0]}'.");

            return uri;
        }

        private static string BuildIndexName(string indexPrefix, string indexScope)
        {
            var prefix = NormalizeToken(indexPrefix);
            if (prefix.Length == 0)
                prefix = "aevatar";

            var scope = NormalizeToken(indexScope);
            if (scope.Length == 0)
                scope = "audit-trail";

            return $"{prefix}-{scope}";
        }

        private static string NormalizeToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var chars = token
                .Trim()
                .ToLowerInvariant()
                .Select(static ch => char.IsLetterOrDigit(ch) ? ch : '-')
                .ToArray();
            return new string(chars).Trim('-');
        }

        private async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            string operation,
            CancellationToken ct)
        {
            if (response.IsSuccessStatusCode)
                return;

            _ = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Elasticsearch audit artifact operation failed. operation={Operation} statusCode={StatusCode} errorType={ErrorType}",
                operation,
                (int)response.StatusCode,
                "backend_rejected");
            throw new InvalidOperationException(
                $"Elasticsearch {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. errorType=backend_rejected");
        }

        private static bool IsIndexNotFoundPayload(string payload) =>
            payload.Contains("index_not_found_exception", StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            _indexManager.Dispose();
            _httpClient.Dispose();
        }
    }
}
