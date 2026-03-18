using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.CommandLine;
using Aevatar.Tools.Cli.Commands;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public class AppConfigWorkflowIntegrationTests
{
    [Fact]
    public async Task ConfigUiEnsureCommand_WhenConfigUiIsHealthy_ShouldReturnUrlInJson()
    {
        var port = AllocateTcpPort();
        await using var app = await StartConfigHealthServerAsync(port);

        var root = new RootCommand();
        root.AddCommand(ConfigCommand.Create());
        var output = await CaptureStdOutAsync(() => root.InvokeAsync(
            [
                "config",
                "ui",
                "ensure",
                "--port",
                port.ToString(),
                "--json",
            ]));

        output.ExitCode.Should().Be(0);
        using var json = JsonDocument.Parse(output.StdOut);
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("data").GetProperty("url").GetString().Should().Be($"http://localhost:{port}");
        json.RootElement.GetProperty("data").GetProperty("started").GetBoolean().Should().BeFalse();
    }

    private static async Task<(int ExitCode, string StdOut)> CaptureStdOutAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = await action();
            return (exitCode, writer.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static int AllocateTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<WebApplication> StartConfigHealthServerAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();
        app.MapGet("/api/health", () => Results.Text("ok"));
        await app.StartAsync();
        return app;
    }

}
