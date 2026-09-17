using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal static class ChannelAdoptionFacadeTestSupport
{
    public static ChannelRelayRegistrationFacade Create(
        INyxChannelBotAdoptionService? adoption = null,
        ChannelAgentKeyWriteMode writeMode = ChannelAgentKeyWriteMode.NyxIdDefault)
    {
        if (adoption is null)
        {
            adoption = Substitute.For<INyxChannelBotAdoptionService>();
            adoption.AdoptAsync(Arg.Any<NyxChannelBotAdoptionRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => new NyxChannelBotAdoptionResult(true, "accepted",
                    call.Arg<NyxChannelBotAdoptionRequest>().Bot.Platform.Value, RegistrationId: "reg-1"));
        }
        var ownerScope = "scope-1";
        var ownerResolver = Substitute.For<IChannelRegistrationOwnerResolver>();
        ownerResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ownerScope = call.ArgAt<string>(1);
                return new ChannelRegistrationOwnerResolution(new VerifiedChannelRegistrationOwner(
                    ownerScope, new ChannelRegistrationKeyOwner(ChannelRegistrationKeyOwnerKind.Personal, ownerScope), null), "");
            });
        var client = new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new DetailHandler(() => ownerScope)));
        return new ChannelRelayRegistrationFacade(adoption ?? Substitute.For<INyxChannelBotAdoptionService>(),
            new VerifiedNyxChannelBotDetail.Reader(client), ownerResolver, writeMode);
    }

    private sealed class DetailHandler(Func<string> scope) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = Uri.UnescapeDataString(request.RequestUri!.Segments.Last());
            // This test fixture gives each platform a distinct existing Bot ID.
            var platform = id.StartsWith("bot-", StringComparison.Ordinal) ? id[4..] : "lark";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id, platform, user_id = scope(), status = "active", is_active = true,
                    webhook_url = "https://nyx.example.com/webhook",
                })),
            });
        }
    }
}
