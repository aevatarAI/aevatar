using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ApplicationPolicyMode = Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionLLMModelCatalogPolicyQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldReturnNullForMissingPolicyWithoutPrimingProjection()
    {
        var reader = new RecordingDocumentReader();
        var port = new ProjectionLLMModelCatalogPolicyQueryPort(reader);

        var snapshot = await port.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        snapshot.Should().BeNull();
        reader.GetKeys.Should().Equal("llm-model-catalog-policy-scope-scope-alpha");
    }

    [Fact]
    public async Task GetAsync_ShouldMapTypedScopeSourceAndSelection()
    {
        var document = new LLMModelCatalogPolicyCurrentStateDocument
        {
            Id = "llm-model-catalog-policy-scope-scope-alpha",
            ActorId = "llm-model-catalog-policy-scope-scope-alpha",
            StateVersion = 17,
            LastEventId = "event-17",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-15T08:00:00Z")),
            OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
            ScopeId = "scope-alpha",
            Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.Custom,
            LastMutationId = "mutation-observed",
        };
        document.Sources.Add(UserSource());
        var port = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = document });

        var snapshot = await port.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        snapshot.Should().NotBeNull();
        snapshot!.Mode.Should().Be(ApplicationPolicyMode.Custom);
        snapshot.StateVersion.Should().Be(17);
        snapshot.LastMutationId.Should().Be("mutation-observed");
        snapshot.UpdatedAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-15T08:00:00Z"));
        snapshot.Sources[0].SourceIdentity.Should()
            .Be(new NyxIDUserServiceModelSourceIdentity("user-svc-beta"));
        snapshot.Sources[0].ModelSelection.Should()
            .BeEquivalentTo(new ExplicitLLMModels(["gpt-5.5", "o3"]));
    }

    [Fact]
    public async Task GetAsync_ShouldMapTypedPlatformCatalogSource()
    {
        var document = ValidPlatformDocument();
        document.Sources.Add(CatalogSource());
        var port = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = document });

        var snapshot = await port.GetAsync(LLMModelCatalogPolicyOwner.Platform);

        snapshot.Should().NotBeNull();
        snapshot!.Sources.Should().ContainSingle();
        snapshot.Sources[0].SourceIdentity.Should()
            .Be(new NyxIDCatalogServiceModelSourceIdentity("catalog-svc-alpha"));
        snapshot.Sources[0].ModelSelection.Should()
            .BeEquivalentTo(new ExplicitLLMModels(["gpt-5.5"]));
    }

    [Fact]
    public async Task GetAsync_ShouldRejectDocumentOwnedByDifferentScope()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new LLMModelCatalogPolicyCurrentStateDocument
            {
                Id = "llm-model-catalog-policy-scope-scope-alpha",
                OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
                ScopeId = "scope-beta",
                Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.Custom,
            },
        };
        var port = new ProjectionLLMModelCatalogPolicyQueryPort(reader);

        var act = () => port.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner does not match*");
    }

    [Fact]
    public async Task GetAsync_ShouldRejectSourceIdentityThatDoesNotMatchOwner()
    {
        var scopeDocument = ValidScopeDocument();
        scopeDocument.Sources.Add(CatalogSource());
        var scopePort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = scopeDocument });

        var scopeAct = () => scopePort.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await scopeAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source identity does not match its owner*");

        var platformDocument = ValidPlatformDocument();
        platformDocument.Sources.Add(UserSource());
        var platformPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = platformDocument });

        var platformAct = () => platformPort.GetAsync(LLMModelCatalogPolicyOwner.Platform);

        await platformAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source identity does not match its owner*");
    }

    [Fact]
    public async Task GetAsync_ShouldRejectInvalidOwnerModeAndInheritedSources()
    {
        var platformDocument = ValidPlatformDocument();
        platformDocument.Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.InheritPlatform;
        var platformPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = platformDocument });

        var platformAct = () => platformPort.GetAsync(LLMModelCatalogPolicyOwner.Platform);

        await platformAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*platform*mode must be custom*");

        var scopeDocument = ValidScopeDocument();
        scopeDocument.Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.InheritPlatform;
        scopeDocument.Sources.Add(UserSource());
        var scopePort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = scopeDocument });

        var scopeAct = () => scopePort.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await scopeAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inherited scope*must not contain sources*");
    }

    [Fact]
    public async Task GetAsync_ShouldRejectIncompleteCommittedStateEvidence()
    {
        var document = ValidScopeDocument();
        document.ActorId = "different-actor";
        var port = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = document });

        var act = () => port.GetAsync(LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*document identity is invalid*");
    }

    [Fact]
    public async Task GetAsync_ShouldRejectNonCanonicalProjectedSourceAndModelValues()
    {
        var identityDocument = ValidScopeDocument();
        var nonCanonicalIdentity = UserSource();
        nonCanonicalIdentity.Source.UserServiceId = " user-svc-beta ";
        identityDocument.Sources.Add(nonCanonicalIdentity);
        var identityPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = identityDocument });

        var identityAct = () => identityPort.GetAsync(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await identityAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source identity is invalid*");

        var modelDocument = ValidScopeDocument();
        var nonCanonicalModel = UserSource();
        nonCanonicalModel.ExplicitModels.UpstreamModelIds[0] = " gpt-5.5";
        modelDocument.Sources.Add(nonCanonicalModel);
        var modelPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = modelDocument });

        var modelAct = () => modelPort.GetAsync(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await modelAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit model selection is invalid*");
    }

    [Fact]
    public async Task GetAsync_ShouldRejectProjectedPerSourceModelLimitAndMutationLimit()
    {
        var modelsDocument = ValidScopeDocument();
        var oversizedSelection = UserSource();
        oversizedSelection.ExplicitModels.UpstreamModelIds.Clear();
        oversizedSelection.ExplicitModels.UpstreamModelIds.AddRange(
            Enumerable.Range(0, LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource + 1)
                .Select(static index => $"model-{index}"));
        modelsDocument.Sources.Add(oversizedSelection);
        var modelsPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = modelsDocument });

        var modelsAct = () => modelsPort.GetAsync(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await modelsAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit model selection is invalid*");

        var mutationDocument = ValidScopeDocument();
        mutationDocument.LastMutationId = new string(
            'm',
            LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes + 1);
        var mutationPort = new ProjectionLLMModelCatalogPolicyQueryPort(
            new RecordingDocumentReader { Document = mutationDocument });

        var mutationAct = () => mutationPort.GetAsync(
            LLMModelCatalogPolicyOwner.ForScope("scope-alpha"));

        await mutationAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*committed state evidence is incomplete*");
    }

    private static LLMModelCatalogPolicyCurrentStateDocument ValidScopeDocument() => new()
    {
        Id = "llm-model-catalog-policy-scope-scope-alpha",
        ActorId = "llm-model-catalog-policy-scope-scope-alpha",
        StateVersion = 1,
        LastEventId = "event-1",
        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-15T08:00:00Z")),
        OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
        ScopeId = "scope-alpha",
        Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.Custom,
        LastMutationId = "mutation-alpha",
    };

    private static LLMModelCatalogPolicyCurrentStateDocument ValidPlatformDocument() => new()
    {
        Id = "llm-model-catalog-policy-platform",
        ActorId = "llm-model-catalog-policy-platform",
        StateVersion = 1,
        LastEventId = "event-1",
        UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-15T08:00:00Z")),
        OwnerType = LLMModelCatalogPolicyOwnerType.Platform,
        Mode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode.Custom,
        LastMutationId = "mutation-alpha",
    };

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource CatalogSource() => new()
    {
        Source = new NyxIDModelSourceReference
        {
            CatalogServiceId = "catalog-svc-alpha",
            ServiceSlugSnapshot = "chrono-llm",
        },
        ExplicitModels = ExplicitModels("gpt-5.5"),
    };

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource UserSource()
    {
        var explicitModels = new ExplicitLLMModelIDs();
        explicitModels.UpstreamModelIds.Add("gpt-5.5");
        explicitModels.UpstreamModelIds.Add("o3");
        return new Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource
        {
            Source = new NyxIDModelSourceReference
            {
                UserServiceId = "user-svc-beta",
                ServiceSlugSnapshot = "chrono-llm-public",
            },
            ExplicitModels = explicitModels,
        };
    }

    private static ExplicitLLMModelIDs ExplicitModels(params string[] modelIds)
    {
        var models = new ExplicitLLMModelIDs();
        models.UpstreamModelIds.AddRange(modelIds);
        return models;
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<LLMModelCatalogPolicyCurrentStateDocument, string>
    {
        public LLMModelCatalogPolicyCurrentStateDocument? Document { get; init; }
        public List<string> GetKeys { get; } = [];

        public Task<LLMModelCatalogPolicyCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            GetKeys.Add(key);
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<LLMModelCatalogPolicyCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(
                ProjectionDocumentQueryResult<LLMModelCatalogPolicyCurrentStateDocument>.Empty);
    }
}
