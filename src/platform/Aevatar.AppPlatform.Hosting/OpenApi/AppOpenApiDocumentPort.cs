using System.Text.Json.Nodes;

namespace Aevatar.AppPlatform.Hosting.OpenApi;

internal sealed class AppOpenApiDocumentPort : IAppOpenApiDocumentPort
{
    public JsonObject BuildDocument(string serverUrl)
    {
        var document = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Aevatar AI Control Plane API",
                ["version"] = "2026-03-26",
                ["description"] = "AI-safe OpenAPI surface for app discovery, app mutation, function invocation, and operation observation.",
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = serverUrl,
                },
            },
        };

        document["paths"] = BuildPaths();
        document["components"] = BuildComponents();
        return document;
    }

    private static JsonObject BuildPaths() =>
        new()
        {
            ["/api/ai/openapi"] = new JsonObject
            {
                ["get"] = BuildOperation("Get AI-safe OpenAPI document", "getAiOpenApi", ["ai-bootstrap"], false),
            },
            ["/api/operations/{operationId}"] = new JsonObject
            {
                ["get"] = BuildOperation("Get operation snapshot", "getOperation", ["operations"], true, responseSchemaRef: "#/components/schemas/AppOperationSnapshot"),
            },
            ["/api/operations/{operationId}/result"] = new JsonObject
            {
                ["get"] = BuildOperation("Get operation result", "getOperationResult", ["operations"], true, responseSchemaRef: "#/components/schemas/AppOperationResult"),
            },
            ["/api/operations/{operationId}/events"] = new JsonObject
            {
                ["get"] = BuildOperation("List operation events", "listOperationEvents", ["operations"], true, responseSchemaRef: "#/components/schemas/AppOperationEventArray"),
            },
            ["/api/operations/{operationId}:stream"] = new JsonObject
            {
                ["get"] = BuildStreamReadOperation("Stream operation events", "streamOperation", ["operations"], true),
            },
            ["/api/apps"] = new JsonObject
            {
                ["get"] = BuildOperation("List apps", "listApps", ["apps"], true),
                ["post"] = BuildOperation("Create app", "createApp", ["apps"], true, requestSchemaRef: "#/components/schemas/CreateAppHttpRequest", responseSchemaRef: "#/components/schemas/AppDefinitionSnapshot", successStatusCode: "201"),
            },
            ["/api/apps/resolve"] = new JsonObject
            {
                ["get"] = BuildOperation("Resolve app route", "resolveAppRoute", ["apps"], true),
            },
            ["/api/apps/{appId}"] = new JsonObject
            {
                ["get"] = BuildOperation("Get app", "getApp", ["apps"], true, responseSchemaRef: "#/components/schemas/AppDefinitionSnapshot"),
                ["put"] = BuildOperation("Create or update app", "upsertApp", ["apps"], true, requestSchemaRef: "#/components/schemas/UpsertAppHttpRequest", responseSchemaRef: "#/components/schemas/AppDefinitionSnapshot"),
            },
            ["/api/apps/{appId}:default-release"] = new JsonObject
            {
                ["post"] = BuildOperation("Set app default release", "setDefaultRelease", ["apps"], true, requestSchemaRef: "#/components/schemas/SetDefaultReleaseHttpRequest", responseSchemaRef: "#/components/schemas/AppDefinitionSnapshot"),
            },
            ["/api/apps/{appId}/releases"] = new JsonObject
            {
                ["get"] = BuildOperation("List releases", "listReleases", ["releases"], true),
            },
            ["/api/apps/{appId}/releases/{releaseId}"] = new JsonObject
            {
                ["get"] = BuildOperation("Get release", "getRelease", ["releases"], true, responseSchemaRef: "#/components/schemas/AppReleaseSnapshot"),
                ["put"] = BuildOperation("Create or update release", "upsertRelease", ["releases"], true, requestSchemaRef: "#/components/schemas/UpsertReleaseHttpRequest", responseSchemaRef: "#/components/schemas/AppReleaseSnapshot"),
            },
            ["/api/apps/{appId}/releases/{releaseId}:publish"] = new JsonObject
            {
                ["post"] = BuildOperation("Publish release", "publishRelease", ["releases"], true, responseSchemaRef: "#/components/schemas/AppReleaseSnapshot"),
            },
            ["/api/apps/{appId}/releases/{releaseId}:archive"] = new JsonObject
            {
                ["post"] = BuildOperation("Archive release", "archiveRelease", ["releases"], true, responseSchemaRef: "#/components/schemas/AppReleaseSnapshot"),
            },
            ["/api/apps/{appId}/functions"] = new JsonObject
            {
                ["get"] = BuildOperation("List functions on the default release", "listFunctions", ["functions"], true),
            },
            ["/api/apps/{appId}/functions/{functionId}"] = new JsonObject
            {
                ["get"] = BuildOperation("Get function on the default release", "getFunction", ["functions"], true, responseSchemaRef: "#/components/schemas/AppFunctionDescriptor"),
            },
            ["/api/apps/{appId}/functions/{functionId}:invoke"] = new JsonObject
            {
                ["post"] = BuildOperation("Invoke function on the default release", "invokeFunction", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionInvokeHttpRequest", responseSchemaRef: "#/components/schemas/AppFunctionInvokeAcceptedReceipt", successStatusCode: "202"),
            },
            ["/api/apps/{appId}/functions/{functionId}:stream"] = new JsonObject
            {
                ["post"] = BuildStreamOperation("Stream workflow-backed function on the default release", "streamFunction", ["functions"], true, "#/components/schemas/FunctionStreamHttpRequest"),
            },
            ["/api/apps/{appId}/functions/{functionId}/runs:resume"] = new JsonObject
            {
                ["post"] = BuildOperation("Resume workflow-backed function run on the default release", "resumeFunctionRun", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionRunResumeHttpRequest"),
            },
            ["/api/apps/{appId}/functions/{functionId}/runs:stop"] = new JsonObject
            {
                ["post"] = BuildOperation("Stop workflow-backed function run on the default release", "stopFunctionRun", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionRunStopHttpRequest"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions"] = new JsonObject
            {
                ["get"] = BuildOperation("List functions on an explicit release", "listReleaseFunctions", ["functions"], true),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions/{functionId}"] = new JsonObject
            {
                ["get"] = BuildOperation("Get function on an explicit release", "getReleaseFunction", ["functions"], true, responseSchemaRef: "#/components/schemas/AppFunctionDescriptor"),
                ["put"] = BuildOperation("Create or update function binding", "upsertReleaseFunction", ["functions"], true, requestSchemaRef: "#/components/schemas/AppFunctionRefHttpRequest", responseSchemaRef: "#/components/schemas/AppEntryRef"),
                ["delete"] = BuildOperation("Delete function binding", "deleteReleaseFunction", ["functions"], true, successStatusCode: "204"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions/{functionId}:invoke"] = new JsonObject
            {
                ["post"] = BuildOperation("Invoke function on an explicit release", "invokeReleaseFunction", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionInvokeHttpRequest", responseSchemaRef: "#/components/schemas/AppFunctionInvokeAcceptedReceipt", successStatusCode: "202"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions/{functionId}:stream"] = new JsonObject
            {
                ["post"] = BuildStreamOperation("Stream workflow-backed function on an explicit release", "streamReleaseFunction", ["functions"], true, "#/components/schemas/FunctionStreamHttpRequest"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions/{functionId}/runs:resume"] = new JsonObject
            {
                ["post"] = BuildOperation("Resume workflow-backed function run on an explicit release", "resumeReleaseFunctionRun", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionRunResumeHttpRequest"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/functions/{functionId}/runs:stop"] = new JsonObject
            {
                ["post"] = BuildOperation("Stop workflow-backed function run on an explicit release", "stopReleaseFunctionRun", ["functions"], true, requestSchemaRef: "#/components/schemas/FunctionRunStopHttpRequest"),
            },
            ["/api/apps/{appId}/releases/{releaseId}/resources"] = new JsonObject
            {
                ["get"] = BuildOperation("Get release resources", "getReleaseResources", ["resources"], true, responseSchemaRef: "#/components/schemas/AppReleaseResourcesSnapshot"),
                ["put"] = BuildOperation("Replace release resources", "replaceReleaseResources", ["resources"], true, requestSchemaRef: "#/components/schemas/ReplaceResourcesHttpRequest", responseSchemaRef: "#/components/schemas/AppReleaseResourcesSnapshot"),
            },
            ["/api/apps/{appId}/routes"] = new JsonObject
            {
                ["get"] = BuildOperation("List routes", "listRoutes", ["routes"], true),
                ["put"] = BuildOperation("Create or update route", "upsertRoute", ["routes"], true, requestSchemaRef: "#/components/schemas/UpsertRouteHttpRequest", responseSchemaRef: "#/components/schemas/AppRouteSnapshot"),
                ["delete"] = BuildOperation("Delete route", "deleteRoute", ["routes"], true, successStatusCode: "204"),
            },
        };

    private static JsonObject BuildComponents() =>
        new()
        {
            ["securitySchemes"] = new JsonObject
            {
                ["bearerAuth"] = new JsonObject
                {
                    ["type"] = "http",
                    ["scheme"] = "bearer",
                    ["bearerFormat"] = "JWT",
                },
            },
            ["schemas"] = BuildSchemas(),
        };

    private static JsonObject BuildSchemas() =>
        new()
        {
            ["CreateAppHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["appId"] = BuildStringSchema(),
                    ["ownerScopeId"] = BuildStringSchema(),
                    ["displayName"] = BuildStringSchema(),
                    ["description"] = BuildStringSchema(),
                    ["visibility"] = BuildStringSchema(["private", "public"]),
                    ["defaultReleaseId"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "appId", "ownerScopeId" },
            },
            ["UpsertAppHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ownerScopeId"] = BuildStringSchema(),
                    ["displayName"] = BuildStringSchema(),
                    ["description"] = BuildStringSchema(),
                    ["visibility"] = BuildStringSchema(["private", "public"]),
                    ["defaultReleaseId"] = BuildStringSchema(),
                },
            },
            ["SetDefaultReleaseHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["releaseId"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "releaseId" },
            },
            ["AppServiceRefHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tenantId"] = BuildStringSchema(),
                    ["appId"] = BuildStringSchema(),
                    ["namespace"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["revisionId"] = BuildStringSchema(),
                    ["implementationKind"] = BuildStringSchema(["static", "scripting", "workflow"]),
                    ["role"] = BuildStringSchema(["entry", "companion", "internal"]),
                },
                ["required"] = new JsonArray { "serviceId" },
            },
            ["AppFunctionRefHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["functionId"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["endpointId"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "functionId", "serviceId", "endpointId" },
            },
            ["AppConnectorRefHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["resourceId"] = BuildStringSchema(),
                    ["connectorName"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "resourceId", "connectorName" },
            },
            ["AppSecretRefHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["resourceId"] = BuildStringSchema(),
                    ["secretName"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "resourceId", "secretName" },
            },
            ["UpsertReleaseHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["displayName"] = BuildStringSchema(),
                    ["status"] = BuildStringSchema(["draft", "published", "archived"]),
                    ["services"] = BuildArraySchema("#/components/schemas/AppServiceRefHttpRequest"),
                    ["functions"] = BuildArraySchema("#/components/schemas/AppFunctionRefHttpRequest"),
                    ["connectors"] = BuildArraySchema("#/components/schemas/AppConnectorRefHttpRequest"),
                    ["secrets"] = BuildArraySchema("#/components/schemas/AppSecretRefHttpRequest"),
                },
            },
            ["ReplaceResourcesHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["connectors"] = BuildArraySchema("#/components/schemas/AppConnectorRefHttpRequest"),
                    ["secrets"] = BuildArraySchema("#/components/schemas/AppSecretRefHttpRequest"),
                },
            },
            ["UpsertRouteHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["routePath"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["functionId"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "routePath", "releaseId", "functionId" },
            },
            ["FunctionInvokeBinaryPayloadHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["typeUrl"] = BuildStringSchema(),
                    ["payloadBase64"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Base64 encoded protobuf payload bytes.",
                    },
                },
                ["required"] = new JsonArray { "typeUrl" },
            },
            ["FunctionInvokeTypedPayloadHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["typeUrl"] = BuildStringSchema(),
                    ["payloadJson"] = BuildArbitraryJsonSchema("Protobuf JSON payload encoded with the target message schema."),
                },
                ["required"] = new JsonArray { "typeUrl", "payloadJson" },
            },
            ["FunctionInvokeHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["commandId"] = BuildStringSchema(),
                    ["correlationId"] = BuildStringSchema(),
                    ["binaryPayload"] = new JsonObject
                    {
                        ["$ref"] = "#/components/schemas/FunctionInvokeBinaryPayloadHttpRequest",
                    },
                    ["typedPayload"] = new JsonObject
                    {
                        ["$ref"] = "#/components/schemas/FunctionInvokeTypedPayloadHttpRequest",
                    },
                    ["callerServiceKey"] = BuildStringSchema(),
                    ["callerTenantId"] = BuildStringSchema(),
                    ["callerAppId"] = BuildStringSchema(),
                    ["callerScopeId"] = BuildStringSchema(),
                    ["callerSessionId"] = BuildStringSchema(),
                },
                ["oneOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["required"] = new JsonArray { "binaryPayload" },
                        ["not"] = new JsonObject
                        {
                            ["required"] = new JsonArray { "typedPayload" },
                        },
                    },
                    new JsonObject
                    {
                        ["required"] = new JsonArray { "typedPayload" },
                        ["not"] = new JsonObject
                        {
                            ["required"] = new JsonArray { "binaryPayload" },
                        },
                    },
                },
            },
            ["FunctionStreamHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["prompt"] = BuildStringSchema(),
                    ["sessionId"] = BuildStringSchema(),
                    ["headers"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = BuildStringSchema(),
                    },
                    ["eventFormat"] = BuildStringSchema(["workflow", "agui"]),
                },
                ["required"] = new JsonArray { "prompt" },
            },
            ["FunctionRunResumeHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["actorId"] = BuildStringSchema(),
                    ["runId"] = BuildStringSchema(),
                    ["stepId"] = BuildStringSchema(),
                    ["approved"] = new JsonObject
                    {
                        ["type"] = "boolean",
                    },
                    ["userInput"] = BuildStringSchema(),
                    ["commandId"] = BuildStringSchema(),
                    ["metadata"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = BuildStringSchema(),
                    },
                },
                ["required"] = new JsonArray { "actorId", "runId", "stepId", "approved" },
            },
            ["FunctionRunStopHttpRequest"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["actorId"] = BuildStringSchema(),
                    ["runId"] = BuildStringSchema(),
                    ["reason"] = BuildStringSchema(),
                    ["commandId"] = BuildStringSchema(),
                },
                ["required"] = new JsonArray { "actorId", "runId" },
            },
            ["AppDefinitionSnapshot"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["appId"] = BuildStringSchema(),
                    ["ownerScopeId"] = BuildStringSchema(),
                    ["displayName"] = BuildStringSchema(),
                    ["description"] = BuildStringSchema(),
                    ["visibility"] = BuildStringSchema(),
                    ["defaultReleaseId"] = BuildStringSchema(),
                    ["routePaths"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = BuildStringSchema(),
                    },
                },
            },
            ["AppReleaseSnapshot"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["releaseId"] = BuildStringSchema(),
                    ["appId"] = BuildStringSchema(),
                    ["displayName"] = BuildStringSchema(),
                    ["status"] = BuildStringSchema(),
                },
            },
            ["AppEntryRef"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["entryId"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["endpointId"] = BuildStringSchema(),
                },
            },
            ["AppRouteSnapshot"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["routePath"] = BuildStringSchema(),
                    ["appId"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["entryId"] = BuildStringSchema(),
                },
            },
            ["AppReleaseResourcesSnapshot"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["appId"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["connectorRefs"] = BuildArraySchema("#/components/schemas/AppConnectorRefHttpRequest"),
                    ["secretRefs"] = BuildArraySchema("#/components/schemas/AppSecretRefHttpRequest"),
                },
            },
            ["AppFunctionInvokeAcceptedReceipt"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["appId"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["functionId"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["endpointId"] = BuildStringSchema(),
                    ["requestId"] = BuildStringSchema(),
                    ["targetActorId"] = BuildStringSchema(),
                    ["commandId"] = BuildStringSchema(),
                    ["correlationId"] = BuildStringSchema(),
                    ["operationId"] = BuildStringSchema(),
                    ["statusUrl"] = BuildStringSchema(),
                    ["eventsUrl"] = BuildStringSchema(),
                    ["resultUrl"] = BuildStringSchema(),
                    ["streamUrl"] = BuildStringSchema(),
                },
            },
            ["AppFunctionDescriptor"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["functionId"] = BuildStringSchema(),
                    ["displayName"] = BuildStringSchema(),
                    ["description"] = BuildStringSchema(),
                    ["appId"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["endpointId"] = BuildStringSchema(),
                    ["endpointKind"] = BuildStringSchema(),
                    ["requestTypeUrl"] = BuildStringSchema(),
                    ["responseTypeUrl"] = BuildStringSchema(),
                },
            },
            ["AppOperationSnapshot"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operationId"] = BuildStringSchema(),
                    ["kind"] = BuildStringSchema(),
                    ["status"] = BuildStringSchema(),
                    ["appId"] = BuildStringSchema(),
                    ["releaseId"] = BuildStringSchema(),
                    ["functionId"] = BuildStringSchema(),
                    ["serviceId"] = BuildStringSchema(),
                    ["endpointId"] = BuildStringSchema(),
                    ["requestId"] = BuildStringSchema(),
                    ["targetActorId"] = BuildStringSchema(),
                    ["commandId"] = BuildStringSchema(),
                    ["correlationId"] = BuildStringSchema(),
                    ["createdAt"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["format"] = "date-time",
                    },
                },
            },
            ["AppOperationEvent"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operationId"] = BuildStringSchema(),
                    ["sequence"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["format"] = "uint64",
                    },
                    ["status"] = BuildStringSchema(),
                    ["eventCode"] = BuildStringSchema(),
                    ["message"] = BuildStringSchema(),
                    ["occurredAt"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["format"] = "date-time",
                    },
                },
            },
            ["AppOperationEventArray"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["$ref"] = "#/components/schemas/AppOperationEvent",
                },
            },
            ["AppOperationResult"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["operationId"] = BuildStringSchema(),
                    ["status"] = BuildStringSchema(),
                    ["resultCode"] = BuildStringSchema(),
                    ["message"] = BuildStringSchema(),
                    ["payload"] = BuildArbitraryJsonSchema("Optional protobuf JSON result payload."),
                    ["completedAt"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["format"] = "date-time",
                    },
                },
            },
            ["AuthChallengeRequired"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["code"] = BuildStringSchema(),
                    ["message"] = BuildStringSchema(),
                    ["requiredActions"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = BuildStringSchema(),
                    },
                },
            },
        };

    private static JsonObject BuildOperation(
        string summary,
        string operationId,
        IReadOnlyList<string> tags,
        bool requiresAuth,
        string? requestSchemaRef = null,
        string? responseSchemaRef = null,
        string successStatusCode = "200")
    {
        var operation = new JsonObject
        {
            ["summary"] = summary,
            ["operationId"] = operationId,
            ["tags"] = new JsonArray(tags.Select(static tag => JsonValue.Create(tag)).ToArray()),
            ["responses"] = BuildResponses(responseSchemaRef, successStatusCode),
        };

        if (requiresAuth)
        {
            operation["security"] = new JsonArray
            {
                new JsonObject
                {
                    ["bearerAuth"] = new JsonArray(),
                },
            };
            operation["x-aevatar-human-handoff"] = true;
        }

        if (!string.IsNullOrWhiteSpace(requestSchemaRef))
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject
                        {
                            ["$ref"] = requestSchemaRef,
                        },
                    },
                },
            };
        }

        return operation;
    }

    private static JsonObject BuildStreamOperation(
        string summary,
        string operationId,
        IReadOnlyList<string> tags,
        bool requiresAuth,
        string requestSchemaRef)
    {
        var operation = BuildOperation(
            summary,
            operationId,
            tags,
            requiresAuth,
            requestSchemaRef: requestSchemaRef,
            responseSchemaRef: null);
        operation["responses"] = BuildStreamResponses();
        return operation;
    }

    private static JsonObject BuildStreamReadOperation(
        string summary,
        string operationId,
        IReadOnlyList<string> tags,
        bool requiresAuth)
    {
        var operation = BuildOperation(
            summary,
            operationId,
            tags,
            requiresAuth,
            requestSchemaRef: null,
            responseSchemaRef: null);
        operation["responses"] = BuildStreamResponses();
        return operation;
    }

    private static JsonObject BuildResponses(string? responseSchemaRef, string successStatusCode)
    {
        var responses = new JsonObject
        {
            [successStatusCode] = new JsonObject
            {
                ["description"] = successStatusCode switch
                {
                    "201" => "Created",
                    "202" => "Accepted",
                    "204" => "No Content",
                    _ => "Success",
                },
            },
            ["401"] = new JsonObject
            {
                ["description"] = "Authentication challenge required",
                ["content"] = BuildSchemaContent("#/components/schemas/AuthChallengeRequired"),
            },
            ["403"] = new JsonObject
            {
                ["description"] = "Access denied",
                ["content"] = BuildSchemaContent("#/components/schemas/AuthChallengeRequired"),
            },
        };

        if (!string.Equals(successStatusCode, "204", StringComparison.Ordinal))
        {
            responses[successStatusCode]!["content"] = string.IsNullOrWhiteSpace(responseSchemaRef)
                ? new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "object",
                        },
                    },
                }
                : BuildSchemaContent(responseSchemaRef);
        }

        return responses;
    }

    private static JsonObject BuildStreamResponses() =>
        new()
        {
            ["200"] = new JsonObject
            {
                ["description"] = "Server-sent events stream",
                ["content"] = new JsonObject
                {
                    ["text/event-stream"] = new JsonObject
                    {
                        ["schema"] = new JsonObject
                        {
                            ["type"] = "string",
                        },
                    },
                },
            },
            ["401"] = new JsonObject
            {
                ["description"] = "Authentication challenge required",
                ["content"] = BuildSchemaContent("#/components/schemas/AuthChallengeRequired"),
            },
            ["403"] = new JsonObject
            {
                ["description"] = "Access denied",
                ["content"] = BuildSchemaContent("#/components/schemas/AuthChallengeRequired"),
            },
        };

    private static JsonObject BuildSchemaContent(string schemaRef) =>
        new()
        {
            ["application/json"] = new JsonObject
            {
                ["schema"] = new JsonObject
                {
                    ["$ref"] = schemaRef,
                },
            },
        };

    private static JsonObject BuildStringSchema(IEnumerable<string>? enumValues = null)
    {
        var schema = new JsonObject
        {
            ["type"] = "string",
        };

        if (enumValues != null)
            schema["enum"] = new JsonArray(enumValues.Select(static x => JsonValue.Create(x)).ToArray());

        return schema;
    }

    private static JsonObject BuildArraySchema(string itemSchemaRef) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["$ref"] = itemSchemaRef,
            },
        };

    private static JsonObject BuildArbitraryJsonSchema(string description) =>
        new()
        {
            ["description"] = description,
            ["type"] = new JsonArray
            {
                "object",
                "array",
                "string",
                "number",
                "integer",
                "boolean",
                "null",
            },
        };
}
