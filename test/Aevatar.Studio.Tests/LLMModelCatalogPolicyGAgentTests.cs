using System.Reflection;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.UserConfig;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class LLMModelCatalogPolicyGAgentTests
{
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task ReplaceScopeCustom_ShouldCommitCanonicalTypedSources()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var command = ScopeCustom(expectedVersion: 0, mutationId: " mutation-alpha ");
        command.Sources.Add(UserSource(
            " user-svc-beta ",
            "chrono-llm-public",
            " gpt-5.5 ",
            "gpt-5.5",
            " o3 "));

        await agent.HandleReplacePolicy(command);

        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.OwnerType.Should().Be(LLMModelCatalogPolicyOwnerType.Scope);
        agent.State.ScopeId.Should().Be("scope-alpha");
        agent.State.Mode.Should().Be(LLMModelCatalogPolicyMode.Custom);
        agent.State.LastMutationId.Should().Be("mutation-alpha");
        agent.State.Sources.Should().ContainSingle();
        agent.State.Sources[0].Source.SourceIdentityCase.Should().Be(
            NyxIDModelSourceReference.SourceIdentityOneofCase.UserServiceId);
        agent.State.Sources[0].Source.UserServiceId.Should().Be("user-svc-beta");
        agent.State.Sources[0].ExplicitModels.UpstreamModelIds.Should()
            .Equal("gpt-5.5", "o3");
    }

    [Fact]
    public async Task DuplicateMutationWithEquivalentCanonicalPayload_ShouldBeNoOpBeforeStaleVersionCheck()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var initial = ScopeCustom(0, "mutation-alpha");
        initial.Sources.Add(UserSource(" user-alpha ", "chrono", " model-a "));
        await agent.HandleReplacePolicy(initial);
        var retry = ScopeCustom(0, " mutation-alpha ");
        retry.Sources.Add(UserSource("user-alpha", "chrono", "model-a"));

        await agent.HandleReplacePolicy(retry);

        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.Mode.Should().Be(LLMModelCatalogPolicyMode.Custom);

        retry.ExpectedStateVersion = -1;
        var invalidVersion = () => agent.HandleReplacePolicy(retry);
        await invalidVersion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected_state_version must be non-negative*");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
    }

    [Fact]
    public async Task DuplicateMutationWithDifferentPayload_ShouldRejectConflict()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        await agent.HandleReplacePolicy(ScopeCustom(0, "mutation-alpha"));
        var conflictingRetry = new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
            ScopeId = "scope-alpha",
            Mode = LLMModelCatalogPolicyMode.InheritPlatform,
            ExpectedStateVersion = 0,
            MutationId = "mutation-alpha",
        };

        var act = () => agent.HandleReplacePolicy(conflictingRetry);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mutation_id*already used for a different policy*");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
        agent.State.Mode.Should().Be(LLMModelCatalogPolicyMode.Custom);
    }

    [Fact]
    public async Task Replace_ShouldRejectStaleVersionAndOwnerChange()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        await agent.HandleReplacePolicy(ScopeCustom(0, "mutation-alpha"));

        var stale = () => agent.HandleReplacePolicy(ScopeCustom(0, "mutation-beta"));
        await stale.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected_state_version 0*committed state version 1*");

        var ownerChange = () => agent.HandleReplacePolicy(new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Platform,
            Mode = LLMModelCatalogPolicyMode.Custom,
            ExpectedStateVersion = 1,
            MutationId = "mutation-gamma",
        });
        await ownerChange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner cannot change*");
        agent.EventSourcing!.CurrentVersion.Should().Be(1);
    }

    [Fact]
    public async Task FirstReplace_ShouldRejectOwnerThatDoesNotMatchActorIdentity()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-beta");

        var act = () => agent.HandleReplacePolicy(ScopeCustom(0, "mutation-alpha"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match canonical identity*scope-alpha*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task PlatformPolicy_ShouldRejectInheritanceAndUserServiceSources()
    {
        var inheritAgent = await CreateAgentAsync("llm-model-catalog-policy-platform");
        var inherit = () => inheritAgent.HandleReplacePolicy(new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Platform,
            Mode = LLMModelCatalogPolicyMode.InheritPlatform,
            ExpectedStateVersion = 0,
            MutationId = "mutation-alpha",
        });
        await inherit.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mode must be custom*");

        var userServiceAgent = await CreateAgentAsync("llm-model-catalog-policy-platform-2");
        var command = new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Platform,
            Mode = LLMModelCatalogPolicyMode.Custom,
            ExpectedStateVersion = 0,
            MutationId = "mutation-beta",
        };
        command.Sources.Add(UserSource("user-svc-beta", "chrono-llm-public", "gpt-5.5"));

        var userService = () => userServiceAgent.HandleReplacePolicy(command);
        await userService.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot reference*user service*");
    }

    [Fact]
    public async Task ScopePolicy_ShouldDistinguishInheritanceFromExplicitEmptyCustom()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        await agent.HandleReplacePolicy(new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
            ScopeId = "scope-alpha",
            Mode = LLMModelCatalogPolicyMode.InheritPlatform,
            ExpectedStateVersion = 0,
            MutationId = "mutation-alpha",
        });
        agent.State.Mode.Should().Be(LLMModelCatalogPolicyMode.InheritPlatform);
        agent.State.Sources.Should().BeEmpty();

        await agent.HandleReplacePolicy(ScopeCustom(1, "mutation-beta"));

        agent.State.Mode.Should().Be(LLMModelCatalogPolicyMode.Custom);
        agent.State.Sources.Should().BeEmpty();
        agent.EventSourcing!.CurrentVersion.Should().Be(2);
    }

    [Fact]
    public async Task ScopePolicy_ShouldRejectCatalogServiceSources()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var command = ScopeCustom(0, "mutation-alpha");
        command.Sources.Add(CatalogSource("catalog-svc-alpha", "chrono-llm"));

        var act = () => agent.HandleReplacePolicy(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exact NyxID user service*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" chrono-llm")]
    [InlineData("chrono-llm ")]
    [InlineData("Chrono-llm")]
    [InlineData("chrono--llm")]
    [InlineData("chrono-")]
    public async Task ScopePolicy_ShouldRejectNonCanonicalServiceSlug(string serviceSlug)
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var command = ScopeCustom(0, "mutation-alpha");
        command.Sources.Add(UserSource("user-alpha", serviceSlug, "model-a"));

        var act = () => agent.HandleReplacePolicy(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*canonical NyxID service slug*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task ScopePolicy_ShouldAcceptNyxIdSlugBoundaryValues()
    {
        foreach (var (actorSuffix, slug) in new[]
                 {
                     ("one", "1"),
                     ("digit", "1-chrono"),
                     ("max", new string('a', NyxIdServiceSlugPolicy.MaxLength)),
                 })
        {
            var agent = await CreateAgentAsync(
                $"llm-model-catalog-policy-scope-scope-{actorSuffix}");
            var command = ScopeCustom(0, $"mutation-{actorSuffix}");
            command.ScopeId = $"scope-{actorSuffix}";
            command.Sources.Add(UserSource($"user-{actorSuffix}", slug, "model-a"));

            await agent.HandleReplacePolicy(command);

            agent.State.Sources.Should().ContainSingle();
            agent.State.Sources[0].Source.ServiceSlugSnapshot.Should().Be(slug);
        }
    }

    [Fact]
    public async Task ScopePolicy_ShouldRejectServiceSlugLongerThanNyxIdLimit()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var command = ScopeCustom(0, "mutation-alpha");
        command.Sources.Add(UserSource(
            "user-alpha",
            new string('a', NyxIdServiceSlugPolicy.MaxLength + 1),
            "model-a"));

        var act = () => agent.HandleReplacePolicy(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*canonical NyxID service slug*");
    }

    [Fact]
    public async Task ScopePolicy_ShouldRejectDuplicateServiceSlugs()
    {
        var agent = await CreateAgentAsync("llm-model-catalog-policy-scope-scope-alpha");
        var command = ScopeCustom(0, "mutation-alpha");
        command.Sources.Add(UserSource("user-alpha", "chrono-llm", "model-a"));
        command.Sources.Add(UserSource("user-beta", "chrono-llm", "model-b"));

        var act = () => agent.HandleReplacePolicy(command);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*service slug snapshots must be unique*");
        agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task OversizedPolicyPayload_ShouldBeRejectedBeforeCommit()
    {
        var mutationAgent = await CreateAgentAsync("llm-model-catalog-policy-scope-mutation-limit");
        var oversizedMutation = ScopeCustom(
            0,
            new string('m', LLMModelCatalogPolicyLimits.MaxMutationIdUtf8Bytes + 1));

        var mutationAct = () => mutationAgent.HandleReplacePolicy(oversizedMutation);

        await mutationAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mutation_id*UTF-8 bytes*");
        mutationAgent.EventSourcing!.CurrentVersion.Should().Be(0);

        var sourcesAgent = await CreateAgentAsync("llm-model-catalog-policy-scope-source-limit");
        var oversizedSources = ScopeCustom(0, "mutation-source-limit");
        oversizedSources.Sources.AddRange(Enumerable
            .Range(0, LLMModelCatalogPolicyLimits.MaxSources + 1)
            .Select(static index => UserSource($"user-{index}", "chrono", "model")));

        var sourcesAct = () => sourcesAgent.HandleReplacePolicy(oversizedSources);

        await sourcesAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*more than* sources*");
        sourcesAgent.EventSourcing!.CurrentVersion.Should().Be(0);

        var modelsAgent = await CreateAgentAsync("llm-model-catalog-policy-scope-model-limit");
        var oversizedModels = ScopeCustom(0, "mutation-model-limit");
        oversizedModels.Sources.Add(UserSource(
            "user-models",
            "chrono",
            Enumerable.Range(0, LLMModelCatalogPolicyLimits.MaxExplicitModelsPerSource + 1)
                .Select(static index => $"model-{index}")
                .ToArray()));

        var modelsAct = () => modelsAgent.HandleReplacePolicy(oversizedModels);

        await modelsAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*model source*more than*explicit model IDs*");
        modelsAgent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    private static ReplaceLLMModelCatalogPolicyCommand ScopeCustom(
        long expectedVersion,
        string mutationId) => new()
        {
            OwnerType = LLMModelCatalogPolicyOwnerType.Scope,
            ScopeId = "scope-alpha",
            Mode = LLMModelCatalogPolicyMode.Custom,
            ExpectedStateVersion = expectedVersion,
            MutationId = mutationId,
        };

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource CatalogSource(
        string serviceId,
        string slug)
    {
        var models = new ExplicitLLMModelIDs();
        models.UpstreamModelIds.Add("model-a");
        return new Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource
        {
            Source = new NyxIDModelSourceReference
            {
                CatalogServiceId = serviceId,
                ServiceSlugSnapshot = slug,
            },
            ExplicitModels = models,
        };
    }

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource UserSource(
        string serviceId,
        string slug,
        params string[] modelIds)
    {
        var explicitModels = new ExplicitLLMModelIDs();
        explicitModels.UpstreamModelIds.AddRange(modelIds);
        return new Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource
        {
            Source = new NyxIDModelSourceReference
            {
                UserServiceId = serviceId,
                ServiceSlugSnapshot = slug,
            },
            ExplicitModels = explicitModels,
        };
    }

    private static async Task<LLMModelCatalogPolicyGAgent> CreateAgentAsync(string actorId)
    {
        var agent = new LLMModelCatalogPolicyGAgent
        {
            EventSourcingBehaviorFactory =
                new DefaultEventSourcingBehaviorFactory<LLMModelCatalogPolicyGAgentState>(
                    new InMemoryEventStore()),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [actorId]);
        await agent.ActivateAsync();
        return agent;
    }
}
