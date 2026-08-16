using System.Diagnostics;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed partial class WorkflowConsoleStaticAssetEndpointTests
{
    [Theory]
    [InlineData("admin-observatory", "Workflow Run Observatory")]
    [InlineData("studio", "<title>Aevatar Studio</title>")]
    public async Task WorkflowStaticShellEndpoints_ShouldRenderInjectedEmbeddedAssets(string endpoint, string marker)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildProvider(),
        };
        http.Response.Body = new MemoryStream();
        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();

        var result = endpoint == "admin-observatory"
            ? WorkflowRunObservatoryEndpoints.GetAdminObservatoryFrame(http, assets)
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
        html.Should().Contain("\"nyxidWeb\":\"https://web.example.test\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
        if (endpoint == "admin-observatory")
        {
            // ADR-0018: session logins must not send explicit `resource` parameters,
            // or NyxID narrows the grant below the deployment's default LLM route.
            html.Should().NotContain("searchParams.append(\"resource\"");
            html.Should().NotContain("form.append(\"resource\"");
            html.Should().Contain("async function fetchWithConsoleAuth(");
            html.Should().Contain("requestAdminShellTokenRefresh(");
            html.Should().Contain("rejectedAccessToken");
            html.Should().Contain("if(window.top !== window) return;");
            html.Should().Contain("location.replace(\"/admin#/observatory\"");
            html.Should().Contain("const url = CFG.nyxidApi + \"/api/v1/admin/users");
            html.Should().NotContain("const url = CFG.authority + \"/api/v1/admin/users");
            html.Should().Contain("\"aria-label\":\"完整 run id\"");
            html.Should().Contain("/api/workflow/observatory/admin/runs/");
            html.Should().Contain("detail.diagnostics");
            html.Should().NotContain("indexOf(\":run:\")");
        }
        else
        {
            html.Should().Contain("/workflow/studio/assets/styles.css");
            html.Should().Contain("/workflow/studio/assets/app.js");
            html.Should().Contain("globalThis.__AEVATAR_ASSISTANT_CONFIG__");
            html.Should().Contain("Aevatar Studio");
            html.Should().Contain("class=\"site-header\"");
            html.Should().NotContain("id=\"studioTitle\"");
            html.Should().NotContain("class=\"workflow-nav\"");
            html.Should().Contain("id=\"servicesButton\"");
            html.Should().Contain("id=\"mobileInspectorButton\"");
            html.Should().Contain("\"enableStudioWireInspector\":false");
            html.Should().NotContain("class=\"studio-tabs\"");
            html.Should().Contain("<div class=\"group-label\">当前实录</div>");
            html.Should().Contain("name=\"color-scheme\" content=\"only light\"");
            html.Should().NotContain("themeButton");
            html.Should().NotContain("workflow: \"studio\"");
        }
    }

    [Fact]
    public async Task WorkflowObservatory_Authentication_ShouldPreserveRefreshedTokensAndDelegateCurrentTokenRefresh()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function getToken(){');
            const end = html.indexOf('\nfunction navigateAdminShell(', start);
            assert.notEqual(start, -1, 'workflow auth helpers must exist');
            assert.notEqual(end, -1, 'workflow auth helper boundary must exist');

            const records = new Map();
            const listeners = new Set();
            const messages = [];
            const fetchCalls = [];
            let scenario = 'stale';
            let releaseFirst = null;
            let signOutRejectedToken = null;
            function response(status) { return {status, ok:status >= 200 && status < 300}; }
            const localStorage = {
              getItem:key => records.has(key) ? records.get(key) : null,
              setItem:(key,value) => records.set(key,value),
              removeItem:key => records.delete(key),
            };
            const parent = {
              postMessage(message) {
                messages.push(message);
                if(message.type !== 'auth-refresh-request') return;
                setImmediate(() => {
                  if(scenario === 'refresh-success') {
                    records.set('console:test:token', JSON.stringify({access_token:'parent-refreshed',refresh_token:'parent-refresh'}));
                  }
                  for(const listener of [...listeners]) listener({
                    origin:'https://console.example.test', source:parent,
                    data:{source:'aevatar-backend-console-suite',type:'auth-refresh-result',requestId:message.requestId,refreshed:scenario === 'refresh-success'}
                  });
                });
              }
            };
            const window = {
              parent,
              frameElement:{getAttribute:name => name === 'data-console-frame' ? '1' : null},
              addEventListener:(type,listener) => { if(type === 'message') listeners.add(listener); },
              removeEventListener:(type,listener) => { if(type === 'message') listeners.delete(listener); },
            };
            const context = {
              TOKEN_KEY:'console:test:token', localStorage, window,
              location:{origin:'https://console.example.test'},
              randomString:() => 'request-suffix',
              fetch:async (path,init) => {
                fetchCalls.push({path,init});
                if(scenario === 'stale' && fetchCalls.length === 1) {
                  return await new Promise(resolve => { releaseFirst = () => resolve(response(401)); });
                }
                if((scenario === 'refresh-success' || scenario === 'refresh-failure') && fetchCalls.length === 1) return response(401);
                return response(200);
              },
              signOutSilent:token => { signOutRejectedToken = token; return true; },
              setTimeout, clearTimeout, setImmediate, Date, Promise, Error, console,
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async function(){
              records.set('console:test:token', JSON.stringify({access_token:'old-access',refresh_token:'old-refresh'}));
              const staleRequest = context.fetchWithConsoleAuth('/api/probe');
              while(!releaseFirst) await new Promise(resolve => setImmediate(resolve));
              records.set('console:test:token', JSON.stringify({access_token:'fresh-access',refresh_token:'fresh-refresh'}));
              releaseFirst();
              assert.equal((await staleRequest).status, 200);
              assert.equal(fetchCalls.length, 2);
              assert.equal(fetchCalls[0].init.headers.Authorization, 'Bearer old-access');
              assert.equal(fetchCalls[1].init.headers.Authorization, 'Bearer fresh-access');
              assert.equal(JSON.parse(records.get('console:test:token')).access_token, 'fresh-access');
              assert.equal(messages.length, 0, 'an already refreshed token does not ask the parent to rotate again');

              scenario = 'refresh-success'; fetchCalls.length = 0; messages.length = 0;
              records.set('console:test:token', JSON.stringify({access_token:'current-access',refresh_token:'current-refresh'}));
              assert.equal((await context.fetchWithConsoleAuth('/api/probe')).status, 200);
              assert.equal(messages.length, 1);
              assert.equal(messages[0].type, 'auth-refresh-request');
              assert.equal(messages[0].rejectedAccessToken, 'current-access');
              assert.equal(fetchCalls[1].init.headers.Authorization, 'Bearer parent-refreshed');

              scenario = 'refresh-failure'; fetchCalls.length = 0; messages.length = 0; signOutRejectedToken = null;
              records.set('console:test:token', JSON.stringify({access_token:'rejected-current',refresh_token:'rejected-refresh'}));
              await assert.rejects(() => context.fetchWithConsoleAuth('/api/probe'), /unauthorized/);
              assert.equal(signOutRejectedToken, 'rejected-current');
              assert.equal(JSON.parse(records.get('console:test:token')).access_token, 'rejected-current', 'the iframe delegates final compare-and-clear to the parent shell');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
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
            const toolDetail = { summary: detail.summary, steps: [
              { stepId: 'unsafe-tool', suspensionType: 'tool_approval', completedAtUtc: null },
              { stepId: 'create-approval', suspensionType: 'tool_approval', completedAtUtc: null, toolApproval: {
                executionId: 'exec-alpha', toolName: 'nyxid_proxy', toolCallId: 'call-alpha', approvalRequestId: 'approval-alpha'
              } }
            ] };
            const toolApproval = vm.runInContext('findActiveApproval', context)(toolDetail);
            assert.equal(toolApproval.stepId, 'create-approval');
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('buildApprovalRequest', context)(toolDetail, toolApproval, true, ''))), {
              path: '/api/scopes/scope-alpha/runs/run-alpha:resume',
              body: {
                stepId: 'create-approval',
                approved: true,
                toolApproval: { executionId: 'exec-alpha', toolCallId: 'call-alpha', approvalRequestId: 'approval-alpha' }
              }
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

    [Fact]
    public async Task WorkflowObservatory_ShouldRestorePanePositionsByRoute()
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

            const context = { URLSearchParams };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('observatoryListKey', 'observatoryDetailKey')}
              ${functionSource('observatoryDetailKey', 'readObservatoryListState')}
              ${functionSource('readObservatoryListState', 'writeObservatoryListState')}
              ${functionSource('writeObservatoryListState', 'readObservatoryDetailState')}
              ${functionSource('readObservatoryDetailState', 'writeObservatoryDetailState')}
              ${functionSource('writeObservatoryDetailState', 'paneScrollPosition')}
              ${functionSource('paneScrollPosition', 'applyPaneScrollState')}
            `, context);

            const records = new Map();
            const storage = {
              getItem(key) { return records.has(key) ? records.get(key) : null; },
              setItem(key, value) { records.set(key, value); }
            };
            const routeA = {scope:'scope-alpha',status:'failed',origin:'',definition:'wf-alpha',schedule:'',from:'',to:'',run:'run-alpha',tab:'logs'};
            const routeB = {...routeA, run:'run-beta'};
            const key = 'console:test:observatory:view';
            const routeAListKey = vm.runInContext('observatoryListKey', context)(routeA);
            const routeBListKey = vm.runInContext('observatoryListKey', context)(routeB);
            const routeADetailKey = vm.runInContext('observatoryDetailKey', context)(routeA);
            const routeBDetailKey = vm.runInContext('observatoryDetailKey', context)(routeB);

            vm.runInContext('writeObservatoryListState', context)(storage, key, routeA, 180);
            vm.runInContext('writeObservatoryDetailState', context)(storage, key, routeA, 760);
            vm.runInContext('writeObservatoryDetailState', context)(storage, key, routeB, 40);
            assert.equal(vm.runInContext('readObservatoryListState', context)(storage, key, routeA), 180);
            assert.equal(vm.runInContext('readObservatoryListState', context)(storage, key, routeB), 180);
            assert.equal(vm.runInContext('readObservatoryDetailState', context)(storage, key, routeA), 760);
            assert.equal(vm.runInContext('readObservatoryDetailState', context)(storage, key, routeB), 40);
            assert.equal(routeAListKey, routeBListKey);
            assert.notEqual(routeADetailKey, routeBDetailKey);
            assert.match(routeADetailKey, /run-alpha/);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:0,scrollHeight:100,clientHeight:100}, 760), 760);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:0,scrollHeight:900,clientHeight:300}, 760), 0);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:180,scrollHeight:900,clientHeight:300}, 760), 180);

            storage.setItem(key, '{bad json');
            storage.setItem(key + ':detail', '{bad json');
            assert.equal(vm.runInContext('readObservatoryListState', context)(storage, key, routeA), 0);
            assert.equal(vm.runInContext('readObservatoryDetailState', context)(storage, key, routeA), 0);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task WorkflowObservatory_ShouldExposeExplicitImmersiveAndRefreshActions()
    {
        var html = await GetObservatoryHtmlAsync();
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
              ].filter(index => index !== -1);
              const end = nextStarts.length ? Math.min(...nextStarts) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const classChanges = [];
            const messages = [];
            const records = new Map();
            const context = {
              document: {
                body: {classList:{toggle(name, enabled){classChanges.push([name, enabled]);}}},
                activeElement: null
              },
              sessionStorage: {getItem(key){return records.get(key)||null;},setItem(key,value){records.set(key,value);}},
              window: {parent:{postMessage(message){messages.push(message);}}},
              location: {origin:'https://console.example.test'},
              isEmbeddedInAdmin: () => true,
              state: {immersive:false},
              OBSERVATORY_IMMERSIVE_KEY: 'console:test:observatory:immersive',
              refreshCalls: 0, renderCalls: 0,
              refreshRuns: async () => { context.refreshCalls++; return true; },
              refreshDetail: async () => { context.refreshCalls++; return true; },
              runsSig: () => 'runs', detailSig: () => 'detail',
              cache: {runs:[],details:{'run-alpha':{}}},
              lastRunsSig:'',lastDetailSig:'',
              render: () => { context.renderCalls++; }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('setImmersive', 'toggleImmersive')}
              ${functionSource('toggleImmersive', 'refreshObservatory')}
              ${functionSource('refreshObservatory', 'renderTopbar')}
            `, context);

            vm.runInContext('setImmersive', context)(true);
            assert.equal(context.state.immersive, true);
            assert.equal(records.get('console:test:observatory:immersive'), '1');
            assert.deepEqual(classChanges.at(-1), ['observatory-immersive', true]);
            assert.equal(messages.at(-1).type, 'observatory-immersive');
            assert.equal(messages.at(-1).enabled, true);

            context.state.selectedRunId = 'run-alpha';
            vm.runInContext('(async()=>refreshObservatory())()', context).then(() => {
              assert.equal(context.refreshCalls, 2);
              assert.equal(context.renderCalls, 1);
            }).catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("沉浸观测");
        html.Should().Contain("刷新数据");
        html.Should().Contain("body.observatory-immersive .list-pane");
        html.Should().Contain("body.observatory-immersive { --topbar-h:0px; }");
    }

    private static async Task<string> GetObservatoryHtmlAsync()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildProvider(),
        };
        http.Response.Body = new MemoryStream();
        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();
        await WorkflowRunObservatoryEndpoints.GetAdminObservatoryFrame(http, assets).ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> GetStudioAssetAsync(
        Func<HttpContext, IBackendConsoleAssetService, IResult> endpoint)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildProvider(),
        };
        http.Response.Body = new MemoryStream();
        var assets = http.RequestServices.GetRequiredService<IBackendConsoleAssetService>();
        await endpoint(http, assets).ExecuteAsync(http);
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
                ["Aevatar:NyxId:Authority"] = "https://web.example.test",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);
        services.AddLogging();
        return services.BuildServiceProvider();
    }
}
