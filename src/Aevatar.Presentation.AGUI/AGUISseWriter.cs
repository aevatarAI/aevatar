// ─────────────────────────────────────────────────────────────
// AGUISseWriter — SSE 事件序列化写入器
// 将 AGUIEvent 序列化为 JSON 并写入 HTTP Response Body
// 格式: data: {json}\n\n
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// 向 HTTP Response 写入 SSE 格式的 AG-UI 事件。
/// 每个事件序列化为 JSON（camelCase），以 data: 前缀写出。
/// </summary>
public sealed class AGUISseWriter : IAsyncDisposable
{
    private readonly HttpResponse _response;
    private readonly JsonSerializerOptions _json;

    public AGUISseWriter(HttpResponse response, JsonSerializerOptions? json = null)
    {
        _response = response;
        _json = json ?? DefaultJsonOptions;
    }

    public async Task WriteAsync(AGUIEvent evt, CancellationToken ct)
    {
        if (evt == null) return;

        // 用运行时类型序列化，保留派生类字段
        var payload = JsonSerializer.Serialize((object)evt, evt.GetType(), _json);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
