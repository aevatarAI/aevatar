using System.Text.Json;
using Aevatar.Tools.Cli.Commands;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ConfigCliExecutionTests
{
    // ─── Result factory methods ───

    [Fact]
    public void Ok_ShouldReturnSuccessResult()
    {
        var result = ConfigCliExecution.Ok("done", data: 42);

        result.Ok.Should().BeTrue();
        result.Code.Should().Be("OK");
        result.Message.Should().Be("done");
        result.ExitCode.Should().Be(0);
        result.Data.Should().Be(42);
    }

    [Fact]
    public void InvalidArgument_ShouldReturnExitCode2()
    {
        var result = ConfigCliExecution.InvalidArgument("bad arg");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("INVALID_ARGUMENT");
        result.ExitCode.Should().Be(2);
    }

    [Fact]
    public void NotFound_ShouldReturnExitCode3()
    {
        var result = ConfigCliExecution.NotFound("missing");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("NOT_FOUND");
        result.ExitCode.Should().Be(3);
    }

    [Fact]
    public void ValidationFailed_ShouldReturnExitCode4()
    {
        var result = ConfigCliExecution.ValidationFailed("invalid format");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("VALIDATION_FAILED");
        result.ExitCode.Should().Be(4);
    }

    [Fact]
    public void IoError_ShouldReturnExitCode5()
    {
        var result = ConfigCliExecution.IoError("disk full");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("IO_ERROR");
        result.ExitCode.Should().Be(5);
    }

    [Fact]
    public void ExternalProbeFailed_ShouldReturnExitCode6()
    {
        var result = ConfigCliExecution.ExternalProbeFailed("timeout");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("EXTERNAL_PROBE_FAILED");
        result.ExitCode.Should().Be(6);
    }

    [Fact]
    public void Unexpected_ShouldReturnExitCode1()
    {
        var result = ConfigCliExecution.Unexpected("boom");

        result.Ok.Should().BeFalse();
        result.Code.Should().Be("UNEXPECTED_ERROR");
        result.ExitCode.Should().Be(1);
    }

    // ─── ExecuteAsync exception mapping ───

    [Fact]
    public async Task ExecuteAsync_SuccessAction_ShouldReturnZero()
    {
        var exitCode = await ConfigCliExecution.ExecuteAsync(
            asJson: false, quiet: true,
            _ => Task.FromResult(ConfigCliExecution.Ok("ok")),
            CancellationToken.None);

        exitCode.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(ExceptionMappingCases))]
    public async Task ExecuteAsync_ShouldMapExceptionToCorrectExitCode(
        Exception exception, int expectedExitCode)
    {
        var exitCode = await ConfigCliExecution.ExecuteAsync(
            asJson: false, quiet: true,
            _ => throw exception,
            CancellationToken.None);

        exitCode.Should().Be(expectedExitCode);
    }

    public static TheoryData<Exception, int> ExceptionMappingCases => new()
    {
        { new ArgumentException("bad"), 2 },
        { new JsonException("parse error"), 4 },
        { new FormatException("bad format"), 4 },
        { new FileNotFoundException("missing"), 3 },
        { new DirectoryNotFoundException("missing dir"), 3 },
        { new KeyNotFoundException("no key"), 3 },
        { new InvalidOperationException("invalid op"), 4 },
        { new HttpRequestException("network"), 6 },
        { new TaskCanceledException("timeout"), 6 },
        { new UnauthorizedAccessException("denied"), 5 },
        { new IOException("io fail"), 5 },
        { new NullReferenceException("unexpected"), 1 },
    };

    // ─── ResolveInputValueAsync ───

    [Fact]
    public async Task ResolveInputValueAsync_WithExplicitValue_ShouldReturnTrimmed()
    {
        var value = await ConfigCliExecution.ResolveInputValueAsync(
            explicitValue: "  hello  ",
            readFromStdin: false,
            valueName: "test");

        value.Should().Be("hello");
    }

    [Fact]
    public async Task ResolveInputValueAsync_BothExplicitAndStdin_ShouldThrow()
    {
        var act = () => ConfigCliExecution.ResolveInputValueAsync(
            explicitValue: "value",
            readFromStdin: true,
            valueName: "test");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*cannot be set together with --stdin*");
    }

    [Fact]
    public async Task ResolveInputValueAsync_NeitherExplicitNorStdin_ShouldThrow()
    {
        var act = () => ConfigCliExecution.ResolveInputValueAsync(
            explicitValue: null,
            readFromStdin: false,
            valueName: "API key");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*API key*required*");
    }

    [Fact]
    public async Task ResolveInputValueAsync_WhitespaceExplicit_ShouldThrow()
    {
        var act = () => ConfigCliExecution.ResolveInputValueAsync(
            explicitValue: "   ",
            readFromStdin: false,
            valueName: "test");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*required*");
    }

    // ─── ConfirmOrThrow ───

    [Fact]
    public void ConfirmOrThrow_WithYesFlag_ShouldReturnTrue()
    {
        var result = ConfigCliExecution.ConfirmOrThrow(yes: true, "delete item");

        result.Should().BeTrue();
    }

    // ─── ConfigCliResult record equality ───

    [Fact]
    public void ConfigCliResult_WithSameValues_ShouldBeEqual()
    {
        var a = new ConfigCliResult(true, "OK", "done", 0, null);
        var b = new ConfigCliResult(true, "OK", "done", 0, null);

        a.Should().Be(b);
    }

    [Fact]
    public void ConfigCliResult_WithDifferentValues_ShouldNotBeEqual()
    {
        var a = ConfigCliExecution.Ok("done");
        var b = ConfigCliExecution.NotFound("missing");

        a.Should().NotBe(b);
    }
}
