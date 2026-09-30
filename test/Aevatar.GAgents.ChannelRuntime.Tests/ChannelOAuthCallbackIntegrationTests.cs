using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class ChannelConversationTurnRunnerTests
{
    [Fact]
    public async Task OAuthStart_ToSignedCallback_ToBindingCommit_ToStreamedOriginalChannel()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var runtime = new CallbackIntegrationInbox();
        var llm = new CallbackIntegrationLLM { Reply = "Authorization is verified; continuing the original report." };
        var external = new CallbackIntegrationOAuthHttp();
        var subject = new ExternalSubjectRef { Platform = "lark", Tenant = "tenant-original", ExternalUserId = "sender-original" };
        var query = Substitute.For<IExternalIdentityBindingQueryPort>();
        var owner = Substitute.For<IOwnerScopeResolver>();
        var capability = Substitute.For<INyxIdCapabilityBroker>();
        capability.IssueShortLivedByBindingIdAsync(Arg.Any<ExternalSubjectRef>(), "oauth-binding-exact",
            Arg.Any<CapabilityScope>(), Arg.Any<CancellationToken>()).Returns(new CapabilityHandle { AccessToken = "verified-original-sender" });
        var issuer = new NyxIdConnectLinkVerifierTests.Issuer();
        var registration = BuildNewRegistrationEntry();
        var vault = new InMemorySecretVault(clock);
        var key = await vault.PutAsync(new StoreSecretRequest(CredentialSecretPurposes.ChannelNyxIdAgentKey,
            registration.ScopeId, registration.NyxAgentApiKeyId, "original-registration-agent-key", "callback-integration"));
        registration.ChannelAgentKey.SecretReference = key.Reference.Clone();
        registration.WorkflowResultDeliveryCredential = key.Reference.Clone();
        var outbound = new RecordingJsonHandler("""{"message_id":"oauth-original-output"}""");
        var bindingActorId = ExternalIdentityBindingGAgent.BuildActorId(subject);
        var commit = CallbackBindingDispatch<CommitBindingCommand>(runtime, bindingActorId);
        var replace = CallbackBindingDispatch<ReplaceBindingCommand>(runtime, bindingActorId);
        var confirm = CallbackBindingDispatch<ConfirmBindingGrantCommand>(runtime, bindingActorId);
        var abandon = CallbackBindingDispatch<AbandonBindingPreparationCommand>(runtime, bindingActorId);
        var observation = Substitute.For<ICommandDispatchService<ObserveBrokerCapabilityCommand,
            ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();
        observation.DispatchAsync(Arg.Any<ObserveBrokerCapabilityCommand>(), Arg.Any<CancellationToken>()).Returns(
            CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt("oauth-client", "capability-observed", "capability-correlation")));
        var snapshot = new AevatarOAuthClientSnapshot("configured-client", clock.GetUtcNow(), "key-v1",
            Convert.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            clock.GetUtcNow(), "https://id.example.test", true, clock.GetUtcNow(),
            RedirectUri: NyxIdRedirectUriResolver.Resolve(), OauthScope: AevatarOAuthClientScopes.AuthorizationScope);
        var clientProvider = Substitute.For<IAevatarOAuthClientProvider>();
        clientProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var options = Options.Create(new NyxIdBrokerOptions
        {
            PublicApiBaseUrl = "https://api.example.test", ResourceServerBaseUrl = "https://api.example.test",
        });
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(external, disposeHandler: false));
        using var services = CreateCallbackIntegrationServices(clock, runtime, llm, external, issuer, registration,
            vault, outbound, collection => collection
                .AddSingleton<NyxIdRemoteCapabilityBroker>(sp => new NyxIdRemoteCapabilityBroker(httpFactory,
                    clientProvider, options, new StateTokenCodec(clientProvider, options, clock), query, clock,
                    NullLogger<NyxIdRemoteCapabilityBroker>.Instance, sp.GetRequiredService<IExternalCallbackCommandPort>()))
                .AddSingleton<IOAuthContinuationExecutionPort>(sp => new OAuthContinuationExecutionPort(
                    sp.GetRequiredService<NyxIdRemoteCapabilityBroker>(), capability, query, owner,
                    commit, replace, confirm, abandon, observation, NullLogger<OAuthContinuationExecutionPort>.Instance)));
        runtime.Services = services;
        var conversation = (ConversationGAgent)(await runtime.CreateAsync<ConversationGAgent>("conversation-original")).Agent;
        var broker = services.GetRequiredService<NyxIdRemoteCapabilityBroker>();
        var original = BuildInboundActivity("Authorize NyxID and continue my original report", "oauth-original-activity",
            ConversationScope.Thread, "thread-original",
            new OutboundDeliveryContext { ReplyMessageId = "oauth-original-anchor", CorrelationId = "oauth-original-correlation" },
            new TransportExtras { NyxPlatform = "lark", NyxAgentApiKeyId = registration.NyxAgentApiKeyId,
                NyxRegistrationScopeId = registration.ScopeId }, botId: registration.NyxAgentApiKeyId);
        var challenge = await broker.StartExternalBindingAsync(subject, new ChannelCallbackOrigin
        {
            ConversationActorId = conversation.Id, ChannelRegistrationId = registration.Id,
            ActionId = original.Id, OriginalActivity = original,
        });
        challenge.AuthorizeUrl.Should().NotBeEmpty("the caller sends the link in the original private reply");
        outbound.Requests.Should().BeEmpty();
        await runtime.DrainAsync();
        var operation = runtime.Agents.OfType<ExternalCallbackGAgent>().Should().ContainSingle().Subject;
        operation.State.Registration.LinkDeliveryMode.Should().Be(ExternalCallbackLinkDeliveryMode.Caller);
        operation.State.LinkPresented.Should().BeFalse();
        outbound.Requests.Should().BeEmpty("the operation must not duplicate the caller's authorization card");
        var signed = QueryHelpers.ParseQuery(new Uri(challenge.AuthorizeUrl).Query)["state"].ToString();
        var decoded = await broker.TryDecodeStateTokenAsync(signed);
        decoded.ContinuationRequested.Should().BeTrue();
        decoded.CorrelationId.Should().Be(operation.State.Registration.CallbackId);
        decoded.ExternalSubject.Should().Be(subject);

        Task<IResult> Submit() => IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(
            "single-use-code", signed, null, "json", broker, capability, query, commit, replace, owner,
            observation, NullLoggerFactory.Instance, CancellationToken.None,
            services.GetRequiredService<IExternalCallbackCommandPort>());
        var receipt = await Submit();
        ((IStatusCodeHttpResult)receipt).StatusCode.Should().Be(StatusCodes.Status202Accepted);
        external.CodeExchanges.Should().Be(0, "the HTTP callback does not execute the one-use code exchange");
        await runtime.DrainAsync(envelope => envelope.Payload.Is(CommitBindingCommand.Descriptor));
        external.CodeExchanges.Should().Be(1);
        var binding = runtime.Agents.OfType<ExternalIdentityBindingGAgent>().Should().ContainSingle().Subject;
        binding.State.BindingId.Should().BeEmpty("the binding command has only been accepted into its inbox");
        operation.State.OauthPhase.Should().Be(ExternalCallbackAuthorizationPhase.BindingPending);
        operation.State.Result.Should().Be(CallbackResult.Unspecified);
        llm.Requests.Should().BeEmpty();
        outbound.Requests.Should().BeEmpty();

        await runtime.DrainAsync();
        binding.State.BindingId.Should().Be("oauth-binding-exact");
        binding.State.OwnerScopeId.Should().Be("sender-owner");
        binding.State.CallbackDecisions.Should().ContainSingle().Which.Outcome.Succeeded.Should().BeTrue();
        operation.State.Result.Should().Be(CallbackResult.Succeeded);
        operation.State.VerifiedReferences.BindingId.Should().Be("oauth-binding-exact");
        operation.State.VerifiedReferences.OwnerScopeId.Should().Be("sender-owner");
        operation.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        conversation.State.ExternalCallbackActions.Should().ContainSingle().Which.ResumeAdmitted.Should().BeTrue();
        runtime.Agents.OfType<AgentRunGAgent>().Should().ContainSingle().Which.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        llm.Requests.Should().ContainSingle();
        string.Join("\n", llm.Requests[0].Messages.Select(message => message.Content)).Should()
            .Contain("Authorize NyxID and continue my original report").And.Contain("Succeeded");
        issuer.BindingId.Should().Be("oauth-binding-exact");
        outbound.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer original-registration-agent-key");
        outbound.Requests[0].Body.Should().Contain("oauth-original-anchor").And.Contain(llm.Reply);
        await Submit();
        await runtime.DrainAsync();
        external.CodeExchanges.Should().Be(1);
        llm.Requests.Should().ContainSingle();
        outbound.Requests.Should().ContainSingle();
    }

    private static ICommandDispatchService<T, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
        CallbackBindingDispatch<T>(CallbackIntegrationInbox runtime, string actorId) where T : class, IMessage =>
        new ChannelIdentityOAuthCommandDispatch<T, ExternalIdentityBindingGAgent>(runtime, runtime,
            new ChannelIdentityOAuthCommandRoute<T>(_ => new ChannelIdentityOAuthCommandTarget(actorId, "oauth-callback-integration")));

    private sealed class CallbackIntegrationOAuthHttp : HttpMessageHandler
    {
        public int CodeExchanges { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/oauth/token");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers.ParseQuery(body);
            form["grant_type"].ToString().Should().Be("authorization_code");
            form["code"].ToString().Should().Be("single-use-code");
            form["code_verifier"].ToString().Should().NotBeNullOrWhiteSpace();
            form["redirect_uri"].ToString().Should().Be(NyxIdRedirectUriResolver.Resolve());
            CodeExchanges++;
            var token = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"uid\":\"sender-owner\"}")).TrimEnd('=') + ".signature";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { binding_id = "oauth-binding-exact", id_token = token })),
            };
        }
    }
}
