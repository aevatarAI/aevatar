using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class DistributedMixedVersionClusterIntegrationTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [DistributedClusterIntegrationFact]
    public async Task AgentsEndpoint_ShouldBeReachableAcrossConfiguredMixedNodes()
    {
        using var client = new HttpClient();
        var nodes = GetClusterNodeBaseUrls();
        nodes.Count.Should().Be(GetExpectedNodeCount());

        IReadOnlyList<string>[]? snapshots = null;
        var consistent = await WaitUntilAsync(async () =>
        {
            snapshots = await Task.WhenAll(nodes.Select(node => QueryAgentsAsync(client, node)));
            return snapshots.All(snapshot => snapshot.SequenceEqual(snapshots[0], StringComparer.Ordinal));
        });

        consistent.Should().BeTrue("agent snapshots should converge across all configured mixed-version nodes");
        snapshots.Should().NotBeNull();
    }

    [DistributedClusterIntegrationFact]
    public async Task WorkflowsEndpoint_ShouldBeReachableAcrossConfiguredMixedNodes()
    {
        using var client = new HttpClient();
        var nodes = GetClusterNodeBaseUrls();
        nodes.Count.Should().Be(GetExpectedNodeCount());

        IReadOnlyList<string>[]? snapshots = null;
        var consistent = await WaitUntilAsync(async () =>
        {
            snapshots = await Task.WhenAll(nodes.Select(node => QueryWorkflowsAsync(client, node)));
            return snapshots.All(snapshot => snapshot.SequenceEqual(snapshots[0], StringComparer.Ordinal))
                   && snapshots[0].Count > 0;
        });

        consistent.Should().BeTrue("workflow definitions should converge across all configured mixed-version nodes");
        snapshots.Should().NotBeNull();
        snapshots![0].Should().NotBeEmpty();
    }

    private static async Task<IReadOnlyList<string>> QueryAgentsAsync(HttpClient client, string baseUrl)
    {
        var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/agents");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];

        var payload = await response.Content.ReadAsStringAsync();
        var agents = JsonSerializer.Deserialize<List<AgentSummaryDto>>(payload, JsonOptions) ?? [];
        return agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .Select(agent => agent.Id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> QueryWorkflowsAsync(HttpClient client, string baseUrl)
    {
        var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/workflows");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return [];

        var payload = await response.Content.ReadAsStringAsync();
        var workflows = JsonSerializer.Deserialize<List<string>>(payload, JsonOptions) ?? [];
        workflows.Sort(StringComparer.Ordinal);
        return workflows;
    }

    private static IReadOnlyList<string> GetClusterNodeBaseUrls()
    {
        var nodes = new List<string>
        {
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE1_BASE_URL"),
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE2_BASE_URL"),
            GetRequiredEnvironmentVariable("AEVATAR_TEST_CLUSTER_NODE3_BASE_URL"),
        };

        TryAppendNode(nodes, "AEVATAR_TEST_CLUSTER_NODE4_BASE_URL");
        TryAppendNode(nodes, "AEVATAR_TEST_CLUSTER_NODE5_BASE_URL");
        TryAppendNode(nodes, "AEVATAR_TEST_CLUSTER_NODE6_BASE_URL");
        return nodes;
    }

    private static int GetExpectedNodeCount()
    {
        var rawValue = Environment.GetEnvironmentVariable("AEVATAR_TEST_CLUSTER_EXPECTED_NODE_COUNT");
        if (int.TryParse(rawValue, out var value) && value > 0)
            return value;
        return 6;
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

    private static void TryAppendNode(List<string> nodes, string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value))
            nodes.Add(value);
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
