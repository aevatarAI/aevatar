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
        html.Should().Contain("https://id.example.test");
        html.Should().Contain("client-example");
        html.Should().Contain("console:test");
        html.Should().Contain("https://api.example.test/api/v1/proxy/s/aevatar");
        html.Should().Contain("\"nyxidWeb\":\"https://web.example.test\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("https://nyx.chrono-ai.fun");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
        if (endpoint == "admin-observatory")
        {
            html.Should().Contain("searchParams.append(\"resource\"");
            html.Should().Contain("form.append(\"resource\"");
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

        app.Should().Contain("import \"./transport.js?v=20260807-readiness-optional-quiet\"");
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
        blocks.Should().Contain("export function buildConnectCardBlock(");
        html.Should().Contain("id=\"readinessPanel\"");
        html.Should().Contain("id=\"readinessRecovery\"");
        html.Should().Contain("id=\"readinessRecoveryButton\"");
        html.Should().Contain("id=\"needsYouFilterButton\"");
        html.Should().NotContain("id=\"taskPhaseList\"");
        html.Should().Contain("id=\"composerInputRequest\"");
        styles.Should().Contain(".connect-card");
        styles.Should().Contain(".readiness-panel");
        styles.Should().Contain(".needs-you-panel");
        styles.Should().Contain(".history-filter");
        styles.Should().Contain(".actor-plan-meta");
        styles.Should().Contain(".actor-substeps");
        styles.Should().Contain(".actor-task.collapsed");
        styles.Should().Contain(".cc-progress");
        styles.Should().Contain(".activity-card.collapsed");
        styles.Should().Contain("--assistant-card-max-width: 860px");
        styles.Should().Contain("--assistant-card-inline-gutter: 48px");
        styles.Should().Contain("--workspace-max-width: 1400px");
        styles.Should().Contain("--sidebar-width: 264px");
        styles.Should().Contain("--conversation-max-width: 900px");
        styles.Should().Contain("--conversation-inline-gutter: 48px");
        styles.Should().Contain("width: min(448px, calc(100% - 48px))");
        styles.Should().Contain("grid-template-columns: var(--sidebar-width) minmax(0, 1fr)");
        app.Should().Contain("展开计划详情");
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
        html.Should().Contain("app.js?v=20260807-readiness-optional-quiet");
        html.Should().Contain("styles.css?v=20260807-readiness-optional-quiet");
        app.Should().Contain("transport.js?v=20260807-readiness-optional-quiet");
        app.Should().Contain("readiness.js?v=20260807-readiness-optional-quiet");
        transport.Should().Contain("readiness.js?v=20260807-readiness-optional-quiet");
        actorState.Should().Contain("protocol.js?v=20260807-readiness-optional-quiet");
        blocks.Should().Contain("protocol.js?v=20260807-readiness-optional-quiet");
        html.Should().Contain("<span class=\"brand-name\">Aevatar Studio</span>");
        html.Should().NotContain("class=\"brand-mark\"");
        styles.Should().Contain("color-scheme: only light");
        styles.Should().NotContain("color-scheme: dark");
        styles.Should().NotContain("prefers-color-scheme");
        styles.Should().Contain("--bg: #f4f5f7");
        styles.Should().Contain("--accent: #2563eb");
        styles.Should().Contain("--accent-strong: #1d4ed8");
        styles.Should().Contain("--success: #15a34a");
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
    public async Task WorkflowStudio_AssetCacheKeys_ShouldStayConsistentAcrossHtmlAndTransitiveImports()
    {
        var html = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetStudioPage);
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        var blocks = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantBlocks);

        var versions = new[] { html, app, transport, actorState, blocks }
            .SelectMany(source => System.Text.RegularExpressions.Regex.Matches(
                source,
                @"\.(?:js|css)\?v=([A-Za-z0-9-]+)")
                .Select(match => match.Groups[1].Value))
            .ToList();

        versions.Should().NotBeEmpty();
        versions.Distinct().Should().ContainSingle(
            "every stylesheet, module, and transitive import must share one cache key so a stale readiness.js can never pair with a newer page");
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
              ${functionSource('observatoryRouteKey', 'readObservatoryViewState')}
              ${functionSource('readObservatoryViewState', 'writeObservatoryViewState')}
              ${functionSource('writeObservatoryViewState', 'paneScrollPosition')}
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
            const routeAKey = vm.runInContext('observatoryRouteKey', context)(routeA);

            vm.runInContext('writeObservatoryViewState', context)(storage, key, routeA, {list:180, detail:760});
            vm.runInContext('writeObservatoryViewState', context)(storage, key, routeB, {list:25, detail:40});
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('readObservatoryViewState', context)(storage, key, routeA))), {list:180, detail:760});
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('readObservatoryViewState', context)(storage, key, routeB))), {list:25, detail:40});
            assert.match(routeAKey, /run-alpha/);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:0,scrollHeight:100,clientHeight:100}, 760), 760);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:0,scrollHeight:900,clientHeight:300}, 760), 0);
            assert.equal(vm.runInContext('paneScrollPosition', context)({scrollTop:180,scrollHeight:900,clientHeight:300}, 760), 180);

            storage.setItem(key, '{bad json');
            assert.deepEqual(JSON.parse(JSON.stringify(vm.runInContext('readObservatoryViewState', context)(storage, key, routeA))), {list:0, detail:0});
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
