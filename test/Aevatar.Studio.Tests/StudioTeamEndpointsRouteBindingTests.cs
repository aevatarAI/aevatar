using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Regression guard mirroring <see cref="StudioMemberEndpointsRouteBindingTests"/>
/// for the team-first surface (ADR-0017). Forces endpoint construction so a
/// future drop of <c>[FromServices]</c> on any team handler — which would
/// re-trigger the <c>RequestDelegateFactory</c> BindAsync collision on
/// <see cref="IStudioTeamService"/> — fails this test before mainnet startup.
/// </summary>
public sealed class StudioTeamEndpointsRouteBindingTests
{
    [Fact]
    public void Map_ShouldBuildAllRoutes_WithoutBindAsyncCollision()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IStudioTeamService, NoOpTeamService>();
        builder.Services.AddSingleton<IStudioMemberService, NoOpMemberServiceForTeam>();
        builder.Services.AddRouting();
        var app = builder.Build();

        StudioTeamEndpoints.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .ToList();

        // Six routes: create, list, get, patch, archive, list-members.
        endpoints.Should().HaveCount(6);
    }

    private sealed class NoOpTeamService : IStudioTeamService
    {
        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());

        public Task<StudioTeamSummaryResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromException<StudioTeamSummaryResponse>(new NotImplementedException());
    }

    private sealed class NoOpMemberServiceForTeam : IStudioMemberService
    {
        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberSummaryResponse>(new NotImplementedException());

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberDetailResponse>(new NotImplementedException());

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingAcceptedResponse>(new NotImplementedException());

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberBindingViewResponse(null));

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingRunStatusResponse>(new NotImplementedException());

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberEndpointContractResponse?>(null);

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingActivationResponse>(new NotImplementedException());

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingRevisionActionResponse>(new NotImplementedException());

        public Task<StudioMemberDetailResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberDetailResponse>(new NotImplementedException());
    }
}
