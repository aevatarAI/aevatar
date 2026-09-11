using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class ChannelRegistrationNyxIdAuthorizationPort(NyxIdApiClient client)
    : IChannelRegistrationNyxIdAuthorizationPort
{
    private readonly NyxIdApiClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public async Task<NyxIdApiAccessResult<NyxIdUserServices>> ReadUserServicesAsync(
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var response = await _client.ListUserServicesAsync(accessToken, ct);
            return NyxIdApiAccessResponseParser.ParseUserServices(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UserServicesUnavailable();
        }
        catch (HttpRequestException)
        {
            return UserServicesUnavailable();
        }
        catch (InvalidOperationException)
        {
            return UserServicesUnavailable();
        }
    }

    public async Task<NyxIdApiAccessResult<NyxIdApiKeyScopePlan>> PlanApiKeyScopeAsync(
        string accessToken,
        IReadOnlyList<string> selectedServiceIds,
        string? targetOrganizationId,
        CancellationToken ct)
    {
        try
        {
            var response = await _client.PlanApiKeyScopeAsync(
                accessToken,
                selectedServiceIds,
                targetOrganizationId,
                ct);
            return NyxIdApiAccessResponseParser.ParseScopePlan(response);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ScopePlanUnavailable();
        }
        catch (HttpRequestException)
        {
            return ScopePlanUnavailable();
        }
        catch (InvalidOperationException)
        {
            return ScopePlanUnavailable();
        }
    }

    private static NyxIdApiAccessResult<NyxIdUserServices> UserServicesUnavailable() =>
        new(
            null,
            new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.Transport,
                "nyxid_user_services_unavailable"));

    private static NyxIdApiAccessResult<NyxIdApiKeyScopePlan> ScopePlanUnavailable() =>
        new(
            null,
            new NyxIdApiAccessFailure(
                NyxIdApiAccessFailureKind.Transport,
                "nyxid_scope_plan_unavailable"));
}
