using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationOwnerResolverTests
{
    [Fact]
    public async Task ResolveAsync_PersonalOwner_DoesNotReadOrganizations()
    {
        var currentUser = Substitute.For<INyxIdCurrentUserResolver>();
        currentUser.ResolveCurrentUserIdAsync("owner-token", Arg.Any<CancellationToken>())
            .Returns("user-alpha");
        var handler = new RecordingHandler("""{"orgs":[{"id":"org-alpha","your_role":"admin"}]}""");
        var resolver = CreateResolver(currentUser, handler);

        var result = await resolver.ResolveAsync(
            "owner-token",
            "user-alpha",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Owner!.AuthenticatedActorId.Should().Be("user-alpha");
        result.Owner.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Personal,
            "user-alpha"));
        result.Owner.TargetOrganizationId.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_OrganizationAdmin_ReturnsVerifiedOrganizationOwner()
    {
        var currentUser = Substitute.For<INyxIdCurrentUserResolver>();
        currentUser.ResolveCurrentUserIdAsync("owner-token", Arg.Any<CancellationToken>())
            .Returns("user-alpha");
        var handler = new RecordingHandler(
            """{"orgs":[{"id":"org-alpha","your_role":"admin"}]}""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var resolver = new ChannelRegistrationOwnerResolver(currentUser, client);

        var result = await resolver.ResolveAsync(
            "owner-token",
            "org-alpha",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Owner!.AuthenticatedActorId.Should().Be("user-alpha");
        result.Owner.KeyOwner.Should().Be(new ChannelRegistrationKeyOwner(
            ChannelRegistrationKeyOwnerKind.Organization,
            "org-alpha"));
        result.Owner.TargetOrganizationId.Should().Be("org-alpha");
        handler.Requests.Should().Equal((HttpMethod.Get, "/api/v1/orgs"));
    }

    [Theory]
    [InlineData("""{"orgs":[{"id":"org-alpha","your_role":"member"}]}""")]
    [InlineData("""{"orgs":[{"id":"org-alpha","your_role":"viewer"}]}""")]
    [InlineData("""{"orgs":[{"id":"org-other","your_role":"admin"}]}""")]
    [InlineData("""{"organizations":[]}""")]
    [InlineData("""{"orgs":"org-alpha"}""")]
    [InlineData("""{"orgs":[{"id":" org-alpha","your_role":"admin"}]}""")]
    [InlineData("""{"error":true,"status":503}""")]
    [InlineData("{not-json")]
    public async Task ResolveAsync_OrganizationAuthorityCannotBeProven_ReturnsForbidden(
        string responseBody)
    {
        var currentUser = Substitute.For<INyxIdCurrentUserResolver>();
        currentUser.ResolveCurrentUserIdAsync("owner-token", Arg.Any<CancellationToken>())
            .Returns("user-alpha");
        var handler = new RecordingHandler(responseBody);
        var resolver = CreateResolver(currentUser, handler);

        var result = await resolver.ResolveAsync(
            "owner-token",
            "org-alpha",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Owner.Should().BeNull();
        result.ErrorCode.Should().Be("service_owner_forbidden");
        handler.Requests.Should().Equal((HttpMethod.Get, "/api/v1/orgs"));
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellation_PropagatesWithoutOrganizationRead()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var currentUser = Substitute.For<INyxIdCurrentUserResolver>();
        currentUser.ResolveCurrentUserIdAsync("owner-token", caller.Token)
            .Returns(_ => Task.FromCanceled<string?>(caller.Token));
        var handler = new RecordingHandler("""{"orgs":[]}""");
        var resolver = CreateResolver(currentUser, handler);

        var act = () => resolver.ResolveAsync(
            "owner-token",
            "org-alpha",
            caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_CurrentUserLookupFails_ReturnsForbidden()
    {
        var currentUser = Substitute.For<INyxIdCurrentUserResolver>();
        currentUser.ResolveCurrentUserIdAsync("owner-token", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(new HttpRequestException("provider failure")));
        var handler = new RecordingHandler("""{"orgs":[]}""");
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler));
        var resolver = new ChannelRegistrationOwnerResolver(currentUser, client);

        var result = await resolver.ResolveAsync(
            "owner-token",
            "org-alpha",
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Owner.Should().BeNull();
        result.ErrorCode.Should().Be("service_owner_forbidden");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void AddNyxIdRelayChannel_RegistersOwnerResolver()
    {
        var services = new ServiceCollection();

        services.AddNyxIdRelayChannel();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IChannelRegistrationOwnerResolver) &&
            descriptor.ImplementationType == typeof(ChannelRegistrationOwnerResolver) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static ChannelRegistrationOwnerResolver CreateResolver(
        INyxIdCurrentUserResolver currentUser,
        HttpMessageHandler handler) =>
        new(
            currentUser,
            new NyxIdApiClient(
                new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
                new HttpClient(handler)));

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
