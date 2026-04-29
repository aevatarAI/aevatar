using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Regression guard for the failure mode reported on PR #457:
///
/// Minimal API's <c>RequestDelegateFactory</c> probes every parameter
/// type for a <c>BindAsync</c> custom-binder hook. <see cref="IStudioMemberService"/>
/// itself defines an instance method named <c>BindAsync</c>. Without
/// <c>[FromServices]</c> on the parameter, the binder rejects the route at
/// startup with <c>"BindAsync method found on IStudioMemberService with
/// incorrect format"</c> — *before* any request is served.
///
/// The handler-level unit tests in
/// <see cref="StudioMemberEndpointsTests"/> miss this because they invoke
/// the static handlers via reflection and never exercise
/// <c>RequestDelegateFactory</c>. This test exercises the actual route
/// pipeline by forcing endpoint construction; if a future contributor
/// drops <c>[FromServices]</c> from any handler, this test goes red
/// instead of the regression silently shipping until mainnet startup.
/// </summary>
public sealed class StudioMemberEndpointsRouteBindingTests
{
    [Fact]
    public void Map_ShouldBuildAllRoutes_WithoutBindAsyncCollision()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IStudioMemberService, NoOpMemberService>();
        builder.Services.AddRouting();
        var app = builder.Build();

        StudioMemberEndpoints.Map(app);

        // Forcing endpoint construction is what triggers the
        // RequestDelegateFactory probe that previously threw on
        // IStudioMemberService.BindAsync — this is the exact codepath
        // mainnet host startup hits on Build().
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .ToList();

        // Nine routes mapped: create, list, get, bind, get-binding,
        // contract, activate, retire, patch (ADR-0017).
        endpoints.Should().HaveCount(9);
    }

    private sealed class NoOpMemberService : IStudioMemberService
    {
        // The route-binding test never dispatches a request, so none of
        // these are actually called. They exist to satisfy DI; making
        // them throw NotImplementedException would also work, but
        // returning trivially keeps the surface boring.
        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberSummaryResponse>(new NotImplementedException());

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromException<StudioMemberDetailResponse>(new NotImplementedException());

        public Task<StudioMemberBindingResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            Task.FromException<StudioMemberBindingResponse>(new NotImplementedException());

        public Task<StudioMemberBindingContractResponse?> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberBindingContractResponse?>(null);

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
