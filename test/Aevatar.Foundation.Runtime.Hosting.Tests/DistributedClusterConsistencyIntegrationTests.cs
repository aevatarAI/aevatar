using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class DistributedClusterConsistencyIntegrationTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [DistributedClusterIntegrationFact]
    public async Task WorkflowsEndpoint_ShouldReturnConsistentWorkflowSetAcrossAllNodes()
    {
        using var client = new HttpClient();
        var nodes = GetClusterNodeBaseUrls();

        IReadOnlyList<string>[]? snapshots = null;
        var consistent = await WaitUntilAsync(async () =>
        {
            snapshots = await Task.WhenAll(nodes.Select(node => QueryWorkflowsAsync(client, node)));
            return snapshots.All(snapshot => snapshot.SequenceEqual(snapshots[0], StringComparer.Ordinal))
                   && snapshots[0].Count > 0;
        });

        consistent.Should().BeTrue("workflow definitions should be loaded consistently on all nodes");
        snapshots.Should().NotBeNull();
        snapshots![0].Should().NotBeEmpty();
    }

    [DistributedClusterIntegrationFact]
    public async Task AgentsEndpoint_ShouldReturnConsistentAgentSetAcrossAllNodes()
    {
        using var client = new HttpClient();
        var nodes = GetClusterNodeBaseUrls();

        IReadOnlyList<string>[]? snapshots = null;
        var consistent = await WaitUntilAsync(async () =>
        {
            snapshots = await Task.WhenAll(nodes.Select(node => QueryAgentsAsync(client, node)));
            return snapshots.All(snapshot => snapshot.SequenceEqual(snapshots[0], StringComparer.Ordinal));
        });

        consistent.Should().BeTrue("agent snapshots should converge across all cluster nodes");
        snapshots.Should().NotBeNull();
    }

    private static async Task<IReadOnlyList<string>> QueryWorkflowsAsync(HttpClient client, string baseUrl)
    {
        var response = await GetOkWithRetryAsync(client, $"{baseUrl.TrimEnd('/')}/api/workflows");
        var payload = await response.Content.ReadAsStringAsync();
        var workflows = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? [];
        workflows.Sort(StringComparer.Ordinal);
        return workflows;
    }

    private static async Task<IReadOnlyList<string>> QueryAgentsAsync(HttpClient client, string baseUrl)
    {
        var response = await GetOkWithRetryAsync(client, $"{baseUrl.TrimEnd('/')}/api/agents");
        var payload = await response.Content.ReadAsStringAsync();
        var agents = JsonSerializer.Deserialize<List<AgentSummaryDto>>(payload, JsonOptions) ?? [];
        var ids = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .Select(agent => agent.Id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return ids;
    }

    private static async Task<HttpResponseMessage> GetOkWithRetryAsync(HttpClient client, string url)
    {
        HttpResponseMessage? lastResponse = null;
        var deadline = DateTime.UtcNow + ProbeTimeout;
        while (DateTime.UtcNow <= deadline)
        {
            var response = await client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.OK)
                return response;

            lastResponse?.Dispose();
            lastResponse = response;
            await Task.Delay(ProbeInterval);
        }

        var statusCode = lastResponse?.StatusCode.ToString() ?? "NoResponse";
        lastResponse?.Dispose();
        throw new InvalidOperationException($"Failed to get HTTP 200 from '{url}'. Last status: {statusCode}.");
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + ProbeTimeout;
        while (DateTime.UtcNow <= deadline)
        {
            if (await probe())
                return true;

            await Task.Delay(ProbeInterval);
        }

        return false;
    }

    private static IReadOnlyList<string> GetClusterNodeBaseUrls()
    {
        string[] values =
        [
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE1_BASE_URL"),
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE2_BASE_URL"),
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE3_BASE_URL"),
        ];
        return values;
    }

    private static string GetRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing environment variable '{variableName}'.");
        return value;
    }

    private sealed class AgentSummaryDto
    {
        public string? Id { get; set; }
    }
}
