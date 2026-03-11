// ─────────────────────────────────────────────────────────────
// AGUISseWriter — SSE 事件序列化写入器
// 将 AGUIEvent 序列化为 JSON 并写入 HTTP Response Body
// 格式: data: {json}\n\n
// ─────────────────────────────────────────────────────────────

using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// 向 HTTP Response 写入 SSE 格式的 AG-UI 事件。
/// 每个事件序列化为 JSON（camelCase），以 data: 前缀写出。
/// </summary>
public sealed class AGUISseWriter : IAsyncDisposable
{
    private static readonly TypeRegistry DefaultTypeRegistry = TypeRegistry.FromFiles(
        AGUIEvent.Descriptor.File,
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
