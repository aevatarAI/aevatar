using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.ContentArtifacts;

/// <summary>
/// Regression guard mirroring <see cref="StudioTeamEndpointsRouteBindingTests"/> for the
/// content-artifact surface. The handler tests invoke methods directly and never build the
/// routes, so a body parameter on a verb that forbids inferred bodies (DELETE on the pin
/// resource) escaped review and failed mainnet startup instead. Forcing endpoint construction
/// here fails that class of regression before the host does.
/// </summary>
public sealed class ContentArtifactEndpointsRouteBindingTests
{
    [Fact]
    public void Map_ShouldBuildAllRoutes_IncludingDeleteWithExplicitBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IContentArtifactService, NoOpContentArtifactService>();
        builder.Services.AddSingleton<IContentArtifactPinService, NoOpContentArtifactPinService>();
        builder.Services.AddRouting();
        var app = builder.Build();

        ContentArtifactEndpoints.Map(app);

        // Forces RequestDelegateFactory to bind every handler; an inferred body on
        // DELETE/GET throws InvalidOperationException here.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.Should().HaveCount(15);
        endpoints.Should().Contain(endpoint =>
            endpoint.RoutePattern.RawText == "/api/scopes/{scopeId}/content-artifact-pins/{pinKey}");
    }

    private sealed class NoOpContentArtifactService : IContentArtifactService
    {
        public Task<ContentArtifactAcceptedReceipt> CreateAsync(
            string scopeId, CreateContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactListResponse> ListAsync(
            string scopeId, ContentArtifactQueryRequest query, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactListResponse>(new NotImplementedException());

        public Task<ContentArtifactCurrentStateResponse> GetAsync(
            string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactCurrentStateResponse>(new NotImplementedException());

        public Task<ContentArtifactRevisionResponse> GetRevisionAsync(
            string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactRevisionResponse>(new NotImplementedException());

        public Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(
            string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactRevisionResponse>(new NotImplementedException());

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
            string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactRevisionContentResponse>(new NotImplementedException());

        public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(
            string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(
            string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(
            string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(
            string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(
            string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(
            string scopeId, AttachContentArtifactsToRunRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactRunAttachmentReceipt>(new NotImplementedException());
    }

    private sealed class NoOpContentArtifactPinService : IContentArtifactPinService
    {
        public Task<ContentArtifactPinCurrentStateResponse> GetAsync(
            string scopeId, string pinKey, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactPinCurrentStateResponse>(new NotImplementedException());

        public Task<ContentArtifactPinAcceptedReceipt> SetAsync(
            string scopeId, string pinKey, SetContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactPinAcceptedReceipt>(new NotImplementedException());

        public Task<ContentArtifactPinAcceptedReceipt> ClearAsync(
            string scopeId, string pinKey, ClearContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) =>
            Task.FromException<ContentArtifactPinAcceptedReceipt>(new NotImplementedException());
    }
}
