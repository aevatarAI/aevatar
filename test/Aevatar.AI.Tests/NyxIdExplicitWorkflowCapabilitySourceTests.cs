using System.Net;
using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExplicitWorkflowCapabilitySourceTests
{
    [Fact]
    public void AddNyxIdTools_ShouldRegisterExplicitRequestCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExplicitWorkflowCapabilitySource));
    }

    [Fact]
    public async Task InspectAsync_ShouldBuildExplicitProofFromExactUserServiceWithoutMcpRead()
    {
        var handler = new InventoryHandler(UserServices(Service()));
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedSelector.Should().BeEquivalentTo(Selector());
        result.SelectedCapability.CapabilityCase.Should()
            .Be(ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest);
        var proof = result.SelectedCapability.NyxIdUserRequest;
        proof.Request.Should().BeEquivalentTo(Selector().NyxIdRequest);
        proof.ServiceSlugSnapshot.Should().Be("shared-slug");
        proof.ContractDigest.Should().NotBeNullOrWhiteSpace();
        proof.ExecutionPolicy.Risk.Should().Be(NyxIdOperationRisk.ReadOnly);
        proof.ExecutionPolicy.Approval.Should().Be(NyxIdOperationApproval.None);
        proof.ExecutionPolicy.EnforcementOwner.Should().Be(NyxIdOperationEnforcementOwner.Aevatar);
        proof.ExecutionPolicy.AllowedExecutionModes.Should().Equal(
            ExternalCapabilityExecutionMode.Interactive,
            ExternalCapabilityExecutionMode.Durable);
        result.Sources.Should().ContainSingle().Which.SourceKind.Should()
            .Be(ExternalCapabilitySourceKind.NyxIdUserServices);
        handler.Requests.Should().Equal(new RequestRecord("/api/v1/keys", "caller-credential"));
    }

    [Theory]
    [InlineData("missing", ExternalCapabilityReadinessStatus.ServiceRegistrationRequired)]
    [InlineData("inactive", ExternalCapabilityReadinessStatus.CredentialConnectionRequired)]
    [InlineData("inaccessible", ExternalCapabilityReadinessStatus.ServiceAccessDenied)]
    [InlineData("ambiguous", ExternalCapabilityReadinessStatus.SourceStale)]
    public async Task InspectAsync_ShouldFailClosedForUnusableExactService(
        string scenario,
        ExternalCapabilityReadinessStatus expectedStatus)
    {
        var response = scenario switch
        {
            "missing" => UserServices(Service(id: "usvc-beta")),
            "inactive" => UserServices(Service(active: false)),
            "inaccessible" => UserServices(Service(credentialSource: "org", allowed: false)),
            "ambiguous" => UserServices(Service(), Service(slug: "other-slug")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var source = CreateSource(new InventoryHandler(response));

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.SelectedCapability.Should().BeNull();
        result.Blockers.Should().ContainSingle().Which.Code.Should().NotBeNullOrWhiteSpace();
    }

    private static NyxIdExplicitWorkflowCapabilitySource CreateSource(InventoryHandler handler)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" };
        return new NyxIdExplicitWorkflowCapabilitySource(
            new NyxIdApiClient(options, new HttpClient(handler)),
            options,
            new FixedTimeProvider());
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new("scope-alpha", "nyx-user-alpha", "caller-credential");

    private static ExternalWorkflowCapabilitySelector Selector()
    {
        var request = new NyxIdRequestSelector
        {
            UserServiceId = "usvc-alpha",
            Method = NyxIdRequestMethod.Get,
            PathTemplate = "/api/resources/{resource_id}",
            BodyMode = NyxIdRequestBodyMode.None,
            ResponseMode = NyxIdRequestResponseMode.Text,
        };
        request.QueryParameters.Add("page_size");
        request.HeaderParameters.Add("If-Match");
        return new ExternalWorkflowCapabilitySelector { NyxIdRequest = request };
    }

    private static string UserServices(params string[] services) =>
        $"{{\"services\":[{string.Join(',', services)}]}}";

    private static string Service(
        string id = "usvc-alpha",
        string slug = "shared-slug",
        bool active = true,
        string credentialSource = "personal",
        bool allowed = true)
    {
        var source = credentialSource == "personal"
            ? "{\"type\":\"personal\"}"
            : $"{{\"type\":\"org\",\"org_id\":\"org-alpha\",\"org_name\":\"Org Alpha\",\"role\":\"member\",\"allowed\":{allowed.ToString().ToLowerInvariant()}}}";
        return $"{{\"id\":\"{id}\",\"slug\":\"{slug}\",\"label\":\"Example service\",\"catalog_service_name\":null,\"is_active\":{active.ToString().ToLowerInvariant()},\"credential_source\":{source}}}";
    }

    private sealed class InventoryHandler(string response) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var record = new RequestRecord(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Parameter ?? string.Empty);
            Requests.Add(record);
            if (record.Path != "/api/v1/keys")
                throw new InvalidOperationException($"Unexpected explicit admission request: {record.Path}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed record RequestRecord(string Path, string BearerToken);
}
