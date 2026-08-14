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
            html.Should().Contain("\"enableStudioWireInspector\":false");
            html.Should().NotContain("class=\"studio-tabs\"");
            html.Should().Contain("<div class=\"group-label\">当前实录</div>");
            html.Should().Contain("name=\"color-scheme\" content=\"only light\"");
            html.Should().NotContain("themeButton");
            html.Should().NotContain("workflow: \"studio\"");
        }
    }

    [Fact]
    public async Task WorkflowStudio_ChatTransport_ShouldMapOnlyCanonicalAssistantCommands()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                source.indexOf('function ' + name + '('),
                source.indexOf('async function ' + name + '('),
                source.indexOf('export function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                source.indexOf('\nfunction ' + nextName + '(', start),
                source.indexOf('\nasync function ' + nextName + '(', start),
                source.indexOf('\nexport function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served Studio transport');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return source.slice(start, end).replace(/^export /, '');
            }

            const context = {
              JSON,
              structuredClone,
              crypto:{randomUUID:()=> 'generated-alpha'}
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('mapAttachment', 'canonicalAssistantRequest')}
              ${functionSource('canonicalAssistantRequest', 'forwardAssistant')}
            `, context);

            assert.deepEqual(JSON.parse(JSON.stringify(context.canonicalAssistantRequest({
              surface:'nyxid-chat', type:'text', clientRequestId:'request-first',
              prompt:'Create a workflow', attachment:null
            }))), {
              type:'text', clientRequestId:'request-first', prompt:'Create a workflow'
            });
            assert.deepEqual(JSON.parse(JSON.stringify(context.canonicalAssistantRequest({
              surface:'nyxid-chat', type:'text', conversationId:'conversation-alpha',
              clientRequestId:'request-second', prompt:'Inspect this file',
              attachment:{name:'input.txt',mediaType:'text/plain',dataBase64:'aGVsbG8='}
            }))), {
              type:'text', conversationId:'conversation-alpha',
              clientRequestId:'request-second', prompt:'Inspect this file',
              inputParts:[{type:'file',name:'input.txt',mediaType:'text/plain',dataBase64:'aGVsbG8='}]
            });
            assert.deepEqual(JSON.parse(JSON.stringify(context.canonicalAssistantRequest({
              surface:'nyxid-chat', type:'approval.resolve', conversationId:'conversation-alpha',
              requestId:'approval-alpha', approved:true, reason:'Approved by user'
            }, 'approval-alpha'))), {
              type:'approval.resolve', conversationId:'conversation-alpha',
              requestId:'approval-alpha', approved:true, reason:'Approved by user',
              clientRequestId:'client-approval-approval-alpha'
            });
            assert.deepEqual(JSON.parse(JSON.stringify(context.canonicalAssistantRequest({
              surface:'nyxid-chat', type:'task.stop', conversationId:'conversation-alpha',
              turnId:'turn-alpha', stopRequestId:'stop-alpha', clientRequestId:'client-stop-alpha',
              expectedStateVersion:7
            }))), {
              type:'task.stop', conversationId:'conversation-alpha', turnId:'turn-alpha',
              stopRequestId:'stop-alpha', clientRequestId:'client-stop-alpha', expectedStateVersion:7
            });
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        transport.Should().Contain("authorizedFetch(\"/api/chat\"");
        transport.Should().Contain("\"Idempotency-Key\": clientRequestId");
        transport.Should().NotContain("workflow: \"studio\"");
    }

    [Fact]
    public async Task WorkflowStudio_AssistantAssets_ShouldShipNyxIdV4FeatureParity()
    {
        var html = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetStudioPage);
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        var readiness = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantReadiness);
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        var blocks = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantBlocks);
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        var styles = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantStyles);
        var lucide = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantLucide);
        var marked = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantMarked);
        var purify = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantPurify);

        app.Should().Contain("import \"./transport.js?v=20260814-m46-nyxid-api-routing\"");
        app.Should().Contain("async function sendPrompt(");
        app.Should().Contain("async function loadConversations(");
        app.Should().Contain("async function refreshActorState(");
        app.Should().Contain("async function submitActorControl(");
        app.Should().Contain("async function submitNeedsYouDecision(");
        app.Should().Contain("async function submitPendingInputFromComposer(");
        app.Should().Contain("async function submitComposer(");
        app.Should().Contain("async function loadReadiness(");
        app.Should().Contain("describeReadinessFailure(state.readiness.error)");
        app.Should().Contain("state.pendingFirstTurn ||=");
        app.Should().Contain("已受理，等待 Actor 确认");
        app.Should().Contain("async function submitApproval(");
        app.Should().Contain("async function submitActionContinuation(");
        app.Should().Contain("async function selectAttachment(");
        app.Should().Contain("conversationStates: new Map()");
        app.Should().Contain("function createRequestTrace(entry, run)");
        app.Should().Contain("currentTraceKey: null");
        app.Should().Contain("function currentRequestRun(");
        app.Should().Contain("conversation.run = state.run;");
        app.Should().Contain("function switchWorkspaceView(view,");
        app.Should().Contain("function isReviewingHistoricalTrace(");
        app.Should().Contain("queueRequestTraceRender(conversation)");
        app.Should().Contain("function renderTraceOperations(entry, trace)");
        app.Should().Contain("function renderTraceOperationOverview(trace)");
        app.Should().Contain("if (!record && !explicitKey && trace.activeModelOperationKey)");
        app.Should().Contain("function selectTraceOperation(entry, trace, key,");
        app.Should().Contain("trace.selectedOperationKey = key;");
        app.Should().Contain("renderTraceOperations(entry, selectedRequestTrace(entry) || traces[0]);");
        app.Should().Contain("function renderTraceOperationInspector(trace)");
        app.Should().Contain("dom.traceOperationSection.classList.toggle(\"hidden\", !record);");
        app.Should().Contain("dom.traceOperationKindFact.textContent = traceOperationKindLabel(record.kind);");
        app.Should().Contain("const inputValue = [String(record.input || \"\"), toolCatalog]");
        app.Should().Contain("dom.traceOperationInputFact.textContent = inputValue;");
        app.Should().Contain("record.reasoning ? `Reasoning:");
        app.Should().Contain("dom.traceOperationOutputFact.textContent = outputValue;");
        protocol.Should().Contain("export function normalizeFrame(");
        protocol.Should().Contain("export function validateActionContinuation(");
        protocol.Should().Contain("schemaVersion !== 4");
        protocol.Should().Contain("\"nyxid.input.request\": \"input_requested\"");
        protocol.Should().Contain("\"nyxid.approval.request\": \"approval_requested\"");
        protocol.Should().Contain("value.step = normalizeStep(value.step)");
        actorState.Should().Contain("export function reduceActorEvent(");
        actorState.Should().Contain("export function applyCurrentStateResult(");
        actorState.Should().Contain("pendingInput: null");
        actorState.Should().Contain("latestApprovalResolution: null");
        readiness.Should().Contain("export function normalizeReadinessSnapshot(");
        readiness.Should().Contain("Readiness snapshot contains secret fields");
        transport.Should().Contain("/api/v1/assistant/readiness");
        transport.Should().Contain("const errorCode = refreshResult.errorCode");
        transport.Should().Contain("authorizedFetch(\"/api/chat\"");
        transport.Should().Contain("ADR-0018");
        transport.Should().NotContain("append(\"resource\"");
        blocks.Should().Contain("export function buildConnectCardBlock(");
        html.Should().Contain("id=\"readinessPanel\"");
        html.Should().Contain("id=\"readinessRecovery\"");
        html.Should().Contain("id=\"readinessRecoveryButton\"");
        html.Should().Contain("id=\"needsYouFilterButton\"");
        html.Should().NotContain("id=\"taskPhaseList\"");
        html.Should().Contain("id=\"composerInputRequest\"");
        html.Should().Contain("class=\"content-view-switch\"");
        html.Should().Contain("id=\"requestTraceList\"");
        html.Should().Contain("Operation ledger");
        html.Should().Contain("id=\"traceOperationOverview\"");
        html.Should().Contain("aria-label=\"Input、Model、Tools Duration 概览\"");
        html.Should().Contain("id=\"traceOperationList\"");
        html.Should().Contain("role=\"listbox\" aria-label=\"按时间排列的操作记录\"");
        html.Should().Contain("id=\"traceOperationSection\"");
        html.Should().Contain("id=\"traceOperationInputFact\"");
        html.Should().Contain("id=\"traceOperationOutputFact\"");
        html.Should().Contain("id=\"traceClientRequestFact\"");
        html.Should().Contain("class=\"hidden\" id=\"eventsTabButton\"");
        html.Should().Contain("/workflow/studio/assets/vendor/lucide.min.js");
        html.Should().Contain("/workflow/studio/assets/vendor/marked.min.js");
        html.Should().Contain("/workflow/studio/assets/vendor/purify.min.js");
        html.Should().NotContain("https://unpkg.com");
        lucide.Should().Contain("@license lucide v0.563.0 - ISC");
        marked.Should().Contain("marked v15.0.12");
        purify.Should().Contain("DOMPurify 3.2.6");
        styles.Should().Contain(".connect-card");
        styles.Should().Contain(".readiness-panel");
        styles.Should().Contain(".needs-you-panel");
        styles.Should().Contain(".history-filter");
        styles.Should().Contain(".actor-plan-meta");
        styles.Should().Contain(".actor-substeps");
        styles.Should().Contain(".actor-task.collapsed");
        styles.Should().Contain(".cc-progress");
        styles.Should().Contain(".activity-card.collapsed");
        styles.Should().Contain(".content-view-switch");
        styles.Should().Contain(".request-trace-row");
        styles.Should().Contain(".request-trace-readonly");
        styles.Should().Contain(".trace-operation-ledger");
        styles.Should().Contain(".trace-operation-lane");
        styles.Should().Contain(".trace-operation-row");
        styles.Should().Contain(".trace-operation-detail");
        styles.Should().Contain("--assistant-card-max-width: 720px");
        styles.Should().Contain("--assistant-card-inline-gutter: 40px");
        styles.Should().Contain("--workspace-max-width: 1240px");
        styles.Should().Contain("--sidebar-width: 240px");
        styles.Should().Contain("--conversation-max-width: 760px");
        styles.Should().Contain("--conversation-inline-gutter: 40px");
        styles.Should().Contain("width: min(448px, calc(100% - 48px))");
        styles.Should().Contain("grid-template-columns: var(--sidebar-width) minmax(0, 1fr)");
        app.Should().Contain("展开计划详情");
        app.Should().Contain("root.dataset.collapsed = \"false\"");
        app.Should().NotContain("root.dataset.collapsed = \"true\"");
        app.Should().Contain("state.config.enableStudioWireInspector === true && state.auth.authenticated");
        transport.Should().Contain("backendConfig.enableStudioWireInspector === true");
        app.Should().NotContain("https://aevatar-console-backend-api.aevatar.ai");
        app.Should().NotContain("https://nyx-api.chrono-ai.fun");
        app.Should().Contain("cc-progress-step");
        app.Should().NotContain("function setStudioTab(tab)");
        app.Should().NotContain("尚未取得必需能力的有效证明");
        app.Should().NotContain("暂时无法确认运行准备状态");
        html.Should().NotContain("id=\"assistantNavButton\"");
        html.Should().NotContain("id=\"openSettingsNav\"");
        styles.Should().NotContain(".studio-tabs");
        styles.Should().Contain(".composer-wrap {\n  position: relative;");
        styles.Should().Contain(".chat-column {\n  min-width: 0;\n  min-height: 0;\n  height: 100%;\n  overflow: hidden;");
        app.Should().Contain("await submitActorControl(\"steer\", null, instruction)");
        app.Should().Contain("type: \"input.resolve\"");
        app.Should().NotContain("freeText.className = \"needs-you-free-text\"");
        styles.Should().Contain("@media (max-width:");
        html.Should().Contain("<meta name=\"color-scheme\" content=\"only light\"");
        html.Should().Contain("app.js?v=20260814-m46-nyxid-api-routing");
        html.Should().Contain("styles.css?v=20260814-m46-nyxid-api-routing");
        app.Should().Contain("transport.js?v=20260814-m46-nyxid-api-routing");
        app.Should().Contain("readiness.js?v=20260814-m46-nyxid-api-routing");
        transport.Should().Contain("readiness.js?v=20260814-m46-nyxid-api-routing");
        actorState.Should().Contain("protocol.js?v=20260814-m46-nyxid-api-routing");
        blocks.Should().Contain("protocol.js?v=20260814-m46-nyxid-api-routing");
        html.Should().Contain("<span class=\"brand-name\">Aevatar Studio</span>");
        html.Should().NotContain("class=\"brand-mark\"");
        styles.Should().Contain("color-scheme: only light");
        styles.Should().NotContain("color-scheme: dark");
        styles.Should().NotContain("prefers-color-scheme");
        styles.Should().Contain("--bg: #eceff4");
        styles.Should().Contain("--accent: #2f5cf6");
        styles.Should().Contain("--accent-strong: #1e44d8");
        styles.Should().Contain("--success: #12a15c");
        styles.Should().NotContain("--accent: #0f766e");
        styles.Should().NotContain("--accent-secondary: #df6b45");
        styles.Should().Contain("overflow-y: scroll");
        styles.Should().Contain("scrollbar-gutter: stable");
        styles.Should().Contain(".thread::-webkit-scrollbar-thumb");
        styles.Should().Contain("scrollbar-color: var(--tertiary) var(--surface)");
        styles.Should().Contain("min-height: 108px;\n  flex-direction: column;");
        styles.Should().Contain("width: 100%;\n  min-width: 0;\n  height: 40px;");
        styles.Should().Contain(".recent-session-list {\n  min-height: 0;\n  flex: 1 1 0;");
        styles.Should().NotContain("min-height: 480px");
        styles.Should().NotContain("data-theme");
    }

    [Fact]
    public async Task WorkflowStudio_RequestTraces_ShouldKeepClientIdentityAndIsolateHistoricalControls()
    {
        var html = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetStudioPage);
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = source.indexOf('function ' + name + '(');
              const end = source.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist in the served Studio app');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return source.slice(start, end);
            }

            const hidden = [];
            const classList = name => ({
              add(value) { hidden.push([name, value]); },
              remove() {},
              toggle() {},
            });
            const dom = {
              sendButton: { classList: classList('send') },
              steerButton: { classList: classList('steer') },
              stopButton: { classList: classList('stop') },
              observationDisconnectButton: { classList: classList('observation') },
              promptInput: { disabled: false },
              attachButton: { disabled: false },
              composerServicesButton: { disabled: false },
              composerStatus: { textContent: '' },
            };
            const context = {
              Map,
              state: { activeConversation: null },
              dom,
              isActiveConversationContext: () => true,
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('createRequestTrace', 'currentRequestTrace')}
              ${functionSource('currentRequestTrace', 'selectedRequestTrace')}
              ${functionSource('selectedRequestTrace', 'traceForRun')}
              ${functionSource('traceForRun', 'isReviewingHistoricalTrace')}
              ${functionSource('isReviewingHistoricalTrace', 'requestTraceInput')}
              ${functionSource('requestTraceInput', 'requestTraceOutput')}
              ${functionSource('requestTraceOutput', 'requestTraceStatusLabel')}
              ${functionSource('inspectorRequestTrace', 'inspectorRunState')}
              ${functionSource('inspectorRunState', 'paintRunStatus')}
              ${functionSource('renderActorControlUi', 'renderSteps')}
            `, context);

            const firstRun = {
              clientRequestId: 'client-request-one', context: {}, events: [], tools: new Map(),
              assistantText: 'First request result',
            };
            const secondRun = {
              clientRequestId: 'client-request-two', context: {}, events: [], tools: new Map(),
            };
            const entry = {
              run: firstRun,
              traces: new Map(),
              traceOrder: [],
              selectedTraceKey: null,
            };
            context.state.activeConversation = entry;

            const firstTrace = vm.runInContext('createRequestTrace', context)(entry, firstRun);
            assert.equal(entry.currentTraceKey, 'client-request-one');
            assert.equal(vm.runInContext('currentRequestRun', context)(entry), firstRun);
            const duplicate = vm.runInContext('createRequestTrace', context)(entry, firstRun);
            assert.equal(duplicate, firstTrace);
            assert.equal(entry.traces.size, 1);
            assert.deepEqual(entry.traceOrder, ['client-request-one']);

            const secondTrace = vm.runInContext('createRequestTrace', context)(entry, secondRun);
            assert.notEqual(secondTrace, firstTrace);
            assert.equal(entry.traces.size, 2);
            assert.deepEqual(entry.traceOrder, ['client-request-two', 'client-request-one']);
            assert.equal(entry.currentTraceKey, 'client-request-two');
            assert.equal(vm.runInContext('currentRequestRun', context)(entry), secondRun);
            assert.equal(entry.selectedTraceKey, 'client-request-two');

            firstRun.context.runId = 'run-server-one';
            firstRun.context.turnId = 'turn-server-one';
            assert.equal(vm.runInContext('attachRequestTraceServerFacts', context)(entry, firstRun, {
              runId: 'run-server-one', turnId: 'turn-server-one',
            }), firstTrace);
            assert.equal(firstTrace.serverRunId, 'run-server-one');
            assert.equal(firstTrace.serverTurnId, 'turn-server-one');
            assert.equal(vm.runInContext('createRequestTrace', context)(entry, firstRun), firstTrace);
            assert.equal(vm.runInContext('traceForRun', context)(entry, firstRun), firstTrace);
            assert.equal(entry.currentTraceKey, 'client-request-two', 'looking up an existing trace does not make it current');
            assert.equal(vm.runInContext('currentRequestRun', context)(entry), secondRun);
            assert.equal(entry.traces.size, 2, 'server facts do not create or collapse client-owned traces');
            assert.deepEqual([...entry.traces.keys()], ['client-request-one', 'client-request-two']);

            entry.run = secondRun;
            entry.selectedTraceKey = 'client-request-one';
            assert.equal(vm.runInContext('currentRequestTrace', context)(entry), secondTrace);
            assert.equal(vm.runInContext('selectedRequestTrace', context)(entry), firstTrace);
            assert.equal(vm.runInContext('isReviewingHistoricalTrace', context)(entry), true);
            assert.equal(vm.runInContext('currentRequestRun', context)(entry), secondRun,
              'historical selection cannot redirect the live request pointer');
            assert.equal(vm.runInContext('inspectorRequestTrace', context)(entry), firstTrace);
            assert.equal(vm.runInContext('inspectorRunState', context)(entry), firstRun);
            assert.equal(vm.runInContext('requestTraceOutput', context)(firstRun), 'First request result');

            vm.runInContext('renderActorControlUi', context)();
            assert.deepEqual(hidden, [
              ['send', 'hidden'], ['steer', 'hidden'], ['stop', 'hidden'], ['observation', 'hidden'],
            ]);
            assert.equal(dom.promptInput.disabled, true);
            assert.equal(dom.attachButton.disabled, true);
            assert.equal(dom.composerServicesButton.disabled, true);
            assert.match(dom.composerStatus.textContent, /历史轨迹/);

            entry.selectedTraceKey = 'client-request-two';
            assert.equal(vm.runInContext('isReviewingHistoricalTrace', context)(entry), false);
            assert.equal(vm.runInContext('inspectorRunState', context)(entry), secondRun);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("function attachRequestTraceServerFacts(entry, run, event)");
        app.Should().Contain("attachRequestTraceServerFacts(conversationContext || state.activeConversation, state.run, event)");
        app.Should().Contain("dom.routeSection.classList.toggle(\"hidden\", historical);");
        app.Should().Contain("renderMarkdown(dom.traceOutputFact, output);");
        html.Should().Contain("历史轨迹仅供查看；返回当前轨迹后才能使用运行控制。");
        html.Should().Contain("id=\"traceOutputFact\"");
        html.Should().Contain("id=\"routeSection\"");
        var sendPromptStart = app.IndexOf("async function sendPrompt(", StringComparison.Ordinal);
        var ownerRunAssignment = app.IndexOf("conversation.run = state.run;", sendPromptStart, StringComparison.Ordinal);
        var firstTraceCreation = app.IndexOf(
            "createRequestTrace(conversation, state.run);",
            sendPromptStart,
            StringComparison.Ordinal);
        ownerRunAssignment.Should().BeGreaterThan(sendPromptStart);
        firstTraceCreation.Should().BeGreaterThan(ownerRunAssignment,
            "the conversation must own the new request before its first trajectory render");
    }

    [Fact]
    public async Task WorkflowStudio_OperationInspector_ShouldStayIsolatedFromTheDefaultConversationView()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = source.indexOf('function ' + name + '(');
              const end = source.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist in the served Studio app');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return source.slice(start, end);
            }

            const toggles = [];
            const fact = () => ({textContent:'', className:'', title:'', classList:{toggle(){}}});
            const entry = {mainView:'conversation'};
            const record = {
              key:'model:model-0', id:'model-0', kind:'model', title:'deepseek-chat',
              status:'done', model:'deepseek-chat', provider:'deepseek', round:0,
              sessionId:'session-alpha',
              finishReason:'stop', usage:{totalTokens:12}, input:'Prompt', output:'Answer',
              reasoning:'', error:'', tools:['search'], startedAt:1700000000000,
              completedAt:1700000000100,
            };
            const trace = {selected:record};
            const context = {
              state:{activeConversation:entry},
              dom:{
                traceOperationSection:{classList:{toggle(name, hidden){toggles.push([name, hidden]);}}},
                traceOperationKindFact:fact(), traceOperationTitleFact:fact(),
                traceOperationIdFact:fact(), traceOperationStatusFact:fact(),
                traceOperationStartedFact:fact(), traceOperationDurationFact:fact(),
                traceOperationInputSection:fact(), traceOperationOutputSection:fact(),
                traceOperationInputFact:fact(), traceOperationOutputFact:fact(),
              },
              inspectorRequestTrace:() => trace,
              selectedTraceOperation:candidate => candidate?.selected || null,
              traceOperationKindLabel:kind => kind.toUpperCase(),
              traceOperationStatusLabel:status => status,
              traceOperationStartedAt:() => '22:13:20.000',
              traceOperationDuration:() => '100ms',
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('inspectorTraceOperation', 'renderTraceOperationInspector')}
              ${functionSource('renderTraceOperationInspector', 'paintRunStatus')}
            `, context);

            assert.equal(context.inspectorTraceOperation(entry, trace), null,
              'the default conversation view must not select an operation');
            context.renderTraceOperationInspector(trace);
            assert.deepEqual(toggles.at(-1), ['hidden', true]);
            assert.equal(context.dom.traceOperationTitleFact.textContent, '',
              'hidden trajectory facts must not overwrite the original inspector');

            entry.mainView = 'traces';
            assert.equal(context.inspectorTraceOperation(entry, trace), record);
            context.renderTraceOperationInspector(trace);
            assert.deepEqual(toggles.at(-1), ['hidden', false]);
            assert.equal(context.dom.traceOperationTitleFact.textContent, 'deepseek-chat');
            assert.equal(context.dom.traceOperationDurationFact.textContent, '100ms');
            assert.match(context.dom.traceOperationInputFact.textContent, /Available tools:\nsearch/);
            assert.match(context.dom.traceOperationOutputFact.textContent, /Provider: deepseek/);
            assert.match(context.dom.traceOperationOutputFact.textContent, /Total tokens: 12/);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("const operation = inspectorTraceOperation(entry, trace);");
        app.Should().Contain("const record = inspectorTraceOperation(state.activeConversation, trace);");
    }

    [Fact]
    public async Task WorkflowStudio_OperationLedger_ShouldKeepEveryModelRoundAndToolCallAsAnIndependentLiveRecord()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function ensureTraceOperationState(');
            const end = source.indexOf('\nfunction traceOperationDuration(', start);
            assert.notEqual(start, -1, 'the operation ledger reducer must exist');
            assert.notEqual(end, -1, 'the operation ledger reducer must have a stable boundary');

            const context = {
              Map, Date, Number,
              createId: prefix => prefix + '-generated',
              mergeUsage: (current, next) => ({...(current || {}), ...(next || {})}),
              requestTraceInput: trace => String(trace?.run?.request?.prompt || ''),
              traceForRun: entry => entry.trace,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const run = {
              clientRequestId: 'request-alpha',
              startedAt: 1700000000000,
              request: {prompt: 'Find the current deployment status'},
            };
            const trace = {
              clientRequestId: run.clientRequestId,
              run,
              records: [],
              recordIndex: new Map(),
              selectedOperationKey: null,
              activeModelOperationKey: null,
              followLatestOperation: true,
              nextOperationSequence: 0,
            };
            const entry = {trace};
            const apply = context.applyRequestTraceEvent;
            context.createInputTraceOperation(trace);
            const input = trace.recordIndex.get('input:request-alpha');
            assert.ok(input);
            assert.equal(context.traceOperationDurationMs(input), null,
              'request timing must not be presented as an independent Input duration');

            apply(entry, run, {
              type: 'model_start', operationId: 'model-round-0', sessionId: 'session-shared',
              round: 0, model: 'deepseek-chat', provider: 'deepseek', sequence: 10,
              timestamp: 1700000000100,
            });
            const firstModel = trace.recordIndex.get('model:model-round-0');
            assert.ok(firstModel, 'model_start creates a record before any text exists');
            assert.equal(firstModel.output, '');
            assert.equal(firstModel.model, 'deepseek-chat');
            assert.equal(firstModel.provider, 'deepseek');
            assert.equal(firstModel.round, 0);
            assert.equal(firstModel.serverSequence, 10);
            assert.equal(context.traceOperationDurationMs(firstModel), null);

            apply(entry, run, {
              type: 'model_start', operationId: 'model-round-0', sessionId: 'session-shared',
              round: 0, model: 'deepseek-chat', sequence: 10, timestamp: 1700000000100,
            });
            apply(entry, run, {
              type: 'model_end', operationId: 'model-round-0', sessionId: 'session-shared',
              round: 0, model: 'deepseek-chat', content: '', success: true,
              usage: {promptTokens: 12, completionTokens: 0, totalTokens: 12},
              sequence: 11, timestamp: 1700000000300,
            });
            apply(entry, run, {
              type: 'model_end', operationId: 'model-round-0', sessionId: 'session-shared',
              round: 0, model: 'deepseek-chat', content: '', success: true,
              usage: {promptTokens: 12, completionTokens: 0, totalTokens: 12},
              sequence: 11, timestamp: 1700000000300,
            });
            assert.equal(firstModel.status, 'done');
            assert.equal(firstModel.output, '', 'a tool-call-only model round is still retained');
            assert.equal(context.traceOperationDurationMs(firstModel), 200);
            assert.equal(trace.records.length, 2, 'duplicate model frames upsert the stable operation');

            trace.selectedOperationKey = firstModel.key;
            trace.followLatestOperation = false;

            // Delivery and clocks can disagree. The committed server sequence owns ledger ordering.
            apply(entry, run, {
              type: 'model_start', operationId: 'model-round-1', sessionId: 'session-shared',
              round: 1, model: 'deepseek-chat', sequence: 20, timestamp: 1700000000400,
            });
            apply(entry, run, {
              type: 'tool_start', toolCallId: 'call-search', toolName: 'search',
              argumentsJson: '{"token":"raw-secret-must-not-leak"}',
              sequence: 12, timestamp: 1700000000800,
            });
            const tool = trace.recordIndex.get('tool:call-search');
            assert.ok(tool);
            assert.equal(tool.input, '', 'tool_start never exposes raw arguments');
            assert.equal(tool.serverSequence, 12);
            assert.equal(context.traceOperationDurationMs(tool), null);

            apply(entry, run, {
              type: 'tool_start', toolCallId: 'call-search', toolName: 'search',
              sequence: 12, timestamp: 1700000000800,
            });
            apply(entry, run, {
              type: 'tool_end', toolCallId: 'call-search', toolName: 'search',
              argumentsJson: '{"query":"deployment status"}', result: null,
              success: false, error: 'upstream unavailable',
              sequence: 13, timestamp: 1700000001100,
            });
            apply(entry, run, {
              type: 'tool_end', toolCallId: 'call-search', toolName: 'search',
              argumentsJson: '{"query":"deployment status"}', result: null,
              success: false, error: 'upstream unavailable',
              sequence: 13, timestamp: 1700000001100,
            });
            assert.equal(tool.input, '{"query":"deployment status"}');
            assert.equal(tool.output, 'upstream unavailable');
            assert.equal(tool.status, 'error');
            assert.equal(context.traceOperationDurationMs(tool), 300);

            apply(entry, run, {
              type: 'model_end', operationId: 'model-round-1', sessionId: 'session-shared',
              round: 1, model: 'deepseek-chat', content: 'Deployment is degraded.', success: true,
              usage: {promptTokens: 16, completionTokens: 4, totalTokens: 20},
              sequence: 21, timestamp: 1700000000600,
            });
            const secondModel = trace.recordIndex.get('model:model-round-1');
            assert.ok(secondModel);
            assert.notEqual(secondModel, firstModel, 'rounds in one role session are independent');
            assert.equal(secondModel.output, 'Deployment is degraded.');
            assert.equal(secondModel.round, 1);
            assert.equal(secondModel.serverSequence, 20);
            assert.equal(context.traceOperationDurationMs(secondModel), 200);
            assert.equal(trace.selectedOperationKey, firstModel.key,
              'live upserts do not steal selection while the operator inspects a record');

            const records = context.orderedTraceOperations(trace);
            assert.deepEqual(JSON.parse(JSON.stringify(records.map(record => record.key))), [
              'input:request-alpha',
              'model:model-round-0',
              'tool:call-search',
              'model:model-round-1',
            ]);
            assert.deepEqual(JSON.parse(JSON.stringify(records.map(record => record.kind))),
              ['input', 'model', 'tool', 'model']);
            assert.equal(trace.records.length, 4, 'each logical operation owns exactly one live row');

            apply(entry, run, {
              type: 'raw_observed', observedType: 'WorkflowLlmInvocationStartedEvent',
              observed: {stepId: 'workflow-step', roleActorId: 'role-alpha'},
              timestamp: 1700000001200,
            });
            assert.equal(trace.records.length, 4,
              'a workflow-level invocation must not masquerade as a provider response');

            apply(entry, run, {
              type: 'model_end', operationId: 'model-out-of-order', sessionId: 'session-shared',
              round: 2, model: 'deepseek-chat', content: 'Already complete', success: true,
              sequence: 31, timestamp: 1700000001500,
            });
            apply(entry, run, {
              type: 'model_start', operationId: 'model-out-of-order', sessionId: 'session-shared',
              round: 2, model: 'deepseek-chat', sequence: 30, timestamp: 1700000001300,
            });
            const outOfOrder = trace.recordIndex.get('model:model-out-of-order');
            assert.equal(outOfOrder.status, 'done', 'late start cannot reactivate a completed model');
            assert.equal(context.traceOperationDurationMs(outOfOrder), 200);
            assert.notEqual(trace.activeModelOperationKey, outOfOrder.key);
            const beforeLegacyDelta = trace.records.length;
            apply(entry, run, {type: 'text_delta', delta: 'duplicate legacy text'});
            assert.equal(trace.records.length, beforeLegacyDelta,
              'legacy text cannot create a second model after typed lifecycle is observed');
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_Protocol_ShouldPreserveTypedModelAndToolOperationFrames()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = {structuredClone, TextDecoder, URL, console};
            vm.createContext(context);
            vm.runInContext(source, context);

            const modelStart = context.normalizeFrame({
              type: 'MODEL_CALL_START', timestamp: 1700000000100, sequence: 10,
              modelCallStart: {
                operationId: 'model-round-0', sessionId: 'session-shared',
                round: 0, model: 'deepseek-chat',
              },
            });
            assert.equal(modelStart.type, 'model_start');
            assert.equal(modelStart.operationId, 'model-round-0');
            assert.equal(modelStart.sessionId, 'session-shared');
            assert.equal(modelStart.round, 0);
            assert.equal(modelStart.model, 'deepseek-chat');
            assert.equal(modelStart.sequence, 10);
            assert.equal(modelStart.raw.timestamp, 1700000000100);

            const modelEnd = context.normalizeFrame({
              modelCallEnd: {
                operationId: 'model-round-0', sessionId: 'session-shared', round: 0,
                model: 'deepseek-chat', content: '', reasoningContent: 'tool required',
                usage: {promptTokens: 12, completionTokens: 0, totalTokens: 12},
                finishReason: 'tool_calls', success: true, error: '',
              },
              timestamp: 1700000000300, sequence: 11,
            });
            assert.equal(modelEnd.type, 'model_end');
            assert.equal(modelEnd.operationId, 'model-round-0');
            assert.equal(modelEnd.content, '');
            assert.equal(modelEnd.reasoningContent, 'tool required');
            assert.equal(modelEnd.usage.totalTokens, 12);
            assert.equal(modelEnd.finishReason, 'tool_calls');
            assert.equal(modelEnd.success, true);
            assert.equal(modelEnd.sequence, 11);

            const toolStart = context.normalizeFrame({
              type: 'TOOL_CALL_START', sequence: 12,
              toolCallStart: {toolCallId: 'call-search', toolName: 'search'},
            });
            assert.equal(toolStart.type, 'tool_start');
            assert.equal(toolStart.toolCallId, 'call-search');
            assert.equal(toolStart.argumentsJson, undefined,
              'tool start does not carry raw arguments');
            assert.equal(toolStart.sequence, 12);

            const toolEnd = context.normalizeFrame({
              type: 'TOOL_CALL_END', sequence: 13,
              toolCallEnd: {
                toolCallId: 'call-search', argumentsJson: '{"query":"status"}',
                result: null, success: false, error: 'upstream unavailable',
              },
            });
            assert.equal(toolEnd.type, 'tool_end');
            assert.equal(toolEnd.toolCallId, 'call-search');
            assert.equal(toolEnd.argumentsJson, '{"query":"status"}');
            assert.equal(toolEnd.success, false);
            assert.equal(toolEnd.error, 'upstream unavailable');
            assert.equal(toolEnd.sequence, 13);
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_LoadConversation_ShouldNotMoveFocusToComposer()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var start = app.IndexOf("async function loadConversation(", StringComparison.Ordinal);
        var end = app.IndexOf("\nfunction actorStateTurnId(", start, StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var loadConversation = app[start..end];
        loadConversation.Should().NotContain("dom.promptInput.focus()");
        loadConversation.Should().Contain("scrollThread()");
    }

    [Fact]
    public async Task WorkflowStudio_Composer_ShouldRoutePendingInputAndActiveTaskCommands()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function activePendingInputContext()');
            const end = source.indexOf('\nasync function sendPrompt(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const entry = {
              actorId:'actor-alpha', actorProjection:null, draft:'',
              needsYouDrafts:new Map(), needsYouSubmissions:new Map()
            };
            let acceptsInput = true;
            const decisions = [];
            const controls = [];
            const messages = [];
            let sends = 0;
            const context = {
              Map, Set,
              state:{activeConversation:entry,config:{surface:'nyxid-chat'}},
              dom:{promptInput:{value:''}},
              entryActorProjection:(candidate) => candidate?.actorProjection || null,
              needsYouKey:(kind, requestId) => `${kind}:${requestId}`,
              createId:(prefix) => `${prefix}-alpha`,
              submitNeedsYouDecision:async (...args) => {
                decisions.push(args);
                return acceptsInput;
              },
              submitActorControl:async (...args) => { controls.push(args); },
              sendPrompt:async () => { sends += 1; },
              withConversationState:(_candidate, action) => action(),
              addUserMessage:(message) => { messages.push(message); },
              autoResizeComposer:() => {},
              persistConversationState:() => {},
              renderComposerInputRequest:() => {}
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            entry.actorProjection = {
              stateVersion:7,
              pendingInput:{
                requestId:'request-alpha', allowFreeText:true,
                options:[{optionId:'option-alpha',label:'建议答案'}]
              }
            };
            context.dom.promptInput.value = '完整回答';
            entry.draft = '完整回答';
            await context.submitComposer();
            assert.equal(decisions.length, 1);
            assert.equal(decisions[0][1], 'input');
            assert.equal(decisions[0][2], 'request-alpha');
            assert.deepEqual(JSON.parse(JSON.stringify(decisions[0][3])), {
              type:'input.resolve', answer:{freeText:'完整回答'}
            });
            assert.equal(context.dom.promptInput.value, '');
            assert.equal(entry.draft, '');
            assert.deepEqual(messages, ['完整回答']);
            assert.equal(controls.length, 0);
            assert.equal(sends, 0);

            acceptsInput = false;
            context.dom.promptInput.value = '失败后保留';
            entry.draft = '失败后保留';
            await context.submitPendingInputFromComposer();
            assert.equal(decisions.length, 2);
            assert.equal(context.dom.promptInput.value, '失败后保留');
            assert.equal(entry.draft, '失败后保留');
            assert.deepEqual(messages, ['完整回答']);

            entry.actorProjection = {
              stateVersion:8, pendingInput:null,
              activeTurn:{turnId:'turn-alpha'}, task:{status:'active'}
            };
            context.dom.promptInput.value = '改为只处理后端';
            await context.submitComposer();
            assert.deepEqual(controls, [['steer', null, '改为只处理后端']]);
            assert.equal(sends, 0);

            entry.actorProjection = {stateVersion:9,pendingInput:null,task:{status:'succeeded'}};
            context.dom.promptInput.value = '开始新任务';
            await context.submitComposer();
            assert.equal(sends, 1);
            })().catch((error) => {
              console.error(error);
              process.exitCode = 1;
            });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_TaskStepProtocol_ShouldDecodeWrappedV4PlanChange()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const event = context.normalizeFrame({
              type:'CUSTOM', sequence:41, custom:{name:'nyxid.task.step.changed', payload:{
                taskId:'task-alpha', planRevision:2,
                changeKind:'NYX_ID_CHAT_STEP_CHANGE_KIND_ADDED',
                step:{
                  stepId:'step-tool', order:2, kind:'NYX_ID_CHAT_STEP_KIND_TOOL',
                  status:'NYX_ID_CHAT_STEP_STATUS_RUNNING',
                  externalEffect:'NYX_ID_CHAT_EFFECT_EVIDENCE_NOT_STARTED',
                  addedBy:'NYX_ID_CHAT_STEP_ADDED_BY_REPLAN', dependsOn:['step-plan'],
                  estimate:{kind:'NYX_ID_CHAT_STEP_ESTIMATE_KIND_DURATION',seconds:20},
                  substeps:[{substepId:'substep-alpha',title:'Validate repository',
                    status:'NYX_ID_CHAT_SUBSTEP_STATUS_DONE'}]
                }
              }}
            });

            assert.equal(event.type, 'task_step_changed');
            assert.equal(event.sequence, 41);
            assert.equal(event.payload.taskId, 'task-alpha');
            assert.equal(event.payload.planRevision, 2);
            assert.equal(event.payload.changeKind, 'added');
            assert.equal(event.payload.step.kind, 'tool');
            assert.equal(event.payload.step.status, 'running');
            assert.equal(event.payload.step.addedBy, 'replan');
            assert.deepEqual(JSON.parse(JSON.stringify(event.payload.step.dependsOn)), ['step-plan']);
            assert.equal(event.payload.step.estimate.kind, 'duration');
            assert.equal(event.payload.step.substeps[0].status, 'done');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_TaskStepProtocol_ShouldDecodeConditionStepsAndExpiredNeedsYouOutcomes()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            // Conditional branches commit a condition step, and local approval expiry commits an
            // expired needs-you resolution. normalizeEnum throws NYXID_ENUM_INVALID on any value
            // the decoder never declared, and consumeSse then degrades the whole frame to a
            // protocol error, so an undeclared value silently stops the run card from rendering.
            const conditionStep = context.normalizeFrame({
              type:'CUSTOM', sequence:51, custom:{name:'nyxid.task.step.changed', payload:{
                taskId:'task-alpha', planRevision:3,
                changeKind:'NYX_ID_CHAT_STEP_CHANGE_KIND_STATUS',
                step:{
                  stepId:'step-condition', order:3, kind:'NYX_ID_CHAT_STEP_KIND_CONDITION',
                  status:'NYX_ID_CHAT_STEP_STATUS_DONE',
                  externalEffect:'NYX_ID_CHAT_EFFECT_EVIDENCE_NOT_APPLIED',
                  addedBy:'NYX_ID_CHAT_STEP_ADDED_BY_INITIAL'
                }
              }}
            });

            assert.equal(conditionStep.type, 'task_step_changed');
            assert.equal(conditionStep.payload.step.kind, 'condition');
            assert.equal(conditionStep.payload.step.externalEffect, 'not_applied');

            // The numeric wire form resolves positionally, so the declared order must stay in
            // proto field-number order (condition is 8).
            const numericConditionStep = context.normalizeFrame({
              type:'CUSTOM', sequence:52, custom:{name:'nyxid.task.step.changed', payload:{
                taskId:'task-alpha', planRevision:3, changeKind:1,
                step:{ stepId:'step-condition', order:3, kind:8, status:4,
                  externalEffect:2, addedBy:1 }
              }}
            });

            assert.equal(numericConditionStep.payload.step.kind, 'condition');

            const expiredNeedsYou = context.normalizeFrame({
              type:'CUSTOM', sequence:53, custom:{name:'nyxid.input.changed', payload:{
                requestId:'input-alpha', clientRequestId:'client-input',
                outcome:'NYX_ID_CHAT_NEEDS_YOU_RESOLUTION_OUTCOME_EXPIRED'
              }}
            });

            assert.equal(expiredNeedsYou.type, 'input_changed');
            assert.equal(expiredNeedsYou.payload.outcome, 'expired');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_Protocol_ShouldNormalizePlanGateStatusFromEveryWireForm()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const snapshotWith = (gate) => context.normalizeFrame({
              type:'CUSTOM', sequence:61, custom:{name:'nyxid.task.snapshot', payload:{
                schemaVersion:4, actorId:'conversation-alpha', turnId:'turn-alpha',
                taskId:'task-alpha', planId:'plan-alpha', planRevision:2,
                title:'Post the update', status:'active', gate,
                steps:[{stepId:'step-plan',order:1,kind:'llm',status:'done',
                  externalEffect:'not_started',addedBy:'initial'}]
              }}
            });

            const prefixed = snapshotWith({
              mode:'NYX_ID_CHAT_PLAN_GATE_MODE_CONFIRM',
              status:'NYX_ID_CHAT_PLAN_GATE_STATUS_PENDING',
              reason:'Effect-capable step', requestId:'gate-alpha',
              planId:'plan-alpha', planRevision:2
            });
            assert.equal(prefixed.payload.gate.mode, 'confirm');
            assert.equal(prefixed.payload.gate.status, 'pending');
            assert.equal(prefixed.payload.gate.planRevision, 2);

            const lowercase = snapshotWith({ mode:'confirm', status:'satisfied' });
            assert.equal(lowercase.payload.gate.status, 'satisfied');

            const numeric = snapshotWith({ mode:2, status:3 });
            assert.equal(numeric.payload.gate.status, 'rejected');

            // The projected gate defaults status to an empty string before the actor decides
            // anything; that must stay absent rather than throwing or resolving to a decision.
            const empty = snapshotWith({ mode:'auto', status:'' });
            assert.equal(empty.payload.gate.status, '');

            // An undeclared status fails the frame closed rather than rendering an invented
            // decision: the decoder degrades it to a typed protocol error.
            const invalid = snapshotWith({ mode:'confirm', status:'approved' });
            assert.equal(invalid.type, 'protocol_error');
            assert.equal(invalid.code, 'NYXID_ENUM_INVALID');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_PlanGate_ShouldDecideThroughTheActorOwnedPlanResolveCommand()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);

        // The decision must ride the typed plan.resolve command with the gate's own requestId
        // and the exact plan identity, never a synthesized local admission.
        app.Should().Contain("type: \"plan.resolve\"");
        app.Should().Contain("submitNeedsYouDecision(entry, \"plan\", gate.requestId");
        app.Should().Contain("planRevision: gate.planRevision");

        // Availability is actor-owned: the affordance exists only while the committed gate
        // status is pending, and an unknown status never reads as satisfied.
        app.Should().Contain("actorPendingPlanGate");
        app.Should().Contain("actorPlanGateStatus(projection.task) !== \"pending\"");

        // Confirming a plan is local admission only; it must not be presented as NyxID
        // authorization or as proof that an external effect happened.
        app.Should().Contain("不授予 NyxID 访问权限");
    }

    [Fact]
    public async Task WorkflowStudio_PendingPlanSnapshot_ShouldRefreshCurrentStateBeforeDecision()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const planStart = source.indexOf('function actorPlanGateStatus(');
            const planEnd = source.indexOf('\nfunction needsYouKey(', planStart);
            const frameStart = source.indexOf('function handleFrame(');
            const frameEnd = source.indexOf('\nfunction pickContext(', frameStart);
            assert.notEqual(planStart, -1);
            assert.notEqual(planEnd, -1);
            assert.notEqual(frameStart, -1);
            assert.notEqual(frameEnd, -1);

            const timers = [];
            let refreshes = 0;
            let conversationLoads = 0;
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:{stateVersion:0},
              actorStateRefreshTimer:null
            };
            const context = {
              conversationContext:null,
              state:{activeConversation:entry,run:{}},
              normalizeFrame:(raw) => raw,
              recordEvent:() => {},
              applyRequestTraceEvent:() => {},
              entryActorProjection:(candidate) => candidate.actorProjection,
              reduceActorEvent:(_projection, event) => event.projection,
              renderActorProjection:() => {},
              renderActionCards:() => {},
              renderActiveConversationState:() => {},
              renderInspector:() => {},
              refreshActorState:() => { refreshes += 1; },
              loadConversations:() => { conversationLoads += 1; },
              window:{
                clearTimeout:() => {},
                setTimeout:(callback, delay) => {
                  timers.push({callback, delay});
                  return timers.length;
                }
              }
            };
            vm.createContext(context);
            vm.runInContext(
              source.slice(planStart, planEnd) + '\n' + source.slice(frameStart, frameEnd),
              context
            );

            const snapshot = (status) => ({
              type:'task_snapshot',
              projection:{
                stateVersion:0,
                task:{taskId:'task-alpha',gate:{
                  status, requestId:'gate-alpha', planId:'plan-alpha', planRevision:2
                }}
              }
            });

            context.handleFrame(snapshot('pending'));
            assert.equal(timers.length, 1);
            assert.equal(timers[0].delay, 300);
            timers[0].callback();
            assert.equal(refreshes, 1);
            assert.equal(conversationLoads, 1);

            timers.length = 0;
            context.handleFrame(snapshot('satisfied'));
            assert.equal(timers.length, 0);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_TaskStepSourceLabel_ShouldUseTypedPostconditionCheck()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);

        app.Should().Contain("source.postcondition.check");
        app.Should().NotContain("source.postcondition.postconditionKind");
    }

    [Fact]
    public async Task WorkflowStudio_Protocol_ShouldPreserveTypedRunStoppedPayload()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const event = context.normalizeFrame({
              type:'RUN_STOPPED',
              runStopped:{
                status:'stopped', detail:'Stopped after committed partial work.',
                partialWork:{stateVersion:17,effectEvidence:'confirmed'}
              }
            });

            assert.equal(event.type, 'run_stopped');
            assert.equal(event.status, 'stopped');
            assert.equal(event.detail, 'Stopped after committed partial work.');
            assert.equal(event.partialWork.stateVersion, 17);
            assert.equal(event.partialWork.effectEvidence, 'confirmed');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_WireInspector_ShouldRequireHostFlagAndAuthenticatedSession()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = source.indexOf('function ' + name + '(');
              const end = source.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return source.slice(start, end);
            }

            function element() {
              const classes = new Set();
              const attributes = new Map();
              return {
                classes, attributes,
                classList:{toggle(name, enabled){
                  if (enabled) classes.add(name); else classes.delete(name);
                }},
                setAttribute(name, value){attributes.set(name, value);}
              };
            }

            const context = {
              state:{config:{enableStudioWireInspector:true},auth:{authenticated:false}},
              dom:{
                runPanel:element(), eventsPanel:element(), runTabButton:element(),
                eventsTabButton:element()
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('configureWireInspector', 'updateElapsed')}
              ${functionSource('setInspectorTab', 'openMobilePanel')}
            `, context);

            context.configureWireInspector();
            assert.equal(context.dom.eventsTabButton.classes.has('hidden'), true);
            assert.equal(context.dom.eventsTabButton.attributes.get('aria-hidden'), 'true');
            context.setInspectorTab('events');
            assert.equal(context.dom.runPanel.classes.has('hidden'), false);
            assert.equal(context.dom.eventsPanel.classes.has('hidden'), true);

            context.state.auth.authenticated = true;
            context.configureWireInspector();
            assert.equal(context.dom.eventsTabButton.classes.has('hidden'), false);
            assert.equal(context.dom.eventsTabButton.attributes.get('aria-hidden'), 'false');
            context.setInspectorTab('events');
            assert.equal(context.dom.runPanel.classes.has('hidden'), true);
            assert.equal(context.dom.eventsPanel.classes.has('hidden'), false);

            context.state.config.enableStudioWireInspector = false;
            context.setInspectorTab('events');
            assert.equal(context.dom.runPanel.classes.has('hidden'), false);
            assert.equal(context.dom.eventsPanel.classes.has('hidden'), true);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ReadinessAsset_ShouldDescribeActionableRecovery()
    {
        var readiness = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantReadiness);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { URL, Date, Set, Error };
            vm.createContext(context);
            vm.runInContext(source, context);

            const inactive = context.describeReadinessFailure({status:401,code:'OAUTH_CLIENT_INACTIVE'});
            assert.equal(inactive.freshness, '登录配置不可用');
            assert.match(inactive.summary, /已停用/);
            assert.match(inactive.guidance, /重复登录不会恢复/);
            assert.equal(inactive.action, 'retry');
            assert.equal(inactive.actionLabel, '修复后重新检查');

            const expired = context.describeReadinessFailure({status:401,code:'AUTH_REQUIRED'});
            assert.equal(expired.action, 'login');
            assert.equal(expired.actionLabel, '重新登录');

            const forbidden = context.describeReadinessFailure({status:403});
            assert.equal(forbidden.action, 'account');
            assert.match(forbidden.guidance, /访问策略/);

            const missingEndpoint = context.describeReadinessFailure({status:404});
            assert.match(missingEndpoint.summary, /尚未提供/);
            assert.match(missingEndpoint.guidance, /部署 assistant readiness 接口/);

            const invalid = context.describeReadinessFailure({
              status:502, code:'READINESS_INVALID',
              reason:'Capability has unknown fields: platformEvidence'
            });
            assert.equal(invalid.freshness, '契约不匹配');
            assert.match(invalid.summary, /契约不一致：Capability has unknown fields: platformEvidence/);
            assert.match(invalid.guidance, /nyxid-assistant-readiness\.v1/);
            assert.equal(invalid.action, 'retry');

            const invalidWithoutReason = context.describeReadinessFailure({status:502,code:'READINESS_INVALID'});
            assert.equal(invalidWithoutReason.summary, 'NyxID readiness 响应与 Studio 契约不一致。');

            const secretReason = context.describeReadinessFailure({
              status:502, code:'READINESS_INVALID', reason:'Bearer leaked-token-value'
            });
            assert.doesNotMatch(secretReason.summary, /leaked-token-value/);

            const upstreamBadGateway = context.describeReadinessFailure({status:502});
            assert.match(upstreamBadGateway.summary, /暂时无法提供/);
            assert.equal(upstreamBadGateway.freshness, '服务暂时不可用');

            const unavailable = context.describeReadinessFailure({status:503});
            assert.match(unavailable.summary, /暂时无法提供/);

            const disconnected = context.describeReadinessFailure(new TypeError('Failed to fetch'));
            assert.match(disconnected.summary, /无法连接/);
            assert.match(disconnected.guidance, /网络或 VPN/);

            const unexpected = context.describeReadinessFailure({status:418});
            assert.equal(unexpected.freshness, '检查失败 (418)');
            """;

        var result = await RunNodeAsync(script, readiness);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        readiness.Should().NotContain("状态格式不兼容");
        readiness.Should().NotContain("无法识别的运行状态");
        readiness.Should().NotContain("契约版本一致");
    }

    [Fact]
    public async Task WorkflowStudio_ReadinessAsset_ShouldAcceptTheDeployedNyxIdContractAcrossSplitHosts()
    {
        var readiness = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantReadiness);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { URL, Date, Set, Error };
            vm.createContext(context);
            vm.runInContext(source, context);

            // Same shape as the live nyxid-assistant-readiness.v1 response: the
            // managementUrl origin is NyxID's web frontend, which differs from the
            // OIDC authority (API host) in a split-host deployment.
            const fixture = {
              revision:'nyxid-assistant-readiness.v1',
              evaluatedAt:'2026-08-07T06:50:21.842990344Z',
              capabilities:[{
                capabilityId:'api-github', label:'GitHub', required:false, status:'cannot_use',
                connectionState:'connected', grantState:'missing', requestedScopes:['repo'],
                managementUrl:'https://nyx-web.example.test/keys', reasonCode:'grant_missing'
              }]
            };

            const splitHost = context.normalizeReadinessSnapshot(fixture, {
              managementOrigins:['https://nyx-web.example.test', 'https://nyx-api.example.test']
            });
            assert.equal(splitHost.revision, 'nyxid-assistant-readiness.v1');
            assert.equal(splitHost.evaluatedAt, '2026-08-07T06:50:21.842Z');
            assert.equal(splitHost.capabilities[0].status, 'cannot_use');
            assert.equal(splitHost.capabilities[0].grantState, 'missing');
            assert.equal(splitHost.capabilities[0].reasonCode, 'grant_missing');
            assert.equal(splitHost.capabilities[0].managementUrl, 'https://nyx-web.example.test/keys');
            assert.deepEqual(JSON.parse(JSON.stringify(splitHost.managementUrlDrops)), []);

            // A console that only trusts the API origin keeps the capability facts
            // and drops the unproven link instead of rejecting the snapshot.
            const apiOnly = context.normalizeReadinessSnapshot(fixture, {
              managementOrigins:['https://nyx-api.example.test']
            });
            assert.equal(apiOnly.capabilities[0].status, 'cannot_use');
            assert.equal(apiOnly.capabilities[0].managementUrl, null);
            assert.deepEqual(JSON.parse(JSON.stringify(apiOnly.managementUrlDrops)), [{
              capabilityId:'api-github', origin:'https://nyx-web.example.test'
            }]);

            const nullLink = context.normalizeReadinessSnapshot({
              ...fixture, capabilities:[{...fixture.capabilities[0], managementUrl:null}]
            }, {managementOrigins:['https://nyx-web.example.test']});
            assert.equal(nullLink.capabilities[0].managementUrl, null);
            assert.deepEqual(JSON.parse(JSON.stringify(nullLink.managementUrlDrops)), []);
            """;

        var result = await RunNodeAsync(script, readiness);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ReadinessAsset_ShouldRejectIncompatiblePayloadsWithSafeSpecificReasons()
    {
        var readiness = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantReadiness);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { URL, Date, Set, Error };
            vm.createContext(context);
            vm.runInContext(source, context);

            const fixture = {
              revision:'nyxid-assistant-readiness.v1',
              evaluatedAt:'2026-08-07T06:50:21.842990344Z',
              capabilities:[{
                capabilityId:'api-github', label:'GitHub', required:false, status:'cannot_use',
                connectionState:'connected', grantState:'missing', requestedScopes:['repo'],
                managementUrl:'https://nyx-web.example.test/keys', reasonCode:'grant_missing'
              }]
            };
            const origins = {managementOrigins:['https://nyx-web.example.test']};
            const capture = (mutated) => {
              try {
                context.normalizeReadinessSnapshot(mutated, origins);
                assert.fail('expected rejection');
              } catch (error) {
                assert.equal(error.code, 'READINESS_INVALID');
                return error;
              }
            };

            const unknownField = capture({...fixture, platformEvidence:{}});
            assert.equal(unknownField.reason, 'Readiness snapshot has unknown fields: platformEvidence');
            const unknownCapabilityField = capture({
              ...fixture, capabilities:[{...fixture.capabilities[0], evidenceKind:'platform'}]
            });
            assert.equal(unknownCapabilityField.reason, 'Capability has unknown fields: evidenceKind');

            const withoutGrantState = {...fixture.capabilities[0]};
            delete withoutGrantState.grantState;
            assert.equal(capture({...fixture, capabilities:[withoutGrantState]}).reason, 'grantState is missing');
            assert.equal(
              capture({...fixture, capabilities:[{...fixture.capabilities[0], status:'maybe'}]}).reason,
              'status is invalid');
            assert.equal(
              capture({...fixture, capabilities:[{...fixture.capabilities[0], managementUrl:'http://nyx-web.example.test/keys'}]}).reason,
              'managementUrl must be https');
            assert.equal(
              capture({...fixture, capabilities:[{...fixture.capabilities[0], managementUrl:'not a url'}]}).reason,
              'managementUrl is invalid');

            const secretField = capture({...fixture, accessToken:'nyx_0123456789abcdef'});
            assert.equal(secretField.reason, 'Readiness snapshot contains secret fields');
            assert.doesNotMatch(String(secretField.message), /nyx_0123456789abcdef/);
            const secretValue = capture({
              ...fixture, capabilities:[{...fixture.capabilities[0], label:'Bearer nyx-secret-value'}]
            });
            assert.equal(secretValue.reason, 'Readiness snapshot contains secret values');
            assert.doesNotMatch(String(secretValue.message), /nyx-secret-value/);
            """;

        var result = await RunNodeAsync(script, readiness);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_FirstTurn_ShouldOnlyBlockOnMissingRequiredCapabilities()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function firstTurnReadinessBlocked()');
            const end = source.indexOf('\nasync function loadServices(', start);
            assert.notEqual(start, -1, 'firstTurnReadinessBlocked must exist in the served Studio app');
            assert.notEqual(end, -1, 'loadServices must follow firstTurnReadinessBlocked');

            const context = {
              state:{
                config:{surface:'nyxid-chat'},
                actorId:null,
                readiness:{loading:false, error:null, snapshot:null}
              }
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);
            const blocked = () => vm.runInContext('firstTurnReadinessBlocked()', context);

            context.state.readiness = {loading:true, error:null, snapshot:null};
            assert.equal(blocked(), true, 'an in-flight check holds the first turn');

            context.state.readiness = {loading:false, error:null, snapshot:null};
            assert.equal(blocked(), true, 'an unchecked session holds the first turn');

            // The optional api-github capability may be unusable (grant_missing)
            // without holding the first run.
            context.state.readiness = {loading:false, error:null, snapshot:{capabilities:[{
              capabilityId:'api-github', required:false, status:'cannot_use'
            }]}};
            assert.equal(blocked(), false);

            context.state.readiness = {loading:false, error:null, snapshot:{capabilities:[{
              capabilityId:'model', required:true, status:'missing'
            }]}};
            assert.equal(blocked(), true, 'a missing required capability holds the first turn');

            // A failed advisory check must not deadlock the chat.
            context.state.readiness = {loading:false, error:{status:502}, snapshot:null};
            assert.equal(blocked(), false);

            context.state.actorId = 'conversation-alpha';
            context.state.readiness = {loading:true, error:null, snapshot:null};
            assert.equal(blocked(), false, 'existing conversations never re-gate');
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ReadinessPanel_ShouldKeepOptionalCapabilitiesQuiet()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('const readinessStatusCopy = {');
            const end = source.indexOf('\nfunction renderReadiness(', start);
            assert.notEqual(start, -1, 'readiness status copy must exist in the served Studio app');
            assert.notEqual(end, -1, 'renderReadiness must follow the readiness copy maps');

            const context = {};
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);
            const label = (capability) => vm.runInContext('readinessStatusLabel', context)(capability);

            // Optional capabilities read as neutral on/off facts; only required
            // capabilities keep the blocking state words.
            assert.equal(label({required:false, status:'cannot_use'}), '未启用');
            assert.equal(label({required:false, status:'missing'}), '未启用');
            assert.equal(label({required:false, status:'cannot_check'}), '未启用');
            assert.equal(label({required:false, status:'available'}), '可用');
            assert.equal(label({required:true, status:'missing'}), '缺失');
            assert.equal(label({required:true, status:'cannot_use'}), '不可使用');
            assert.equal(label({required:true, status:'available'}), '可用');
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("readiness-optional");
        app.Should().Contain("不影响使用");
        app.Should().Contain("state.readinessOptionalOpen");
        var styles = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantStyles);
        styles.Should().Contain(".readiness-row.optional .readiness-status");
        styles.Should().Contain(".readiness-optional > summary");
    }

    [Fact]
    public async Task WorkflowStudio_ActorTaskCard_ShouldStayAnchoredAfterTheNewestUserMessage()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function mountActorTask(');
            const end = source.indexOf('\nasync function submitActorControl(', start);
            assert.notEqual(start, -1, 'mountActorTask must exist in the served Studio app');
            assert.notEqual(end, -1, 'submitActorControl must follow mountActorTask');

            const context = {};
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);
            const mount = vm.runInContext('mountActorTask', context);

            function fakeThread() {
              const children = [];
              const thread = {
                children,
                querySelectorAll(selector) {
                  assert.equal(selector, ':scope > .message.user');
                  return children.filter((child) => child.kind === 'user');
                },
                append(node) {
                  remove(node);
                  children.push(node);
                  node.connected = true;
                },
              };
              function remove(node) {
                const index = children.indexOf(node);
                if (index >= 0) children.splice(index, 1);
              }
              function decorate(node) {
                Object.defineProperty(node, 'nextElementSibling', {
                  get() {
                    const index = children.indexOf(node);
                    return index >= 0 ? children[index + 1] ?? null : null;
                  },
                });
                node.after = (inserted) => {
                  remove(inserted);
                  children.splice(children.indexOf(node) + 1, 0, inserted);
                  inserted.connected = true;
                };
                return node;
              }
              thread.add = (kind, name) => {
                const node = decorate({ kind, name });
                thread.append(node);
                return node;
              };
              return thread;
            }

            const thread = fakeThread();
            const card = { kind: 'actor-task', name: 'card', get isConnected() { return this.connected === true; } };
            Object.defineProperty(card, 'nextElementSibling', {
              get() {
                const index = thread.children.indexOf(card);
                return index >= 0 ? thread.children[index + 1] ?? null : null;
              },
            });
            card.after = () => { throw new Error('the card itself is never an anchor'); };

            // The assistant shell can arrive before the first actor snapshot; the
            // card still lands between the user message and the reply.
            const user1 = thread.add('user', 'user1');
            const assistant1 = thread.add('assistant', 'assistant1');
            mount(thread, card);
            assert.deepEqual(thread.children.map((child) => child.name), ['user1', 'card', 'assistant1']);

            // Re-rendering without new messages keeps the card where it is.
            mount(thread, card);
            assert.deepEqual(thread.children.map((child) => child.name), ['user1', 'card', 'assistant1']);

            // A later turn pulls the card down next to the newest user message
            // instead of stranding it inside history.
            const user2 = thread.add('user', 'user2');
            const assistant2 = thread.add('assistant', 'assistant2');
            mount(thread, card);
            assert.deepEqual(
              thread.children.map((child) => child.name),
              ['user1', 'assistant1', 'user2', 'card', 'assistant2']);

            // Without any user message (restored empty view) the card appends once.
            const bare = fakeThread();
            const bareCard = { kind: 'actor-task', name: 'bare-card', get isConnected() { return this.connected === true; } };
            mount(bare, bareCard);
            mount(bare, bareCard);
            assert.deepEqual(bare.children.map((child) => child.name), ['bare-card']);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().NotContain("if (!root.isConnected) entry.thread.append(root);");
    }

    [Fact]
    public async Task WorkflowStudio_AssetCacheKeys_ShouldTrackChangedEntryAssetsAndStableImports()
    {
        var html = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetStudioPage);
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        var blocks = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantBlocks);

        var entryVersions = System.Text.RegularExpressions.Regex.Matches(
                html,
                @"\.(?:js|css)\?v=([A-Za-z0-9-]+)")
            .Select(match => match.Groups[1].Value)
            .ToList();
        var transitiveVersions = new[] { app, transport, actorState, blocks }
            .SelectMany(source => System.Text.RegularExpressions.Regex.Matches(
                source,
                @"\.(?:js|css)\?v=([A-Za-z0-9-]+)")
                .Select(match => match.Groups[1].Value))
            .ToList();

        entryVersions.Should().NotBeEmpty();
        entryVersions.Should().OnlyContain(static version =>
            version == "20260814-m46-nyxid-api-routing");
        transitiveVersions.Should().NotBeEmpty();
        transitiveVersions.Should().OnlyContain(static version =>
            version == "20260814-m46-nyxid-api-routing");
        html.Should().Contain("styles.css?v=");
        html.Should().Contain("app.js?v=");
        app.Should().Contain("transport.js?v=");
        app.Should().Contain("readiness.js?v=");
        transport.Should().Contain("readiness.js?v=");
        actorState.Should().Contain("protocol.js?v=");
        blocks.Should().Contain("protocol.js?v=");
    }

    [Fact]
    public async Task WorkflowStudio_ActorProjection_ShouldConvergeNeedsYouFactsFromLiveAndCurrentState()
    {
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8')
              .replace(/^import[^;]+;\s*/m, '')
              .replace(/^export /gm, '');
            const context = { structuredClone, validateActionRequest:value => value };
            vm.createContext(context);
            vm.runInContext(source, context);

            let live = context.createActorProjection('conversation-alpha');
            live = context.reduceActorEvent(live, {type:'task_snapshot', sequence:20, payload:{
              schemaVersion:4, actorId:'conversation-alpha', turnId:'turn-alpha',
              taskId:'task-alpha', planId:'plan-alpha', planRevision:1,
              title:'Update GitHub safely', status:'active', gate:{mode:'auto',reason:null},
              steps:[{stepId:'step-plan',order:1,kind:'llm',status:'done',
                externalEffect:'not_started',addedBy:'initial'}]
            }});
            live = context.reduceActorEvent(live, {type:'task_step_changed', sequence:21, payload:{
              taskId:'task-alpha', planRevision:2, changeKind:'added', step:{
                stepId:'step-tool',order:2,kind:'tool',status:'running',
                externalEffect:'not_started',addedBy:'replan',dependsOn:['step-plan'],
                estimate:{kind:'duration',seconds:20},substeps:[{
                  substepId:'substep-alpha',title:'Validate repository',status:'done'}]
              }
            }});
            assert.equal(live.task.planId, 'plan-alpha');
            assert.equal(live.task.planRevision, 2);
            assert.equal(live.steps.size, 2);
            assert.deepEqual(live.steps.get('step-tool').dependsOn, ['step-plan']);
            live = context.reduceActorEvent(live, {type:'input_requested', sequence:23, payload:{
              requestId:'input-alpha', turnId:'turn-alpha', taskId:'task-alpha', stepId:'step-input',
              prompt:'Select regions', options:[{optionId:'option-sg',label:'Singapore'}],
              allowFreeText:false, multiSelect:true, askedAt:'2026-08-01T12:00:00Z'
            }});
            assert.equal(live.pendingInput.requestId, 'input-alpha');
            assert.equal(live.attentionKind, 'input');
            live = context.reduceActorEvent(live, {type:'input_changed', sequence:24, payload:{
              requestId:'input-alpha', clientRequestId:'client-input', outcome:'accepted'
            }});
            assert.equal(live.pendingInput, null);
            assert.equal(live.latestInputResolution.requestId, 'input-alpha');

            let current = context.createActorProjection('conversation-alpha');
            const applied = context.applyCurrentStateResult(current, {status:'current', stateVersion:31, snapshot:{
              actorId:'conversation-alpha', scopeId:'scope-alpha', stateVersion:31, progressSequence:31,
              activeTurn:null, latestTurn:null, recentTerminalTurns:[], activeTask:{
                schemaVersion:4, actorId:'conversation-alpha', turnId:'turn-alpha',
                taskId:'task-alpha', planId:'plan-alpha', planRevision:2,
                title:'Update GitHub safely', status:'active', gate:{mode:'confirm',reason:'Effect'},
                steps:[{stepId:'step-tool',order:2,kind:'tool',status:'waiting',
                  externalEffect:'not_started',addedBy:'replan',dependsOn:['step-plan'],
                  substeps:[{substepId:'substep-alpha',title:'Validate repository',status:'done'}]}]
              },
              pendingInput:null, pendingApproval:{
                approvalRequestId:'approval-alpha', turnId:'turn-alpha', taskId:'task-alpha',
                stepId:'step-tool', toolName:'repository_delete', action:'repository.delete',
                target:'repository:repo-alpha', reversibility:'irreversible', grantBoundary:'within_grant'
              }, latestInputResolution:null, latestApprovalResolution:null, taskStatus:'active',
              attentionKind:'approval', attentionSince:'2026-08-01T12:05:00Z',
              activeStepSummary:'Delete repository.', pendingActions:[], controlFence:null,
              latestControlResult:null, continuationAdmission:null
            }});
            assert.equal(applied.projection.pendingApproval.approvalRequestId, 'approval-alpha');
            assert.equal(applied.projection.pendingApproval.reversibility, 'irreversible');
            assert.equal(applied.projection.attentionKind, 'approval');
            assert.equal(applied.projection.task.planId, 'plan-alpha');
            assert.equal(applied.projection.task.planRevision, 2);
            assert.equal(applied.projection.steps.get('step-tool').substeps[0].status, 'done');
            assert.equal(applied.reloadWithoutCursor, false);
            """;

        var result = await RunNodeAsync(script, actorState);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_Uc2Projection_ShouldConvergeSteerStopReloadAndDistinctRestart()
    {
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8')
              .replace(/^import[^;]+;\s*/m, '')
              .replace(/^export /gm, '');
            const context = { structuredClone, validateActionRequest:value => value };
            vm.createContext(context);
            vm.runInContext(source, context);

            const completedSearch = {
              stepId:'step-uc2-search',order:2,kind:'tool',status:'done',
              description:'Aevatar web search - find Greek dinner candidates.',
              source:{tool:{toolName:'web_search'}},externalEffect:'not_applied',
              operation:{key:{conversationActorId:'conversation-uc2',turnId:'turn-uc2-1',
                taskId:'task-uc2',stepId:'step-uc2-search',
                operationId:'operation-uc2-search',operationGeneration:1},
                kind:'tool',phase:'succeeded',mayChangeExternalState:false,
                idempotent:true,idempotencyKey:'operation-uc2-search'},
              addedBy:'replan',dependsOn:['step-uc2-gaps'],availableActions:{},
              substeps:[
                {substepId:'prepare-operation',title:'Build search query',status:'done'},
                {substepId:'execute-operation',title:'Search current web results',status:'done'}
              ]
            };
            const inputStep = {
              stepId:'step-uc2-gaps',order:1,kind:'input',status:'done',
              source:{input:{requestId:'input-uc2-gaps'}},externalEffect:'not_applied',
              addedBy:'initial',availableActions:{}
            };
            let live = context.createActorProjection('conversation-uc2');
            live = context.reduceActorEvent(live, {type:'task_snapshot',sequence:20,payload:{
              schemaVersion:4,actorId:'conversation-uc2',turnId:'turn-uc2-1',
              taskId:'task-uc2',planId:'plan-uc2',planRevision:2,
              title:'Research a ready-to-book dinner shortlist',status:'active',
              gate:{mode:'auto',reason:'Read and draft only.'},steps:[
                inputStep,completedSearch,{
                  stepId:'step-uc2-compare',order:3,kind:'llm',status:'running',
                  source:{llm:{}},externalEffect:'not_started',addedBy:'replan',
                  dependsOn:['step-uc2-search'],availableActions:{stop:true}
                }
              ]
            }});
            live = context.reduceActorEvent(live, {type:'task_snapshot',sequence:31,payload:{
              schemaVersion:4,actorId:'conversation-uc2',turnId:'turn-uc2-2',
              taskId:'task-uc2',planId:'plan-uc2',planRevision:3,
              title:'Refine for 7 pm and a private room',status:'active',
              gate:{mode:'auto',reason:'Read and draft only.'},steps:[
                inputStep,completedSearch,{
                  stepId:'step-uc2-compare',order:3,kind:'llm',status:'cancelled',
                  source:{llm:{}},externalEffect:'not_started',addedBy:'replan',availableActions:{}
                },{
                  stepId:'step-uc2-refine',order:4,kind:'llm',status:'running',
                  source:{llm:{}},externalEffect:'not_started',addedBy:'steering',
                  operation:{key:{conversationActorId:'conversation-uc2',turnId:'turn-uc2-2',
                    taskId:'task-uc2',stepId:'step-uc2-refine',
                    operationId:'operation-uc2-refine',operationGeneration:1},
                    kind:'llm',phase:'running',mayChangeExternalState:false,
                    idempotent:true,idempotencyKey:'operation-uc2-refine'},
                  dependsOn:['step-uc2-search'],availableActions:{stop:true}
                }
              ]
            }});
            assert.equal(live.task.taskId, 'task-uc2');
            assert.equal(live.task.turnId, 'turn-uc2-2');
            assert.equal(live.task.planRevision, 3);
            assert.equal(live.steps.get('step-uc2-search').source.tool.toolName, 'web_search');
            assert.equal(live.steps.get('step-uc2-search').substeps.length, 2);
            assert.equal(live.steps.get('step-uc2-search').substeps[0].substepId, 'prepare-operation');
            assert.equal(live.steps.get('step-uc2-search').operation.key.operationId, 'operation-uc2-search');
            assert.equal(live.steps.get('step-uc2-compare').status, 'cancelled');
            assert.equal(live.steps.get('step-uc2-refine').addedBy, 'steering');

            const receipt = 'Stopped. Partial-work receipt: 2 completed steps were retained. ' +
              'Retained: Answer logistics and agree to research-only scope; ' +
              'Aevatar web search - find Greek dinner candidates. ' +
              'Unfinished work was fenced; the in-flight operation could not be proven cancelled. ' +
              'Fenced: Refine for 7 pm and a private room. No external effect was applied. ' +
              'Late evidence cannot advance this stopped task.';
            const stopped = context.applyCurrentStateResult(
              context.createActorProjection('conversation-uc2'), {
                status:'current',stateVersion:36,snapshot:{
                  actorId:'conversation-uc2',scopeId:'scope-uc2',stateVersion:36,
                  progressSequence:36,
                  activeTurn:{turnId:'turn-uc2-2',taskId:'task-uc2',status:'stopped'},
                  latestTurn:{turnId:'turn-uc2-2',taskId:'task-uc2',status:'stopped',safeMessage:receipt},
                  recentTerminalTurns:[{turnId:'turn-uc2-2',taskId:'task-uc2',status:'stopped'}],
                  activeTask:{schemaVersion:4,actorId:'conversation-uc2',turnId:'turn-uc2-2',
                    taskId:'task-uc2',planId:'plan-uc2',planRevision:3,status:'stopped',
                    safeMessage:receipt,gate:{mode:'auto',reason:'Read and draft only.'},steps:[
                      inputStep,completedSearch,{
                        stepId:'step-uc2-compare',order:3,kind:'llm',status:'cancelled',
                        source:{llm:{}},externalEffect:'not_started',addedBy:'replan',availableActions:{}
                      },{
                        stepId:'step-uc2-refine',order:4,kind:'llm',status:'cancelled',
                        source:{llm:{}},externalEffect:'not_applied',addedBy:'steering',
                        operation:{key:{conversationActorId:'conversation-uc2',turnId:'turn-uc2-2',
                          taskId:'task-uc2',stepId:'step-uc2-refine',
                          operationId:'operation-uc2-refine',operationGeneration:1},
                          kind:'llm',phase:'running',mayChangeExternalState:false,
                          idempotent:true,idempotencyKey:'operation-uc2-refine'},availableActions:{}
                      }
                    ]},
                  pendingInput:null,pendingApproval:null,latestInputResolution:null,
                  latestApprovalResolution:null,taskStatus:'stopped',attentionKind:'none',
                  activeStepSummary:null,pendingActions:[],
                  controlFence:{kind:'stop',requestId:'stop-uc2-1',clientRequestId:'client-stop-uc2-1',
                    turnId:'turn-uc2-2',taskId:'task-uc2',outcome:'uncancellable',safeMessage:receipt},
                  latestControlResult:null,continuationAdmission:null
                }
              });
            assert.equal(stopped.reloadWithoutCursor, false);
            assert.equal(stopped.projection.task.status, 'stopped');
            assert.equal(stopped.projection.controlFence.requestId, 'stop-uc2-1');
            assert.equal(stopped.projection.controlFence.outcome, 'uncancellable');
            assert.match(stopped.projection.controlFence.safeMessage, /No external effect was applied/);
            assert.equal(stopped.projection.steps.get('step-uc2-search').substeps.length, 2);
            assert.equal(stopped.projection.steps.get('step-uc2-search').operation.phase, 'succeeded');
            assert.equal(stopped.projection.steps.get('step-uc2-refine').status, 'cancelled');

            const restarted = context.applyCurrentStateResult(stopped.projection, {
              status:'current',stateVersion:48,snapshot:{
                actorId:'conversation-uc2',scopeId:'scope-uc2',stateVersion:48,progressSequence:48,
                activeTurn:{turnId:'turn-uc2b-1',taskId:'task-uc2b',status:'active'},
                latestTurn:{turnId:'turn-uc2b-1',taskId:'task-uc2b',status:'active'},
                recentTerminalTurns:[{turnId:'turn-uc2-2',taskId:'task-uc2',status:'stopped'}],
                activeTask:{schemaVersion:4,actorId:'conversation-uc2',turnId:'turn-uc2b-1',
                  taskId:'task-uc2b',planId:'plan-uc2b',planRevision:1,status:'active',
                  gate:{mode:'auto',reason:'Read and draft only.'},steps:[{
                    stepId:'step-uc2b-search',order:1,kind:'tool',status:'running',
                    source:{tool:{toolName:'web_search'}},externalEffect:'not_started',
                    operation:{key:{conversationActorId:'conversation-uc2',turnId:'turn-uc2b-1',
                      taskId:'task-uc2b',stepId:'step-uc2b-search',
                      operationId:'operation-uc2b-search',operationGeneration:1},
                      kind:'tool',phase:'running',mayChangeExternalState:false,
                      idempotent:true,idempotencyKey:'operation-uc2b-search'},
                    addedBy:'initial',availableActions:{stop:true}
                  }]},
                pendingInput:null,pendingApproval:null,latestInputResolution:null,
                latestApprovalResolution:null,taskStatus:'active',attentionKind:'none',
                activeStepSummary:null,pendingActions:[],controlFence:null,
                latestControlResult:null,continuationAdmission:null
              }
            });
            assert.equal(restarted.projection.task.taskId, 'task-uc2b');
            assert.equal(restarted.projection.task.turnId, 'turn-uc2b-1');
            assert.equal(restarted.projection.steps.has('step-uc2-refine'), false);
            assert.equal(restarted.projection.steps.get('step-uc2b-search').source.tool.toolName, 'web_search');
            assert.equal(restarted.projection.steps.get('step-uc2b-search').operation.key.taskId, 'task-uc2b');
            assert.equal(restarted.projection.controlFence, null);
            """;

        var result = await RunNodeAsync(script, actorState);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ConversationProtocol_ShouldPreserveAuthoritativeAttentionSummary()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('export function normalizeConversationIndex(');
            const end = source.indexOf('\nexport function normalizeStoredMessages(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);
            const context = {};
            vm.createContext(context);
            vm.runInContext(source.slice(start, end).replace(/^export /, ''), context);
            const result = context.normalizeConversationIndex({conversations:[{
              id:'conversation-alpha', title:'Deploy', attentionKind:'approval',
              attentionSince:'2026-08-01T12:05:00Z', activeStepSummary:'Delete repository.',
              taskStatus:'active', stateVersion:31
            }]});
            assert.deepEqual(JSON.parse(JSON.stringify(result[0])), {
              id:'conversation-alpha', title:'Deploy', serviceId:'', serviceKind:'', createdAt:null,
              updatedAt:null, messageCount:0, llmRoute:null, llmModel:null, taskStatus:'active',
              attentionKind:'approval', attentionSince:'2026-08-01T12:05:00Z',
              activeStepSummary:'Delete repository.', stateVersion:31
            });
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
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
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('dataLookup', 'parseT')}
              ${functionSource('parseT', 'clockUTC')}
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
