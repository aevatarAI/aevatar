using System.Diagnostics;
using System.Net;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Mainnet.Host.Api.BackendConsole;
using Aevatar.Mainnet.Host.Api.Cqrs;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class BackendConsoleStaticAssetEndpointTests
{
    [Theory]
    [InlineData("/admin", "Aevatar Backend Console")]
    [InlineData("/auto/callback", "正在完成登录")]
    [InlineData("/cqrs", "CQRS")]
    [InlineData("/voice", "Voice")]
    [InlineData("/workflow/skills", "Skills")]
    public async Task StaticShellEndpoints_ShouldRenderEmbeddedHtmlWithInjectedConfig(string path, string marker)
    {
        await using var app = await CreateAppAsync();
        var response = await app.GetTestClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        html.Should().Contain(marker);
        html.Should().Contain("https://id.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().Contain(
            "\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\",\"https://api.example.test/api/v1/proxy/s/ornn-api\"]");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("https://nyx.chrono-ai.fun");
        html.Should().NotContain("https://nyx-api.chrono-ai.fun");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
        if (path == "/cqrs")
        {
            html.Should().Contain("const NYXID_API = CFG.nyxidApi");
            html.Should().Contain("const NYXID_USER_API = NYXID_API");
            html.Should().NotContain("const NYXID_AUTHORITY = CFG.authority");
        }
        if (path == "/admin")
        {
            html.Should().Contain("var NYX_API=BACKEND_CONSOLE_CONFIG.nyxidApi");
            html.Should().Contain("fetch(NYX_API+'/api/v1/admin/users");
            html.Should().NotContain("var NYX_AUTHORITY=BACKEND_CONSOLE_CONFIG.authority");
            html.Should().Contain("searchParams.append('resource'");
            html.Should().Contain("function observatoryFrameSource()");
            html.Should().NotContain("function bindObservatory(");
        }
        else if (path == "/auto/callback")
        {
            html.Should().Contain("form.append(\"resource\"");
        }
        else
        {
            html.Should().Contain("searchParams.append(\"resource\"");
            html.Should().Contain(path == "/workflow/skills"
                ? "f.append(\"resource\""
                : "form.append(\"resource\"");
        }
    }

    [Fact]
    public async Task AdminShell_AuditRefresh_ShouldReloadOnEntryAndGlobalRefresh()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("if(!AUDIT_LOADING) loadAuditTrail();");
        html.Should().Contain("if((curParts()[0]||defaultModule())==='audit')");
        html.Should().Contain("toast('正在刷新审计日志');");
        html.Should().NotContain(
            "if(!AUDIT_LOADED||AUDIT_LOADING){ if(!AUDIT_LOADING) loadAuditTrail(); }");
    }

    [Fact]
    public async Task AdminShell_AuditTrail_ShouldRenderCurrentContract()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const asyncStart = html.indexOf('async function ' + name + '(');
              const syncStart = html.indexOf('function ' + name + '(');
              const start = asyncStart !== -1 ? asyncStart : syncStart;
              const nextStarts = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(function(index) { return index !== -1; });
              const end = nextStarts.length ? Math.min.apply(null, nextStarts) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = { assert };
            vm.createContext(context);
            vm.runInContext(`
              var AUDIT_DATA = [], AUDIT_LOADED = false, AUDIT_LOADING = false;
              var AUDIT_ERR = null, AUDIT_FORBIDDEN = false, AUDIT_CURSOR = null;
              var AUDIT_HAS_MORE = false, AUDIT_WATERMARK = null;
              var AUDIT_STATE = { cat: 'all', result: 'all', text: '' };
              var response = {
                records: [
                  {
                    id: 'audit-1',
                    occurredAtUtc: '2026-07-30T03:26:31Z',
                    lifecyclePhase: 'terminal',
                    terminalOutcome: 'succeeded',
                    operationKind: 'Workflow',
                    operationName: 'workflow.run.completed',
                    scopeId: 'scope-a',
                    auditActorId: 'audit_actor:abc',
                    identityKeyId: 'key-2026-07',
                    target: { kind: 'workflow_run', id: 'run-1', displayName: 'Run One' },
                    correlation: { correlationId: 'corr-1', workflowRunId: 'run-1' },
                    provenance: { runId: 'run-1' }
                  },
                  {
                    id: 'audit-2',
                    occurredAtUtc: '2026-07-30T03:25:31Z',
                    lifecyclePhase: 'accepted',
                    terminalOutcome: null,
                    operationKind: 'Api',
                    operationName: 'scope.binding.upsert.attempted',
                    scopeId: 'scope-a',
                    auditActorId: 'audit_actor:abc',
                    identityKeyId: 'key-2026-07',
                    target: { kind: 'scope_binding', id: 'binding-1' },
                    correlation: { correlationId: 'corr-2' },
                    provenance: null
                  }
                ],
                coverage: {
                  continuationCursor: 'cursor-2',
                  ingestionWatermark: '2026-07-30T03:26:30Z'
                }
              };
              function adminJson() { return Promise.resolve(response); }
              function auditBuildQuery() { return ''; }
              function esc(value) {
                return String(value == null ? '' : value)
                  .replace(/&/g, '&amp;').replace(/</g, '&lt;')
                  .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
              }

              ${functionSource('auditResultTag', 'auditActionCategory')}
              ${functionSource('auditActionCategory', 'auditRerender')}
              ${functionSource('auditRerender', 'auditAgo')}
              ${functionSource('auditAgo', 'auditFmtTime')}
              ${functionSource('auditFmtTime', 'auditBuildQuery')}
              ${functionSource('auditApplyPage', 'loadAuditTrail')}
              ${functionSource('loadAuditTrail', 'loadAuditMore')}
              ${functionSource('auditFilteredRows', 'auditGate')}
              ${functionSource('auditTable', 'auditPager')}
            `, context);

            vm.runInContext(`(async function() {
              await loadAuditTrail();
              assert.equal(AUDIT_CURSOR, 'cursor-2');
              assert.equal(AUDIT_WATERMARK, '2026-07-30T03:26:30Z');

              const table = auditTable();
              assert.match(table, /workflow\.run\.completed/);
              assert.match(table, /成功/);
              assert.match(table, /已接受/);
              assert.match(table, /workflow_run/);
              assert.match(table, /run-1/);
              assert.match(table, /corr-1/);
              assert.doesNotMatch(table, />未知</);

              AUDIT_STATE.cat = 'workflow';
              AUDIT_STATE.result = 'succeeded';
              const filtered = auditFilteredRows();
              assert.equal(filtered.length, 1);
              assert.equal(filtered[0].id, 'audit-1');
            })()`, context).catch(function(error) {
              console.error(error);
              process.exitCode = 1;
            });
            """;

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
        process.Should().NotBeNull("Node.js is required to execute the shipped admin audit behavior");
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(html);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        process.ExitCode.Should().Be(0, $"the audit contract regression should pass. stdout: {output} stderr: {error}");
    }

    [Fact]
    public async Task AdminShell_Channels_ShouldEmbedCanonicalSurfaceWithoutDuplicateMutations()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("suiteFrame('/channels','通道接入')");
        html.Should().NotContain("function doRegister()");
        html.Should().NotContain("a==='wzPermImport'");
        html.Should().NotContain("a==='wzPublish'");
        html.Should().NotContain("CHANNELS_DATA.splice");
    }

    [Fact]
    public async Task AdminShell_ObservatoryRoute_ShouldEmbedCanonicalSurfaceWithDeepLink()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = { URLSearchParams };
            vm.createContext(context);
            vm.runInContext(`
              function suiteFrame(src, title) { return { src, title }; }
              function parseHash() { return { q: {
                scope: 'scope-alpha',
                status: 'failed',
                origin: 'schedule,api',
                definition: 'wf-alpha',
                schedule: 'sched-alpha',
                from: '2026-07-29T00:00:00Z',
                to: '2026-07-30T00:00:00Z',
                run: 'run-alpha',
                tab: 'steps',
                ignored: 'must-not-cross'
              } }; }
              ${functionSource('observatoryFrameSource', 'viewObservatoryFrame')}
              ${functionSource('viewObservatoryFrame', 'viewCqrs')}
              ${functionSource('observatoryHash', 'syncObservatoryHash')}
            `, context);

            const frame = vm.runInContext('viewObservatoryFrame().html', context);
            assert.equal(frame.title, '运行观测台');
            assert.equal(frame.src, '/workflow/observatory?scope=scope-alpha&status=failed&origin=schedule%2Capi&definition=wf-alpha&schedule=sched-alpha&from=2026-07-29T00%3A00%3A00Z&to=2026-07-30T00%3A00%3A00Z&run=run-alpha&tab=steps');
            assert.equal(vm.runInContext('observatoryHash', context)({scope:'scope-alpha',run:'run-alpha',tab:'steps',ignored:'no'}), '#/observatory?scope=scope-alpha&run=run-alpha&tab=steps');
            assert.equal(vm.runInContext('observatoryHash', context)({scope:'mine',tab:'timeline'}), '#/observatory');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().NotContain("function bindObservatory(");
    }


    [Fact]
    public async Task AdminShell_CrossLinks_ShouldBridgeObservatoryCqrsAndAudit()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();
        var admin = await client.GetStringAsync("/admin");
        var cqrs = await client.GetStringAsync("/cqrs");

        admin.Should().Contain("data-act=\"auditOpenRun\"");
        admin.Should().Contain("function viewCqrs()");
        admin.Should().Contain("p.set('owner',q.owner)");
        admin.Should().Contain("if(msg.type==='navigate'&&['cqrs','audit'].indexOf(msg.module)>=0)");
        admin.Should().Contain("var run=(parseHash().q||{}).run; if(run) AUDIT_STATE.text=run");

        cqrs.Should().Contain("function renderPurposeBanner()");
        cqrs.Should().Contain("function healthOf(s)");
        cqrs.Should().Contain("版本滞后");
        cqrs.Should().Contain("Envelope Inspector");
        cqrs.Should().Contain("function loadScopeIntrospection(scopeActorId)");
        cqrs.Should().Contain("尚无最近 committed envelope 元数据");
        cqrs.Should().Contain("function openAdminObservatory(scopeId)");
        cqrs.Should().Contain("function readDeepLinkFilters()");
        cqrs.Should().Contain("本页回答：读侧投影是否健康");
        cqrs.Should().Contain("StateVersion 差，不是毫秒");
    }

    [Fact]
    public async Task CqrsObservatory_SelectScope_ShouldLoadDetailAndRecentEnvelopeMetadata()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/cqrs");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const markers = ['function ' + name + '(', 'async function ' + name + '('];
              const nextMarkers = ['\nfunction ' + nextName + '(', '\nasync function ' + nextName + '('];
              const start = markers.map(marker => html.indexOf(marker)).filter(index => index >= 0).sort((a,b) => a-b)[0] ?? -1;
              const end = nextMarkers.map(marker => html.indexOf(marker, start)).filter(index => index >= 0).sort((a,b) => a-b)[0] ?? -1;
              assert.notEqual(start, -1, name + ' must exist in the served CQRS asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const calls = [];
            const context = {
              encodeURIComponent,
              state: { selectedScope:null, introspection:{ status:'idle', scopeActorId:null, detail:null, envelopes:[], error:null } },
              render() {},
              async authFetch(url) {
                calls.push(url);
                const empty = url.includes('scope-empty');
                if(url.endsWith('/recent-envelopes?take=20')) {
                  return { ok:true, status:200, async json() { return { envelopes: empty ? [] : [{
                    eventId:'event-alpha',
                    typeUrl:'type.googleapis.com/aevatar.WorkflowRunUpdated',
                    stateVersion:41,
                    timestampUtc:null
                  }] }; } };
                }
                return { ok:true, status:200, async json() { return {
                  scopeActorId: empty ? 'scope-empty' : 'scope/alpha',
                  stateVersion:12,
                  lastObservedVersion:11,
                  lastSuccessfulVersion:10,
                  updatedAt:'2026-07-30T08:00:00Z'
                }; } };
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('selectScope', 'loadScopeIntrospection')}
              ${functionSource('loadScopeIntrospection', 'loadReadModels')}
            `, context);

            (async () => {
              await vm.runInContext("selectScope('scope/alpha')", context);
              assert.deepEqual(calls, [
                '/api/cqrs/scopes/scope%2Falpha',
                '/api/cqrs/scopes/scope%2Falpha/recent-envelopes?take=20'
              ]);
              assert.equal(context.state.selectedScope, 'scope/alpha');
              assert.equal(context.state.introspection.status, 'ok');
              assert.equal(context.state.introspection.detail.stateVersion, 12);
              assert.equal(context.state.introspection.envelopes[0].eventId, 'event-alpha');
              assert.equal(context.state.introspection.envelopes[0].stateVersion, 41);
              assert.equal(context.state.introspection.envelopes[0].timestampUtc, null);

              calls.length = 0;
              await vm.runInContext("selectScope('scope-empty')", context);
              assert.equal(context.state.introspection.status, 'empty');
              assert.deepEqual(context.state.introspection.envelopes, []);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("尚无最近 committed envelope 元数据");
        html.Should().Contain("只展示元数据，不返回 payload");
        html.Should().NotContain("规划中能力（不阻塞主路径）");
    }

    [Fact]
    public async Task WorkflowSkillScheduleProducers_ShouldSendSelectedTeamId()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();
        var workflowSkills = await client.GetStringAsync("/workflow/skills");
        var admin = await client.GetStringAsync("/admin");

        workflowSkills.Should().Contain("loadSkillTeams");
        workflowSkills.Should().Contain("data-team-owner-select");
        ScheduleRequestSnippet(
                workflowSkills,
                "apiSend(\"/api/workflow/skills/\"+encodeURIComponent(guid)+\"/schedule\"")
            .Should()
            .Contain("teamId:");

        admin.Should().Contain("loadSkillTeams");
        admin.Should().Contain("data-team-owner-select");
        ScheduleRequestSnippet(
                admin,
                "adminApi('/api/workflow/skills/'+encodeURIComponent(s.guid)+'/schedule'")
            .Should()
            .Contain("teamId:");
    }

    [Fact]
    public async Task AdminShell_SkillsWithLegacyToken_ShouldOfferResourceReauthorization()
    {
        await using var app = await CreateAppAsync();
        var admin = await app.GetTestClient().GetStringAsync("/admin");

        admin.Should().Contain("if(!loginResourcesGranted())");
        admin.Should().Contain("当前登录未授权技能服务");
        admin.Should().Contain("data-act=\"skAuthorize\"");
    }

    private static string ScheduleRequestSnippet(string html, string scheduleCall)
    {
        var index = html.IndexOf(scheduleCall, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, $"static producer should call {scheduleCall}");
        var start = Math.Max(0, index - 300);
        var length = Math.Min(html.Length - start, 700);
        return html.Substring(start, length);
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
        process.Should().NotBeNull("Node.js is required to execute shipped backend-console behavior");
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test";
        builder.Configuration["Aevatar:BackendConsole:OidcClientId"] = "client-example";
        builder.Configuration["Aevatar:BackendConsole:OidcScope"] = "openid profile";
        builder.Configuration["Aevatar:BackendConsole:NyxApiBaseUrl"] = "https://api.example.test";
        builder.Configuration["Aevatar:BackendConsole:StorageKey"] = "console:test";
        builder.Configuration["Aevatar:BackendConsole:DefaultReturnPath"] = "/admin";
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        var app = builder.Build();
        app.MapAdminConsoleEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapCqrsObservatoryPageEndpoints();
        app.MapVoiceConsoleEndpoints();
        app.MapWorkflowSkillsEndpoints();
        await app.StartAsync();
        return app;
    }
}
