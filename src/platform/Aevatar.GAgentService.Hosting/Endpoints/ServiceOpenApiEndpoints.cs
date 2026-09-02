using System.Text.Json;
using Aevatar.AI.ToolProviders.ServiceInvoke.Schema;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static partial class ServiceEndpoints
{
    private const string ServiceOpenApiJsonContentType = "application/json";
    private const string ServiceOpenApiTypeUrlPrefix = "type.googleapis.com/";

    public static RouteGroupBuilder MapGAgentServiceOpenApiEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{serviceId}/openapi.json", HandleGetServiceOpenApiAsync)
            .AllowAnonymous();
        return group;
    }

    private static async Task<IResult> HandleGetServiceOpenApiAsync(
        HttpContext http,
        string serviceId,
        [AsParameters] ServiceIdentityQuery query,
        [FromServices] IServiceIdentityContextResolver identityResolver,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        if (!ServiceIdentityEndpointAccess.TryResolveIdentity(
                identityResolver,
                query.TenantId,
                query.AppId,
                query.Namespace,
                serviceId,
                out var identity,
                out var denied))
        {
            return denied;
        }

        var catalog = await catalogReader.GetAsync(identity, ct);
        if (catalog == null)
            return Results.NotFound(new
            {
                code = "SERVICE_NOT_FOUND",
                message = $"Service '{serviceId}' was not found.",
            });

        var revisionCatalog = await revisionCatalogReader.GetAsync(identity, ct);
        var revision = ResolveOpenApiRevision(catalog, revisionCatalog);
        var descriptors = BuildDescriptorIndex(revision?.PreparedArtifact?.ProtocolDescriptorSet);
        var document = BuildOpenApiDocument(http, identity, catalog, revision, descriptors);
        return Results.Json(document, contentType: ServiceOpenApiJsonContentType);
    }

    private static ServiceRevisionSnapshot? ResolveOpenApiRevision(
        ServiceCatalogSnapshot catalog,
        ServiceRevisionCatalogSnapshot? revisionCatalog)
    {
        if (revisionCatalog == null || revisionCatalog.Revisions.Count == 0)
            return null;

        var revisionId = !string.IsNullOrWhiteSpace(catalog.ActiveServingRevisionId)
            ? catalog.ActiveServingRevisionId
            : catalog.DefaultServingRevisionId;

        return !string.IsNullOrWhiteSpace(revisionId)
            ? revisionCatalog.Revisions.FirstOrDefault(x => string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal))
            : revisionCatalog.Revisions
                .OrderByDescending(x => x.PublishedAt ?? x.PreparedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, MessageDescriptor> BuildDescriptorIndex(ByteString? descriptorSet)
    {
        if (descriptorSet == null || descriptorSet.IsEmpty)
            return new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);

        try
        {
            var fds = FileDescriptorSet.Parser.ParseFrom(descriptorSet);
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(fds.File.Select(x => x.ToByteString()));
            var index = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
            foreach (var file in fileDescriptors)
                IndexMessages(file.MessageTypes, index);

            return index;
        }
        catch (InvalidProtocolBufferException)
        {
            return new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        }
    }

    private static void IndexMessages(
        IEnumerable<MessageDescriptor> messages,
        IDictionary<string, MessageDescriptor> index)
    {
        foreach (var message in messages)
        {
            index[message.FullName] = message;
            IndexMessages(message.NestedTypes, index);
        }
    }

    private static Dictionary<string, object?> BuildOpenApiDocument(
        HttpContext http,
        ServiceIdentity identity,
        ServiceCatalogSnapshot catalog,
        ServiceRevisionSnapshot? revision,
        IReadOnlyDictionary<string, MessageDescriptor> descriptors)
    {
        var paths = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var endpoint in catalog.Endpoints.OrderBy(x => x.EndpointId, StringComparer.Ordinal))
        {
            var relativePath = $"/api/services/{Uri.EscapeDataString(identity.ServiceId)}/invoke/{Uri.EscapeDataString(endpoint.EndpointId)}";
            paths[relativePath] = new Dictionary<string, object?>
            {
                ["post"] = BuildOpenApiOperation(identity, catalog, endpoint, revision, descriptors),
            };
        }

        return new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = string.IsNullOrWhiteSpace(catalog.DisplayName) ? catalog.ServiceId : catalog.DisplayName,
                ["version"] = string.IsNullOrWhiteSpace(revision?.RevisionId) ? "current" : revision!.RevisionId,
            },
            ["servers"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["url"] = BuildServerUrl(http),
                },
            },
            ["paths"] = paths,
        };
    }

    private static Dictionary<string, object?> BuildOpenApiOperation(
        ServiceIdentity identity,
        ServiceCatalogSnapshot catalog,
        ServiceEndpointSnapshot endpoint,
        ServiceRevisionSnapshot? revision,
        IReadOnlyDictionary<string, MessageDescriptor> descriptors)
    {
        var requestSchema = ResolveJsonSchema(endpoint.RequestTypeUrl, descriptors);
        var operation = new Dictionary<string, object?>
        {
            ["operationId"] = BuildOpenApiOperationId(catalog.ServiceId, endpoint.EndpointId),
            ["summary"] = string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpoint.EndpointId : endpoint.DisplayName,
            ["description"] = endpoint.Description ?? string.Empty,
            ["x-aevatar-tool"] = new Dictionary<string, object?>
            {
                ["serviceKey"] = ServiceKeys.Build(identity),
                ["serviceId"] = catalog.ServiceId,
                ["endpointId"] = endpoint.EndpointId,
                ["endpointKind"] = endpoint.Kind,
                ["revisionId"] = revision?.RevisionId ?? string.Empty,
                ["requestTypeUrl"] = endpoint.RequestTypeUrl,
                ["responseTypeUrl"] = endpoint.ResponseTypeUrl,
            },
            ["requestBody"] = new Dictionary<string, object?>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = requestSchema,
                    },
                },
            },
            ["responses"] = new Dictionary<string, object?>
            {
                ["202"] = new Dictionary<string, object?>
                {
                    ["description"] = "Accepted for dispatch.",
                },
            },
        };

        return operation;
    }

    private static object ResolveJsonSchema(
        string requestTypeUrl,
        IReadOnlyDictionary<string, MessageDescriptor> descriptors)
    {
        var fullName = NormalizeOpenApiTypeUrl(requestTypeUrl);
        if (!string.IsNullOrWhiteSpace(fullName) &&
            descriptors.TryGetValue(fullName, out var descriptor))
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(ProtoToJsonSchemaConverter.Convert(descriptor))
                   ?? new Dictionary<string, object?> { ["type"] = "object" };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
        };
    }

    private static string NormalizeOpenApiTypeUrl(string typeUrl)
    {
        var value = typeUrl?.Trim() ?? string.Empty;
        return value.StartsWith(ServiceOpenApiTypeUrlPrefix, StringComparison.Ordinal)
            ? value[ServiceOpenApiTypeUrlPrefix.Length..]
            : value;
    }

    private static string BuildOpenApiOperationId(string serviceId, string endpointId)
    {
        var normalized = new string($"{serviceId}_{endpointId}"
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return string.Join('_', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildServerUrl(HttpContext http)
    {
        var request = http.Request;
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        return $"{request.Scheme}://{request.Host}{pathBase}";
    }
}
