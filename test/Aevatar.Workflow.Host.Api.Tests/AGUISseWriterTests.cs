using System.Text.Json;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AGUISseWriterTests
{
    [Fact]
    public async Task WriteAsync_WithEvent_ShouldEmitSseFrameUsingCamelCase()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(http.Response);
        await writer.WriteAsync(
            new AGUIEvent
            {
                Timestamp = 123,
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = "thread-1",
                    RunId = "run-1",
                    Result = Any.Pack(new StringValue { Value = "ok" }),
                },
            },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();

        text.Should().StartWith("data: ");
        text.Should().Contain("\n\n");

        var payload = text["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        root.GetProperty("runFinished").GetProperty("threadId").GetString().Should().Be("thread-1");
        root.GetProperty("runFinished").GetProperty("runId").GetString().Should().Be("run-1");
        root.GetProperty("runFinished").GetProperty("result").GetProperty("@type").GetString().Should().Contain("StringValue");
        root.GetProperty("runFinished").GetProperty("result").GetProperty("value").GetString().Should().Be("ok");
        ReadFlexibleInt64(root.GetProperty("timestamp")).Should().Be(123);
    }

    [Fact]
    public async Task WriteAsync_WithNullEvent_ShouldDoNothing()
    {
        var http = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        await using var writer = new AGUISseWriter(http.Response);
        AGUIEvent? evt = null;
        await writer.WriteAsync(evt!, CancellationToken.None);

        http.Response.Body.Length.Should().Be(0);
    }

    private static long ReadFlexibleInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String => long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unexpected timestamp JSON kind: {value.ValueKind}"),
        };
    }
}
