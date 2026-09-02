using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdExternalWorkflowCapabilitySourceTests
{
    private const string CatalogDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AddNyxIdTools_ShouldRegisterNyxIdCapabilitySource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdTools(options => options.BaseUrl = "https://nyxid.invalid");

        services.Should().Contain(static descriptor =>
            descriptor.ServiceType == typeof(IExternalWorkflowCapabilitySource) &&
            descriptor.ImplementationType == typeof(NyxIdExternalWorkflowCapabilitySource));
    }

    [Fact]
    public async Task ListAsync_ShouldUseOnlyMcpConfigAndPreserveExactIdentities()
    {
        var handler = new CatalogHandler
        {
            Body = Config(
                Service("usvc-alpha", "shared-slug", Endpoint("endpoint-alpha")),
                Service("usvc-beta", "shared-slug", Endpoint("endpoint-beta"))),
        };
        var source = CreateSource(handler);

        var discovery = await source.ListAsync(Access(), CancellationToken.None);

        discovery.CandidateCount.Should().Be(2);
        discovery.RejectedCount.Should().Be(0);
        discovery.Diagnostics.Should().BeEmpty();
        discovery.Capabilities.Should().HaveCount(2);
        discovery.Capabilities.Select(static item =>
                (item.Selector.NyxIdOperation.UserServiceId, item.Selector.NyxIdOperation.EndpointId))
            .Should().BeEquivalentTo(new[]
            {
                ("usvc-alpha", "endpoint-alpha"),
                ("usvc-beta", "endpoint-beta"),
            });
        discovery.Capabilities.Should().OnlyContain(static item =>
            item.Source.SourceKind == ExternalCapabilitySourceKind.NyxIdMcpConfig &&
            item.Source.SourceId == "nyxid-mcp-config:caller:nyx-user-alpha" &&
            item.Source.SourceVersion == 0 &&
            item.Source.ContentDigest == CatalogDigest);
        handler.Requests.Should().ContainSingle().Which.Should().Be(
            new RequestRecord("/api/v1/mcp/config", "runtime-caller-credential"));
    }

    [Fact]
    public async Task ListAsync_ShouldReadLiveCatalogOnEveryRequest()
    {
        var handler = new CatalogHandler { Body = Config(Service()) };
        var source = CreateSource(handler);

        await source.ListAsync(Access(), CancellationToken.None);
        await source.ListAsync(Access(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().OnlyContain(static request =>
            request.Path == "/api/v1/mcp/config");
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:ABCDEF")]
    [InlineData("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task ListAsync_ShouldRejectInvalidNyxIdCatalogDigest(string catalogDigest)
    {
        var config = JsonNode.Parse(Config(Service()))!.AsObject();
        config["catalog_digest"] = catalogDigest;
        var source = CreateSource(new CatalogHandler { Body = config.ToJsonString() });

        var result = await source.ListAsync(Access(), CancellationToken.None);

        result.Capabilities.Should().BeEmpty();
        result.Diagnostics.Should().ContainSingle().Which.Code.Should()
            .Be(ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable);
    }

    [Fact]
    public async Task InspectAsync_ShouldUseTypedTextResponseInsteadOfFreeFormDescription()
    {
        var endpoint = Endpoint();
        endpoint["response_description"] = "application/pdf download";
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        result.SelectedSelector.NyxIdOperation.UserServiceId.Should().Be("usvc-alpha");
        result.SelectedSelector.NyxIdOperation.EndpointId.Should().Be("endpoint-alpha");
        var proof = result.SelectedCapability.NyxIdUserService;
        proof.UserServiceId.Should().Be("usvc-alpha");
        proof.ServiceSlugSnapshot.Should().Be("shared-slug");
        proof.EndpointId.Should().Be("endpoint-alpha");
        proof.HttpMethod.Should().Be("GET");
        proof.PathTemplate.Should().Be("/items/{item_id}");
        proof.ContractDigest.Should().NotBeNullOrWhiteSpace();
        proof.Parameters.Should().ContainSingle(parameter =>
            parameter.Name == "item_id" &&
            parameter.Location == NyxIdOperationParameterLocation.Path &&
            parameter.Required);
        proof.ResponsePolicy.TextAllowed.Should().BeTrue();
        proof.ResponsePolicy.FileArtifactAllowed.Should().BeFalse();
        proof.ResponsePolicy.MediaTypes.Should().Equal("application/json");
        result.Sources.Should().ContainSingle().Which.SourceKind.Should()
            .Be(ExternalCapabilitySourceKind.NyxIdMcpConfig);
    }

    [Fact]
    public async Task InspectAsync_ShouldBuildFileArtifactProofFromTypedBinaryResponse()
    {
        var endpoint = Endpoint();
        endpoint["response"] = Response(true, "application/pdf", "application/octet-stream");
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        var policy = result.SelectedCapability.NyxIdUserService.ResponsePolicy;
        policy.TextAllowed.Should().BeFalse();
        policy.FileArtifactAllowed.Should().BeTrue();
        policy.MediaTypes.Should().Equal("application/octet-stream", "application/pdf");
    }

    [Fact]
    public async Task ListAsync_ShouldRejectTypedBinaryResponseForNonGetOperation()
    {
        var endpoint = Endpoint(method: "POST", path: "/items");
        endpoint["response"] = Response(true, "application/octet-stream");
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var result = await source.ListAsync(Access(), CancellationToken.None);

        result.Capabilities.Should().BeEmpty();
        result.RejectedCount.Should().Be(1);
        result.Diagnostics.Should().ContainSingle(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedResponse);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid_content_types")]
    [InlineData("invalid_binary_flag")]
    [InlineData("invalid_media_type")]
    public async Task ListAsync_ShouldRejectMissingOrMalformedTypedResponse(string scenario)
    {
        var endpoint = Endpoint();
        switch (scenario)
        {
            case "missing":
                endpoint.Remove("response");
                break;
            case "invalid_content_types":
                endpoint["response"]!["content_types"] = "application/json";
                break;
            case "invalid_binary_flag":
                endpoint["response"]!["binary_artifact"] = "false";
                break;
            case "invalid_media_type":
                endpoint["response"]!["content_types"] = new JsonArray("bad\r\nmedia");
                break;
            default:
                throw new InvalidOperationException($"Unknown scenario: {scenario}");
        }
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var result = await source.ListAsync(Access(), CancellationToken.None);

        result.Capabilities.Should().BeEmpty();
        result.RejectedCount.Should().Be(1);
        result.Diagnostics.Should().ContainSingle(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedResponse);
    }

    [Theory]
    [InlineData("GET", NyxIdOperationRisk.ReadOnly, NyxIdOperationApproval.None, true, false)]
    [InlineData("POST", NyxIdOperationRisk.Write, NyxIdOperationApproval.Required, false, false)]
    [InlineData("DELETE", NyxIdOperationRisk.Destructive, NyxIdOperationApproval.Required, false, true)]
    public async Task InspectAsync_ShouldDeriveConservativeExecutionPolicy(
        string method,
        NyxIdOperationRisk risk,
        NyxIdOperationApproval approval,
        bool durableAllowed,
        bool destructive)
    {
        var endpoint = Endpoint(method: method);
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var discovery = await source.ListAsync(Access(), CancellationToken.None);
        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        discovery.Capabilities.Should().ContainSingle().Which.Destructive.Should().Be(destructive);
        var policy = result.SelectedCapability.NyxIdUserService.ExecutionPolicy;
        policy.Risk.Should().Be(risk);
        policy.Approval.Should().Be(approval);
        policy.EnforcementOwner.Should().Be(NyxIdOperationEnforcementOwner.Aevatar);
        policy.AllowedExecutionModes.Should().Contain(ExternalCapabilityExecutionMode.Interactive);
        policy.AllowedExecutionModes.Contains(ExternalCapabilityExecutionMode.Durable)
            .Should().Be(durableAllowed);
    }

    [Fact]
    public async Task InspectAsync_ShouldRequireExactServiceAndEndpointSelection()
    {
        var source = CreateSource(new CatalogHandler { Body = Config(Service()) });

        var result = await source.InspectAsync(
            Access(),
            Selector(endpointId: string.Empty),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.OperationSelectionRequired);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_SELECTION_REQUIRED");
    }

    [Fact]
    public async Task ListAsync_ShouldRejectPlatformAndGenericProxyServicesWithTypedDiagnostics()
    {
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(
                Service(),
                Service("platform-alpha", "platform", Endpoint("platform-endpoint"),
                    isUserService: false),
                Service("generic-alpha", "generic", Endpoint("generic-endpoint"),
                    isGenericProxy: true)),
        });

        var result = await source.ListAsync(Access(), CancellationToken.None);

        result.CandidateCount.Should().Be(3);
        result.RejectedCount.Should().Be(2);
        result.Capabilities.Should().ContainSingle().Which.Selector.NyxIdOperation.UserServiceId
            .Should().Be("usvc-alpha");
        result.Diagnostics.Select(static item => item.Code).Should().BeEquivalentTo(new[]
        {
            ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService,
            ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected,
        });
    }

    [Fact]
    public async Task ListAsync_ShouldRejectMissingAndDuplicateServiceIdentity()
    {
        var missingSource = CreateSource(new CatalogHandler
        {
            Body = Config(Service(null, "missing", Endpoint())),
        });
        var duplicateSource = CreateSource(new CatalogHandler
        {
            Body = Config(
                Service("usvc-alpha", "one", Endpoint("endpoint-one")),
                Service("usvc-alpha", "two", Endpoint("endpoint-two"))),
        });

        var missing = await missingSource.ListAsync(Access(), CancellationToken.None);
        var duplicate = await duplicateSource.ListAsync(Access(), CancellationToken.None);

        missing.Capabilities.Should().BeEmpty();
        missing.RejectedCount.Should().Be(1);
        missing.Diagnostics.Should().Contain(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity);
        duplicate.Capabilities.Should().BeEmpty();
        duplicate.RejectedCount.Should().Be(2);
        duplicate.Diagnostics.Should().Contain(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousServiceIdentity &&
            item.Count == 2);
    }

    [Fact]
    public async Task ListAsync_ShouldRejectMissingAndDuplicateEndpointIdentity()
    {
        var missingSource = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [Endpoint(null)])),
        });
        var duplicateSource = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints:
            [
                Endpoint("endpoint-alpha", path: "/one"),
                Endpoint("endpoint-alpha", path: "/two"),
            ])),
        });

        var missing = await missingSource.ListAsync(Access(), CancellationToken.None);
        var duplicate = await duplicateSource.ListAsync(Access(), CancellationToken.None);

        missing.Capabilities.Should().BeEmpty();
        missing.RejectedCount.Should().Be(1);
        missing.Diagnostics.Should().Contain(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity);
        duplicate.Capabilities.Should().BeEmpty();
        duplicate.RejectedCount.Should().Be(2);
        duplicate.Diagnostics.Should().Contain(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousEndpointIdentity &&
            item.Count == 2);
    }

    [Fact]
    public async Task ListAsync_ShouldRejectCaseInsensitiveDuplicateHeaderIdentity()
    {
        var endpoint = Endpoint();
        endpoint["parameters"] = new JsonArray(
            Parameter("Accept", "header", false),
            Parameter("accept", "header", false));
        var source = CreateSource(new CatalogHandler
        {
            Body = Config(Service(endpoints: [endpoint])),
        });

        var discovery = await source.ListAsync(Access(), CancellationToken.None);

        discovery.Capabilities.Should().BeEmpty();
        discovery.RejectedCount.Should().Be(1);
        discovery.Diagnostics.Should().Contain(item =>
            item.Code == ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter);
    }

    [Theory]
    [MemberData(nameof(UnsupportedEndpointCases))]
    public async Task Admission_ShouldFailClosedForUnsupportedEndpointContracts(
        string config,
        ExternalCapabilityDiscoveryDiagnosticCode diagnosticCode,
        string blockerCode)
    {
        var source = CreateSource(new CatalogHandler { Body = config });

        var discovery = await source.ListAsync(Access(), CancellationToken.None);
        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        discovery.Capabilities.Should().BeEmpty();
        discovery.RejectedCount.Should().Be(1);
        discovery.Diagnostics.Should().Contain(item => item.Code == diagnosticCode);
        readiness.Status.Should().Be(ExternalCapabilityReadinessStatus.EndpointContractRequired);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be(blockerCode);
    }

    [Fact]
    public async Task ListAsync_ShouldUseOnlyTheCallerCatalogWhenOrganizationCredentialDiffers()
    {
        var handler = new CatalogHandler
        {
            ResponsesByBearerToken =
            {
                ["runtime-caller-credential"] = new CatalogResponse(
                    HttpStatusCode.OK, Config(Service())),
                ["runtime-organization-credential"] = new CatalogResponse(
                    HttpStatusCode.OK, Config(Service("usvc-beta", "other", Endpoint()))),
            },
        };
        var source = CreateSource(handler);

        var result = await source.ListAsync(AccessWithOrganization(), CancellationToken.None);

        result.CandidateCount.Should().Be(1);
        result.RejectedCount.Should().Be(0);
        result.Capabilities.Should().ContainSingle();
        result.Diagnostics.Should().BeEmpty();
        handler.Requests.Should().ContainSingle().Which.BearerToken.Should()
            .Be("runtime-caller-credential");
    }

    [Fact]
    public async Task InspectAsync_ShouldUseCallerCatalogWhenOrganizationCredentialWouldFail()
    {
        var handler = new CatalogHandler
        {
            ResponsesByBearerToken =
            {
                ["runtime-caller-credential"] = new CatalogResponse(
                    HttpStatusCode.OK, Config(Service())),
                ["runtime-organization-credential"] = new CatalogResponse(
                    HttpStatusCode.ServiceUnavailable, "unavailable"),
            },
        };
        var source = CreateSource(handler);

        var result = await source.InspectAsync(
            AccessWithOrganization(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        handler.Requests.Should().ContainSingle().Which.BearerToken.Should()
            .Be("runtime-caller-credential");
    }

    [Theory]
    [InlineData(false, ExternalCapabilityReadinessStatus.SourceStale, "NYXID_SOURCE_UNAVAILABLE")]
    [InlineData(true, ExternalCapabilityReadinessStatus.ServiceAccessDenied, "NYXID_CALLER_ACCESS_REQUIRED")]
    public async Task InspectAsync_ShouldMapMalformedAndDeniedCatalogsToTypedFailure(
        bool denied,
        ExternalCapabilityReadinessStatus status,
        string blockerCode)
    {
        var handler = denied
            ? new CatalogHandler
            {
                DefaultResponse = new CatalogResponse(HttpStatusCode.Forbidden, "denied"),
            }
            : new CatalogHandler { Body = "{ malformed" };
        var source = CreateSource(handler);

        var discovery = await source.ListAsync(Access(), CancellationToken.None);
        var readiness = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Interactive,
            CancellationToken.None);

        discovery.Capabilities.Should().BeEmpty();
        discovery.Diagnostics.Should().ContainSingle().Which.Code.Should()
            .Be(ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable);
        readiness.Status.Should().Be(status);
        readiness.Blockers.Should().ContainSingle().Which.Code.Should().Be(blockerCode);
    }

    [Fact]
    public async Task InspectAsync_ShouldRejectDurableWriteBeforeAuthorizationCatalogRead()
    {
        var endpoint = Endpoint(method: "POST");
        var queryPort = new RecordingCatalogQueryPort(ReadyCatalogSnapshot());
        var source = CreateSource(
            new CatalogHandler { Body = Config(Service(endpoints: [endpoint])) },
            queryPort);

        var result = await source.InspectAsync(
            Access(),
            Selector(),
            ExternalCapabilityExecutionMode.Durable,
            CancellationToken.None);

        result.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        result.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("NYXID_OPERATION_DURABLE_EXECUTION_NOT_ALLOWED");
        queryPort.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task InspectAsync_ShouldRequireExactDurableAuthorizationGrant()
    {
        var accepted = CreateSource(
            new CatalogHandler { Body = Config(Service()) },
            new RecordingCatalogQueryPort(ReadyCatalogSnapshot()));
        var rejected = CreateSource(
            new CatalogHandler { Body = Config(Service()) },
            new RecordingCatalogQueryPort(ReadyCatalogSnapshot("usvc-beta")));

        var acceptedResult = await accepted.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);
        var rejectedResult = await rejected.InspectAsync(
            Access(), Selector(), ExternalCapabilityExecutionMode.Durable, CancellationToken.None);

        acceptedResult.Status.Should().Be(ExternalCapabilityReadinessStatus.Ready);
        acceptedResult.Sources.Select(static source => source.SourceKind).Should().BeEquivalentTo(new[]
        {
            ExternalCapabilitySourceKind.NyxIdMcpConfig,
            ExternalCapabilitySourceKind.DurableAuthorizationCatalog,
        });
        rejectedResult.Status.Should().Be(ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable);
        rejectedResult.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be("DURABLE_AUTHORIZATION_UNAVAILABLE");
    }

    public static TheoryData<string, ExternalCapabilityDiscoveryDiagnosticCode, string> UnsupportedEndpointCases
    {
        get
        {
            var cookie = Endpoint();
            cookie["parameters"] = new JsonArray(Parameter("session", "cookie", true));

            var sensitiveHeader = Endpoint();
            sensitiveHeader["parameters"] = new JsonArray(Parameter("Authorization", "header", true));

            var contentTypeHeader = Endpoint(path: "/items");
            contentTypeHeader["parameters"] = new JsonArray(Parameter("Content-Type", "header", true));

            var unsatisfiableAcceptHeader = Endpoint(path: "/items");
            unsatisfiableAcceptHeader["parameters"] = new JsonArray(Parameter(
                "Accept",
                "header",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("text/plain"),
                }));

            var unsupportedBody = Endpoint(method: "POST");
            unsupportedBody["request_body_schema"] = new JsonObject { ["type"] = "object" };
            unsupportedBody["request_content_type"] = "text/plain";
            unsupportedBody["request_body_required"] = true;

            var unsupportedSchema = Endpoint();
            unsupportedSchema["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "string" },
                        new JsonObject { ["type"] = "integer" }),
                }));

            var malformedSchema = Endpoint();
            malformedSchema["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject { ["type"] = 7 }));

            var malformedRequired = Endpoint();
            malformedRequired["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["value"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray(JsonValue.Create(7)),
                }));

            var unsupportedConstraint = Endpoint();
            unsupportedConstraint["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[A-Z]+$",
                }));

            var emptyEnum = Endpoint();
            emptyEnum["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(),
                }));

            var nullEnum = Endpoint();
            nullEnum["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray((JsonNode?)null),
                }));

            var arrayParameter = Endpoint(path: "/items");
            arrayParameter["parameters"] = new JsonArray(Parameter(
                "item_ids",
                "query",
                false,
                new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                }));

            var styledParameter = Endpoint(path: "/items");
            var deepObject = Parameter("filter", "query", false);
            deepObject["style"] = "deepObject";
            styledParameter["parameters"] = new JsonArray(deepObject);

            var malformedParameterRequired = Endpoint();
            var malformedRequiredParameter = Parameter("item_id", "path", true);
            malformedRequiredParameter["required"] = "true";
            malformedParameterRequired["parameters"] = new JsonArray(malformedRequiredParameter);

            var missingBodyRequired = Endpoint();
            missingBodyRequired.Remove("request_body_required");

            var malformedBodyRequired = Endpoint();
            malformedBodyRequired["request_body_required"] = "false";

            var mismatchedStringEnum = Endpoint();
            mismatchedStringEnum["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(JsonValue.Create(7)),
                }));

            var mismatchedIntegerEnum = Endpoint();
            mismatchedIntegerEnum["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "integer",
                    ["enum"] = new JsonArray("7"),
                }));

            var untypedNumericEnum = Endpoint();
            untypedNumericEnum["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["enum"] = new JsonArray(JsonValue.Create(7)),
                }));

            var objectEnum = Endpoint(method: "POST", path: "/items");
            objectEnum["request_body_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["enum"] = new JsonArray("ignored-at-runtime"),
            };
            objectEnum["request_content_type"] = "application/json";
            objectEnum["request_body_required"] = true;

            var arrayEnum = Endpoint(method: "POST", path: "/items");
            arrayEnum["request_body_schema"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["enum"] = new JsonArray("ignored-at-runtime"),
            };
            arrayEnum["request_content_type"] = "application/json";
            arrayEnum["request_body_required"] = true;

            var arrayWithoutItems = Endpoint(method: "POST", path: "/items");
            arrayWithoutItems["request_body_schema"] = new JsonObject { ["type"] = "array" };
            arrayWithoutItems["request_content_type"] = "application/json";
            arrayWithoutItems["request_body_required"] = true;

            var malformedPathTemplate = Endpoint(path: "/items/{item_id");
            var undeclaredPathPlaceholder = Endpoint(path: "/items/{other_id}");
            var unusedPathParameter = Endpoint(path: "/items");
            unusedPathParameter["parameters"] = new JsonArray(Parameter("item_id", "path", true));
            var encodedTraversalPath = Endpoint(path: "/items/%2e%2e/secrets");
            var doubleEncodedTraversalPath = Endpoint(path: "/items/%252e%252e/secrets");
            var backslashPath = Endpoint(path: "/items\\secrets");

            var reservedQueryParameter = Endpoint(path: "/items");
            reservedQueryParameter["parameters"] = new JsonArray(
                Parameter("_NYXID_VIA", "query", false));

            var emptySchemaType = Endpoint();
            emptySchemaType["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject { ["type"] = " " }));

            var scalarWithObjectKeywords = Endpoint();
            scalarWithObjectKeywords["parameters"] = new JsonArray(Parameter(
                "item_id",
                "path",
                true,
                new JsonObject
                {
                    ["type"] = "string",
                    ["properties"] = new JsonObject
                    {
                        ["ignored"] = new JsonObject { ["type"] = "string" },
                    },
                }));

            var bodyWithoutMediaType = Endpoint(method: "POST", path: "/items");
            bodyWithoutMediaType["request_body_schema"] = new JsonObject { ["type"] = "object" };
            bodyWithoutMediaType["request_content_type"] = null;
            bodyWithoutMediaType["request_body_required"] = true;

            var getWithBody = Endpoint(method: "GET", path: "/items");
            getWithBody["request_body_schema"] = new JsonObject { ["type"] = "object" };
            getWithBody["request_content_type"] = "application/json";
            getWithBody["request_body_required"] = true;

            var unnormalizedRequired = Endpoint(method: "POST", path: "/items");
            unnormalizedRequired["request_body_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray(" value "),
            };
            unnormalizedRequired["request_content_type"] = "application/json";
            unnormalizedRequired["request_body_required"] = true;

            var undeclaredRequiredProperty = Endpoint(method: "POST", path: "/items");
            undeclaredRequiredProperty["request_body_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["value"] = new JsonObject { ["type"] = "string" },
                },
                ["required"] = new JsonArray("missing"),
            };
            undeclaredRequiredProperty["request_content_type"] = "application/json";
            undeclaredRequiredProperty["request_body_required"] = true;

            return new TheoryData<string, ExternalCapabilityDiscoveryDiagnosticCode, string>
            {
                { Config(Service(endpoints: [cookie])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [sensitiveHeader])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [contentTypeHeader])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [unsatisfiableAcceptHeader])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [unsupportedBody])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody, "NYXID_ENDPOINT_BODY_UNSUPPORTED" },
                { Config(Service(endpoints: [unsupportedSchema])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [malformedSchema])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [malformedRequired])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [unsupportedConstraint])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [emptyEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [nullEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [arrayParameter])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [styledParameter])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [malformedParameterRequired])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [missingBodyRequired])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody, "NYXID_ENDPOINT_BODY_UNSUPPORTED" },
                { Config(Service(endpoints: [malformedBodyRequired])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody, "NYXID_ENDPOINT_BODY_UNSUPPORTED" },
                { Config(Service(endpoints: [mismatchedStringEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [mismatchedIntegerEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [untypedNumericEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [objectEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [arrayEnum])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [arrayWithoutItems])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [malformedPathTemplate])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [undeclaredPathPlaceholder])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [unusedPathParameter])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [encodedTraversalPath])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [doubleEncodedTraversalPath])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [backslashPath])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [])), ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity, "NYXID_ENDPOINT_IDENTITY_INVALID" },
                { Config(Service(endpoints: [reservedQueryParameter])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter, "NYXID_ENDPOINT_PARAMETER_UNSUPPORTED" },
                { Config(Service(endpoints: [emptySchemaType])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [scalarWithObjectKeywords])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [bodyWithoutMediaType])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody, "NYXID_ENDPOINT_BODY_UNSUPPORTED" },
                { Config(Service(endpoints: [getWithBody])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody, "NYXID_ENDPOINT_BODY_UNSUPPORTED" },
                { Config(Service(endpoints: [unnormalizedRequired])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
                { Config(Service(endpoints: [undeclaredRequiredProperty])), ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema, "NYXID_ENDPOINT_SCHEMA_UNSUPPORTED" },
            };
        }
    }

    private static NyxIdExternalWorkflowCapabilitySource CreateSource(
        CatalogHandler handler,
        INyxIdAuthorizationCatalogQueryPort? queryPort = null)
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyxid.invalid" };
        return new NyxIdExternalWorkflowCapabilitySource(
            new NyxIdApiClient(options, new HttpClient(handler)),
            options,
            new FixedTimeProvider(),
            queryPort);
    }

    private static ExternalWorkflowCapabilityAccessContext Access() =>
        new(
            "scope-owner-alpha",
            "nyx-caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("runtime-caller-credential"));

    private static ExternalWorkflowCapabilityAccessContext AccessWithOrganization() =>
        new(
            "scope-owner-alpha",
            "nyx-caller-alpha",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("runtime-caller-credential"),
            "runtime-organization-credential");

    private static ExternalWorkflowCapabilitySelector Selector(
        string userServiceId = "usvc-alpha",
        string endpointId = "endpoint-alpha") =>
        new()
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = userServiceId,
                EndpointId = endpointId,
            },
        };

    private static string Config(params JsonObject[] services)
    {
        var serviceArray = new JsonArray(services.Select(static service => service.DeepClone()).ToArray());
        return new JsonObject
        {
            ["contract_version"] = "1.0",
            ["catalog_digest"] = CatalogDigest,
            ["user_id"] = "nyx-user-alpha",
            ["proxy_base_url"] = "https://nyxid.invalid/api/v1/proxy",
            ["services"] = serviceArray,
            ["total_services"] = services.Length,
            ["total_endpoints"] = services.Sum(static service =>
                service["endpoints"] is JsonArray endpoints ? endpoints.Count : 0),
        }.ToJsonString();
    }

    private static JsonObject Service(
        string? id = "usvc-alpha",
        string slug = "shared-slug",
        JsonObject? endpoint = null,
        bool isUserService = true,
        bool isGenericProxy = false,
        JsonObject[]? endpoints = null)
    {
        var values = endpoints ?? [endpoint ?? Endpoint()];
        var service = new JsonObject
        {
            ["service_name"] = id is null ? "Missing identity" : $"Service {id}",
            ["service_slug"] = slug,
            ["description"] = "Example service",
            ["service_category"] = isUserService ? "user_service" : "platform",
            ["is_user_service"] = isUserService,
            ["is_generic_proxy"] = isGenericProxy,
            ["endpoints"] = new JsonArray(values.Select(static value => value.DeepClone()).ToArray()),
        };
        if (id is not null)
            service["service_id"] = id;
        return service;
    }

    private static JsonObject Endpoint(
        string? id = "endpoint-alpha",
        string method = "GET",
        string path = "/items/{item_id}")
    {
        var endpoint = new JsonObject
        {
            ["name"] = id ?? "missing-endpoint",
            ["description"] = "Get one item",
            ["method"] = method,
            ["path"] = path,
            ["parameters"] = path.Contains("{item_id}", StringComparison.Ordinal)
                ? new JsonArray(Parameter("item_id", "path", true))
                : new JsonArray(),
            ["request_body_schema"] = null,
            ["request_content_type"] = null,
            ["request_body_required"] = false,
            ["response_description"] = "200 OK",
            ["response"] = Response(false, "application/json"),
        };
        if (id is not null)
            endpoint["endpoint_id"] = id;
        return endpoint;
    }

    private static JsonObject Response(bool? binaryArtifact, params string[] contentTypes) =>
        new()
        {
            ["content_types"] = new JsonArray(
                contentTypes.Select(static value => JsonValue.Create(value)).ToArray()),
            ["binary_artifact"] = binaryArtifact is null
                ? null
                : JsonValue.Create(binaryArtifact.Value),
        };

    private static JsonObject Parameter(
        string name,
        string location,
        bool required,
        JsonObject? schema = null) =>
        new()
        {
            ["name"] = name,
            ["in"] = location,
            ["required"] = required,
            ["schema"] = schema ?? new JsonObject { ["type"] = "string" },
            ["description"] = "Example parameter",
        };

    private static NyxIdAuthorizationCatalogSnapshot ReadyCatalogSnapshot(
        string userServiceId = "usvc-alpha")
    {
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = "nyx-caller-alpha",
        };
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = userServiceId,
            ServiceSlug = "shared-slug",
            DisplayName = "Example service",
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
            ResourceOwner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "nyx-caller-alpha",
            },
        };
        NyxIdAuthorizationServiceEvidence[] services = [service];
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            17,
            new DateTimeOffset(2026, 7, 29, 1, 59, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 29, 2, 5, 0, TimeSpan.Zero),
            "scope-plan-contract/v1",
            "scope-plan-policy/v1",
            new DateTimeOffset(2026, 7, 29, 1, 59, 0, TimeSpan.Zero),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services),
            services,
            Activated: true);
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public string Body { get; init; } = Config();

        public CatalogResponse? DefaultResponse { get; init; }

        public Dictionary<string, CatalogResponse> ResponsesByBearerToken { get; } =
            new(StringComparer.Ordinal);

        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            Requests.Add(new RequestRecord(path, token));
            if (!string.Equals(path, "/api/v1/mcp/config", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected workflow discovery request: {path}");

            var catalog = ResponsesByBearerToken.TryGetValue(token, out var configured)
                ? configured
                : DefaultResponse ?? new CatalogResponse(HttpStatusCode.OK, Body);
            return Task.FromResult(new HttpResponseMessage(catalog.Status)
            {
                Content = new StringContent(catalog.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public int ReadCount { get; private set; }

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 29, 2, 0, 0, TimeSpan.Zero);
    }

    private sealed record CatalogResponse(HttpStatusCode Status, string Body);

    private sealed record RequestRecord(string Path, string BearerToken);
}
