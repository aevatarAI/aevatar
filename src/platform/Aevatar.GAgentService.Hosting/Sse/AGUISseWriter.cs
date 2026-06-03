using System.Text;
using Aevatar.AGUI.Contracts;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Sse;

/// <summary>
/// Writes AG-UI events to an HTTP response as SSE frames.
/// </summary>
public sealed class AGUISseWriter : IAsyncDisposable
{
    private static readonly TypeRegistry DefaultTypeRegistry = TypeRegistry.FromFiles(
        AGUIEvent.Descriptor.File,
        GAgentDraftRunResultPayload.Descriptor.File,
        AnyReflection.Descriptor,
        StructReflection.Descriptor,
        WrappersReflection.Descriptor);

    private readonly HttpResponse _response;
    private readonly JsonFormatter _jsonFormatter;

    public AGUISseWriter(HttpResponse response, TypeRegistry? typeRegistry = null)
    {
        _response = response;
        _jsonFormatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithFormatDefaultValues(false)
                .WithTypeRegistry(typeRegistry ?? DefaultTypeRegistry));
    }

    public async Task WriteAsync(AGUIEvent evt, CancellationToken ct)
    {
        if (evt == null) return;

        var payload = _jsonFormatter.Format(evt);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
