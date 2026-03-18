namespace Aevatar.App.Tests;

[Collection("AppHost")]
public sealed class AppScriptsStudioApiTests
{
    private readonly AppHostFixture _fixture;

    public AppScriptsStudioApiTests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScriptValidationEndpoint_ShouldAcceptValidSource_AndReportCompilerFailures()
    {
        using var valid = await _fixture.PostJsonAsync("/api/app/scripts/validate", new
        {
            scriptId = "smoke-script",
            scriptRevision = "draft-1",
            source = AppTestData.ValidScriptSource,
        });
        valid.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        valid.RootElement.GetProperty("errorCount").GetInt32().Should().Be(0);
        valid.RootElement.GetProperty("primarySourcePath").GetString().Should().NotBeNullOrWhiteSpace();

        using var invalid = await _fixture.PostJsonAsync("/api/app/scripts/validate", new
        {
            scriptId = "broken-script",
            scriptRevision = "draft-1",
            source = AppTestData.InvalidScriptSource,
        });
        invalid.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        invalid.RootElement.GetProperty("errorCount").GetInt32().Should().BeGreaterThan(0);
        invalid.RootElement.GetProperty("diagnostics")[0].GetProperty("severity").GetString().Should().Be("error");
        invalid.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ScriptDraftRun_ShouldRejectBlankSource_AndMaterializeRuntimeSnapshot()
    {
        using var rejected = await _fixture.PostJsonAsync("/api/app/scripts/draft-run", new
        {
            scriptId = "empty-script",
            scriptRevision = "draft-1",
            source = string.Empty,
            input = "ignored",
        }, HttpStatusCode.BadRequest);
        rejected.RootElement.GetProperty("code").GetString().Should().Be("SCRIPT_SOURCE_REQUIRED");

        using var draftRun = await _fixture.PostJsonAsync("/api/app/scripts/draft-run", new
        {
            scriptId = "smoke-script",
            scriptRevision = "draft-1",
            source = AppTestData.ValidScriptSource,
            input = "  hello world  ",
        });

        draftRun.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        var runtimeActorId = draftRun.RootElement.GetProperty("runtimeActorId").GetString();
        var readModelUrl = draftRun.RootElement.GetProperty("readModelUrl").GetString();
        runtimeActorId.Should().NotBeNullOrWhiteSpace();
        readModelUrl.Should().NotBeNullOrWhiteSpace();

        using var snapshot = await _fixture.WaitForJsonAsync(
            readModelUrl!,
            static document =>
            {
                var payloadJson = document.RootElement.GetProperty("readModelPayloadJson").GetString();
                if (string.IsNullOrWhiteSpace(payloadJson))
                    return false;

                using var payload = JsonDocument.Parse(payloadJson);
                return string.Equals(payload.RootElement.GetProperty("status").GetString(), "ok", StringComparison.Ordinal) &&
                       string.Equals(payload.RootElement.GetProperty("output").GetString(), "HELLO WORLD", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(30));

        snapshot.RootElement.GetProperty("actorId").GetString().Should().Be(runtimeActorId);
        snapshot.RootElement.GetProperty("scriptId").GetString().Should().Be("smoke-script");
        snapshot.RootElement.GetProperty("revision").GetString().Should().Be("draft-1");
        snapshot.RootElement.GetProperty("stateVersion").GetInt64().Should().BeGreaterThan(0);

        var readModelPayloadJson = snapshot.RootElement.GetProperty("readModelPayloadJson").GetString();
        readModelPayloadJson.Should().NotBeNullOrWhiteSpace();

        using var payloadDocument = JsonDocument.Parse(readModelPayloadJson!);
        payloadDocument.RootElement.GetProperty("input").GetString().Should().Be("  hello world  ");
        payloadDocument.RootElement.GetProperty("output").GetString().Should().Be("HELLO WORLD");
        payloadDocument.RootElement.GetProperty("status").GetString().Should().Be("ok");
        payloadDocument.RootElement.GetProperty("last_command_id").GetString().Should().NotBeNullOrWhiteSpace();
        payloadDocument.RootElement.GetProperty("notes").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal("trimmed", "uppercased");
    }
}
