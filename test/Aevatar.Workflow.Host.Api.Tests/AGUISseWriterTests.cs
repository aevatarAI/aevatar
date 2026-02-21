using System.Text.Json;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
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
            new RunFinishedEvent
            {
                ThreadId = "thread-1",
                RunId = "run-1",
                Result = new { FinalText = "ok" },
                Timestamp = 123,
            },
            CancellationToken.None);

        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();

        text.Should().StartWith("data: ");
        text.Should().Contain("\n\n");

        var payload = text["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("type").GetString().Should().Be("RUN_FINISHED");
        doc.RootElement.GetProperty("threadId").GetString().Should().Be("thread-1");
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
        doc.RootElement.GetProperty("timestamp").GetInt64().Should().Be(123);
        doc.RootElement.GetProperty("result").GetProperty("finalText").GetString().Should().Be("ok");
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
}
