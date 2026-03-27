using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class AppStudioEndpointsTests
{
    [Fact]
    public void NormalizeStudioDocumentId_ShouldSlugifyReadableNames()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            " Customer Support Workflow 2026 ",
            "workflow");

        result.Should().Be("customer-support-workflow-2026");
    }

    [Fact]
    public void NormalizeStudioDocumentId_WhenInputIsBlank_ShouldUseFallbackPrefix()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            "   ",
            "script");

        result.Should().StartWith("script-");
    }

    [Fact]
    public void AppScriptProtocol_ShouldRoundTripStringsAndLists()
    {
        var state = AppScriptProtocol.CreateState(
            input: "hello",
            output: "HELLO",
            status: "ok",
            lastCommandId: "command-1",
            notes: ["trimmed", "uppercased"]);

        AppScriptProtocol.GetString(state, AppScriptProtocol.InputField).Should().Be("hello");
        AppScriptProtocol.GetString(state, AppScriptProtocol.OutputField).Should().Be("HELLO");
        AppScriptProtocol.GetString(state, AppScriptProtocol.StatusField).Should().Be("ok");
        AppScriptProtocol.GetString(state, AppScriptProtocol.LastCommandIdField).Should().Be("command-1");
        AppScriptProtocol.GetStringList(state, AppScriptProtocol.NotesField).Should().Equal("trimmed", "uppercased");
    }
}
