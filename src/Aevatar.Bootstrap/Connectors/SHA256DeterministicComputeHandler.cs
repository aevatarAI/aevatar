using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Bootstrap.Connectors;

/// <summary>Computes a lowercase SHA-256 digest over the UTF-8 bytes of an input string.</summary>
public sealed class SHA256DeterministicComputeHandler : IDeterministicComputeHandler
{
    public const string HandlerName = "deterministic_compute";
    public const string OperationId = "sha256_utf8";
    public const int Version = 1;

    private const string InputSchemaDigest =
        "sha256:54669b5e6a1bfebb4d15788d41b5cd5fb8e51fc2d982eb2383a42262c748c90a";
    private const string OutputSchemaDigest =
        "sha256:6ece1c260f47c45b60dcac33ad9b45ad37e52e71a09c7f9924478d4c4b347852";

    private static readonly IReadOnlyList<DeterministicAlgorithmDescriptor> AlgorithmDescriptors =
    [
        new(OperationId, Version, InputSchemaDigest, OutputSchemaDigest),
    ];

    public string Name => HandlerName;

    public IReadOnlyList<DeterministicAlgorithmDescriptor> Algorithms => AlgorithmDescriptors;

    // Implement (issue #3526):
    //   Behavior: Execute one versioned, pure UTF-8 SHA-256 conversion with a schema-checked JSON boundary.
    //   Why this shape: The host callback stays deterministic and reusable without adding a workflow primitive or deployment unit.
    public Task<HostCallbackConnectorResponse> HandleAsync(
        HostCallbackConnectorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(request.Operation, OperationId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Failure($"unsupported deterministic algorithm '{request.Operation}'"));

        if (!TryReadText(request.Payload, out var text, out var error))
            return Task.FromResult(Failure(error));

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return Task.FromResult(new HostCallbackConnectorResponse
        {
            Success = true,
            Result = new JsonObject
            {
                ["sha256"] = digest,
            },
        });
    }

    private static HostCallbackConnectorResponse Failure(string error) =>
        new()
        {
            Success = false,
            Error = error,
        };

    private static bool TryReadText(string payload, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payload schema violation: expected JSON object";
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 ||
                !string.Equals(properties[0].Name, "text", StringComparison.Ordinal) ||
                properties[0].Value.ValueKind != JsonValueKind.String)
            {
                error = "payload schema violation: expected exactly one string property 'text'";
                return false;
            }

            text = properties[0].Value.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "payload schema violation: invalid JSON";
            return false;
        }
    }
}
