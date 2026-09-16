using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed partial class WorkflowConsoleStaticAssetEndpointTests
{
    [Fact]
    public async Task WorkflowStudio_ServiceAccessReviewTransport_ShouldKeepTheReviewBearerNarrow()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);

        transport.Should().Contain("const SERVICE_ACCESS_REVIEW_TOKEN_KEY =");
        transport.Should().Contain("function getServiceAccessReviewToken()");
        transport.Should().Contain("function clearServiceAccessReviewToken()");
        transport.Should().Contain("async function fetchServiceAccessReviewCatalog()");
        transport.Should().Contain("async function continueServiceAccessReview(body, init = {})");
        transport.Should().Contain("`${config.nyxidApi}/api/v1/mcp/config`");
        transport.Should().Contain("request.type !== \"action.continue\"");
        transport.Should().Contain("serviceAccessReviewAuthorizedFetch(\"/api/chat\"");
        transport.Should().Contain("pending.authFlow === SERVICE_ACCESS_REVIEW_FLOW");
        transport.Should().Contain("setServiceAccessReviewToken(token)");
        transport.Should().Contain("clearServiceAccessReviewToken();");
        transport.Should().Contain("function onServiceAccessReviewResult(listener)");
        transport.Should().Contain("window.open(");
        transport.Should().Contain("message.requestId !== serviceAccessReviewRequestId");
        transport.Should().Contain("authorizedFetch(\"/api/chat\"");
        transport.Should().NotContain("setToken(token);\n}",
            "the standalone OAuth callback must branch before storing a review token");
    }

    [Fact]
    public async Task WorkflowStudio_RunningUi_ShouldExposeAPersistentAccessibleStatus()
    {
        var html = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetStudioPage);
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var styles = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantStyles);

        html.Should().Contain(
            "id=\"composerStatus\" role=\"status\" aria-live=\"polite\" aria-atomic=\"true\" aria-busy=\"false\"");
        app.Should().Contain("function setComposerStatus(message, { working = false } = {})");
        app.Should().Contain("dom.composerStatus.classList.toggle(\"working\", working)");
        app.Should().Contain(
            "setComposerStatus(\"Agent 正在执行当前任务；仍可输入 steering 指令\", { working: true })");
        styles.Should().Contain(".composer-status.working::before");
        styles.Should().Contain("animation: spin 720ms linear infinite");
        styles.Should().Contain(".composer-status.working::before { animation: none; }");
    }

    [Fact]
    public async Task WorkflowStudio_ActionContinuationCredentialRefreshRequired_ShouldNotExpireTheSession()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('async function authorizedFetch(input, init = {}) {');
            const end = source.indexOf('\nasync function serviceAccessReviewAuthorizedFetch(', start);
            assert.notEqual(start, -1, 'authorized fetch must exist');
            assert.notEqual(end, -1, 'review fetch must follow authorized fetch');

            let refreshCalls = 0;
            let clearCalls = 0;
            let loginCalls = 0;
            const context = {
              Response,
              ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE:
                'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED',
              getToken:() => ({access_token:'session-bearer'}),
              nativeFetch:async () => new Response(JSON.stringify({
                code:'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED',
                message:'The action continuation requires a refreshed NyxID credential.',
              }), {
                status:401,
                headers:{'Content-Type':'application/json'},
              }),
              replacementToken:() => null,
              requestAdminShellTokenRefresh:async () => {
                refreshCalls += 1;
                return {token:null,errorCode:'AUTH_REQUIRED',reason:'expired'};
              },
              clearToken:() => { clearCalls += 1; },
              notifyAdminShellAuth:() => { loginCalls += 1; },
              jsonResponse:(value, status) => new Response(JSON.stringify(value), {
                status,
                headers:{'Content-Type':'application/json'},
              }),
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const response = await context.authorizedFetch('/api/chat', {method:'POST'});
            const payload = await response.json();

            assert.equal(response.status, 401);
            assert.equal(payload.code, 'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED');
            assert.equal(refreshCalls, 0, 'a service grant review is not a session refresh');
            assert.equal(clearCalls, 0, 'the valid session bearer must be preserved');
            assert.equal(loginCalls, 0, 'the admin shell must not show the ordinary login gate');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ProxyCatalog_ShouldExposeExactMcpServiceResources()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                source.indexOf('function ' + name + '('),
                source.indexOf('export function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                source.indexOf('\nfunction ' + nextName + '(', start),
                source.indexOf('\nexport function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served Studio transport');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return source.slice(start, end).replace(/^export /, '');
            }

            const context = {};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('trimBaseUrl', 'uniqueStrings')}
              ${functionSource('uniqueStrings', 'jsonResponse')}
              ${functionSource('proxyCatalogResourceUri', 'normalizeProxyCatalog')}
              ${functionSource('normalizeProxyCatalog', 'catalogAuthKind')}
            `, context);

            const catalog = context.normalizeProxyCatalog({
              proxy_base_url:'https://id.example.test/api/v1/proxy/',
              services:[
                {service_id:'svc-aevatar',service_slug:'aevatar',service_name:'Aevatar',is_user_service:true},
                {service_id:'svc-github',service_slug:'api-github',service_name:'GitHub',is_user_service:true},
              ]
            }, [
              'https://id.example.test/api/v1/proxy/s/aevatar',
              'https://id.example.test/api/v1/proxy/s/ornn-api',
            ]);

            assert.equal(catalog.proxyBaseUrl, 'https://id.example.test/api/v1/proxy');
            assert.deepEqual(JSON.parse(JSON.stringify(catalog.services)), [
              {
                userServiceId:'svc-aevatar', serviceSlug:'aevatar', serviceName:'Aevatar',
                resourceUri:'https://id.example.test/api/v1/proxy/s/aevatar', isUserService:true
              },
              {
                userServiceId:'svc-github', serviceSlug:'api-github', serviceName:'GitHub',
                resourceUri:'https://id.example.test/api/v1/proxy/s/api-github', isUserService:true
              },
            ]);
            assert.deepEqual(JSON.parse(JSON.stringify(catalog.resources)), [
              'https://id.example.test/api/v1/proxy/s/aevatar',
              'https://id.example.test/api/v1/proxy/s/ornn-api',
              'https://id.example.test/api/v1/proxy/s/api-github',
            ]);
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        transport.Should().Contain("/api/v1/mcp/config");
        transport.Should().Contain("/api/nyxid/proxy-catalog");
    }

    [Fact]
    public async Task WorkflowStudio_Protocol_ShouldAcceptOnlyExactServiceAccessReviewParams()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, URL };
            vm.createContext(context);
            vm.runInContext(source, context);

            const valid = context.validateActionRequest({
              schemaVersion:4,
              actorId:'action-actor-alpha',
              originTurnId:'turn-alpha',
              taskId:'task-alpha',
              stepId:'step-access-review',
              actionRequestId:'action-access-review-alpha',
              action:'service.access_review',
              params:{serviceAccessReview:{
                userServiceId:'us-github-alpha',
                serviceSlug:'api-github',
                resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              }},
            });
            assert.equal(valid.action, 'service.access_review');
            assert.deepEqual(JSON.parse(JSON.stringify(valid.params)), {
              serviceAccessReview:{
                userServiceId:'us-github-alpha',
                serviceSlug:'api-github',
                resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              },
            });

            assert.throws(() => context.validateActionRequest({
              ...valid,
              params:{serviceAccessReview:{
                ...valid.params.serviceAccessReview,
                resourceUri:'https://id.example.test/api/v1/proxy/s/slack',
              }},
            }), error => error && error.code === 'NYXID_ACTION_PARAMS_INVALID');

            assert.throws(() => context.validateActionRequest({
              ...valid,
              params:{serviceAccessReview:{
                ...valid.params.serviceAccessReview,
                bearerToken:'must-never-cross-the-wire',
              }},
            }));

            const continuation = context.validateActionContinuation({
              type:'action.continue',
              clientRequestId:'client-access-review-alpha',
              originTurnId:'turn-alpha',
              actions:[{
                actionRequestId:'action-access-review-alpha',
                originTurnId:'turn-alpha',
                disposition:'completed',
                resource:{userService:{userServiceId:'us-github-alpha'}},
              }],
            }, {expectedAction:'service.access_review'});
            assert.equal(continuation.actions[0].resource.userService.userServiceId, 'us-github-alpha');
            assert.throws(() => context.validateActionContinuation({
              type:'action.continue',
              clientRequestId:'client-access-review-beta',
              originTurnId:'turn-alpha',
              actions:[{
                actionRequestId:'action-access-review-alpha',
                originTurnId:'turn-alpha',
                disposition:'completed',
              }],
            }, {expectedAction:'service.access_review'}),
            error => error && error.code === 'NYXID_ACTION_RESOURCE_INVALID');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ServiceAccessReviewCard_ShouldRepresentOAuthClientAccess()
    {
        var blocks = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantBlocks);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8')
              .replace(/^import[^;]+;\s*/m, '')
              .replace(/^export /gm, '');
            const context = { validateActionRequest:value => value };
            vm.createContext(context);
            vm.runInContext(source, context);

            const block = context.buildConnectCardBlock({
              schemaVersion:4,
              actorId:'action-actor-alpha',
              originTurnId:'turn-alpha',
              taskId:'task-alpha',
              stepId:'step-access-review',
              actionRequestId:'action-access-review-alpha',
              action:'service.access_review',
              params:{serviceAccessReview:{
                userServiceId:'us-github-alpha',
                serviceSlug:'api-github',
                resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              }},
            }, {
              connected:[{
                slug:'api-github',
                name:'GitHub',
                description:'GitHub issues and repositories',
                authKind:'oauth',
                userServices:[{userServiceId:'us-github-alpha'}],
              }],
              available:[],
            });

            assert.equal(block.type, 'service_access_review_card');
            assert.equal(block.variant, 'serviceAccessReview');
            assert.equal(block.user_service_id, 'us-github-alpha');
            assert.equal(block.resource_uri, 'https://id.example.test/api/v1/proxy/s/api-github');
            assert.equal(block.service_name, 'GitHub');
            assert.equal(block.auth_kind, 'oauth');
            assert.equal(block.state, 'needs_review');
            assert.match(block.steps[0].title, /OAuth client/);
            assert.match(block.steps[2].body, /actor/);
            """;

        var result = await RunNodeAsync(script, blocks);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
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

        app.Should().Contain("import \"./transport.js?v=20260823-m62-studio-redesign\"");
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
        app.Should().Contain("function buildTrajectoryRows(entry)");
        app.Should().Contain("function renderTrajectoryLedger(entry, rows)");
        app.Should().Contain("function renderTrajectoryOverview(entry, rows)");
        app.Should().Contain("function renderTrajectoryDetails(entry)");
        app.Should().Contain("function bindTrajectoryOverview()");
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
        transport.Should().Contain("beginServiceAccessReview");
        transport.Should().Contain("url.searchParams.append(\"resource\"");
        transport.Should().Contain("url.searchParams.set(\"prompt\", \"consent\")");
        blocks.Should().Contain("export function buildConnectCardBlock(");
        html.Should().Contain("id=\"readinessPanel\"");
        html.Should().Contain("id=\"readinessRecovery\"");
        html.Should().Contain("id=\"readinessRecoveryButton\"");
        html.Should().Contain("id=\"needsYouFilterButton\"");
        html.Should().NotContain("id=\"taskPhaseList\"");
        html.Should().Contain("id=\"composerInputRequest\"");
        html.Should().Contain("class=\"content-view-switch\"");
        html.Should().Contain("class=\"trajectory-toolbar\"");
        html.Should().Contain("id=\"trajectoryDurationButton\"");
        html.Should().Contain("id=\"trajectoryFoldRequestsButton\"");
        html.Should().Contain("id=\"trajectoryFoldCallsButton\"");
        html.Should().Contain("id=\"trajectorySearchInput\"");
        html.Should().Contain("id=\"traceOperationOverview\"");
        html.Should().Contain("aria-label=\"Input、Model、Tools 时间总览\"");
        html.Should().Contain("id=\"trajectoryOverviewTrack\"");
        html.Should().Contain("id=\"traceOperationList\"");
        html.Should().Contain("id=\"trajectoryDetails\"");
        html.Should().Contain("id=\"trajectoryDetailsTabs\"");
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
        styles.Should().Contain(".request-trace-readonly");
        styles.Should().Contain(".trajectory-toolbar");
        styles.Should().Contain(".trajectory-overview");
        styles.Should().Contain(".trajectory-span");
        styles.Should().Contain(".trajectory-table");
        styles.Should().Contain(".trajectory-row");
        styles.Should().Contain(".trajectory-details");
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
        html.Should().Contain("app.js?v=20260823-m62-studio-redesign");
        html.Should().Contain("styles.css?v=20260823-m62-studio-redesign");
        app.Should().Contain("transport.js?v=20260823-m62-studio-redesign");
        app.Should().Contain("readiness.js?v=20260823-m62-studio-redesign");
        transport.Should().Contain("readiness.js?v=20260823-m62-studio-redesign");
        actorState.Should().Contain("protocol.js?v=20260823-m62-studio-redesign");
        blocks.Should().Contain("protocol.js?v=20260823-m62-studio-redesign");
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
    public async Task WorkflowStudio_BrowserConnectCompletion_ShouldRefreshDurableCatalogBeforeContinuation()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const refreshStart = source.indexOf('async function refreshNyxIdAuthorizationCatalog(userServiceId) {');
            const refreshEnd = source.indexOf('\nasync function submitConnectCredential(', refreshStart);
            const connectStart = source.indexOf('async function refreshConnectCard(card) {');
            const connectEnd = source.indexOf('\nfunction updateLiveConnectCards()', connectStart);
            assert.notEqual(refreshStart, -1, 'durable authorization catalog refresh helper must exist');
            assert.notEqual(refreshEnd, -1, 'credential completion must follow the catalog refresh helper');
            assert.notEqual(connectStart, -1, 'browser connect completion handler must exist');
            assert.notEqual(connectEnd, -1, 'browser connect completion handler boundary must exist');

            const calls = [];
            const context = {
              Set,
              state:{config:{resources:[]},connectors:{connected:[],available:[]}},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              fetch:async (path, init) => {
                if (path === '/api/nyxid/proxy-catalog') {
                  calls.push({kind:'proxy-catalog'});
                  return {
                    ok:true,
                    status:200,
                    json:async () => ({
                      proxyBaseUrl:'https://id.example.test/api/v1/proxy',
                      resources:['https://id.example.test/api/v1/proxy/s/github'],
                      services:[{
                        userServiceId:'user-service-new',
                        serviceSlug:'github',
                        resourceUri:'https://id.example.test/api/v1/proxy/s/github',
                      }],
                    }),
                  };
                }
                calls.push({
                  kind:'catalog-refresh',
                  path,
                  method:init?.method || 'GET',
                  body:JSON.parse(String(init?.body || '{}')),
                });
                return {
                  ok:true,
                  status:200,
                  json:async () => ({
                    ready:true,
                    refreshStatus:'observed',
                    visibilityStatus:'visible',
                    requiredStateVersion:42,
                    visibleStateVersion:42,
                  }),
                };
              },
              renderConnectCard:() => {},
              loadConnectors:async () => { calls.push({kind:'connectors'}); },
              buildConnectCardBlock:() => ({service_name:'GitHub',state:'needs_connection',steps:[]}),
              matchingUserServiceIds:() => new Set(['user-service-new']),
              submitActionContinuation:async (_card, disposition, resource) => {
                calls.push({kind:'continuation',disposition,resource});
              },
              loadServices:() => { calls.push({kind:'services'}); },
            };
            vm.createContext(context);
            vm.runInContext(source.slice(refreshStart, refreshEnd), context);
            vm.runInContext(source.slice(connectStart, connectEnd), context);

            const card = {
              busy:false,
              error:'',
              status:'waiting_for_user',
              slug:'github',
              request:{
                actorId:'action-actor-alpha',
                originTurnId:'turn-alpha',
                actionRequestId:'action-alpha',
                params:{catalogService:{serviceSlug:'github'}},
              },
              block:{service_name:'GitHub',state:'needs_connection',steps:[]},
              externalBaseline:new Set(),
            };
            await context.refreshConnectCard(card);

            const refreshIndex = calls.findIndex((call) => call.kind === 'catalog-refresh');
            const continuationIndex = calls.findIndex((call) => call.kind === 'continuation');
            assert.ok(refreshIndex >= 0, 'browser completion refreshes the durable catalog');
            assert.ok(continuationIndex > refreshIndex, 'continuation is reported only after catalog visibility');
            assert.equal(calls[refreshIndex].path, '/api/auth/nyxid/authorization-catalog:refresh');
            assert.equal(calls[refreshIndex].method, 'POST');
            assert.deepEqual(JSON.parse(JSON.stringify(calls[refreshIndex].body)), {
              requiredUserServiceIds:['user-service-new'],
            });
            assert.deepEqual(JSON.parse(JSON.stringify(calls[continuationIndex])), {
              kind:'continuation',
              disposition:'completed',
              resource:{userService:{userServiceId:'user-service-new'}},
            });
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ApiKeyConnectCompletion_ShouldRefreshDurableCatalogBeforeContinuation()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const refreshStart = source.indexOf('async function refreshNyxIdAuthorizationCatalog(userServiceId) {');
            const credentialEnd = source.indexOf('\nfunction continuationIntent(', refreshStart);
            assert.notEqual(refreshStart, -1, 'durable authorization catalog refresh helper must exist');
            assert.notEqual(credentialEnd, -1, 'credential completion handler boundary must exist');

            const calls = [];
            const context = {
              Set,
              state:{config:{resources:[]}},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              matchingUserServiceIds:() => new Set(['user-service-existing']),
              fetch:async (path, init) => {
                if (path === '/api/nyxid/keys') {
                  calls.push({kind:'credential-create',method:init?.method || 'GET'});
                  return {
                    ok:true,
                    status:200,
                    json:async () => ({userService:{userServiceId:'user-service-key'}}),
                  };
                }
                if (path === '/api/nyxid/proxy-catalog') {
                  calls.push({kind:'proxy-catalog'});
                  return {
                    ok:true,
                    status:200,
                    json:async () => ({
                      proxyBaseUrl:'https://id.example.test/api/v1/proxy',
                      resources:['https://id.example.test/api/v1/proxy/s/github'],
                      services:[{
                        userServiceId:'user-service-key',
                        serviceSlug:'github',
                        resourceUri:'https://id.example.test/api/v1/proxy/s/github',
                      }],
                    }),
                  };
                }
                calls.push({
                  kind:'catalog-refresh',
                  path,
                  method:init?.method || 'GET',
                  body:JSON.parse(String(init?.body || '{}')),
                });
                return {
                  ok:true,
                  status:200,
                  json:async () => ({
                    ready:true,
                    refreshStatus:'observed',
                    visibilityStatus:'visible',
                    requiredStateVersion:43,
                    visibleStateVersion:43,
                  }),
                };
              },
              responseError:async () => new Error('request failed'),
              renderConnectCard:() => {},
              submitActionContinuation:async (_card, disposition, resource) => {
                calls.push({kind:'continuation',disposition,resource});
              },
              loadServices:() => { calls.push({kind:'services'}); },
            };
            vm.createContext(context);
            vm.runInContext(source.slice(refreshStart, credentialEnd), context);

            const input = {value:'github-token'};
            const card = {
              slug:'github',
              busy:false,
              error:'',
              status:'needs_connection',
              keyInputOpen:true,
              request:{params:{catalogService:{serviceSlug:'github'}}},
              block:{service_name:'GitHub'},
            };
            await context.submitConnectCredential(card, input.value, input);

            const createIndex = calls.findIndex((call) => call.kind === 'credential-create');
            const refreshIndex = calls.findIndex((call) => call.kind === 'catalog-refresh');
            const continuationIndex = calls.findIndex((call) => call.kind === 'continuation');
            assert.ok(createIndex >= 0, 'API key is created first');
            assert.ok(refreshIndex > createIndex, 'durable catalog refresh follows credential creation');
            assert.ok(continuationIndex > refreshIndex, 'continuation is reported only after catalog visibility');
            assert.equal(calls[refreshIndex].path, '/api/auth/nyxid/authorization-catalog:refresh');
            assert.equal(calls[refreshIndex].method, 'POST');
            assert.deepEqual(JSON.parse(JSON.stringify(calls[refreshIndex].body)), {
              requiredUserServiceIds:['user-service-key'],
            });
            assert.deepEqual(JSON.parse(JSON.stringify(calls[continuationIndex])), {
              kind:'continuation',
              disposition:'completed',
              resource:{userService:{userServiceId:'user-service-key'}},
            });
            assert.equal(input.value, '');
            assert.deepEqual([...card.externalBaseline], ['user-service-existing']);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_CatalogRefreshPending_ShouldNotBeAcceptedAsReady()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const refreshStart = source.indexOf('async function refreshNyxIdAuthorizationCatalog(userServiceId) {');
            const refreshEnd = source.indexOf('\nasync function submitConnectCredential(', refreshStart);
            const connectStart = source.indexOf('async function refreshConnectCard(card) {');
            const connectEnd = source.indexOf('\nfunction updateLiveConnectCards()', connectStart);
            assert.notEqual(refreshStart, -1, 'durable authorization catalog refresh helper must exist');
            assert.notEqual(refreshEnd, -1, 'credential completion must follow the catalog refresh helper');
            assert.notEqual(connectStart, -1, 'browser connect completion handler must exist');
            assert.notEqual(connectEnd, -1, 'browser connect completion handler boundary must exist');

            let continuationCalls = 0;
            const context = {
              Set,
              state:{config:{resources:[]},connectors:{connected:[],available:[]}},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              fetch:async (path) => path === '/api/nyxid/proxy-catalog'
                ? ({
                    ok:true,
                    status:200,
                    json:async () => ({
                      proxyBaseUrl:'https://id.example.test/api/v1/proxy',
                      resources:['https://id.example.test/api/v1/proxy/s/github'],
                      services:[{
                        userServiceId:'user-service-new',
                        serviceSlug:'github',
                        resourceUri:'https://id.example.test/api/v1/proxy/s/github',
                      }],
                    }),
                  })
                : ({
                    ok:true,
                    status:202,
                    json:async () => ({
                      ready:false,
                      refreshStatus:'observed',
                      visibilityStatus:'projection_pending',
                      requiredStateVersion:51,
                      visibleStateVersion:50,
                    }),
                  }),
              renderConnectCard:() => {},
              loadConnectors:async () => {},
              buildConnectCardBlock:() => ({service_name:'GitHub',state:'needs_connection',steps:[]}),
              matchingUserServiceIds:() => new Set(['user-service-new']),
              submitActionContinuation:async () => { continuationCalls += 1; },
              loadServices:() => {},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(refreshStart, refreshEnd), context);
            vm.runInContext(source.slice(connectStart, connectEnd), context);

            const card = {
              busy:false,
              error:'',
              status:'waiting_for_user',
              slug:'github',
              request:{
                actorId:'action-actor-alpha',
                originTurnId:'turn-alpha',
                actionRequestId:'action-alpha',
                params:{catalogService:{serviceSlug:'github'}},
              },
              block:{service_name:'GitHub',state:'needs_connection',steps:[]},
              externalBaseline:new Set(),
            };
            await context.refreshConnectCard(card);

            assert.equal(continuationCalls, 0, 'projection-pending catalog cannot report completion');
            assert.equal(card.busy, false);
            assert.equal(card.status, 'error');
            assert.match(card.error, /尚未可见/);
            assert.match(card.note, /不会向 Actor 报告完成/);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ServiceAccessReviewAction_ShouldRequestExactConsentWithoutReportingCompletion()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function serviceAccessReviewParams(card) {');
            const end = source.indexOf('\nasync function resumePendingServiceAccessReview()', start);
            assert.notEqual(start, -1, 'typed service access review helpers must exist');
            assert.notEqual(end, -1, 'resume handler must follow the access review launcher');

            const calls = [];
            const storage = new Map();
            const context = {
              SERVICE_ACCESS_REVIEW_KEY:'aevatar-studio:pending-service-access-review:v1',
              globalThis:{AevatarStudioAuth:{
                beginServiceAccessReview:async (resources) => calls.push({kind:'consent',resources}),
              }},
              actionResourceUserServiceId:(resource) => resource?.userService?.userServiceId || '',
              readJsonStorage:(key) => JSON.parse(storage.get(key) || 'null'),
              writeStorage:(key, value) => storage.set(key, value),
              removeStorage:(key) => storage.delete(key),
              renderConnectCard:() => {},
              continuationIntent:(_card, disposition, resource) => ({
                type:'action.continue', clientRequestId:'client-action-original',
                originTurnId:'turn-origin',
                actions:[{
                  actionRequestId:'action-access-review',
                  originTurnId:'turn-origin',
                  disposition,
                  resource,
                }],
              }),
              Date,
              JSON,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const card = {
              conversation:{actorId:'conversation-alpha'},
              request:{
                actorId:'action-actor-alpha', originTurnId:'turn-origin',
                taskId:'task-alpha', stepId:'step-access-review',
                actionRequestId:'action-access-review', action:'service.access_review',
                params:{serviceAccessReview:{
                  userServiceId:'svc-github',
                  serviceSlug:'api-github',
                  resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
                }},
              },
              block:{service_name:'GitHub'},
              status:'needs_review', busy:false, note:'', error:'',
            };
            const started = await context.beginServiceAccessReviewAction(card);

            assert.equal(started, true);
            const consent = calls.find((call) => call.kind === 'consent');
            assert.deepEqual(JSON.parse(JSON.stringify(consent.resources)), [
              'https://id.example.test/api/v1/proxy/s/api-github',
            ]);
            const pending = context.readJsonStorage(context.SERVICE_ACCESS_REVIEW_KEY);
            assert.deepEqual(JSON.parse(JSON.stringify(pending)), {
              schemaVersion:3,
              action:'service.access_review',
              conversationId:'conversation-alpha',
              actorId:'action-actor-alpha',
              originTurnId:'turn-origin',
              actionRequestId:'action-access-review',
              disposition:'completed',
              resource:{userService:{userServiceId:'svc-github'}},
              serviceSlug:'api-github',
              userServiceId:'svc-github',
              resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              clientRequestId:'client-action-original',
              createdAt:pending.createdAt,
            });
            assert.equal(typeof pending.createdAt, 'number');
            assert.equal(card.status, 'reauthorizing');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActionContinuationCredentialRefresh_ShouldResumeTheOriginalCardWithReviewBearer()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function proxyCatalogContainsService(catalog, userServiceId, serviceSlug, resourceUri = "") {');
            const end = source.indexOf('\nasync function refreshConnectCard(card) {', start);
            assert.notEqual(start, -1, 'service access review helpers must exist');
            assert.notEqual(end, -1, 'refresh card must follow continuation helpers');

            const calls = [];
            const storage = new Map();
            const resource = {userService:{userServiceId:'svc-github'}};
            const card = {
              request:{
                actorId:'action-actor-alpha', originTurnId:'turn-origin',
                taskId:'task-alpha', stepId:'step-connect',
                actionRequestId:'action-connect', action:'service.connect',
                params:{catalogService:{serviceSlug:'api-github'}},
              },
              block:{
                service_name:'GitHub', catalog_slug:'api-github',
                state:'needs_connection', steps:[],
              },
              status:'waiting_for_user', busy:false, error:'', note:'',
              continuation:null, report:null,
            };
            const entry = {
              actorId:'conversation-alpha', controller:null, controllers:new Set(),
              run:{cardElements:new Map([['action-actor-alpha:action-connect',card]])},
            };
            card.conversation = entry;

            const context = {
              AbortController,
              Date,
              JSON,
              Map,
              Set,
              SERVICE_ACCESS_REVIEW_KEY:'aevatar-studio:pending-service-access-review:v1',
              ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE:
                'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED',
              state:{
                activeConversation:entry,
                activeController:null,
                conversations:[{id:'conversation-alpha'}],
              },
              globalThis:{AevatarStudioAuth:{
                serviceResourceUri:(slug) => `https://id.example.test/api/v1/proxy/s/${slug}`,
                beginServiceAccessReview:async (resources) => calls.push({kind:'consent',resources}),
                fetchServiceAccessReviewCatalog:async () => ({
                  ok:true,
                  status:200,
                  json:async () => ({
                    proxyBaseUrl:'https://id.example.test/api/v1/proxy',
                    resources:['https://id.example.test/api/v1/proxy/s/api-github'],
                    services:[{
                      userServiceId:'svc-github',
                      serviceSlug:'api-github',
                      resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
                    }],
                  }),
                }),
                clearServiceAccessReviewToken:() => calls.push({kind:'clear-review-token'}),
                continueServiceAccessReview:async () => {
                  throw new Error('the first continuation must use the session bearer');
                },
              }},
              actionResourceUserServiceId:(value) => value?.userService?.userServiceId || '',
              createId:() => 'client-action-original',
              validateActionContinuation:(value) => value,
              restoreCachedAction:() => true,
              clearExternalJourneyTimer:() => {},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              fetch:async () => ({ok:false,status:401}),
              responseError:async () => {
                const error = new Error('The action continuation requires a refreshed NyxID credential.');
                error.code = 'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED';
                error.status = 401;
                return error;
              },
              setRunningUi:() => {},
              renderConnectCard:() => {},
              withConversationState:(_entry, callback) => callback(),
              releaseConversationController:(conversation, controller) => {
                conversation.controllers.delete(controller);
                if (conversation.controller === controller) conversation.controller = null;
              },
              readJsonStorage:(key) => JSON.parse(storage.get(key) || 'null'),
              writeStorage:(key, value) => storage.set(key, value),
              removeStorage:(key) => storage.delete(key),
              loadConversation:async () => calls.push({kind:'load-conversation'}),
              findConversationState:() => entry,
              refreshActionActorState:async (_entry, actorId, options) => {
                calls.push({kind:'actor-refresh',actorId,options});
              },
              renderActionCards:() => calls.push({kind:'render-actions'}),
              actionEntryKey:(actorId, actionRequestId) => `${actorId}:${actionRequestId}`,
              responseErrorFromCatalog:async () => new Error('catalog failed'),
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const firstResult = await context.submitActionContinuation(card, 'completed', resource);
            const pending = context.readJsonStorage(context.SERVICE_ACCESS_REVIEW_KEY);

            assert.equal(firstResult.credentialRefreshStarted, true);
            assert.equal(card.status, 'reauthorizing');
            assert.match(card.note, /更新 NyxID 服务授权/);
            assert.equal(entry.run.cardElements.get('action-actor-alpha:action-connect'), card);
            assert.equal(card.conversation, entry);
            assert.deepEqual(JSON.parse(JSON.stringify(pending)), {
              schemaVersion:3,
              action:'service.connect',
              conversationId:'conversation-alpha',
              actorId:'action-actor-alpha',
              originTurnId:'turn-origin',
              actionRequestId:'action-connect',
              disposition:'completed',
              resource,
              serviceSlug:'api-github',
              userServiceId:'svc-github',
              resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              clientRequestId:'client-action-original',
              createdAt:pending.createdAt,
            });
            assert.equal(
              calls.some((call) => call.kind === 'consent'),
              false,
              'an async 401 cannot open a popup without a fresh user gesture');

            const opened = await context.beginActionContinuationCredentialRefresh(
              card,
              'completed',
              resource,
            );
            assert.equal(opened, true);
            assert.deepEqual(JSON.parse(JSON.stringify(
              calls.find((call) => call.kind === 'consent').resources,
            )), ['https://id.example.test/api/v1/proxy/s/api-github']);

            context.submitActionContinuation = async (target, disposition, resumedResource, options) => {
              calls.push({
                kind:'resumed-continuation',
                target,
                disposition,
                resource:resumedResource,
                options,
                clientRequestId:target.continuation?.clientRequestId,
              });
              target.status = 'verified';
              return {verified:true,terminalObserved:true};
            };
            const resumed = await context.resumePendingServiceAccessReview();
            const continuation = calls.find((call) => call.kind === 'resumed-continuation');

            assert.equal(resumed, true);
            assert.equal(continuation.target, card);
            assert.equal(continuation.disposition, 'completed');
            assert.deepEqual(JSON.parse(JSON.stringify(continuation.resource)), resource);
            assert.deepEqual(JSON.parse(JSON.stringify(continuation.options)), {
              credential:'serviceAccessReview',
            });
            assert.equal(continuation.clientRequestId, 'client-action-original');
            assert.equal(storage.has(context.SERVICE_ACCESS_REVIEW_KEY), false);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_OAuthReturn_ShouldResumeTheSameActionContinuationIdentity()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function proxyCatalogContainsService(catalog, userServiceId, serviceSlug, resourceUri = "") {');
            const end = source.indexOf('\nasync function submitConnectCredential(', start);
            assert.notEqual(start, -1, 'service access review helpers must exist');
            assert.notEqual(end, -1, 'credential completion must follow service access review helpers');

            const calls = [];
            const pending = {
              schemaVersion:3,
              action:'service.access_review',
              conversationId:'conversation-alpha',
              actorId:'action-actor-alpha',
              originTurnId:'turn-origin',
              actionRequestId:'action-access-review',
              disposition:'completed',
              resource:{userService:{userServiceId:'svc-github'}},
              serviceSlug:'api-github',
              userServiceId:'svc-github',
              resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
              clientRequestId:'client-action-original',
              createdAt:Date.now(),
            };
            const card = {
              request:{
                actorId:'action-actor-alpha', originTurnId:'turn-origin',
                taskId:'task-alpha', stepId:'step-access-review',
                actionRequestId:'action-access-review', action:'service.access_review',
                params:{serviceAccessReview:{
                  userServiceId:'svc-github',
                  serviceSlug:'api-github',
                  resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
                }},
              },
              status:'needs_review', busy:false,
            };
            const entry = {
              actorId:'conversation-alpha',
              run:{cardElements:new Map([['action-actor-alpha:action-access-review',card]])},
            };
            let terminalObserved = false;
            const context = {
              SERVICE_ACCESS_REVIEW_KEY:'aevatar-studio:pending-service-access-review:v1',
              state:{conversations:[{id:'conversation-alpha'}]},
              globalThis:{AevatarStudioAuth:{
                fetchServiceAccessReviewCatalog:async () => {
                  calls.push({kind:'review-catalog'});
                  return {
                    ok:true,
                    status:200,
                    json:async () => ({
                      proxyBaseUrl:'https://id.example.test/api/v1/proxy',
                      resources:['https://id.example.test/api/v1/proxy/s/api-github'],
                      services:[{
                        userServiceId:'svc-github',
                        serviceSlug:'api-github',
                        resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
                      }],
                    }),
                  };
                },
                clearServiceAccessReviewToken:() => calls.push({kind:'clear-review-token'}),
              }},
              actionResourceUserServiceId:(resource) => resource?.userService?.userServiceId || '',
              readJsonStorage:() => pending,
              writeStorage:() => {},
              removeStorage:(key) => calls.push({kind:'clear-pending',key}),
              findConversationState:() => entry,
              loadConversation:async () => calls.push({kind:'load-conversation'}),
              refreshActionActorState:async (_entry, actorId, options) => {
                calls.push({kind:'actor-refresh',actorId,options});
              },
              renderActionCards:() => calls.push({kind:'render-actions'}),
              actionEntryKey:(actorId, actionRequestId) => `${actorId}:${actionRequestId}`,
              validateActionContinuation:(value) => value,
              submitActionContinuation:async (target, disposition, resource) => {
                calls.push({
                  kind:'continuation', disposition, resource,
                  clientRequestId:target.continuation?.clientRequestId,
                });
                return {verified:true,terminalObserved};
              },
              renderConnectCard:() => {},
              continuationIntent:() => { throw new Error('resume must restore the original clientRequestId'); },
              responseError:async () => new Error('request failed'),
              Date,
              JSON,
              Map,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const waitingForTerminal = await context.resumePendingServiceAccessReview();
            assert.equal(waitingForTerminal, false);
            assert.equal(calls.some((call) => call.kind === 'clear-pending'), false);
            assert.equal(calls.some((call) => call.kind === 'clear-review-token'), false);

            terminalObserved = true;
            const resumed = await context.resumePendingServiceAccessReview();

            assert.equal(resumed, true);
            const reviewIndex = calls.findIndex((call) => call.kind === 'review-catalog');
            const continuationIndex = calls.findIndex((call) => call.kind === 'continuation');
            assert.ok(reviewIndex >= 0);
            assert.ok(continuationIndex > reviewIndex);
            assert.deepEqual(JSON.parse(JSON.stringify(calls[continuationIndex])), {
              kind:'continuation', disposition:'completed',
              resource:{userService:{userServiceId:'svc-github'}},
              clientRequestId:'client-action-original',
            });
            assert.deepEqual(calls.slice(-2).map((call) => call.kind), [
              'clear-pending',
              'clear-review-token',
            ]);
            assert.equal(card.continuation.clientRequestId, 'client-action-original');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("await resumePendingServiceAccessReview();");
    }

    [Fact]
    public async Task WorkflowStudio_ServiceAccessReviewPopup_ShouldResumeInPlaceWithoutReloading()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function markServiceAccessReviewInterrupted(pending, message) {');
            const end = source.indexOf('\nasync function submitConnectCredential(', start);
            assert.notEqual(start, -1, 'popup result handler must exist');
            assert.notEqual(end, -1, 'credential flow must follow popup result handler');

            const calls = [];
            let pending = {conversationId:'conversation-alpha'};
            const context = {
              SERVICE_ACCESS_REVIEW_KEY:'pending-service-review',
              state:{activeController:null,auth:{authenticated:true}},
              readJsonStorage:() => pending,
              resumePendingServiceAccessReview:async () => {
                calls.push({kind:'resume'});
                pending = null;
                return true;
              },
              loadServices:async () => calls.push({kind:'services'}),
              setComposerStatus:(message, options = {}) => calls.push({
                kind:'status', message, working:options.working === true,
              }),
              showToast:(message) => calls.push({kind:'toast',message}),
              renderActorControlUi:() => calls.push({kind:'render-controls'}),
              findConversationState:() => null,
              actionEntryKey:() => '',
              renderConnectCard:() => {},
            };
            vm.createContext(context);
            vm.runInContext(`let serviceAccessReviewResumePromise = null;\n${source.slice(start, end)}`, context);

            const resumed = await context.handleServiceAccessReviewResult({
              status:'succeeded', requestId:'request-alpha',
            });

            assert.equal(resumed, true);
            assert.equal(calls.filter((call) => call.kind === 'resume').length, 1);
            assert.equal(calls.filter((call) => call.kind === 'services').length, 1);
            assert.deepEqual(calls.filter((call) => call.kind === 'status').map((call) => ({
              message:call.message, working:call.working,
            })), [
              {message:'NyxID 授权已更新，正在恢复原任务…',working:true},
              {message:'生产环境 · 使用当前账户的 services，高风险操作需要确认',working:false},
            ]);
            assert.equal(calls.some((call) => call.kind === 'toast' && /原任务已继续/.test(call.message)), true);
            assert.equal(calls.at(-1).kind, 'render-controls');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("AevatarStudioAuth.onServiceAccessReviewResult");
        app.Should().NotContain("location.reload()",
            "the authorization completion handler must keep the mounted Studio session");
    }

    [Fact]
    public async Task WorkflowStudio_ActionContinuation_ShouldConvergeBeforeTheSseStreamCloses()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const proofStart = source.indexOf('function actionResourceUserServiceId(resource) {');
            const proofEnd = source.indexOf('\nfunction connectDeepLink(', proofStart);
            const continuationStart = source.indexOf('function continuationIntent(card, disposition, resource = null) {');
            const continuationEnd = source.indexOf('\nasync function refreshConnectCard(card) {', continuationStart);
            assert.notEqual(proofStart, -1, 'action proof helpers must exist');
            assert.notEqual(proofEnd, -1, 'connectDeepLink must follow action proof helpers');
            assert.notEqual(continuationStart, -1, 'action continuation helpers must exist');
            assert.notEqual(continuationEnd, -1, 'refreshConnectCard must follow action continuation');

            const actionRequestId = 'action-alpha';
            const actorId = 'action-actor-alpha';
            const userServiceId = 'user-service-alpha';
            const projection = {
              actorId,
              actions:new Map([[actionRequestId, {
                actionRequestId,
                postconditionResult:{
                  verified:true,
                  actionRequestId,
                  disposition:'completed',
                  resource:{userService:{userServiceId}},
                },
              }]]),
              steps:new Map(),
            };
            const conversation = {
              actorId:'conversation-alpha',
              controller:null,
              controllers:new Set(),
            };
            const continuationFrameResults = [];
            const handledFrames = [];
            const reviewSubmissions = [];
            let refreshCalls = 0;
            const context = {
              AbortController,
              Map,
              Set,
              JSON,
              state:{activeConversation:conversation,activeController:null},
              globalThis:{AevatarStudioAuth:{
                continueServiceAccessReview:async (body) => {
                  reviewSubmissions.push(body);
                  return {ok:true,status:200};
                },
              }},
              createId:() => 'client-action-alpha',
              validateActionContinuation:(value) => value,
              restoreCachedAction:() => true,
              clearExternalJourneyTimer:() => {},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              fetch:async () => { throw new Error('review continuation must not use the session bearer'); },
              responseError:async () => new Error('request failed'),
              setRunningUi:() => {},
              renderConnectCard:() => {},
              withConversationState:(_entry, callback) => callback(),
              handleFrame:(raw) => {
                handledFrames.push(raw.type);
                return raw;
              },
              refreshActionActorState:async () => {
                refreshCalls += 1;
                return projection;
              },
              actorProjectionFor:() => projection,
              consumeSse:async (_response, onFrame) => {
                for (const frame of [
                  {type:'keepalive'},
                  {type:'text_delta',delta:'Issue #42'},
                  {type:'run_finished'},
                ]) {
                  const decision = await onFrame(frame);
                  continuationFrameResults.push(decision);
                  if (decision === false) break;
                }
              },
              releaseConversationController:(entry, controller) => {
                entry.controllers.delete(controller);
                if (entry.controller === controller) entry.controller = null;
              },
            };
            vm.createContext(context);
            vm.runInContext(source.slice(proofStart, proofEnd), context);
            vm.runInContext(source.slice(continuationStart, continuationEnd), context);

            const card = {
              action:{actionRequestId},
              request:{
                actorId,
                originTurnId:'turn-alpha',
                taskId:'task-alpha',
                stepId:'step-access-review',
                actionRequestId,
                action:'service.access_review',
                params:{serviceAccessReview:{
                  userServiceId,
                  serviceSlug:'api-github',
                  resourceUri:'https://id.example.test/api/v1/proxy/s/api-github',
                }},
              },
              conversation,
              status:'needs_connection',
              busy:false,
              error:'',
              note:'',
              continuation:null,
              report:null,
            };

            const result = await context.submitActionContinuation(card, 'completed', {
              userService:{userServiceId},
            });

            assert.deepEqual(
              handledFrames,
              ['keepalive','text_delta','run_finished'],
              'postcondition verification must not hide resumed LLM/tool/final frames');
            assert.deepEqual(
              continuationFrameResults,
              [undefined,undefined,false],
              'only a terminal run frame may stop continuation observation');
            assert.equal(refreshCalls, 1);
            assert.equal(card.status, 'verified');
            assert.equal(card.busy, false);
            assert.equal(card.error, '');
            assert.match(card.note, /Actor 已确认/);
            assert.equal(reviewSubmissions.length, 1);
            assert.equal(reviewSubmissions[0].type, 'action.continue');
            assert.equal(reviewSubmissions[0].conversationId, actorId);
            assert.equal(
              reviewSubmissions[0].actions[0].resource.userService.userServiceId,
              userServiceId);
            assert.deepEqual(JSON.parse(JSON.stringify(result)), {
              verified:true,
              terminalObserved:true,
            });
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ServiceAccessReviewDecline_ShouldUseTheSessionBearer()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function continuationIntent(card, disposition, resource = null) {');
            const end = source.indexOf('\nasync function refreshConnectCard(card) {', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const sessionRequests = [];
            const reviewRequests = [];
            const conversation = {
              actorId:'conversation-alpha', controller:null, controllers:new Set(),
            };
            const context = {
              AbortController,
              JSON,
              Set,
              ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED_CODE:
                'NYXID_ACTION_CONTINUATION_CREDENTIAL_REFRESH_REQUIRED',
              state:{activeConversation:conversation,activeController:null},
              globalThis:{AevatarStudioAuth:{
                continueServiceAccessReview:async (body) => {
                  reviewRequests.push(body);
                  throw new Error('decline must not require a review bearer');
                },
              }},
              createId:() => 'client-action-decline',
              validateActionContinuation:(value) => value,
              restoreCachedAction:() => true,
              clearExternalJourneyTimer:() => {},
              demoHeaders:() => ({'Content-Type':'application/json'}),
              fetch:async (path, init) => {
                sessionRequests.push({path,body:JSON.parse(init.body)});
                return {ok:true,status:200};
              },
              responseError:async () => new Error('request failed'),
              setRunningUi:() => {},
              renderConnectCard:() => {},
              withConversationState:(_entry, callback) => callback(),
              handleFrame:(raw) => raw,
              consumeSse:async (_response, onFrame) => {
                await onFrame({type:'run_finished'});
              },
              releaseConversationController:(entry, controller) => {
                entry.controllers.delete(controller);
                if (entry.controller === controller) entry.controller = null;
              },
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const card = {
              request:{
                actorId:'action-actor-alpha', originTurnId:'turn-alpha',
                actionRequestId:'action-access-review', action:'service.access_review',
              },
              conversation,
              status:'needs_review', busy:false, error:'', note:'',
              continuation:null, report:null,
            };
            const result = await context.submitActionContinuation(card, 'declined');

            assert.equal(reviewRequests.length, 0);
            assert.equal(sessionRequests.length, 1);
            assert.equal(sessionRequests[0].path, '/api/demo/chat');
            assert.equal(sessionRequests[0].body.type, 'action.continue');
            assert.equal(sessionRequests[0].body.actions[0].disposition, 'declined');
            assert.deepEqual(JSON.parse(JSON.stringify(result)), {
              verified:false, terminalObserved:true,
            });
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

}
