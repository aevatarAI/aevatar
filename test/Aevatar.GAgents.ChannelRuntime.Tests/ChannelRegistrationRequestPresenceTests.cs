using System.Text.Json;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationRequestPresenceTests
{
    [Theory]
    [InlineData("{}", true, false, ChannelRegistrationAuthorizationMode.NyxidDefault)]
    [InlineData("{\"service_ids\":[]}", true, true, ChannelRegistrationAuthorizationMode.NyxidDefault)]
    [InlineData("{\"authorization_mode\":\"explicit_service_allowlist\",\"service_ids\":[]}", true, true, ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist)]
    [InlineData("{\"service_ids\":null}", false, false, ChannelRegistrationAuthorizationMode.NyxidDefault)]
    public void ServiceSelection_DistinguishesOmissionEmptySelectionAndInvalidNull(
        string json, bool valid, bool specified, ChannelRegistrationAuthorizationMode mode)
    {
        using var document = JsonDocument.Parse(json);

        ChannelRegistrationServiceIdsJsonParser.TryParse(document.RootElement, out var selection).Should().Be(valid);

        selection.Specified.Should().Be(specified);
        selection.AuthorizationMode.Should().Be(mode);
        selection.ServiceIds.Should().BeEmpty();
    }
}
