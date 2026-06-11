using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class StubNyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    public const string SharedServiceId = "chrono-llm-shared";
    public const string SharedServiceSlug = "chrono-llm";
    public const string SharedRouteValue = "/api/v1/proxy/s/chrono-llm";
    public const string SharedDefaultModel = "gpt-5.4";
    private const string SetupUrl = "https://nyxid.aevatar.ai/services";

    private static readonly UserLlmSetupHint SetupHint = new(
        SetupUrl,
        [
            new UserLlmPreset(
                Id: SharedServiceId,
                Title: "使用 chrono-llm 共享额度",
                Description: "使用集群共享 LLM service,无需自带 key。",
                Activation: new UseExistingService(
                    ServiceId: SharedServiceId,
                    RouteValue: SharedRouteValue,
                    DefaultModel: SharedDefaultModel)),
        ]);

    public Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(new NyxIdLlmServicesResult([], SetupHint));
    }

    public Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.FromResult(SetupHint);
    }

    public Task<NyxIdLlmService> ProvisionAsync(
        UserLlmSelectionContext context,
        string accessToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(new NyxIdLlmService(
            UserServiceId: SharedServiceId,
            ServiceSlug: SharedServiceSlug,
            DisplayName: "chrono-llm shared",
            RouteValue: SharedRouteValue,
            DefaultModel: SharedDefaultModel,
            Models: [SharedDefaultModel],
            Status: "ready",
            Source: "shared",
            Allowed: true,
            Description: "Shared cluster LLM service."));
    }
}
