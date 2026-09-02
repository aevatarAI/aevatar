using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Abstractions.Slash;
using Aevatar.GAgents.NyxidChat.LlmSelection;
using Aevatar.GAgents.NyxidChat.Slash;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using StudioConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins the deterministic /model selection path for issue #556.
/// </summary>
public sealed class ModelSlashCommandHandlerTests
{
    private static readonly NyxIdLlmService ChronoLlm = new(
        CatalogEntryId: null,
        ServiceSlug: "chrono-llm",
        DisplayName: "chrono-llm shared",
        RouteValue: "/api/v1/proxy/s/chrono-llm",
        ModelCatalog: EnumeratedCatalog("gpt-5.4"),
        Status: "ready",
        Source: "shared",
        Allowed: true,
        Description: "Shared service",
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            "us-chrono"));

    private static readonly NyxIdLlmService OpenAi = new(
        CatalogEntryId: null,
        ServiceSlug: "openai-work",
        DisplayName: "OpenAI (work)",
        RouteValue: "/api/v1/proxy/s/openai-work",
        ModelCatalog: EnumeratedCatalog("gpt-4o"),
        Status: "ready",
        Source: "user",
        Allowed: true,
        Description: "Work key",
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            "us-openai"));

    private static ChannelSlashCommandContext Context(
        string subAndArgs = "",
        string? bindingValue = "bnd_sender",
        string registrationScopeId = "owner-scope",
        string commandName = "model") => new()
    {
        CommandName = commandName,
        ArgumentText = subAndArgs,
        Subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant",
            ExternalUserId = "ou_user",
        },
        BindingIdValue = bindingValue,
        RegistrationId = "reg",
        RegistrationScopeId = registrationScopeId,
        SenderId = "ou_user",
        SenderName = "Eric",
        IsPrivateChat = true,
    };

    [Fact]
    public void RequiresBinding_AndAliases_AreDeclared()
    {
        var handler = CreateHandler();

        handler.RequiresBinding.Should().BeTrue();
        handler.Aliases.Should().Equal("models", "llm", "route");
        handler.Usage.ArgumentSyntax.Should().Contain("use");
    }

    [Fact]
    public async Task EmptyModel_RendersCurrentModelOnly()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.5", service: ChronoLlm) },
        };
        var handler = CreateHandler(queryPort: queryPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("**当前 model**");
        reply.Text.Should().Contain("Model: gpt-5.5");
        reply.Text.Should().Contain("Route: chrono-llm shared");
        reply.Text.Should().Contain("`/model list`");
        reply.Text.Should().NotContain("OpenAI (work)");
        reply.Text.Should().NotContain("第 1/1 页");
        queryPort.ReadResources.Should().ContainSingle()
            .Which.Should().Be(UserConfigResourceKey.ForChannelBinding("bnd_sender"));
    }

    [Fact]
    public async Task EmptyRoute_RendersCurrentRouteOnly()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.5", service: ChronoLlm) },
        };
        var handler = CreateHandler(queryPort: queryPort);

        var reply = await handler.HandleAsync(Context(commandName: "route"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("**当前 route**");
        reply.Text.Should().Contain("Route: chrono-llm shared");
        reply.Text.Should().Contain(ChronoLlm.RouteValue);
        reply.Text.Should().Contain("当前 model: gpt-5.5");
        reply.Text.Should().Contain("`/route list`");
        reply.Text.Should().NotContain("OpenAI (work)");
        reply.Text.Should().NotContain("第 1/1 页");
    }

    [Fact]
    public async Task List_RendersAvailableServices()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.4", service: ChronoLlm) },
        };
        var handler = CreateHandler(queryPort: queryPort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "list"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("chrono-llm shared");
        reply.Text.Should().Contain("OpenAI (work)");
        reply.Text.Should().Contain("/route use");
        reply.Text.Should().Contain("(当前)");
        reply.Text.Should().Contain("default model: gpt-5.4");
        reply.Text.Should().NotContain("chrono-llm shared / gpt-5.4");
        reply.Actions.Should().Contain(action => action.Kind == ActionElementKind.Select);
        reply.Actions.Should().Contain(action => action.Kind == ActionElementKind.FormSubmit);
    }

    [Fact]
    public async Task List_PaginatesAvailableServices()
    {
        var services = Enumerable.Range(1, 7)
            .Select(i => ChronoLlm with
            {
                Identity = new UserLlmServiceIdentity(
                    UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                    $"us-{i}"),
                ServiceSlug = $"route-{i}",
                DisplayName = $"Route {i}",
                RouteValue = $"/api/v1/proxy/s/route-{i}",
            })
            .ToArray();
        var catalog = new StubCatalogClient { Services = services };
        var handler = CreateHandler(catalog);

        var pageOne = await handler.HandleAsync(Context(subAndArgs: "list"), default);
        var pageTwo = await handler.HandleAsync(Context(subAndArgs: "list 2", commandName: "route"), default);

        pageOne.Should().NotBeNull();
        pageOne!.Text.Should().Contain("第 1/2 页");
        pageOne.Text.Should().Contain("Route 1");
        pageOne.Text.Should().Contain("Route 5");
        pageOne.Text.Should().NotContain("Route 6");
        pageOne.Actions.Should().Contain(action =>
            action.LlmSelection != null &&
            action.LlmSelection.Action == TextUserLlmOptionsRenderer.ListPageAction &&
            action.LlmSelection.Page == 2);

        pageTwo.Should().NotBeNull();
        pageTwo!.Text.Should().Contain("第 2/2 页");
        pageTwo.Text.Should().Contain("Route 6");
        pageTwo.Text.Should().Contain("Route 7");
        pageTwo.Text.Should().Contain("`/route list 1`");
        pageTwo.Text.Should().NotContain("Route 5");
    }

    [Fact]
    public async Task List_ReturnsFriendlyMessage_WhenCatalogLookupFails()
    {
        var catalog = new StubCatalogClient
        {
            GetServicesError = new InvalidOperationException("NyxID LLM catalog unavailable"),
        };
        var handler = CreateHandler(catalog);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("读取或更新 NyxID LLM service 设置失败");
        reply.Text.Should().NotContain("NyxID LLM catalog unavailable");
    }

    [Fact]
    public async Task List_RequestsProxyScope_ForNyxIdLlmApi()
    {
        var broker = new RecordingCapabilityBroker();
        var handler = CreateHandler(broker: broker);

        await handler.HandleAsync(Context(), default);

        broker.RequestedScopes.Should().ContainSingle().Which.Should().Be(AevatarOAuthClientScopes.Proxy);
    }

    [Fact]
    public async Task List_PreservesBindingAndGuidesGrantReview_WhenBindingScopeMissing()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingScopeMismatchException(Context().Subject)),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("缺少 LLM route 权限");
        reply.Text.Should().Contain("/init");
        reply.Text.Should().NotContain("/unbind");
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task List_PreservesBindingAndGuidesGrantReview_WhenBindingLacksRequiredService()
    {
        var context = Context();
        var dispatchPort = new RecordingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingServiceAccessMismatchException(
                context.Subject,
                ["https://nyxid.test/api/v1/proxy/s/aevatar"])),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(context, default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("Ornn service");
        reply.Text.Should().Contain("Sandbox service");
        reply.Text.Should().Contain("/init");
        reply.Text.Should().NotContain("/unbind");
        dispatchPort.Dispatched.Should().BeEmpty();
    }

    [Fact]
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingRevokedRemotely()
    {
        // NyxID itself returned binding_revoked (e.g. user revoked at NyxID admin
        // or the binding tied to a re-DCR'd cluster client_id was invalidated).
        // Wipe the local readmodel so /init isn't blocked by stale state.
        var dispatchPort = new RecordingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingRevokedException(Context().Subject)),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("失效");
        reply.Text.Should().Contain("清理已提交");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(dispatchPort, expectedReason: "auto_self_heal_remote_revoked");
    }

    [Fact]
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingNotFoundRemotely()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingNotFoundException(Context().Subject)),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("不可用");
        reply.Text.Should().Contain("清理已提交");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(dispatchPort, expectedReason: "auto_self_heal_remote_not_found");
    }

    [Fact]
    public async Task List_DegradesToUnbindGuidance_WhenSelfHealDispatchKeepsThrowing()
    {
        var dispatchPort = new ThrowingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingRevokedException(Context().Subject)),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("失效");
        reply.Text.Should().Contain("清理提交失败");
        reply.Text.Should().Contain("/unbind");
        reply.Text.Should().NotContain("清理已提交");
        dispatchPort.AttemptCount.Should().Be(2, "self-heal must attempt the local revoke twice before degrading");
    }

    private static void AssertRevokeBindingDispatched(RecordingActorDispatchPort dispatchPort, string expectedReason)
    {
        dispatchPort.Dispatched.Should().ContainSingle("self-heal must dispatch exactly one local revoke");
        var (actorId, envelope) = dispatchPort.Dispatched[0];
        actorId.Should().Be(Context().Subject.ToActorId());
        envelope.Route.Direct.TargetActorId.Should().Be(actorId);
        envelope.Route.PublisherActorId.Should().Be("nyxid-chat.model.self-heal");

        var revoke = envelope.Payload.Unpack<RevokeBindingCommand>();
        revoke.Reason.Should().Be(expectedReason);
        revoke.ExternalSubject.Platform.Should().Be("lark");
        revoke.ExternalSubject.Tenant.Should().Be("tenant");
        revoke.ExternalSubject.ExternalUserId.Should().Be("ou_user");
    }

    [Fact]
    public async Task List_RendersSetupHint_WhenCatalogIsEmpty()
    {
        var catalog = new StubCatalogClient { Services = [] };
        var handler = CreateHandler(catalog);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("还没接入任何 LLM service");
        reply.Text.Should().Contain("/model preset");
        reply.Text.Should().Contain("chrono-llm");
    }

    [Fact]
    public async Task Use_Number_WritesRouteAndModel()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 2"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        reply.Text.Should().Contain("更新已提交");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.BindingId.Should().Be("bnd_sender");
        var intent = saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>().Subject;
        intent.UserServiceId.Should().Be(OpenAi.Identity!.NyxIdUserServiceId);
        intent.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ProviderDefault);
        intent.ModelSelection.ModelId.Should().BeEmpty();
    }

    [Fact]
    public async Task Use_ServiceName_WritesMatchingRoute()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use openai-work"), default);

        reply.Should().NotBeNull();
        preferencePort.Commands.Should().ContainSingle()
            .Subject.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>()
            .Which.UserServiceId.Should().Be(OpenAi.Identity!.NyxIdUserServiceId);
    }

    [Fact]
    public async Task Use_ServiceName_PrefersSelectableDuplicate()
    {
        var disabledGateway = ChronoLlm with
        {
            CatalogEntryId = "chrono-llm",
            Identity = null,
            DisplayName = "Chrono LLM",
            RouteValue = "/api/v1/llm/chrono-llm/v1",
            Status = "not_connected",
            Source = NyxIdLlmProviderSource.GatewayProvider,
            Allowed = false,
        };
        var selectableProxy = ChronoLlm with { DisplayName = "Chrono LLM" };
        var catalog = new StubCatalogClient { Services = [disabledGateway, selectableProxy] };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(catalog, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use Chrono LLM"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("Chrono LLM");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        var intent = saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>().Subject;
        intent.UserServiceId.Should().Be(selectableProxy.Identity!.NyxIdUserServiceId);
        intent.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ProviderDefault);
    }

    [Fact]
    public async Task Use_ServiceNameAndModel_PrefersUserKeyCandidateOverLegacyCatalogCandidate()
    {
        var legacyCatalog = ChronoLlm with
        {
            CatalogEntryId = "svc-chrono",
            Identity = null,
            DisplayName = "Chrono LLM",
            Status = "not_connected",
            Source = NyxIdLlmProviderSource.ProxyService,
            Allowed = false,
        };
        var userKey = ChronoLlm with
        {
            CatalogEntryId = "key-chrono",
            Identity = new UserLlmServiceIdentity(
                UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                "us-key-chrono"),
            DisplayName = "Chrono LLM",
            Source = NyxIdLlmProviderSource.UserService,
            Allowed = true,
            Status = "ready",
        };
        var catalog = new StubCatalogClient { Services = [legacyCatalog, userKey] };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(catalog, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use chrono-llm gpt-5.5"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("Chrono LLM");
        reply.Text.Should().Contain("gpt-5.5");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        var intent = saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>().Subject;
        intent.UserServiceId.Should().Be(userKey.Identity!.NyxIdUserServiceId);
        intent.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ExplicitModel);
        intent.ModelSelection.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Selection_SetByService_UsesExactInventoryIdentityForDuplicateRoute()
    {
        var alpha = OpenAi with
        {
            CatalogEntryId = "catalog-alpha",
            Identity = new UserLlmServiceIdentity(
                UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                "us-alpha"),
        };
        var beta = OpenAi with
        {
            CatalogEntryId = "catalog-beta",
            Identity = new UserLlmServiceIdentity(
                UserLlmIdentityAuthority.NyxIdUserServicesInventory,
                "us-beta"),
        };
        var catalog = new StubCatalogClient { Services = [alpha, beta] };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(new StubUserConfigQueryPort())
            .AddSingleton<IChannelUserLlmPreferencePort>(preferencePort)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var selection = new DefaultUserLlmSelectionService(scopeFactory, new RecordingCapabilityBroker());

        await selection.SetByServiceAsync(
            BuildSelectionContext(),
            "us-beta",
            new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
            default);

        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.BindingId.Should().Be("bnd_sender");
        saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>()
            .Which.UserServiceId.Should().Be("us-beta");
    }

    [Fact]
    public async Task Selection_WritesThroughChannelPreferencePort()
    {
        var provisioned = ChronoLlm with { RouteValue = "/api/v1/proxy/s/chrono-provisioned" };
        var catalog = new StubCatalogClient
        {
            ProvisionedService = provisioned,
            SetupHint = new UserLlmSetupHint(
                "https://nyxid.example/services",
                [
                    new UserLlmPreset(
                        "chrono-provision",
                        "Provision chrono",
                        "Provision shared service",
                        new ProvisionThenUse("chrono/shared")),
                ]),
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(new StubUserConfigQueryPort())
            .AddSingleton<IChannelUserLlmPreferencePort>(preferencePort)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var broker = new RecordingCapabilityBroker(
            "token-for-service-list",
            "token-for-preset-write",
            "token-for-prefixed-model-write");
        var selection = new DefaultUserLlmSelectionService(scopeFactory, broker);
        var context = BuildSelectionContext();

        await selection.SetByServiceAsync(
            context,
            OpenAi.Identity!.NyxIdUserServiceId,
            new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
            default);
        await selection.ApplyPresetAsync(context, "chrono-provision", default);

        catalog.GetServicesCalls.Should().NotContain(call => call.AccessToken == "channel-context");
        catalog.ProvisionCalls.Should().NotContain(call => call.AccessToken == "channel-context");
        catalog.GetServicesCalls.Should().BeEmpty();
        catalog.ProvisionCalls.Should().BeEmpty();
        broker.RequestedScopes.Should().Equal(
            AevatarOAuthClientScopes.Proxy,
            AevatarOAuthClientScopes.Proxy);
        preferencePort.Commands.Should().HaveCount(2);
        preferencePort.Commands[0].BindingId.Should().Be("bnd_sender");
        preferencePort.Commands[0].BearerToken.Should().Be("token-for-service-list");
        preferencePort.Commands[0].Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>();
        preferencePort.Commands[1].BindingId.Should().Be("bnd_sender");
        preferencePort.Commands[1].BearerToken.Should().Be("token-for-preset-write");
        preferencePort.Commands[1].Intent.Should().BeOfType<ActivateUserLlmPresetIntent>()
            .Which.PresetId.Should().Be("chrono-provision");
    }

    [Fact]
    public async Task Use_ServiceNameAndModel_WritesRouteAndModelOverride()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use chrono-llm gpt-5.5"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("chrono-llm shared");
        reply.Text.Should().Contain("gpt-5.5");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        var intent = saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>().Subject;
        intent.UserServiceId.Should().Be(ChronoLlm.Identity!.NyxIdUserServiceId);
        intent.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ExplicitModel);
        intent.ModelSelection.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Use_DisplayNameWithSpaces_WritesMatchingRouteWithoutModelOverride()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use OpenAI (work)"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>()
            .Which.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ProviderDefault);
    }

    [Fact]
    public async Task Use_NumberAndModel_WritesRouteAndModelOverride()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 2 gpt-5.5"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        reply.Text.Should().Contain("gpt-5.5");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        var intent = saved.Intent.Should().BeOfType<SelectUserServiceUserLlmPreferenceIntent>().Subject;
        intent.UserServiceId.Should().Be(OpenAi.Identity!.NyxIdUserServiceId);
        intent.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ExplicitModel);
        intent.ModelSelection.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Use_RawModel_ReturnsUsageAndNeverWrites()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", service: ChronoLlm) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(queryPort: queryPort, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use claude-sonnet-4"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("/model use <编号|service-name> [model-name]");
        preferencePort.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Preset_UseExistingService_WritesRouteAndModel()
    {
        var catalog = new StubCatalogClient { Services = [ChronoLlm] };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(catalog, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "preset chrono-shared"), default);

        reply.Should().NotBeNull();
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Intent.Should().BeOfType<ActivateUserLlmPresetIntent>()
            .Which.PresetId.Should().Be("chrono-shared");
    }

    [Fact]
    public async Task Preset_ProvisionThenUse_PreservesCurrentModel_WhenProvisionedServiceHasNoDefaultModel()
    {
        var provisioned = ChronoLlm with { ModelCatalog = EnumeratedCatalog("gpt-5.4", includeDefault: false) };
        var catalog = new StubCatalogClient
        {
            Services = [],
            ProvisionedService = provisioned,
            SetupHint = new UserLlmSetupHint(
                "https://nyxid.example/services",
                [
                    new UserLlmPreset(
                        "chrono-provision",
                        "Provision chrono",
                        "Provision shared service",
                        new ProvisionThenUse("chrono/shared")),
                ]),
        };
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "current-model", service: OpenAi) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(catalog, queryPort, preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "preset chrono-provision"), default);

        reply.Should().NotBeNull();
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Intent.Should().BeOfType<ActivateUserLlmPresetIntent>()
            .Which.PresetId.Should().Be("chrono-provision");
    }

    [Fact]
    public async Task Reset_ClearsSenderRouteAndModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", service: ChronoLlm) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(queryPort: queryPort, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "reset"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("重置已提交");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Intent.Should().BeOfType<ResetUserLlmPreferenceIntent>();
    }

    [Fact]
    public async Task Use_NumberOutsideAvailableRange_ReturnsFriendlyMessage()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use 7"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("没有编号 7");
        preferencePort.Commands.Should().BeEmpty();
    }

    private static ModelChannelSlashCommandHandler CreateHandler(
        StubCatalogClient? catalog = null,
        StubUserConfigQueryPort? queryPort = null,
        StubChannelUserLlmPreferencePort? preferencePort = null,
        INyxIdCapabilityBroker? broker = null,
        IActorDispatchPort? actorDispatchPort = null)
    {
        catalog ??= new StubCatalogClient();
        queryPort ??= new StubUserConfigQueryPort();
        preferencePort ??= new StubChannelUserLlmPreferencePort();
        broker ??= new RecordingCapabilityBroker();
        actorDispatchPort ??= new RecordingActorDispatchPort();

        var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(queryPort)
            .AddSingleton<IChannelUserLlmPreferencePort>(preferencePort)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = new DefaultUserLlmOptionsService(catalog, scopeFactory, broker);
        var selection = new DefaultUserLlmSelectionService(scopeFactory, broker);
        return new ModelChannelSlashCommandHandler(
            NullLogger<ModelChannelSlashCommandHandler>.Instance,
            actorDispatchPort,
            options,
            selection,
            new TextUserLlmOptionsRenderer());
    }

    private static UserLlmSelectionContext BuildSelectionContext() => new(
        new BindingId { Value = "bnd_sender" },
        Context().Subject,
        "owner-scope");

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatched.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ThrowingActorDispatchPort : IActorDispatchPort
    {
        public int AttemptCount { get; private set; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            AttemptCount++;
            throw new InvalidOperationException("simulated dispatch failure");
        }
    }

    private static StudioConfig MakeConfig(
        string defaultModel,
        NyxIdLlmService service) => new(
        DefaultModel: defaultModel,
        PreferredLlmRoute: service.RouteValue,
        RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
        LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
        RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
        GithubUsername: null,
        MaxToolRounds: 0,
        LlmSelection: new LLMSelection
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = service.RouteValue,
            NyxIdUserServiceId = service.Identity!.NyxIdUserServiceId,
            ServiceSlugSnapshot = service.ServiceSlug,
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = defaultModel,
            },
        });

    private static LLMModelCatalog EnumeratedCatalog(string modelId, bool includeDefault = true) => new()
    {
        Certainty = LLMModelCatalogCertainty.Enumerated,
        DefaultModelId = includeDefault ? modelId : string.Empty,
        ModelIds = { modelId },
    };

    private sealed class StubCatalogClient : INyxIdLlmServiceCatalogClient
    {
        public IReadOnlyList<NyxIdLlmService> Services { get; init; } = [ChronoLlm, OpenAi];
        public NyxIdLlmService ProvisionedService { get; init; } = ChronoLlm;
        public Exception? GetServicesError { get; init; }
        public List<GetServicesCall> GetServicesCalls { get; } = [];
        public List<ProvisionCall> ProvisionCalls { get; } = [];

        public UserLlmSetupHint SetupHint { get; init; } = new(
            "https://nyxid.example/services",
            [
                new UserLlmPreset(
                    "chrono-shared",
                    "使用 chrono-llm 共享额度",
                    "无需自带 key",
                    new UseExistingService(
                        ChronoLlm.Identity!.NyxIdUserServiceId,
                        ChronoLlm.RouteValue,
                        ChronoLlm.ModelCatalog.DefaultModelId)),
            ]);

        public Task<NyxIdLlmServicesResult> GetServicesAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct)
        {
            GetServicesCalls.Add(new GetServicesCall(query, accessToken));
            if (GetServicesError is not null)
                return Task.FromException<NyxIdLlmServicesResult>(GetServicesError);

            return Task.FromResult(new NyxIdLlmServicesResult(Services, SetupHint));
        }

        public Task<UserLlmSetupHint> GetSetupHintAsync(
            UserLlmOptionsQuery query,
            string accessToken,
            CancellationToken ct) =>
            Task.FromResult(SetupHint);

        public Task<NyxIdLlmService> ProvisionAsync(
            UserLlmSelectionContext context,
            string accessToken,
            string provisionEndpointId,
            CancellationToken ct)
        {
            ProvisionCalls.Add(new ProvisionCall(context, accessToken, provisionEndpointId));
            return Task.FromResult(ProvisionedService);
        }

        public sealed record GetServicesCall(UserLlmOptionsQuery Query, string AccessToken);
        public sealed record ProvisionCall(
            UserLlmSelectionContext Context,
            string AccessToken,
            string ProvisionEndpointId);
    }

    private sealed class RecordingCapabilityBroker : INyxIdCapabilityBroker
    {
        private readonly Queue<string> _accessTokens;

        public List<string> RequestedScopes { get; } = new();

        public RecordingCapabilityBroker(params string[] accessTokens)
        {
            _accessTokens = new Queue<string>(
                accessTokens.Length == 0 ? ["token-for-model-list"] : accessTokens);
        }

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default)
        {
            RequestedScopes.Add(scope.Value);
            var accessToken = _accessTokens.Count == 0 ? "token-for-model-list" : _accessTokens.Dequeue();
            return Task.FromResult(new CapabilityHandle
            {
                AccessToken = accessToken,
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                Scope = scope.Value,
            });
        }

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            IssueShortLivedAsync(externalSubject, scope, ct);
    }

    private sealed class ThrowingCapabilityBroker : INyxIdCapabilityBroker
    {
        private readonly Exception _exception;

        public ThrowingCapabilityBroker(Exception exception) => _exception = exception;

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromException<CapabilityHandle>(_exception);

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromException<CapabilityHandle>(_exception);
    }

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        public Dictionary<string, StudioConfig> ByScope { get; } = new(StringComparer.Ordinal);
        public List<UserConfigResourceKey> ReadResources { get; } = [];

        public Task<StudioConfig> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioConfig(string.Empty));

        public Task<StudioConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default)
        {
            ReadResources.Add(resource);
            return Task.FromResult(ByScope.TryGetValue(resource.Value, out var cfg)
                ? cfg
                : new StudioConfig(string.Empty));
        }
    }

    private sealed class StubChannelUserLlmPreferencePort : IChannelUserLlmPreferencePort
    {
        public List<(string BindingId, string? BearerToken, UserLlmPreferenceIntent Intent)> Commands { get; } = [];

        public Task<UserConfigSaveReceipt> SaveAsync(
            string bindingId,
            string? bearerToken,
            UserLlmPreferenceIntent intent,
            CancellationToken ct)
        {
            Commands.Add((bindingId, bearerToken, intent));
            return Task.FromResult(Receipt());
        }

        private static UserConfigSaveReceipt Receipt() => new(
            Accepted: true,
            CommandId: "command-1",
            AckStage: UserConfigCommandAckStage.Accepted,
            ActorId: "user-config-default",
            CorrelationId: "command-1",
            AckedAtUtc: DateTimeOffset.UtcNow);
    }
}
