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
        html.Should().Contain("https://authority.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().Contain("https://api.example.test/api/v1/proxy/s/aevatar");
        html.Should().Contain("\"nyxidWeb\":\"https://api.example.test\"");
        html.Should().NotContain("http://nyxid.internal:3001");
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
            html.Should().Contain("/api/workflow/observatory/activity-runs");
            html.Should().Contain("请求轨迹");
            html.Should().Contain("function normalizeActivityRunFeed(");
            html.Should().Contain("data-duration=\"");
            html.Should().Contain("/api/workflow/observatory/admin/runs/");
            html.Should().Contain("detail.diagnostics");
            html.Should().Contain("function buildOperationRecords(detail)");
            html.Should().Contain("function renderDurationOverview(detail,records)");
            html.Should().Contain("aria-label\":\"Input Model Tools Duration 总览\"");
            html.Should().Contain("{id:\"input\",label:\"Input\"");
            html.Should().Contain("{id:\"model\",label:\"Model\"");
            html.Should().Contain("{id:\"tools\",label:\"Tools\"");
            html.Should().Contain("function renderOperationDetail(record)");
            html.Should().Contain("function renderOperationLedger(records)");
            html.Should().Contain("aria-label\":\"逐条 operation 记录\"");
            html.Should().Contain("Operation ledger");
            html.Should().Contain("state.expandedOperations.has(record.key)");
            html.Should().Contain("wrap.appendChild(renderDurationOverview(detail,records))");
            html.Should().Contain("wrap.appendChild(renderOperationLedger(records))");
            html.Should().NotContain("operationRecordKey(\"step\"");
            html.Should().NotContain("operationLaneForStep(");
            html.Should().NotContain("data-kind=\"step\"");
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
            html.Should().Contain("id=\"traceViewButton\"");
            html.Should().Contain("id=\"requestTracePanel\"");
            html.Should().Contain("id=\"traceReadonlyNotice\"");
            html.Should().Contain("class=\"trajectory-toolbar\"");
            html.Should().Contain("id=\"trajectoryDurationButton\"");
            html.Should().Contain("id=\"trajectoryFoldRequestsButton\"");
            html.Should().Contain("id=\"trajectoryFoldCallsButton\"");
            html.Should().Contain("id=\"trajectorySearchInput\"");
            html.Should().Contain("id=\"trajectoryOverviewTrack\"");
            html.Should().Contain("class=\"trajectory-table\"");
            html.Should().Contain("id=\"trajectoryDetails\"");
            html.Should().Contain("id=\"trajectoryDetailsResize\"");
            // Requests are ledger sections, not a separate navigation rail, and the
            // operation inspector belongs to the trajectory rather than the run drawer.
            html.Should().NotContain("id=\"requestTraceList\"");
            html.Should().NotContain("class=\"request-trace-workspace\"");
            html.Should().NotContain("id=\"traceOperationSection\"");
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
    public async Task WorkflowObservatory_ShouldOwnRouteStateAndOwnerOnlyRunControlActions()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = { URLSearchParams, encodeURIComponent };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('readObservatoryRoute', 'writeObservatoryRoute')}
              ${functionSource('runDetailRequestPath', 'runGraphRequestPath')}
              ${functionSource('resolveRunControlTarget', 'canStopRun')}
              ${functionSource('canStopRun', 'buildStopRequest')}
              ${functionSource('buildStopRequest', 'requestStopRun')}
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

            const detail = { summary: { runId: 'run-alpha', scopeId: 'scope-alpha', status: 'running' }, steps: [
              { stepId: 'named-approval-only', suspensionType: '', completedAtUtc: null },
              { stepId: 'review', suspensionType: 'human_approval', completedAtUtc: null }
            ] };
            const stopDetail = { summary: { runId: 'run alpha/1', scopeId: 'scope-alpha', status: 'running' } };
            const target = vm.runInContext('resolveRunControlTarget', context)(stopDetail, 'run alpha/1', [{
              runId: 'run alpha/1', actorId: 'actor-alpha', scopeId: 'scope-alpha', status: 'running'
            }]);
            assert.deepEqual(JSON.parse(JSON.stringify(target)), {
              scopeId: 'scope-alpha', runId: 'run alpha/1', actorId: 'actor-alpha'
            });
            assert.equal(vm.runInContext('canStopRun', context)(target, 'scope-alpha'), true);
            assert.equal(vm.runInContext('canStopRun', context)(target, 'scope-admin'), false);
            assert.equal(vm.runInContext('canStopRun', context)(target, ''), false);
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('buildStopRequest', context)(target, 'stop-command-alpha'))), {
              path: '/api/scopes/scope-alpha/runs/run%20alpha%2F1:stop',
              body: { reason: 'user requested stop', commandId: 'stop-command-alpha', actorId: 'actor-alpha' }
            });
            const deepLinkTarget = vm.runInContext('resolveRunControlTarget', context)(
              { summary: { runId: 'run-deep-link', scopeId: 'scope-alpha', status: 'running' } },
              'run-deep-link', []);
            assert.deepEqual(JSON.parse(JSON.stringify(deepLinkTarget)), {
              scopeId: 'scope-alpha', runId: 'run-deep-link', actorId: ''
            });
            assert.equal(vm.runInContext('resolveRunControlTarget', context)(
              { summary: { runId: 'run-stale', scopeId: 'scope-alpha', status: 'running' } },
              'run alpha/1', [{ runId: 'run alpha/1', actorId: 'actor-alpha', scopeId: 'scope-alpha' }]
            ), null);
            assert.equal(vm.runInContext('resolveRunControlTarget', context)(
              { summary: { runId: 'run alpha/1', scopeId: 'scope-alpha', status: 'stopped' } },
              'run alpha/1', [{ runId: 'run alpha/1', actorId: 'actor-alpha', scopeId: 'scope-alpha' }]
            ), null);
            assert.equal(vm.runInContext('resolveRunControlTarget', context)(stopDetail, 'run alpha/1', [{
              runId: 'run alpha/1', actorId: 'actor-other', scopeId: 'scope-other'
            }]), null);
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
              ['timeline', 'trajectory', 'steps', 'diagnostics', 'logs', 'artifacts', 'graph']);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("批准并继续");
        html.Should().Contain("停止当前运行");
        html.Should().Contain("停止请求已受理，等待 committed 状态更新");
        html.Should().Contain("stop-not-accepted");
        html.Should().Contain("/api/scopes/");
        html.Should().Contain(":resume");
        html.Should().Contain(":stop");
        html.Should().NotContain("不可修改任何运行");
        html.Should().Contain("function renderTimeline(detail)");
        html.Should().Contain("function renderTrajectory(detail)");
        html.Should().Contain("aria-label\":\"事件时间线\"");
        html.Should().Contain("id:\"panel-trajectory\"");
    }

    [Fact]
    public async Task WorkflowObservatory_ApiRequest_ShouldTreatOnlyGetNotFoundAsEmpty()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }
            function response(status, body) {
              return {
                status,
                ok: status >= 200 && status < 300,
                async json(){ return body; },
                async text(){ return body == null ? '' : JSON.stringify(body); }
              };
            }

            let nextResponse = null;
            const calls = [];
            const context = {
              fetchWithConsoleAuth: async (path, options) => {
                calls.push({ path, options: options || {} });
                return nextResponse;
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('apiRequest', 'api')}
              ${functionSource('buildStopRequest', 'requestStopRun')}
              ${functionSource('requestStopRun', 'stopTargetKey')}
            `, context);

            (async function(){
              nextResponse = response(404, { code:'QUERY_NOT_FOUND' });
              assert.equal(await context.apiRequest('/api/query/missing'), null);

              for(const [status, code] of [[404,'SCOPE_RUN_NOT_FOUND'],[403,'SCOPE_ACCESS_DENIED'],[409,'SCOPE_RUN_AMBIGUOUS'],[500,'STOP_FAILED']]){
                nextResponse = response(status, { code });
                await assert.rejects(
                  () => context.apiRequest('/api/scopes/scope-alpha/runs/run-alpha:stop', { method:'POST' }),
                  new RegExp(code)
                );
              }

              nextResponse = response(202, { accepted:true, acceptedCommandId:'command-alpha' });
              assert.deepEqual(
                JSON.parse(JSON.stringify(await context.apiRequest('/api/scopes/scope-alpha/runs/run-alpha:stop', { method:'POST' }))),
                { accepted:true, acceptedCommandId:'command-alpha' }
              );
              assert.equal(calls.at(-1).options.method, 'POST');

              nextResponse = response(202, { accepted:true, acceptedCommandId:'command-alpha' });
              assert.deepEqual(
                JSON.parse(JSON.stringify(await context.requestStopRun(
                  { scopeId:'scope alpha', runId:'run alpha/1', actorId:'actor-alpha' },
                  'command-alpha'))),
                { accepted:true, acceptedCommandId:'command-alpha' }
              );
              const stopCall = calls.at(-1);
              assert.equal(stopCall.path, '/api/scopes/scope%20alpha/runs/run%20alpha%2F1:stop');
              assert.equal(stopCall.options.method, 'POST');
              assert.equal(stopCall.options.headers['Content-Type'], 'application/json');
              assert.deepEqual(JSON.parse(stopCall.options.body), {
                reason:'user requested stop', commandId:'command-alpha', actorId:'actor-alpha'
              });

              nextResponse = response(202, { accepted:false });
              await assert.rejects(
                () => context.requestStopRun(
                  { scopeId:'scope-alpha', runId:'run-alpha', actorId:'actor-alpha' },
                  'command-alpha'),
                /stop-not-accepted/
              );
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowObservatory_StopControl_ShouldLockAttemptsReuseCommandIdAndIgnoreStaleCompletion()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            const targetStart = html.indexOf('function resolveRunControlTarget(');
            const buildRequestStart = html.indexOf('\nfunction buildStopRequest(', targetStart);
            const stateStart = html.indexOf('function stopTargetKey(');
            const end = html.indexOf('\nfunction findActiveApproval(', stateStart);
            assert.notEqual(targetStart, -1, 'stop target resolution must exist in the served observatory asset');
            assert.notEqual(buildRequestStart, -1, 'buildStopRequest must follow stop target resolution');
            assert.notEqual(stateStart, -1, 'stop control state must exist in the served observatory asset');
            assert.notEqual(end, -1, 'approval helpers must follow the stop control');

            let renderCount = 0;
            const context = {
              randomString: () => 'stable-command-suffix',
              render: () => { renderCount += 1; },
              requestStopRun: null,
              document: { getElementById: () => null },
              setTimeout: () => 0
            };
            vm.createContext(context);
            vm.runInContext(html.slice(targetStart, buildRequestStart) + '\n' + html.slice(stateStart, end), context);

            const targetA = { scopeId:'scope-alpha', runId:'run-alpha', actorId:'actor-alpha' };
            const targetB = { scopeId:'scope-alpha', runId:'run-beta', actorId:'actor-beta' };

            (async function(){
              const calls = [];
              let releaseFirst;
              context.requestStopRun = (target, commandId) => {
                calls.push({ target, commandId });
                return new Promise(resolve => { releaseFirst = resolve; });
              };
              const first = context.submitStopRun(targetA);
              const duplicate = await context.submitStopRun(targetA);
              assert.equal(duplicate, false);
              assert.equal(calls.length, 1, 'pending lock must prevent a duplicate stop command');
              releaseFirst({ accepted:true });
              assert.equal(await first, true);
              let state = vm.runInContext('stopControlState', context);
              assert.equal(state.accepted, true);
              assert.equal(state.commandId, 'observatory-stop-stable-command-suffix');

              context.resetStopControlState();
              context.requestStopRun = async (target, commandId) => {
                calls.push({ target, commandId });
                throw new Error('network-down');
              };
              assert.equal(await context.submitStopRun(targetA), false);
              const retryCommandId = vm.runInContext('stopControlState.commandId', context);
              assert.equal(await context.submitStopRun(targetA), false);
              assert.equal(vm.runInContext('stopControlState.commandId', context), retryCommandId);
              assert.equal(calls.at(-1).commandId, retryCommandId, 'manual retry must reuse the same command id');

              context.resetStopControlState();
              let releaseStale;
              context.requestStopRun = () => new Promise(resolve => { releaseStale = resolve; });
              const stale = context.submitStopRun(targetA);
              context.syncStopControlState(targetB);
              releaseStale({ accepted:true });
              assert.equal(await stale, false);
              state = vm.runInContext('stopControlState', context);
              assert.equal(state.key, 'scope-alpha\nrun-beta');
              assert.equal(state.accepted, false, 'an old response must not mark the newly selected run accepted');

              context.resetStopControlState();
              context.openStopConfirmation({scopeId:'scope-alpha', runId:'run-alpha', actorId:''});
              const hydratedCommandId = vm.runInContext('stopControlState.commandId', context);
              context.syncStopControlState(targetA);
              state = vm.runInContext('stopControlState', context);
              assert.equal(state.key, 'scope-alpha\nrun-alpha');
              assert.equal(state.commandId, hydratedCommandId, 'actor id hydration must preserve command identity');
              assert.equal(state.confirming, true, 'actor id hydration must preserve confirmation state');

              function createNode(tag, attrs = {}, content) {
                const listeners = {};
                return {
                  tag, attrs:{...attrs}, children:[], innerHTML:content == null ? '' : String(content),
                  disabled:false, listeners,
                  appendChild(child){ this.children.push(child); return child; },
                  addEventListener(type, listener){ listeners[type] = listener; },
                  querySelector(){ return null; },
                  focus(){},
                };
              }
              function createHead(){
                const top = createNode('div', {class:'rh-top'});
                const head = createNode('header');
                head.querySelector = selector => selector === '.rh-top' ? top : null;
                return {head, top};
              }

              context.el = createNode;
              context.ICON = {stop:'[stop]'};
              context.esc = value => String(value).replace(/[&<>\"]/g, character => ({
                '&':'&amp;', '<':'&lt;', '>':'&gt;', '\"':'&quot;',
              })[character]);
              context.state = {selectedRunId:'run-alpha'};
              context.cache = {runs:[{runId:'run-alpha', actorId:'actor-alpha', scopeId:'scope-alpha'}]};
              context.adminState = {ownScope:'scope-alpha'};
              const detail = {summary:{runId:'run-alpha', scopeId:'scope-alpha', status:'running'}};

              context.resetStopControlState();
              let rendered = createHead();
              context.renderStopControl(detail, rendered.head);
              assert.equal(rendered.top.children.length, 1, 'own-scope running detail must show one stop control');
              const stopButton = rendered.top.children[0].children[0];
              assert.equal(stopButton.attrs.id, 'stopCurrentRunButton');
              stopButton.listeners.click();
              assert.equal(vm.runInContext('stopControlState.confirming', context), true);

              rendered = createHead();
              context.renderStopControl(detail, rendered.head);
              assert.equal(rendered.head.children.length, 1, 'confirmation panel must render below the header');
              const panel = rendered.head.children[0];
              assert.equal(panel.attrs.role, 'group', 'inline confirmation must not claim modal dialog behavior');
              const confirmationCommandId = vm.runInContext('stopControlState.commandId', context);
              let escaped = false;
              panel.listeners.keydown({key:'Escape', preventDefault(){ escaped = true; }});
              assert.equal(escaped, true);
              assert.equal(vm.runInContext('stopControlState.confirming', context), false);
              assert.equal(
                vm.runInContext('stopControlState.commandId', context), confirmationCommandId,
                'cancel/reopen must keep the same idempotency key for the selected run');

              context.adminState.ownScope = 'scope-other';
              rendered = createHead();
              context.renderStopControl(detail, rendered.head);
              assert.equal(rendered.top.children.length, 0, 'cross-scope detail must stay read-only');

              context.adminState.ownScope = 'scope-alpha';
              context.resetStopControlState();
              context.syncStopControlState(targetA);
              vm.runInContext('stopControlState.accepted = true', context);
              rendered = createHead();
              context.renderStopControl(detail, rendered.head);
              assert.equal(rendered.top.children[0].children[0].attrs.role, 'status');
              assert.ok(renderCount > 0);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowObservatory_Timeline_ShouldRemainDefaultAndKeepCompleteEventDetailsBesideTrajectory()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            function functionSourceToMarker(name, marker) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf(marker, start);
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, marker + ' must follow ' + name);
              return html.slice(start, end);
            }

            function createNode(tag, attrs = {}, content) {
              const node = {
                tag, attrs: {...attrs}, className: attrs.class || '', children: [], style: {},
                innerHTML: content == null ? '' : String(content),
                appendChild(child) { this.children.push(child); return child; },
                setAttribute(key, value) { this.attrs[key] = String(value); },
                insertAdjacentHTML(_position, value) { this.innerHTML += String(value); },
                addEventListener() {},
                querySelector() { return null; },
                classList: { add() {}, toggle() { return false; } },
              };
              return node;
            }

            function treeText(node) {
              return [node.innerHTML, ...node.children.map(treeText)].filter(Boolean).join('\n');
            }

            const routeContext = {URLSearchParams, decodeURIComponent};
            vm.createContext(routeContext);
            vm.runInContext(functionSource('readObservatoryRoute', 'writeObservatoryRoute'), routeContext);
            assert.equal(routeContext.readObservatoryRoute('', '').tab, 'timeline');
            assert.equal(routeContext.readObservatoryRoute('?tab=trajectory', '').tab, 'trajectory');
            assert.equal(routeContext.readObservatoryRoute('?tab=unknown', '').tab, 'timeline');

            const detailSource = functionSource('renderDetail', 'parseRunId');
            assert.match(detailSource,
              /timelinePanel\.appendChild\(renderTimeline\(detail\)\)/,
              'the original Timeline panel must remain wired to renderTimeline');
            assert.match(detailSource,
              /if\(state\.activeTab !== "timeline"\) timelinePanel\.hidden = true/,
              'Timeline must remain visible for the default timeline tab');
            assert.match(detailSource,
              /trajectoryPanel\.appendChild\(renderTrajectory\(detail\)\)/,
              'Trajectory must render through its own sibling panel');
            assert.match(detailSource,
              /if\(state\.activeTab !== "trajectory"\) trajectoryPanel\.hidden = true/,
              'Trajectory visibility must be independent from Timeline');
            assert.ok(
              detailSource.indexOf('tp.appendChild(timelinePanel)') <
                detailSource.indexOf('tp.appendChild(trajectoryPanel)'),
              'Trajectory must be appended beside, not in place of, Timeline');

            const selectionContext = {
              state: {
                selectedRunId: null, scenario: 'normal', activeTab: 'trajectory',
                expandedOperations: new Set(['model:old']), selectedNodeId: 'node-old', graphView: {},
              },
              pendingDetailScrollReset: false,
              routePatch: null,
              writeObservatoryRoute: null,
              document: {body: {setAttribute() {}}},
              resetStopControlState() {},
              render() {},
              loadDetail() {},
            };
            selectionContext.writeObservatoryRoute = patch => { selectionContext.routePatch = patch; };
            vm.createContext(selectionContext);
            vm.runInContext(functionSource('selectRun', 'fetchMe'), selectionContext);
            selectionContext.selectRun('run-new');
            assert.equal(selectionContext.state.activeTab, 'timeline');
            assert.deepEqual(JSON.parse(JSON.stringify(selectionContext.routePatch)),
              {run: 'run-new', tab: 'timeline'});
            assert.equal(selectionContext.state.expandedOperations.size, 0);

            const renderContext = {
              el: createNode,
              esc: value => String(value == null ? '' : value).replace(/[&<>\"]/g, character => ({
                '&': '&amp;', '<': '&lt;', '>': '&gt;', '\"': '&quot;',
              })[character]),
              initials: value => String(value || '').slice(0, 2).toUpperCase(),
              clockUTC: value => String(value).slice(11, 19),
              kindIcon: kind => '[' + kind + ']',
              fmtNum: value => String(value),
              colorJSON: value => 'JSON:' + String(value),
              dataLookup(data, keys) {
                if (!data) return '';
                const entries = Object.entries(data);
                for (const key of keys) {
                  const found = entries.find(([candidate]) => candidate.toLowerCase() === key.toLowerCase());
                  if (found && String(found[1]).trim()) return String(found[1]);
                }
                return '';
              },
              KIND: {
                Message: {label: '模型回复'}, ToolCall: {label: '工具调用'},
                HumanInputRequest: {label: '待人工确认'}, StepFinished: {label: '步骤完成'},
                RunError: {label: '运行错误'},
              },
              REPLY_KINDS: new Set(['Message', 'TextMessage']),
              STEPTYPE_LABEL: {llm: '模型', tool: '工具', human: '人工'},
              DATA_MODEL_KEYS: ['model', 'model_id', 'modelId', 'provider'],
              DATA_TOKEN_KEYS: [
                ['prompt', ['prompt_tokens', 'promptTokens']],
                ['completion', ['completion_tokens', 'completionTokens']],
                ['total', ['total_tokens', 'totalTokens']],
              ],
              TOKEN_CHIP_LABEL: {prompt: '输入', completion: '输出', total: '合计'},
              DATA_CHIP_KEYS: new Set([
                'model', 'model_id', 'modelid', 'provider', 'prompt_tokens', 'prompttokens',
                'completion_tokens', 'completiontokens', 'total_tokens', 'totaltokens',
                'call_id', 'arguments_json', 'result_json', 'success', 'error',
              ]),
              ICON: {chevron: '[chevron]', human: '[human]', lock: '[lock]', check: '[check]', x: '[x]', copy: '[copy]'},
              state: {expanded: new Set(['call-success', 'call-failure'])},
              setTimeout,
            };
            vm.createContext(renderContext);
            vm.runInContext(`
              ${functionSource('renderReplyBubble', 'renderDataDetails')}
              ${functionSource('renderDataDetails', 'operationTypeIcon')}
              ${functionSource('renderTimeline', 'renderToolCall')}
              ${functionSource('renderToolCall', 'jsonField')}
              ${functionSourceToMarker('jsonField', '\n/* ---- Graph')}
            `, renderContext);

            const rendered = renderContext.renderTimeline({
              summary: {status: 'completed'},
              timeline: [
                {
                  kind: 'Message', timestampUtc: '2026-08-14T01:00:00Z', stepId: 'step-alpha',
                  stepType: 'llm', agentId: 'agent-alpha', content: 'Deployment is degraded.',
                  data: {
                    model: 'deepseek-chat', prompt_tokens: '120', completion_tokens: '20',
                    total_tokens: '140', finish_reason: 'stop',
                  },
                },
                {
                  kind: 'ToolCall', timestampUtc: '2026-08-14T01:00:01Z',
                  toolCall: {
                    callId: 'call-success', toolName: 'search', success: true,
                    argumentsJson: '{\"query\":\"deployment status\"}',
                    resultJson: '{\"status\":\"degraded\"}', error: '',
                  },
                },
                {
                  kind: 'ToolCall', timestampUtc: '2026-08-14T01:00:02Z',
                  toolCall: {
                    callId: 'call-failure', toolName: 'fetch_details', success: false,
                    argumentsJson: '{\"id\":\"deployment-alpha\"}', resultJson: '',
                    error: 'upstream unavailable',
                  },
                },
                {
                  kind: 'HumanInputRequest', timestampUtc: '2026-08-14T01:00:03Z',
                  message: 'Approve deployment?',
                },
                {
                  kind: 'StepFinished', timestampUtc: '2026-08-14T01:00:04Z',
                  stepId: 'step-alpha', message: '120 ms · 140 tokens',
                },
                {
                  kind: 'RunError', timestampUtc: '2026-08-14T01:00:05Z',
                  message: 'provider unavailable',
                },
              ],
            });

            const timeline = rendered.children[1];
            assert.equal(timeline.tag, 'ol');
            assert.equal(timeline.attrs['aria-label'], '事件时间线');
            assert.equal(timeline.children.length, 6, 'trajectory must not replace or filter timeline events');
            const text = treeText(rendered);
            for (const expected of [
              '按时间自上而下 · 时间戳为 UTC', '模型回复', 'step-alpha', '@agent-alpha',
              'Deployment is degraded.', 'deepseek-chat', '120', '20', '140',
              '详情 · 1', 'finish_reason', 'search', 'call-success', '参数 · arguments',
              'JSON:{\"query\":\"deployment status\"}', '结果 · result',
              'JSON:{\"status\":\"degraded\"}', 'call-failure', '错误 · error',
              'upstream unavailable', '需要关注 · 等待人工确认', 'Approve deployment?',
              '120 ms', '140 tokens', 'provider unavailable',
            ]) assert.ok(text.includes(expected), 'timeline must retain ' + expected);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain(".tabs::-webkit-scrollbar { display: none; }");
        html.Should().Contain("display: inline-flex; flex: 0 0 auto; align-items: center; gap: 7px;");
    }

    [Fact]
    public async Task WorkflowObservatory_FailureEvidence_ShouldReachEveryViewAndEscapeNestedText()
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
            function createNode(tag, attrs = {}, content) {
              return {
                tag, attrs:{...attrs}, children:[], style:{},
                innerHTML:content == null ? '' : String(content),
                appendChild(child){ this.children.push(child); return child; },
                setAttribute(key,value){ this.attrs[key]=String(value); },
                insertAdjacentHTML(_position,value){ this.innerHTML += String(value); },
                addEventListener(){}, querySelector(){ return null; },
              };
            }
            function treeText(node) {
              return [node && node.innerHTML, ...((node && node.children) || []).map(treeText)].filter(Boolean).join('\n');
            }
            const escapeHtml = value => String(value == null ? '' : value).replace(/[&<>\"]/g, character => ({
              '&':'&amp;', '<':'&lt;', '>':'&gt;', '\"':'&quot;',
            })[character]);
            const cache = {runs:[{runId:'run-alpha',firstFailure:{
              stepId:'normalize',message:'activity <iframe>failure</iframe>',availability:'available'
            }}]};
            const context = {
              cache, el:createNode, esc:escapeHtml,
              document:{createDocumentFragment:()=>createNode('fragment')},
              KIND:{StepFinished:{label:'步骤完成'}}, kindIcon:()=> '[event]',
              STATUS_LABEL:{completed:'已完成',failed:'失败'},
              STEPTYPE_LABEL:{tool:'工具'}, REPLY_KINDS:new Set(['Message','TextMessage']),
              state:{expanded:new Set()}, clockUTC:value=>String(value).slice(11,19),
              renderReplyBubble:()=>createNode('reply'), renderToolCall:()=>createNode('tool'),
              renderDataDetails:()=>null,
            };
            vm.createContext(context);
            const evidenceStart = html.indexOf('function evidenceHasValue(');
            const evidenceEnd = html.indexOf('\nfunction renderDiagnosticStrip(', evidenceStart);
            assert.notEqual(evidenceStart, -1);
            assert.notEqual(evidenceEnd, -1);
            vm.runInContext(`
              ${functionSource('dataLookup', 'parseT')}
              ${html.slice(evidenceStart, evidenceEnd)}
              ${functionSource('renderDiagnosticStrip', 'renderDiagnosticItem')}
              ${functionSource('renderDiagnosticItem', 'renderDiagnostics')}
              ${functionSource('renderSteps', 'failureLogLines')}
              ${functionSource('failureLogLines', 'renderLogs')}
              ${functionSource('renderTimeline', 'renderToolCall')}
            `, context);

            const detail = {
              summary:{runId:'run-alpha',scopeId:'scope-alpha',workflowName:'workflow-alpha',status:'failed',stateVersion:9},
              reportVersion:'3.1',
              compilationError:'compile <script>bad()</script>',
              finalError:'final <img src=x onerror=bad()>',
              sections:{
                overview:{versionStatus:'aligned',detailStateVersion:9,sourceStateVersion:9,reason:''},
                steps:{versionStatus:'VersionMismatch',detailStateVersion:9,sourceStateVersion:8,reason:'stale <b>steps</b>'},
                timeline:{versionStatus:'unavailable',detailStateVersion:9,sourceStateVersion:0,reason:'missing <svg>timeline</svg>'},
              },
              recoveryCapability:{
                workflowDefinitionRevisionId:'rev-alpha',
                retryFailedStep:{eligibility:'unavailable',unavailableReason:'fix <a>access</a>',recommendedActions:['fix_access']},
              },
              diagnostics:[{severity:'error',code:'STEP_FAILED',message:'diagnostic failure'}],
              operations:[{
                kind:'tool',operationId:'op-alpha',sessionId:'session-alpha',toolCallId:'call-alpha',
                toolName:'code_execute',success:false,error:'operation <object>failed</object>',
                argumentsJson:'{\"source\":\"<code>bad</code>\"}',resultJson:'{\"stderr\":\"<stderr>tail</stderr>\"}',
                output:'operation <output>detail</output>',reasoningContent:'reasoning <why>detail</why>'
              }],
              steps:[{
                stepId:'normalize',displayName:'Normalize person',stepType:'tool_call',targetRole:'worker-alpha',
                workerId:'worker-alpha',success:false,outcome:'failed',error:'step <video>failed</video>',
                requestedAtUtc:'2026-08-14T01:00:00Z',completedAtUtc:'2026-08-14T01:00:01Z',durationMs:1000,
                failureOutput:'stderr head\n<script>syntax failure</script>\nstderr tail',failureOutputTruncated:true,
                failureOutcome:{kind:'execution_failed',detail:'nested <img>failure</img>'},
                recoveryFailureKind:'configuration',retryDisposition:'not_retryable',
                requestParameters:{source:'<input>unsafe</input>'},
                completionAnnotations:{reason:'<b>annotation</b>'},
                assignedVariable:'person',assignedValue:'<name>Ada</name>',requestedVariableName:'employee',
                nextStepId:'notify',branchKey:'error',
                fileItemResults:{sourceResultCount:45,resultsTruncated:true,results:[{
                  index:0,path:'report.txt',output:'partial',outputTruncated:true,
                  error:'<file>bad</file>',errorTruncated:true
                }]},
                voteAgreementDecision:{
                  agreed:false,output:'candidate output',outputTruncated:true,
                  reason:'<vote>split</vote>',reasonTruncated:true
                },
                outputPreview:'legacy preview'
              },{
                stepId:'notify',displayName:'Retry notification',stepType:'email_retry',targetRole:'retry-mailer',
                outcome:'waiting',requestedAtUtc:'2026-08-14T01:00:04Z',
                requestParameters:{retryAttempt:'2'},
                latestFailedAttempt:{
                  displayName:'Notify employee',stepType:'tool_call',targetRole:'mailer',
                  workerId:'worker-beta',success:false,error:'SMTP unavailable',
                  failureOutput:'latest failed attempt stderr',failureOutputTruncated:false,
                  retryDisposition:'retryable',requestedAtUtc:'2026-08-14T01:00:02Z',
                  completedAtUtc:'2026-08-14T01:00:03Z',durationMs:1000,
                  requestParameters:{recipientMode:'manager'},completionAnnotations:{provider:'smtp'},
                  fileItemResults:{sourceResultCount:0,resultsTruncated:true,results:[{
                    index:7,success:false,error:'retained unknown-count result'
                  }]}
                }
              }],
              timeline:[{
                kind:'StepFinished',stage:'step.completed',timestampUtc:'2026-08-14T01:00:01Z',
                stepId:'normalize',stepType:'tool',message:'failed',data:{error:'timeline <details>failure</details>'}
              },{
                kind:'RunStopped',stage:'workflow.stopped',timestampUtc:'2026-08-14T01:00:02Z',
                stepId:'normalize',stepType:'tool',message:'stopped <reason>detail</reason>',data:{}
              }]
            };

            const evidence = context.collectFailureEvidence(detail);
            assert.equal(context.evidenceHasValue(false), true, 'false is a real observed value');
            assert.equal(context.evidenceHasValue(0), true, 'zero is a real observed value');
            assert.equal(Object.hasOwn(evidence, 'sectionIssues'), false, 'section availability is not failure evidence');
            assert.equal(evidence.failedSteps.length, 1);
            assert.equal(evidence.retryWaitingSteps.length, 1);
            assert.equal(context.isFailedStep(detail.steps[1]), false, 'waiting retry is not a current failed step');
            assert.equal(context.isRetryWaitingStep(detail.steps[1]), true);
            const retryTimingFields = Object.fromEntries(context.stepTimingEvidenceFields(detail.steps[1]));
            assert.equal(retryTimingFields.currentRetryRequestedAtUtc, '2026-08-14T01:00:04Z');
            assert.equal(retryTimingFields.latestFailedRequestedAtUtc, '2026-08-14T01:00:02Z');
            assert.equal(retryTimingFields.latestFailedCompletedAtUtc, '2026-08-14T01:00:03Z');
            assert.equal(retryTimingFields.latestFailedDurationMs, 1000);
            assert.equal('requestedAtUtc' in retryTimingFields, false);
            assert.equal('completedAtUtc' in retryTimingFields, false);
            assert.equal('durationMs' in retryTimingFields, false, 'cross-attempt duration must not be presented');
            assert.equal(evidence.failedOperations.length, 1);
            assert.equal(evidence.timelineErrors.length, 2);
            assert.equal(evidence.firstFailure.stepId, 'normalize');

            const payload = JSON.parse(context.issuePayload(detail));
            assert.equal(payload.reportVersion, '3.1');
            assert.equal(payload.compilationError, detail.compilationError);
            assert.equal(payload.activityFirstFailure.message, 'activity <iframe>failure</iframe>');
            assert.equal(payload.sections.steps.reason, 'stale <b>steps</b>');
            assert.equal(payload.sectionIssues.length, 2, 'section diagnostics remain in the issue payload');
            assert.equal(payload.recoveryCapability.retryFailedStep.unavailableReason, 'fix <a>access</a>');
            assert.equal(payload.failedOperations[0].error, 'operation <object>failed</object>');
            assert.equal(payload.failedOperations[0].resultJson, '{"stderr":"<stderr>tail</stderr>"}');
            assert.equal(payload.failedSteps[0].workerId, 'worker-alpha');
            assert.equal(payload.failedSteps[0].completionAnnotations.reason, '<b>annotation</b>');
            assert.equal(payload.failedSteps[0].failureOutputTruncated, true);
            assert.equal(payload.failedSteps[0].fileItemResults.results[0].error, '<file>bad</file>');
            assert.equal(payload.failedSteps[0].fileItemResults.sourceResultCount, 45);
            assert.equal(payload.failedSteps[0].fileItemResults.sourceResultCountKnown, true);
            assert.equal(payload.failedSteps[0].fileItemResults.retainedResultCount, 1);
            assert.equal(payload.failedSteps[0].fileItemResults.resultsTruncated, true);
            assert.equal(payload.failedSteps[0].fileItemResults.results[0].outputTruncated, true);
            assert.equal(payload.failedSteps[0].fileItemResults.results[0].errorTruncated, true);
            assert.equal(payload.failedSteps[0].voteAgreementDecision.outputTruncated, true);
            assert.equal(payload.failedSteps[0].voteAgreementDecision.reasonTruncated, true);
            assert.equal(payload.retryWaitingLatestFailedAttempts.length, 1);
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].stepId, 'notify');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].currentRetry.requestedAtUtc, '2026-08-14T01:00:04Z');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].currentRetry.stepType, 'email_retry');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].currentRetry.requestParameters.retryAttempt, '2');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.requestedAtUtc, '2026-08-14T01:00:02Z');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.completedAtUtc, '2026-08-14T01:00:03Z');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.durationMs, 1000);
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.stepType, 'tool_call');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.requestParameters.recipientMode, 'manager');
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.fileItemResults.sourceResultCountKnown, false);
            assert.equal(payload.retryWaitingLatestFailedAttempts[0].latestFailedAttempt.fileItemResults.retainedResultCount, 1);
            assert.equal(payload.timelineErrors[0].error, 'timeline <details>failure</details>');

            const logs = context.failureLogLines(detail).join('\n');
            for (const expected of [
              'COMPILATION ERROR', 'ACTIVITY FIRST FAILURE', 'FAILED OPERATION',
              'worker-alpha', 'completionAnnotations', 'STEP FAILURE OUTPUT [normalize]',
              'STEP FAILURE OUTPUT TRUNCATED [normalize]', 'STEP NESTED EVIDENCE TRUNCATED [normalize]',
              'fileItemResults.results (collection)', 'fileItemResults.results[0].output', 'voteAgreementDecision.reason',
              'RETRY WAITING CURRENT ATTEMPT', 'RETRY WAITING LATEST FAILED ATTEMPT', 'latest failed attempt stderr',
              'email_retry', 'recipientMode', '源结果总数未知，当前保留 1 条',
              'TIMELINE ERROR', 'RECOVERY CAPABILITY'
            ]) assert.ok(logs.includes(expected), 'logs must retain ' + expected);
            assert.ok(!logs.includes('SECTION steps'), 'section diagnostics are not failure log lines');

            const panelText = treeText(context.renderFailureEvidence(detail));
            for (const expected of [
              '已捕获失败证据', '&lt;script&gt;syntax failure&lt;/script&gt;',
              '&lt;b&gt;annotation&lt;/b&gt;', 'fileItemResultsSummary',
              '&lt;vote&gt;split&lt;/vote&gt;', '&lt;stderr&gt;tail&lt;/stderr&gt;',
              '&lt;code&gt;bad&lt;/code&gt;', '&lt;output&gt;detail&lt;/output&gt;',
              '&lt;why&gt;detail&lt;/why&gt;', '保留首尾片段'
            ]) assert.ok(panelText.includes(expected), 'failure panel must retain escaped ' + expected);
            assert.ok(panelText.includes('等待重试 · 最近一次失败尝试'));
            assert.ok(panelText.includes('嵌套失败证据已在投影端按大小上限截断'));
            assert.ok(panelText.includes('fileItemResults.results[0].error'));
            assert.ok(panelText.includes('voteAgreementDecision.output'));
            assert.ok(panelText.includes('当前保留 1/45 条首尾样本'));
            assert.ok(panelText.includes('源结果总数未知，当前保留 1 条首尾样本'));
            assert.ok(!panelText.includes('<script>syntax failure</script>'));

            const completedWithSectionWarning = {
              summary:{runId:'run-completed',scopeId:'scope-alpha',workflowName:'workflow-alpha',status:'completed',stateVersion:10},
              reportVersion:'3.1', compilationError:'', finalError:'',
              sections:{executionPath:{versionStatus:'unavailable',detailStateVersion:10,sourceStateVersion:0,reason:'Execution path graph source version is unavailable.'}},
              diagnostics:[{severity:'warning',code:'section_unavailable',message:'Execution path graph source version is unavailable.',source:'observatory'}],
              operations:[],steps:[],timeline:[]
            };
            const completedEvidence = context.collectFailureEvidence(completedWithSectionWarning);
            assert.equal(context.failureEvidenceCount(completedEvidence), 0);
            assert.equal(treeText(context.renderFailureEvidence(completedWithSectionWarning)), '');
            const completedPayload = JSON.parse(context.issuePayload(completedWithSectionWarning));
            assert.equal(completedPayload.sectionIssues.length, 1);
            assert.equal(completedPayload.diagnostics[0].code, 'section_unavailable');
            const warningStrip = context.renderDiagnosticStrip(completedWithSectionWarning);
            assert.ok(warningStrip.attrs.class.includes('has-warning'));
            assert.ok(!warningStrip.attrs.class.includes('has-error'));
            assert.equal(warningStrip.attrs['aria-label'], '运行警告');
            const warningText = treeText(warningStrip);
            assert.ok(warningText.includes('运行警告'));
            assert.ok(warningText.includes('section_unavailable'));
            assert.ok(!warningText.includes('已捕获失败证据'));

            const oversized = label => label + '_HEAD\n' + 'x'.repeat(9000) + label + '_MIDDLE_MUST_NOT_RENDER' + 'y'.repeat(9000) + '\n' + label + '_TAIL';
            const oversizedOperation = {
              ...detail.operations[0],
              output:oversized('OUTPUT'), resultJson:oversized('RESULT'),
              reasoningContent:oversized('REASONING'), argumentsJson:oversized('ARGUMENTS')
            };
            const summarySnapshot = context.boundedOperationEvidenceSnapshot(oversizedOperation, 360);
            const detailSnapshot = context.boundedOperationEvidenceSnapshot(oversizedOperation, 8192);
            for (const field of ['output','resultJson','reasoningContent','argumentsJson']) {
              assert.ok(summarySnapshot[field].length <= 360, 'summary must bound ' + field);
              assert.ok(detailSnapshot[field].length <= 8192, 'detail must bound ' + field);
              assert.ok(summarySnapshot[field].includes(field === 'resultJson' ? 'RESULT_HEAD' : field === 'reasoningContent' ? 'REASONING_HEAD' : field === 'argumentsJson' ? 'ARGUMENTS_HEAD' : 'OUTPUT_HEAD'));
              assert.ok(!summarySnapshot[field].includes('_MIDDLE_MUST_NOT_RENDER'));
              assert.ok(!detailSnapshot[field].includes('_MIDDLE_MUST_NOT_RENDER'));
            }
            assert.deepEqual(
              [...summarySnapshot.uiEvidenceBounds.truncatedFields].sort(),
              ['argumentsJson','output','reasoningContent','resultJson']);

            const oversizedDetail = {...detail, operations:[oversizedOperation]};
            const oversizedPanelText = treeText(context.renderFailureEvidence(oversizedDetail));
            assert.ok(oversizedPanelText.includes('首屏摘要每字段最多显示 360 字符'));
            assert.ok(oversizedPanelText.includes('OUTPUT_HEAD'));
            assert.ok(oversizedPanelText.includes('OUTPUT_TAIL'));
            assert.ok(!oversizedPanelText.includes('_MIDDLE_MUST_NOT_RENDER'));

            const oversizedLogs = context.failureLogLines(oversizedDetail).join('\n');
            assert.ok(oversizedLogs.includes('RESULT_HEAD'));
            assert.ok(oversizedLogs.includes('RESULT_TAIL'));
            assert.ok(!oversizedLogs.includes('_MIDDLE_MUST_NOT_RENDER'));
            const oversizedPayload = JSON.parse(context.issuePayload(oversizedDetail));
            assert.ok(oversizedPayload.failedOperations[0].argumentsJson.length <= 8192);
            assert.ok(!oversizedPayload.failedOperations[0].argumentsJson.includes('_MIDDLE_MUST_NOT_RENDER'));

            const stepsText = treeText(context.renderSteps(detail));
            for (const expected of [
              'workerId', 'completionAnnotations', 'assignedVariable', 'assignedValue',
              'requestedVariableName', 'failureOutcome', 'fileItemResults', 'voteAgreementDecision',
              '&lt;script&gt;syntax failure&lt;/script&gt;', '&lt;file&gt;bad&lt;/file&gt;', '不代表完整步骤输出',
              '嵌套失败证据已在投影端按大小上限截断',
              '当前步骤正在等待重试', '不代表当前步骤已终止失败',
              'currentRetryRequestedAtUtc', 'latestFailedRequestedAtUtc', 'latestFailedCompletedAtUtc',
              'email_retry', 'recipientMode', '源结果总数未知'
            ]) assert.ok(stepsText.includes(expected), 'Steps must retain ' + expected);

            const timelineText = treeText(context.renderTimeline(detail));
            assert.ok(timelineText.includes('&lt;details&gt;failure&lt;/details&gt;'));
            assert.ok(!timelineText.includes('timeline <details>failure</details>'));
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain("function collectFailureEvidence(detail)");
        html.Should().Contain("function renderFailureEvidence(detail)");
        html.Should().Contain("function failureLogLines(detail)");
        html.Should().Contain("const operation=boundedOperationEvidenceSnapshot(sourceOperation,OPERATION_EVIDENCE_DETAIL_MAX_CHARS);");
        html.Should().Contain("const tool=boundedOperationEvidenceSnapshot(event.toolCall||{},OPERATION_EVIDENCE_DETAIL_MAX_CHARS);");
        html.Should().NotContain("完整 role/tool 输出见 Timeline");
    }

    [Fact]
    public async Task WorkflowObservatory_RequestTraceFeed_ShouldNormalizeCoverageAndScopeTheActivityRequest()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            function singleLineFunctionSource(name) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\n', start);
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, name + ' must end on its declaration line');
              return html.slice(start, end);
            }

            const context = {
              URLSearchParams,
              adminState: { isAdmin: false, currentScope: null },
              filterState: { status: '', origin: '', definition: '', schedule: '', from: '', to: '' },
              cache: { runs: ['preserved'], runFeed: { marker: 'preserved' } },
              state: { scenario: 'normal' },
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('isActivityRunFeedEnvelope', 'normalizeActivityRunFeed')}
              ${functionSource('normalizeActivityRunFeed', 'activityRunFeedRequestPath')}
              ${singleLineFunctionSource('activityRunFeedRequestPath')}
              ${functionSource('listQueryParams', 'hasActiveFilters')}
              ${functionSource('requestTraceCountLabel', 'requestTraceCoverageLabel')}
              ${functionSource('requestTraceCoverageLabel', 'requestTracePreview')}
            `, context);

            const feed = {
              items: [{ runId: 'run-alpha' }, { runId: 'run-beta' }],
              nextCursor: 'cursor-beta',
              hasMore: true,
              totalCount: 247,
            };
            assert.equal(vm.runInContext('isActivityRunFeedEnvelope', context)(feed), true);
            assert.equal(vm.runInContext('isActivityRunFeedEnvelope', context)({ items: [], hasMore: 'false' }), false);
            assert.equal(vm.runInContext('isActivityRunFeedEnvelope', context)({ hasMore: false }), false);

            const normalized = vm.runInContext('normalizeActivityRunFeed', context)(feed);
            assert.deepEqual(JSON.parse(JSON.stringify(normalized)), feed);
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('normalizeActivityRunFeed', context)({
              items: 'invalid', nextCursor: 7, hasMore: 'true', totalCount: -1,
            }))), {
              items: [], nextCursor: null, hasMore: false, totalCount: null,
            });
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('normalizeActivityRunFeed', context)(null))), {
              items: [], nextCursor: null, hasMore: false, totalCount: null,
            });

            assert.equal(
              vm.runInContext('activityRunFeedRequestPath', context)('?take=100&includeTotalCount=true'),
              '/api/workflow/observatory/activity-runs?take=100&includeTotalCount=true',
            );
            assert.equal(
              vm.runInContext('requestTraceCountLabel', context)(feed, 100),
              '100 / 247',
            );
            assert.equal(
              vm.runInContext('requestTraceCoverageLabel', context)(feed, 100),
              '显示最近 100 条，共 247 条；仍有更多请求轨迹',
            );
            assert.equal(
              vm.runInContext('requestTraceCountLabel', context)({ items: [], hasMore: true }, 100),
              '100+',
            );
            assert.equal(
              vm.runInContext('requestTraceCoverageLabel', context)({ items: [], hasMore: false, totalCount: 2 }, 2),
              '共 2 条请求轨迹',
            );

            assert.equal(
              vm.runInContext('listQueryParams', context)(),
              '?take=100&includeTotalCount=true',
            );
            assert.equal(
              vm.runInContext('listQueryParams', context)('cursor alpha+/='),
              '?take=100&includeTotalCount=true&cursor=cursor+alpha%2B%2F%3D',
            );
            assert.equal(
              vm.runInContext('listQueryParams', context)('cursor-alpha', 240),
              '?take=240&includeTotalCount=true&cursor=cursor-alpha',
            );
            assert.equal(
              vm.runInContext('listQueryParams', context)(null, 999),
              '?take=500&includeTotalCount=true',
            );
            assert.equal(
              vm.runInContext('listQueryParams', context)(null, -5),
              '?take=1&includeTotalCount=true',
            );
            context.adminState.isAdmin = true;
            context.adminState.currentScope = 'scope alpha';
            Object.assign(context.filterState, {
              status: 'failed',
              origin: 'ad-hoc-chat',
              definition: 'wf/alpha',
              schedule: 'schedule-alpha',
              from: '2026-08-12T00:00:00Z',
              to: '2026-08-13T00:00:00Z',
            });
            assert.equal(
              vm.runInContext('listQueryParams', context)(),
              '?scope=scope+alpha&status=failed&origin=ad-hoc-chat&definition=wf%2Falpha&schedule=schedule-alpha&from=2026-08-12T00%3A00%3A00Z&to=2026-08-13T00%3A00%3A00Z&take=100&includeTotalCount=true',
            );
            context.adminState.currentScope = '__all__';
            assert.match(vm.runInContext('listQueryParams', context)(), /^\?scope=__all__&/);

            context.api = async path => {
              assert.equal(path, '/api/workflow/observatory/activity-runs?invalid=1');
              return { items: [] };
            };
            context.activityRunFeedRequestPath = query => '/api/workflow/observatory/activity-runs' + query;
            context.listQueryParams = () => '?invalid=1';
            vm.runInContext(functionSource('refreshRuns', 'refreshDetail'), context);
            (async () => {
              assert.equal(await vm.runInContext('refreshRuns', context)(), false);
              assert.equal(context.state.scenario, 'globalError');
              assert.deepEqual(context.cache.runs, ['preserved']);
              assert.deepEqual(context.cache.runFeed, { marker: 'preserved' });
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain("throw new Error(\"invalid-activity-run-feed\")");
        html.Should().NotContain("/api/workflow/observatory/runs/activity");
    }

    [Fact]
    public async Task WorkflowObservatory_OperationLedger_ShouldKeepRepeatedSessionRepliesAndHonestDurations()
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

            const context = {
              Date, Number,
              REPLY_KINDS: new Set(['Message', 'TextMessage']),
              DATA_MODEL_KEYS: ['model', 'model_id', 'modelId'],
            };
            const evidenceStart = html.indexOf('function evidenceHasValue(');
            const evidenceEnd = html.indexOf('\nfunction activityFirstFailure(', evidenceStart);
            assert.notEqual(evidenceStart, -1, 'operation evidence helpers must exist');
            assert.notEqual(evidenceEnd, -1, 'activityFirstFailure must follow operation evidence helpers');
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('dataLookup', 'parseT')}
              ${functionSource('parseT', 'clockUTC')}
              ${html.slice(evidenceStart, evidenceEnd)}
              ${functionSource('operationTimestamp', 'openOperationRecord')}
            `, context);

            const detail = {
              summary: {
                runId: 'run-alpha',
                startedAtUtc: '2026-08-14T01:00:00.000Z',
              },
              input: 'Inspect deployment status',
              inputSummary: 'Inspect deployment status',
              timeline: [
                {stage: 'workflow.start', timestampUtc: '2026-08-14T01:00:00.000Z'},
                {
                  kind: 'Message', stage: 'role.reply', agentId: 'assistant',
                  timestampUtc: '2026-08-14T01:00:00.100Z', content: '',
                  data: {sessionId: 'session-shared', model: 'deepseek-chat'},
                },
                {
                  kind: 'ToolCall', stage: 'tool.call', message: 'search',
                  timestampUtc: '2026-08-14T01:00:00.200Z',
                  toolCall: {
                    callId: 'call-search', toolName: 'search',
                    argumentsJson: '{"query":"deployment status"}',
                    resultJson: '{"status":"degraded"}', success: true, error: '',
                  },
                },
                {
                  kind: 'TextMessage', stage: 'role.reply', agentId: 'assistant',
                  timestampUtc: '2026-08-14T01:00:00.300Z', content: 'Deployment is degraded.',
                  data: {session_id: 'session-shared', model: 'deepseek-chat'},
                },
              ],
            };

            const records = context.buildOperationRecords(detail);
            assert.deepEqual(JSON.parse(JSON.stringify(records.map(record => record.type))),
              ['input', 'model', 'tool', 'model']);
            assert.equal(records.length, 4);
            const models = records.filter(record => record.type === 'model');
            assert.equal(models.length, 2, 'each LLM reply is a separate historical operation');
            assert.equal(models[0].sessionId, 'session-shared');
            assert.equal(models[1].sessionId, 'session-shared');
            assert.notEqual(models[0].key, models[1].key,
              'a shared role-chat session cannot collapse separate LLM replies');
            assert.equal(models[0].content, '', 'tool-call-only replies remain inspectable');
            assert.equal(models[1].content, 'Deployment is degraded.');

            const tool = records.find(record => record.type === 'tool');
            assert.equal(tool.key, 'tool:run-alpha:call-search');
            assert.equal(tool.tool.argumentsJson, '{"query":"deployment status"}');
            assert.equal(tool.tool.resultJson, '{"status":"degraded"}');
            assert.equal(tool.status, '成功');
            for (const record of records) {
              assert.equal(record.durationMs, null,
                'a committed point must not be presented as an invented duration interval');
            }

            const oversizedLegacyContent = 'LEGACY_HEAD\n' + 'x'.repeat(9000) +
              'LEGACY_MIDDLE_MUST_NOT_RENDER' + 'y'.repeat(9000) + '\nLEGACY_TAIL';
            const oversizedLegacyRecords = context.buildOperationRecords({
              summary: {runId: 'run-legacy-large', startedAtUtc: '2026-08-14T01:00:00.000Z'},
              timeline: [{
                kind: 'Message', stage: 'role.reply', agentId: 'assistant',
                timestampUtc: '2026-08-14T01:00:00.100Z', content: oversizedLegacyContent,
                data: {sessionId: 'session-large', model: 'deepseek-chat'},
              }],
            });
            const oversizedLegacyModel = oversizedLegacyRecords.find(record => record.type === 'model');
            assert.ok(oversizedLegacyModel.content.length <= 8192);
            assert.ok(oversizedLegacyModel.preview.length <= 360);
            assert.ok(oversizedLegacyModel.content.includes('LEGACY_HEAD'));
            assert.ok(oversizedLegacyModel.content.includes('LEGACY_TAIL'));
            assert.ok(!oversizedLegacyModel.content.includes('LEGACY_MIDDLE_MUST_NOT_RENDER'));
            assert.equal(oversizedLegacyModel.event.content, oversizedLegacyModel.content,
              'renderReplyBubble must receive the bounded event content');
            assert.deepEqual(
              JSON.parse(JSON.stringify(oversizedLegacyModel.evidenceBounds.truncatedFields)),
              ['content']);
            const whitespaceLegacyModel = context.buildOperationRecords({
              summary: {runId: 'run-legacy-whitespace'},
              timeline: [{
                kind: 'Message', stage: 'role.reply', agentId: 'assistant',
                timestampUtc: '2026-08-14T01:00:00.100Z', content: ' '.repeat(9000), data: {},
              }],
            }).find(record => record.type === 'model');
            assert.equal(whitespaceLegacyModel.toolCallOnly, true,
              'the omission marker must not turn an oversized blank reply into text content');

            const typedRecords = context.buildOperationRecords({
              summary: {runId: 'run-typed', startedAtUtc: '2026-08-14T01:00:00.000Z'},
              inputSummary: 'Inspect deployment status',
              timeline: [],
              operations: [
                {
                  kind: 'tool', operationId: 'tool-1', toolCallId: 'call-1', toolName: 'search',
                  progressSequence: 20,
                  startedAtUtc: '2026-08-14T01:00:00.200Z',
                  completedAtUtc: '2026-08-14T01:00:00.300Z', success: true,
                },
                {
                  kind: 'model', operationId: 'model-0', round: 0,
                  progressSequence: 12,
                  startedAtUtc: '2026-08-14T01:00:00.800Z',
                  completedAtUtc: '2026-08-14T01:00:00.900Z', success: true,
                },
              ],
            });
            assert.deepEqual(JSON.parse(JSON.stringify(typedRecords.map(record => record.type))),
              ['input', 'model', 'tool'], 'committed sequence outranks skewed timestamps');
            assert.equal(typedRecords[1].round, 0);
            assert.equal(typedRecords[1].title, 'Model round 0');
            assert.equal(typedRecords[1].durationMs, 100);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain("const boundedContent=boundedOperationEvidenceValue(rawContent,OPERATION_EVIDENCE_DETAIL_MAX_CHARS);");
        html.Should().Contain("content,reasoning:\"\",event:{...event,content},data,evidenceBounds");
    }

    [Fact]
    public async Task WorkflowObservatory_RequestTracePagination_ShouldAppendIdempotentlyAndRecoverTransientNotFound()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            function singleLineFunctionSource(name) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\n', start);
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, name + ' must end on its declaration line');
              return html.slice(start, end);
            }

            const firstPageRuns = [
              { runId: 'run-new', status: 'running', updatedAtUtc: '2026-08-13T10:00:00Z' },
              { runId: 'run-overlap', status: 'running', updatedAtUtc: '2026-08-13T09:00:00Z', page: 1 },
            ];
            const requestedPaths = [];
            const context = {
              Map,
              URLSearchParams,
              Date,
              adminState: { isAdmin: true, currentScope: 'scope-alpha' },
              filterState: { status: 'running', origin: '', definition: '', schedule: '', from: '', to: '' },
              cache: {
                runs: firstPageRuns,
                runFeed: {
                  items: firstPageRuns,
                  nextCursor: 'cursor older+/=',
                  hasMore: true,
                  totalCount: 5,
                },
                details: {},
              },
              state: { scenario: 'normal', loadingMore: false },
              renderCalls: 0,
              render: () => { context.renderCalls++; },
              api: async path => {
                requestedPaths.push(path);
                return {
                  items: [
                    { runId: 'run-overlap', status: 'completed', updatedAtUtc: '2026-08-13T09:30:00Z', page: 2 },
                    { runId: 'run-old', status: 'completed', updatedAtUtc: '2026-08-13T08:00:00Z' },
                  ],
                  nextCursor: null,
                  hasMore: false,
                  totalCount: 5,
                };
              },
              lastRunsSig: '',
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('isActivityRunFeedEnvelope', 'normalizeActivityRunFeed')}
              ${functionSource('normalizeActivityRunFeed', 'activityRunFeedRequestPath')}
              ${singleLineFunctionSource('activityRunFeedRequestPath')}
              ${functionSource('listQueryParams', 'hasActiveFilters')}
              ${functionSource('runsSig', 'detailSig')}
              ${functionSource('loadMoreRequestTraces', 'refreshDetail')}
            `, context);

            (async () => {
              assert.equal(await vm.runInContext('loadMoreRequestTraces', context)(), true);
              assert.deepEqual(requestedPaths, [
                '/api/workflow/observatory/activity-runs?scope=scope-alpha&status=running&take=100&includeTotalCount=true&cursor=cursor+older%2B%2F%3D',
              ]);
              assert.deepEqual(JSON.parse(JSON.stringify(context.cache.runs.map(run => run.runId))), [
                'run-new', 'run-overlap', 'run-old',
              ]);
              assert.equal(context.cache.runs[1].page, 2, 'a repeated run id is updated in place rather than duplicated');
              assert.equal(context.cache.runFeed.items, context.cache.runs);
              assert.equal(context.cache.runFeed.totalCount, 5);
              assert.equal(context.cache.runFeed.hasMore, false);
              assert.equal(context.cache.runFeed.nextCursor, null);
              assert.equal(context.state.loadingMore, false);
              assert.equal(context.renderCalls, 2, 'loading and settled states both render');

              assert.equal(await vm.runInContext('loadMoreRequestTraces', context)(), false);
              assert.equal(requestedPaths.length, 1, 'the exhausted feed cannot fetch the same page again');
              assert.equal(context.renderCalls, 2);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var paginationResult = await RunNodeAsync(script, html);

        paginationResult.ExitCode.Should().Be(0, paginationResult.Error + paginationResult.Output);

        const string recoveryScript = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('async function poll()');
            const end = html.indexOf('\nfunction startPolling()', start);
            assert.notEqual(start, -1, 'poll must exist in the served observatory asset');
            assert.notEqual(end, -1, 'startPolling must follow poll');

            const context = {
              Date,
              document: { hidden: false, getElementById: () => null },
              state: {
                signedIn: true,
                scenario: 'notFound',
                selectedRunId: 'run-eventually-visible',
                lastSyncedAtUtc: null,
              },
              cache: { runs: [], runFeed: { items: [], hasMore: false, totalCount: 0 }, details: {} },
              lastRunsSig: 'stable',
              lastDetailSig: 'none',
              refreshDetailCalls: 0,
              renderCalls: 0,
              refreshRuns: async () => true,
              runsSig: () => 'stable',
              detailSig: detail => detail ? 'recovered' : 'none',
              refreshDetail: async runId => {
                context.refreshDetailCalls++;
                assert.equal(runId, 'run-eventually-visible');
                if (context.refreshDetailCalls === 1) return false;
                context.cache.details[runId] = { summary: { status: 'running', stateVersion: 1 } };
                context.state.scenario = 'normal';
                return true;
              },
              render: () => { context.renderCalls++; },
              setTimeout,
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async () => {
              await vm.runInContext('poll', context)();
              assert.equal(context.refreshDetailCalls, 1);
              assert.equal(context.state.scenario, 'notFound');
              assert.equal(context.renderCalls, 0);
              assert.equal(context.state.lastSyncedAtUtc, null);

              await vm.runInContext('poll', context)();
              assert.equal(context.refreshDetailCalls, 2, 'notFound remains eligible for a recovery probe');
              assert.equal(context.state.scenario, 'normal');
              assert.equal(context.renderCalls, 1);
              assert.ok(context.state.lastSyncedAtUtc);

              context.state.polling = true;
              await vm.runInContext('poll', context)();
              assert.equal(context.refreshDetailCalls, 2, 'an in-flight poll prevents overlapping requests');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var recoveryResult = await RunNodeAsync(recoveryScript, html);

        recoveryResult.ExitCode.Should().Be(0, recoveryResult.Error + recoveryResult.Output);
        html.Should().Contain("class:\"trace-load-more\"");
        html.Should().Contain("加载更早的请求轨迹");
        html.Should().Contain("if(state.selectedRunId){");
        html.Should().Contain("if(document.hidden || !state.signedIn || state.polling) return;");
        html.Should().Contain("state.polling=false;");
        html.Should().NotContain("state.selectedRunId && state.scenario !== \"notFound\"");
    }

    [Fact]
    public async Task WorkflowObservatory_RequestTraceListRaces_ShouldPreserveLoadedWindowAndDiscardStalePages()
    {
        var html = await GetObservatoryHtmlAsync();
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '('),
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start),
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            function singleLineFunctionSource(name) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\n', start);
              assert.notEqual(start, -1, name + ' must exist in the served observatory asset');
              assert.notEqual(end, -1, name + ' must end on its declaration line');
              return html.slice(start, end);
            }

            function installRefresh(context) {
              vm.createContext(context);
              vm.runInContext(`
                ${functionSource('isActivityRunFeedEnvelope', 'normalizeActivityRunFeed')}
                ${functionSource('normalizeActivityRunFeed', 'activityRunFeedRequestPath')}
                ${singleLineFunctionSource('activityRunFeedRequestPath')}
                ${functionSource('listQueryParams', 'hasActiveFilters')}
                ${functionSource('refreshRuns', 'loadMoreRequestTraces')}
              `, context);
            }

            function installLoadMore(context) {
              vm.createContext(context);
              vm.runInContext(`
                ${functionSource('isActivityRunFeedEnvelope', 'normalizeActivityRunFeed')}
                ${functionSource('normalizeActivityRunFeed', 'activityRunFeedRequestPath')}
                ${singleLineFunctionSource('activityRunFeedRequestPath')}
                ${functionSource('listQueryParams', 'hasActiveFilters')}
                ${functionSource('runsSig', 'detailSig')}
                ${functionSource('loadMoreRequestTraces', 'refreshDetail')}
              `, context);
            }

            (async () => {
              const oldRuns = Array.from({ length: 600 }, (_, index) => {
                const number = String(index + 1).padStart(3, '0');
                return {
                  runId: `old-${number}`,
                  stateVersion: 1,
                  status: 'completed',
                  updatedAtUtc: `2026-08-12T${String(index % 24).padStart(2, '0')}:00:00Z`,
                };
              });
              const insertedRuns = Array.from({ length: 10 }, (_, index) => ({
                runId: `new-${String(index + 1).padStart(2, '0')}`,
                stateVersion: 1,
                status: 'running',
                updatedAtUtc: '2026-08-13T12:00:00Z',
              }));
              const refreshedOldHead = oldRuns.slice(0, 490).map(run => ({
                ...run,
                stateVersion: 2,
                source: 'latest-head',
              }));
              const refreshPaths = [];
              const refreshContext = {
                Map,
                URLSearchParams,
                adminState: { isAdmin: false, currentScope: null },
                filterState: { status: '', origin: '', definition: '', schedule: '', from: '', to: '' },
                cache: {
                  runs: oldRuns,
                  runFeed: {
                    items: oldRuns,
                    nextCursor: 'old-loaded-window-cursor',
                    hasMore: true,
                    totalCount: 600,
                  },
                },
                state: { scenario: 'normal', loadingMore: false, listRequestEpoch: 7 },
                api: async path => {
                  refreshPaths.push(path);
                  return {
                    items: [...insertedRuns, ...refreshedOldHead],
                    nextCursor: 'refreshed-head-cursor',
                    hasMore: true,
                    totalCount: 610,
                  };
                },
              };
              installRefresh(refreshContext);

              assert.equal(await vm.runInContext('refreshRuns', refreshContext)(), true);
              assert.deepEqual(refreshPaths, [
                '/api/workflow/observatory/activity-runs?take=500&includeTotalCount=true',
              ]);
              assert.equal(refreshContext.state.listRequestEpoch, 8);
              assert.equal(refreshContext.cache.runs.length, 610);
              const refreshedIds = refreshContext.cache.runs.map(run => run.runId);
              const uniqueIds = new Set(refreshedIds);
              assert.equal(uniqueIds.size, 610, 'head refresh must not duplicate existing run ids');
              for (let index = 1; index <= 600; index++) {
                assert.equal(uniqueIds.has(`old-${String(index).padStart(3, '0')}`), true,
                  `loaded run old-${String(index).padStart(3, '0')} must survive the bounded head refresh`);
              }
              for (let index = 1; index <= 10; index++) {
                assert.equal(uniqueIds.has(`new-${String(index).padStart(2, '0')}`), true);
              }
              assert.equal(refreshContext.cache.runs[500].runId, 'old-491');
              assert.equal(refreshContext.cache.runs[509].runId, 'old-500');
              assert.equal(refreshContext.cache.runs[609].runId, 'old-600');
              assert.equal(refreshContext.cache.runs.find(run => run.runId === 'old-001').stateVersion, 2);
              assert.equal(refreshContext.cache.runs.find(run => run.runId === 'old-001').source, 'latest-head');
              assert.equal(refreshContext.cache.runFeed.items, refreshContext.cache.runs);
              assert.equal(refreshContext.cache.runFeed.nextCursor, 'old-loaded-window-cursor');
              assert.equal(refreshContext.cache.runFeed.hasMore, true);
              assert.equal(refreshContext.cache.runFeed.totalCount, 610);

              const refreshCount = refreshPaths.length;
              const epochBeforeBusyPoll = refreshContext.state.listRequestEpoch;
              refreshContext.state.loadingMore = true;
              assert.equal(await vm.runInContext('refreshRuns', refreshContext)(), false);
              assert.equal(refreshPaths.length, refreshCount, 'poll refresh must not race an active page request');
              assert.equal(refreshContext.state.listRequestEpoch, epochBeforeBusyPoll);

              let resolveOldPage;
              const stalePaths = [];
              const oldPageResponse = new Promise(resolve => { resolveOldPage = resolve; });
              const staleContext = {
                Map,
                URLSearchParams,
                adminState: { isAdmin: false, currentScope: null },
                filterState: { status: '', origin: '', definition: '', schedule: '', from: '', to: '' },
                cache: {
                  runs: [{ runId: 'old-filter-run', status: 'running', updatedAtUtc: '2026-08-13T10:00:00Z' }],
                  runFeed: {
                    items: [],
                    nextCursor: 'stale page cursor',
                    hasMore: true,
                    totalCount: 2,
                  },
                },
                state: { scenario: 'normal', loadingMore: false, listRequestEpoch: 20 },
                lastRunsSig: 'replacement-signature',
                renderCalls: 0,
                render: () => { staleContext.renderCalls++; },
                api: path => {
                  stalePaths.push(path);
                  return oldPageResponse;
                },
              };
              installLoadMore(staleContext);

              const staleRequest = vm.runInContext('loadMoreRequestTraces', staleContext)();
              assert.equal(staleContext.state.loadingMore, true);
              assert.equal(staleContext.state.listRequestEpoch, 21);
              assert.equal(staleContext.renderCalls, 1);
              assert.deepEqual(stalePaths, [
                '/api/workflow/observatory/activity-runs?take=100&includeTotalCount=true&cursor=stale+page+cursor',
              ]);

              const replacementRuns = [{ runId: 'new-filter-run', status: 'completed' }];
              const replacementFeed = {
                items: replacementRuns,
                nextCursor: null,
                hasMore: false,
                totalCount: 1,
              };
              const replacementCache = { runs: replacementRuns, runFeed: replacementFeed, details: {} };
              staleContext.state.listRequestEpoch++;
              staleContext.state.loadingMore = false;
              staleContext.cache = replacementCache;
              resolveOldPage({
                items: [{ runId: 'stale-page-run', status: 'completed' }],
                nextCursor: null,
                hasMore: false,
                totalCount: 2,
              });

              assert.equal(await staleRequest, false);
              assert.equal(staleContext.cache, replacementCache);
              assert.equal(staleContext.cache.runs, replacementRuns);
              assert.equal(staleContext.cache.runFeed, replacementFeed);
              assert.deepEqual(staleContext.cache.runs.map(run => run.runId), ['new-filter-run']);
              assert.equal(staleContext.state.loadingMore, false);
              assert.equal(staleContext.state.scenario, 'normal');
              assert.equal(staleContext.lastRunsSig, 'replacement-signature');
              assert.equal(staleContext.renderCalls, 1,
                'a stale page may render its initial loading state but cannot render after query reset');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain("if(state.loadingMore) return false;");
        html.Should().Contain("requestEpoch!==state.listRequestEpoch");
        html.Should().Contain("if(requestEpoch===state.listRequestEpoch)");
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
                ["Aevatar:BackendConsole:OidcAuthority"] = "https://authority.example.test",
                ["Aevatar:BackendConsole:OidcClientId"] = "client-example",
                ["Aevatar:BackendConsole:OidcScope"] = "openid profile",
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://api.example.test",
                ["Aevatar:NyxId:Authority"] = "https://authority.example.test",
                ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://nyxid.internal:3001",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);
        services.AddLogging();
        return services.BuildServiceProvider();
    }
}
