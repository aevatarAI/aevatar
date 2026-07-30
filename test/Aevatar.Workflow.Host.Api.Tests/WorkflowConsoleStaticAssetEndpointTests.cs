using System.Diagnostics;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowConsoleStaticAssetEndpointTests
{
    [Theory]
    [InlineData("observatory", "Workflow Run Observatory")]
    [InlineData("studio", "Workflow Studio")]
    public async Task WorkflowStaticShellEndpoints_ShouldRenderInjectedEmbeddedAssets(string endpoint, string marker)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildProvider(),
        };
        http.Response.Body = new MemoryStream();
        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();

        var result = endpoint == "observatory"
            ? WorkflowRunObservatoryEndpoints.GetObservatoryPage(http, assets)
            : WorkflowStudioEndpoints.GetStudioPage(http, assets);

        await result.ExecuteAsync(http);

        http.Response.ContentType.Should().Be("text/html; charset=utf-8");
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var html = await reader.ReadToEndAsync();
        html.Should().Contain(marker);
        html.Should().Contain("https://id.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().Contain("https://api.example.test/api/v1/proxy/s/aevatar");
        html.Should().Contain("searchParams.append(\"resource\"");
        html.Should().Contain("form.append(\"resource\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("https://nyx.chrono-ai.fun");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
        if (endpoint == "observatory")
        {
            html.Should().Contain("const url = CFG.nyxidApi + \"/api/v1/admin/users");
            html.Should().NotContain("const url = CFG.authority + \"/api/v1/admin/users");
            html.Should().Contain("\"aria-label\":\"完整 run id\"");
            html.Should().Contain("/api/workflow/observatory/admin/runs/");
            html.Should().Contain("detail.diagnostics");
            html.Should().NotContain("indexOf(\":run:\")");
        }
    }

    [Fact]
    public async Task WorkflowObservatory_ShouldOwnRouteStateAndOwnerOnlyApprovalActions()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = { URLSearchParams, encodeURIComponent };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('readObservatoryRoute', 'writeObservatoryRoute')}
              ${functionSource('runDetailRequestPath', 'runGraphRequestPath')}
              ${functionSource('findActiveApproval', 'canApproveRun')}
              ${functionSource('canApproveRun', 'buildApprovalRequest')}
              ${functionSource('buildApprovalRequest', 'renderApprovalPanel')}
              ${functionSource('detailTabIds', 'renderTabs')}
            `, context);

            const route = vm.runInContext("readObservatoryRoute('?scope=scope-alpha&status=failed&origin=schedule%2Capi&definition=wf-alpha&schedule=sched-alpha&from=2026-07-29T00%3A00%3A00Z&to=2026-07-30T00%3A00%3A00Z&run=run-alpha&tab=steps', '')", context);
            assert.deepEqual(JSON.parse(JSON.stringify(route)), {
              scope: 'scope-alpha', status: 'failed', origin: 'schedule,api', definition: 'wf-alpha',
              schedule: 'sched-alpha', from: '2026-07-29T00:00:00Z', to: '2026-07-30T00:00:00Z',
              run: 'run-alpha', tab: 'steps'
            });
            assert.equal(vm.runInContext('runDetailRequestPath', context)('run-alpha', { isAdmin:true, currentScope:'__all__' }, [{runId:'run-alpha',scopeId:'scope-owner'}], false), '/api/workflow/observatory/admin/runs/run-alpha');
            assert.equal(vm.runInContext('runDetailRequestPath', context)('run-alpha', { isAdmin:true, currentScope:null, ownScope:'scope-owner' }, [{runId:'run-alpha',scopeId:'scope-owner'}], false), '/api/workflow/observatory/runs/run-alpha');
            assert.equal(vm.runInContext('runDetailRequestPath', context)('run-external', { isAdmin:true, currentScope:null, ownScope:'scope-owner' }, [], false), '/api/workflow/observatory/admin/runs/run-external');
            assert.equal(vm.runInContext('runDetailRequestPath', context)('run-external', { isAdmin:true, currentScope:'scope-external', ownScope:'scope-owner' }, [], false), '/api/workflow/observatory/runs/run-external?scope=scope-external');

            const detail = { summary: { runId: 'run-alpha', scopeId: 'scope-alpha' }, steps: [
              { stepId: 'named-approval-only', suspensionType: '', completedAtUtc: null },
              { stepId: 'review', suspensionType: 'human_approval', completedAtUtc: null }
            ] };
            const approval = vm.runInContext('findActiveApproval', context)(detail);
            assert.equal(approval.stepId, 'review');
            assert.equal(vm.runInContext('canApproveRun', context)(detail, 'scope-alpha'), true);
            assert.equal(vm.runInContext('canApproveRun', context)(detail, 'scope-admin'), false);
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('buildApprovalRequest', context)(detail, approval, true, ''))), {
              path: '/api/scopes/scope-alpha/runs/run-alpha:resume',
              body: { stepId: 'review', approved: true }
            });
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('detailTabIds()', context))),
              ['timeline', 'steps', 'diagnostics', 'logs', 'artifacts', 'graph']);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("批准并继续");
        html.Should().Contain("/api/scopes/");
        html.Should().Contain(":resume");
    }

    private static async Task<string> GetObservatoryHtmlAsync()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildProvider(),
        };
        http.Response.Body = new MemoryStream();
        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();
        await WorkflowRunObservatoryEndpoints.GetObservatoryPage(http, assets).ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunNodeAsync(string script, string input)
    {
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--eval");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull("Node.js is required to execute shipped workflow-observatory behavior");
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test",
                ["Aevatar:BackendConsole:OidcClientId"] = "client-example",
                ["Aevatar:BackendConsole:OidcScope"] = "openid profile",
                ["Aevatar:BackendConsole:NyxApiBaseUrl"] = "https://api.example.test",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);
        services.AddLogging();
        return services.BuildServiceProvider();
    }
}
