using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdAssistantToolSourceTests
{
    private static readonly string[] ManagementReadToolNames =
    [
        "nyxid_profile",
        "nyxid_mfa",
        "nyxid_services",
        "nyxid_api_keys",
        "nyxid_nodes",
        "nyxid_approvals",
        "nyxid_endpoints",
        "nyxid_external_keys",
        "nyxid_notifications",
        "nyxid_providers",
        "nyxid_orgs",
    ];

    [Fact]
    public async Task DiscoverToolsAsync_ShouldExposePinnedAssistantSurfaceOnly()
    {
        using var client = CreateClient(new RecordingHandler());
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);

        var tools = await source.DiscoverToolsAsync();
        var names = tools.Select(static tool => tool.Name).ToArray();

        names.Should().Contain("nyxid_proxy");
        names.Should().Contain("nyxid_require_service");
        names.Should().Contain(ManagementReadToolNames);
        names.Should().NotContain([
            "nyxid_admin",
            "nyxid_code_execute",
            "nyxid_channel_bots",
            "nyxid_channel_events",
            "nyxid_ssh_exec",
            "codex_exec",
        ]);

        foreach (var name in ManagementReadToolNames)
        {
            var tool = tools.Single(candidate => candidate.Name == name);
            tool.IsReadOnly.Should().BeTrue();
            tool.IsDestructive.Should().BeFalse();
            tool.ParametersSchema.Should()
                .NotMatchRegex("(?i)(authorization|api[-_]?key|token|secret|password|credential|cookie)");
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        }

        using var servicesSchema = JsonDocument.Parse(
            tools.Single(static tool => tool.Name == "nyxid_services").ParametersSchema);
        servicesSchema.RootElement.GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("list", "show");
    }

    [Fact]
    public async Task ManagementReadTool_ShouldRejectWritesAndUndeclaredSecretFieldsBeforeHttp()
    {
        var handler = new RecordingHandler();
        using var client = CreateClient(handler);
        var source = new NyxIdAssistantToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            client);
        var services = (await source.DiscoverToolsAsync())
            .Single(static tool => tool.Name == "nyxid_services");

        var writeResult = await services.ExecuteAsync(
            """{"action":"delete","id":"service-alpha"}""");
        var secretResult = await services.ExecuteAsync(
            """{"action":"show","id":"service-alpha","credential":"must-not-pass"}""");

        writeResult.Should().Contain("not callable from the assistant");
        secretResult.Should().Contain("not callable from the assistant");
        handler.RequestCount.Should().Be(0);
    }

    private static NyxIdApiClient CreateClient(HttpMessageHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
