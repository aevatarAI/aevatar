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
        UserServiceId: "svc-chrono",
        ServiceSlug: "chrono-llm",
        DisplayName: "chrono-llm shared",
        RouteValue: "/api/v1/proxy/s/chrono-llm",
        DefaultModel: "gpt-5.4",
        Models: ["gpt-5.4"],
        Status: "ready",
        Source: "shared",
        Allowed: true,
        Description: "Shared service");

    private static readonly NyxIdLlmService OpenAi = new(
        UserServiceId: "svc-openai",
        ServiceSlug: "openai-work",
        DisplayName: "OpenAI (work)",
        RouteValue: "/api/v1/proxy/s/openai-work",
        DefaultModel: "gpt-4o",
        Models: ["gpt-4o"],
        Status: "ready",
        Source: "user",
        Allowed: true,
        Description: "Work key");

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
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.5", route: ChronoLlm.RouteValue) },
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
    }

    [Fact]
    public async Task EmptyRoute_RendersCurrentRouteOnly()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.5", route: ChronoLlm.RouteValue) },
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
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "gpt-5.4", route: ChronoLlm.RouteValue) },
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
                UserServiceId = $"svc-{i}",
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
    public async Task List_SelfHealsAndRebindsMessage_WhenBindingScopeMissing()
    {
        // NyxID rejects the binding's scope set: the binding was issued before
        // aevatar's DCR started requesting `proxy`, so the broker can no longer
        // mint LLM-API tokens for it. Self-heal by revoking the local actor so
        // /init is unblocked, AND tell the user.
        var dispatchPort = new RecordingActorDispatchPort();
        var handler = CreateHandler(
            broker: new ThrowingCapabilityBroker(new BindingScopeMismatchException(Context().Subject)),
            actorDispatchPort: dispatchPort);

        var reply = await handler.HandleAsync(Context(), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("缺少 LLM route 权限");
        reply.Text.Should().Contain("清理已提交");
        reply.Text.Should().Contain("/init");
        AssertRevokeBindingDispatched(dispatchPort, expectedReason: "auto_self_heal_scope_mismatch");
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
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.ScopeId.Should().Be("bnd_sender");
        saved.Option.RouteValue.Should().Be(OpenAi.RouteValue);
        saved.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task Use_ServiceName_WritesMatchingRoute()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use openai"), default);

        reply.Should().NotBeNull();
        preferencePort.SelectedOptions.Should().ContainSingle()
            .Subject.Option.RouteValue.Should().Be(OpenAi.RouteValue);
    }

    [Fact]
    public async Task Use_ServiceName_PrefersSelectableDuplicate()
    {
        var disabledGateway = ChronoLlm with
        {
            UserServiceId = "chrono-llm",
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
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(selectableProxy.RouteValue);
        saved.Model.Should().Be(selectableProxy.DefaultModel);
    }

    [Fact]
    public async Task Use_ServiceNameAndModel_PrefersUserKeyCandidateOverLegacyCatalogCandidate()
    {
        var legacyCatalog = ChronoLlm with
        {
            UserServiceId = "svc-chrono",
            DisplayName = "Chrono LLM",
            Status = "not_connected",
            Source = NyxIdLlmProviderSource.ProxyService,
            Allowed = false,
        };
        var userKey = ChronoLlm with
        {
            UserServiceId = "key-chrono",
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
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(userKey.RouteValue);
        saved.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Selection_SetByService_PrefersSelectableDuplicateForSubmittedServiceId()
    {
        var disabledGateway = ChronoLlm with
        {
            UserServiceId = "chrono-llm",
            DisplayName = "Chrono LLM",
            RouteValue = "/api/v1/llm/chrono-llm/v1",
            Status = "not_connected",
            Source = NyxIdLlmProviderSource.GatewayProvider,
            Allowed = false,
        };
        var selectableProxy = ChronoLlm with { DisplayName = "Chrono LLM" };
        var catalog = new StubCatalogClient { Services = [disabledGateway, selectableProxy] };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var provider = new ServiceCollection()
            .AddSingleton<IUserConfigQueryPort>(new StubUserConfigQueryPort())
            .AddSingleton<IChannelUserLlmPreferencePort>(preferencePort)
            .BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var options = new DefaultUserLlmOptionsService(catalog, scopeFactory);
        var selection = new DefaultUserLlmSelectionService(options, catalog, scopeFactory);

        await selection.SetByServiceAsync(BuildSelectionContext(), "chrono-llm", null, default);

        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(selectableProxy.RouteValue);
        saved.Model.Should().Be(selectableProxy.DefaultModel);
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
        var options = new DefaultUserLlmOptionsService(catalog, scopeFactory, broker);
        var selection = new DefaultUserLlmSelectionService(options, catalog, scopeFactory, broker);
        var context = BuildSelectionContext();

        await selection.SetByServiceAsync(context, "openai-work", null, default);
        await selection.ApplyPresetAsync(context, "chrono-provision", default);
        await selection.SetModelOverrideAsync(context, "chrono-llm/gpt-5.5", default);

        catalog.GetServicesCalls.Should().NotContain(call => call.AccessToken == "channel-context");
        catalog.ProvisionCalls.Should().NotContain(call => call.AccessToken == "channel-context");
        catalog.GetServicesCalls.Should().ContainSingle(call =>
            call.Query.BindingId.Value == "bnd_sender" &&
            call.Query.RegistrationScopeId == "owner-scope" &&
            call.AccessToken == "token-for-service-list");
        catalog.ProvisionCalls.Should().BeEmpty();
        broker.RequestedScopes.Should().Equal(
            AevatarOAuthClientScopes.Proxy,
            AevatarOAuthClientScopes.Proxy,
            AevatarOAuthClientScopes.Proxy);
        preferencePort.SelectedOptions.Should().HaveCount(1);
        preferencePort.SelectedOptions[0].ScopeId.Should().Be("bnd_sender");
        preferencePort.SelectedOptions[0].Option.RouteValue.Should().Be(OpenAi.RouteValue);
        preferencePort.Commands.Should().HaveCount(2);
        preferencePort.Commands[0].ScopeId.Should().Be("bnd_sender");
        preferencePort.Commands[0].BearerToken.Should().Be("token-for-preset-write");
        preferencePort.Commands[0].Command.PresetId.Should().Be("chrono-provision");
        preferencePort.Commands[1].ScopeId.Should().Be("bnd_sender");
        preferencePort.Commands[1].BearerToken.Should().Be("token-for-prefixed-model-write");
        preferencePort.Commands[1].Command.Model.Should().Be("chrono-llm/gpt-5.5");
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
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(ChronoLlm.RouteValue);
        saved.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Use_DisplayNameWithSpaces_WritesMatchingRouteWithoutModelOverride()
    {
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use OpenAI (work)"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("OpenAI (work)");
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(OpenAi.RouteValue);
        saved.Model.Should().Be(OpenAi.DefaultModel);
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
        var saved = preferencePort.SelectedOptions.Should().ContainSingle().Subject;
        saved.Option.RouteValue.Should().Be(OpenAi.RouteValue);
        saved.Model.Should().Be("gpt-5.5");
    }

    [Fact]
    public async Task Use_RawModel_WritesModelOnlyAndPreservesRoute()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", route: ChronoLlm.RouteValue) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(queryPort: queryPort, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "use claude-sonnet-4"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("claude-sonnet-4");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Command.Model.Should().Be("claude-sonnet-4");
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
        saved.Command.PresetId.Should().Be("chrono-shared");
    }

    [Fact]
    public async Task Preset_ProvisionThenUse_PreservesCurrentModel_WhenProvisionedServiceHasNoDefaultModel()
    {
        var provisioned = ChronoLlm with { DefaultModel = null };
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
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "current-model", route: OpenAi.RouteValue) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(catalog, queryPort, preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "preset chrono-provision"), default);

        reply.Should().NotBeNull();
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Command.PresetId.Should().Be("chrono-provision");
    }

    [Fact]
    public async Task Reset_ClearsSenderRouteAndModel()
    {
        var queryPort = new StubUserConfigQueryPort
        {
            ByScope = { ["bnd_sender"] = MakeConfig(defaultModel: "old-model", route: ChronoLlm.RouteValue) },
        };
        var preferencePort = new StubChannelUserLlmPreferencePort();
        var handler = CreateHandler(queryPort: queryPort, preferencePort: preferencePort);

        var reply = await handler.HandleAsync(Context(subAndArgs: "reset"), default);

        reply.Should().NotBeNull();
        reply!.Text.Should().Contain("已清空");
        var saved = preferencePort.Commands.Should().ContainSingle().Subject;
        saved.Command.Reset.Should().BeTrue();
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
        preferencePort.SelectedOptions.Should().BeEmpty();
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
        var selection = new DefaultUserLlmSelectionService(options, catalog, scopeFactory, broker);
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
        string route = UserConfigLlmRouteDefaults.Gateway) => new(
        DefaultModel: defaultModel,
        PreferredLlmRoute: route,
        RuntimeMode: UserConfigRuntimeDefaults.LocalMode,
        LocalRuntimeBaseUrl: UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
        RemoteRuntimeBaseUrl: UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
        GithubUsername: null,
        MaxToolRounds: 0);

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
                    new UseExistingService(ChronoLlm.UserServiceId, ChronoLlm.RouteValue, ChronoLlm.DefaultModel)),
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

        public Task<StudioConfig> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioConfig(string.Empty));

        public Task<StudioConfig> GetAsync(string scopeId, CancellationToken ct = default) =>
            Task.FromResult(ByScope.TryGetValue(scopeId, out var cfg) ? cfg : new StudioConfig(string.Empty));
    }

    private sealed class StubChannelUserLlmPreferencePort : IChannelUserLlmPreferencePort
    {
        public List<(string ScopeId, string? BearerToken, SaveUserLlmPreferenceCommand Command)> Commands { get; } = [];
        public List<(string ScopeId, UserLlmOption Option, string? Model, bool PreserveCurrentModelWhenMissing)> SelectedOptions { get; } = [];

        public Task<UserConfigSaveReceipt> SaveAsync(
            string scopeId,
            string? bearerToken,
            SaveUserLlmPreferenceCommand command,
            CancellationToken ct)
        {
            Commands.Add((scopeId, bearerToken, command));
            return Task.FromResult(Receipt());
        }

        public Task<UserConfigSaveReceipt> SaveSelectedOptionAsync(
            string scopeId,
            UserLlmOption option,
            string? model,
            bool preserveCurrentModelWhenMissing,
            CancellationToken ct)
        {
            SelectedOptions.Add((scopeId, option, model, preserveCurrentModelWhenMissing));
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
