using System.Diagnostics;
using System.Net;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Configuration;
using Aevatar.Mainnet.Host.Api.AI;
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
    [Fact]
    public async Task AgentProfileEditors_ShouldUseThePublishedSkillBodyBudget()
    {
        await using var app = await CreateAppAsync();
        var client = app.GetTestClient();

        var adminHtml = await client.GetStringAsync("/admin");
        var aiHtml = await client.GetStringAsync("/ai");

        adminHtml.Should().Contain("maxSelectedSkillBytes!==65536");
        adminHtml.Should().Contain("当前必须为 65536");
        adminHtml.Should().NotContain("maxSelectedSkillBytes||24576");
        aiHtml.Should().Contain("runtimeValue(\"maxSelectedSkillBytes\", 65536)");
        aiHtml.Should().NotContain("runtimeValue(\"maxSelectedSkillBytes\", 262144)");
    }

    [Fact]
    public async Task AIPage_ShouldSupportHeadWithoutReturningTheDocumentBody()
    {
        await using var app = await CreateAppAsync();
        using var request = new HttpRequestMessage(HttpMethod.Head, "/ai");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AIPage_Logout_ShouldInvalidateAnInFlightTokenRefreshAndPreserveAdminSession()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function sourceBlock(startMarker, endMarker) {
              const start = html.indexOf(startMarker);
              const end = html.indexOf(endMarker, start);
              assert.notEqual(start, -1, startMarker + ' must exist');
              assert.notEqual(end, -1, endMarker + ' must follow ' + startMarker);
              return html.slice(start, end);
            }

            const stored = new Map();
            let releaseRefresh;
            const context = {
              Date, URLSearchParams,
              localStorage: {
                getItem: key => stored.has(key) ? stored.get(key) : null,
                setItem: (key, value) => stored.set(key, value),
                removeItem: key => stored.delete(key),
              },
              sessionStorage: {removeItem() {}},
              fetch: async () => await new Promise(resolve => {
                releaseRefresh = () => resolve({
                  ok: true,
                  json: async () => ({
                    access_token: 'late-access',
                    refresh_token: 'late-refresh',
                    expires_in: 900,
                  }),
                });
              }),
            };
            vm.createContext(context);
            vm.runInContext(`
              var OIDC = {authority:'https://id.example.test',clientId:'client-example'};
              var TOKEN_KEY = 'console:test:ai:token';
              var PKCE_KEY = 'console:test:ai:pkce';
              var refreshOperation = null;
              var refreshTimer = null;
              var refreshSkewMs = 60000;
              var authEpoch = 0;
              var contextRequestId = 0;
              var agentDetailRequestId = 0;
              var runDetailRequestId = 0;
              ${sourceBlock('function getToken()', '\n    function safeReturnPath()')}
            `, context);

            (async () => {
              stored.set('console:test:token', JSON.stringify({access_token:'admin-access'}));
              stored.set('console:test:ai:token', JSON.stringify({
                access_token: 'old-access',
                refresh_token: 'old-refresh',
              }));
              const refresh = context.refreshToken(true);
              assert.equal(typeof releaseRefresh, 'function');

              context.clearToken();
              releaseRefresh();

              assert.equal(await refresh, null);
              assert.equal(stored.has('console:test:ai:token'), false,
                'a late refresh response must not restore a logged-out AI session');
              assert.equal(JSON.parse(stored.get('console:test:token')).access_token, 'admin-access',
                'AI logout must not modify the Admin session');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AIPage_RunDrawer_ShouldRetainFocusAfterDetailRender()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function sourceBlock(startMarker, endMarker) {
              const start = html.indexOf(startMarker);
              const end = html.indexOf(endMarker, start);
              assert.notEqual(start, -1, startMarker + ' must exist');
              assert.notEqual(end, -1, endMarker + ' must follow ' + startMarker);
              return html.slice(start, end);
            }

            let focusedElement = null;
            const loadingCloseButton = {focus() { focusedElement = this; }};
            const renderedCloseButton = {focus() { focusedElement = this; }};
            const root = {
              firstChild: {},
              closeButton: loadingCloseButton,
              _innerHTML: '',
              set innerHTML(value) {
                this._innerHTML = value;
                this.closeButton = renderedCloseButton;
              },
              get innerHTML() { return this._innerHTML; },
              querySelector(selector) {
                assert.equal(selector, '.drawer-close');
                return this.closeButton;
              },
            };
            const context = {
              state: {runDetail:{
                authorityStateVersion:7,
                summary:{workflowName:'Research run',status:'completed',durationMs:42},
                statistics:{completedSteps:1,totalSteps:1},
                usageTotals:{totalTokens:12},
                steps:[],timeline:[],
              }},
              $: id => {
                assert.equal(id, 'drawer-root');
                return root;
              },
              object: value => value && typeof value === 'object' ? value : {},
              arr: value => Array.isArray(value) ? value : [],
              esc: value => String(value == null ? '' : value),
              statusBadge: () => '', icon: () => '', emptyView: () => '',
              formatDuration: value => String(value), formatTime: value => String(value),
            };
            vm.createContext(context);
            vm.runInContext(`
              ${sourceBlock('function renderRunDrawer(', '\n    function closeDrawer(')}
            `, context);

            context.renderRunDrawer('run-7');

            assert.notEqual(root.closeButton, loadingCloseButton);
            assert.equal(focusedElement, renderedCloseButton,
              'the replacement drawer close button must own focus');
            assert.match(root.innerHTML, /data-run-id="run-7"/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AIPage_ModelRouteChanges_ShouldSelectAndPersistRouteDefaultAsNull()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function sourceBlock(startMarker, endMarker) {
              const start = html.indexOf(startMarker);
              const end = html.indexOf(endMarker, start);
              assert.notEqual(start, -1, startMarker + ' must exist');
              assert.notEqual(end, -1, endMarker + ' must follow ' + startMarker);
              return html.slice(start, end);
            }

            const requests = [];
            const routeSelect = {value:'route-a'};
            const modelSelect = {
              value:'', _html:'',
              set innerHTML(value) {
                this._html = value;
                const selected = value.match(/<option value="([^"]*)"[^>]* selected/);
                this.value = selected ? selected[1] : '';
              },
              get innerHTML() { return this._html; },
            };
            const elements = {'model-route':routeSelect, 'model-id':modelSelect};
            const context = {
              state: {
                models: {personalDefault:{settings:{
                  savedSelection:{routeValue:'route-a',modelSelection:{kind:'explicit_model',modelId:'model-a'}},
                  routeOptions:[
                    {routeValue:'route-a',modelCatalog:{modelIds:['model-a','model-a-2']}},
                    {routeValue:'route-b',modelCatalog:{modelIds:['model-b']}},
                  ],
                }}},
                receipts:{},
              },
              $: id => elements[id],
              arr: value => Array.isArray(value) ? value : [],
              object: value => value && typeof value === 'object' ? value : {},
              esc: value => String(value == null ? '' : value),
              jsonOptions: (method, body) => ({method,body:JSON.stringify(body)}),
              api: async (path, options) => {
                requests.push({path,body:JSON.parse(options.body)});
                return {commandId:'command-model-default'};
              },
              acceptedReceipt: (message, receipt) => ({message,commandId:receipt.commandId}),
              render() {}, toast() {}, ignoredError() { return false; }, errorMessage(error) { return String(error); },
            };
            vm.createContext(context);
            vm.runInContext(`
              ${sourceBlock('function selectedRouteOption()', '\n    async function saveCatalog()')}
            `, context);

            (async () => {
              context.syncModelOptions();
              assert.equal(modelSelect.value, 'model-a', 'the saved route keeps its explicit model');

              routeSelect.value = 'route-b';
              context.syncModelOptions();
              assert.equal(modelSelect.value, '', 'a new route must start at Route default');
              assert.match(modelSelect.innerHTML, /<option value="" selected>Route default<\/option>/);

              await context.savePersonalModel();
              assert.deepEqual(requests, [{
                path:'/api/ai/models/personal-default',
                body:{routeValue:'route-b',modelId:null},
              }]);
              assert.equal(context.state.models.personalDefault.settings.savedSelection.modelSelection.modelId, null);
              assert.equal(context.state.models.personalDefault.settings.savedSelection.modelSelection.kind, 'provider_default');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AIPage_StatusBadges_ShouldClassifyInactiveAndInvalidAsFailures()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function statusBadge(');
            const end = html.indexOf('\n    function errorMessage(', start);
            assert.notEqual(start, -1, 'statusBadge must exist');
            assert.notEqual(end, -1, 'errorMessage must follow statusBadge');

            const context = {esc:value => String(value == null ? '' : value)};
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            for (const status of ['inactive','invalid']) {
              const badge = context.statusBadge(status);
              assert.match(badge, /class="badge failed"/);
              assert.doesNotMatch(badge, /class="badge success"/);
            }
            assert.match(context.statusBadge('active'), /class="badge success"/);
            assert.match(context.statusBadge('valid'), /class="badge success"/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AIPage_LoginButton_ShouldIgnoreASecondClickWhilePkceIsInFlight()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function sourceBlock(startMarker, endMarker) {
              const start = html.indexOf(startMarker);
              const end = html.indexOf(endMarker, start);
              assert.notEqual(start, -1, startMarker + ' must exist');
              assert.notEqual(end, -1, endMarker + ' must follow ' + startMarker);
              return html.slice(start, end);
            }

            let digestCalls = 0;
            let releaseDigest;
            const assignments = [];
            const pending = [];
            const button = {disabled:false,textContent:'登录 NyxID'};
            const buttonLabel = {textContent:'使用 NyxID 登录'};
            const context = {
              TextEncoder, URL,
              btoa: value => Buffer.from(value, 'binary').toString('base64'),
              location: {
                origin:'https://ai.example.test', pathname:'/ai', hash:'#/models',
                assign:value => assignments.push(value),
              },
              crypto: {
                getRandomValues: values => values.fill(7),
                subtle: {digest: async () => {
                  digestCalls += 1;
                  return await new Promise(resolve => {
                    releaseDigest = () => resolve(new Uint8Array([1,2,3]).buffer);
                  });
                }},
              },
              sessionStorage: {setItem:(key, value) => pending.push({key,value:JSON.parse(value)})},
              $: id => id === 'login-button' ? button : id === 'login-button-label' ? buttonLabel : null,
              showAuth() {}, errorMessage:error => String(error),
            };
            vm.createContext(context);
            vm.runInContext(`
              var OIDC = {
                authority:'https://id.example.test', clientId:'client-example',
                redirectUri:'https://ai.example.test/auto/callback', loginScope:'openid profile'
              };
              var PKCE_KEY = 'console:test:ai:pkce';
              var loginPending = false;
              ${sourceBlock('function safeReturnPath()', '\n    async function api(')}
            `, context);

            (async () => {
              const first = context.beginLogin();
              const second = context.beginLogin();
              assert.equal(digestCalls, 1, 'the second click must not start another PKCE operation');
              assert.equal(button.disabled, true);
              assert.equal(buttonLabel.textContent, '正在跳转…');

              releaseDigest();
              await Promise.all([first, second]);

              assert.equal(pending.length, 1);
              assert.equal(pending[0].key, 'console:test:ai:pkce');
              assert.equal(pending[0].value.returnTo, '/ai#/models');
              assert.deepEqual(pending[0].value.resources, []);
              assert.equal(assignments.length, 1);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AIPage_SelectAgentOnNarrowScreen_ShouldRevealTheDetailPanel()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/ai");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function revealAgentDetailOnNarrowScreen()');
            const end = html.indexOf('\n    function reloadAgentDetail()', start);
            assert.notEqual(start, -1, 'narrow-screen detail reveal behavior must exist');
            assert.notEqual(end, -1, 'reloadAgentDetail must follow selectAgent');

            const scrollCalls = [];
            let isNarrow = true;
            let animationFrames = 0;
            const detail = {scrollIntoView: options => scrollCalls.push(options)};
            const context = {
              window:{matchMedia: query => ({matches:isNarrow && query === '(max-width: 760px)'})},
              requestAnimationFrame: callback => { animationFrames += 1; callback(); },
              state:{
                agentMode:'system', selectedAgent:null, agentDetail:null, validation:null,
                errors:{agentDetail:null}, receipts:{agents:null},
              },
              authEpoch:0, agentDetailRequestId:0,
              activeAgentCollection:() => ({items:[{profileSlug:'system-alpha'}]}),
              arr:value => Array.isArray(value) ? value : [],
              $:id => id === 'agent-detail' ? detail : null,
              render() {},
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async () => {
              await context.selectAgent('system-alpha');
              assert.equal(context.state.selectedAgent.profileSlug, 'system-alpha');
              assert.equal(scrollCalls.length, 1);
              assert.equal(scrollCalls[0].block, 'start');
              assert.equal(animationFrames, 1);

              isNarrow = false;
              await context.selectAgent('system-alpha');
              assert.equal(animationFrames, 1, 'desktop selection must not schedule an animation frame');
              assert.equal(scrollCalls.length, 1, 'desktop selection must not scroll the detail panel');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Theory]
    [InlineData("/admin", "Aevatar Backend Console")]
    [InlineData("/ai", "Aevatar AI")]
    [InlineData("/admin/studio", "<title>Aevatar Studio</title>")]
    [InlineData("/auto/callback", "正在完成登录")]
    [InlineData("/delivery", "Workflow Delivery Center")]
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
        if (path == "/ai")
        {
            html.Should().NotContain("\"resources\":");
            html.Should().Contain("\"storageKey\":\"console:test:ai\"");
            html.Should().NotContain("\"storageKey\":\"console:test\"");
        }
        else
            html.Should().Contain(
                "\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\",\"https://api.example.test/api/v1/proxy/s/ornn-api\"]");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
        html.Should().NotContain("__AEVATAR_AI_CONFIG__");
        html.Should().NotContain("https://nyx-api.chrono-ai.fun");
        html.Should().NotContain("37a93189-2734-406e-bca1-7dbdf25c5a53");
        if (path == "/cqrs")
        {
            html.Should().Contain("const NYXID_API = CFG.nyxidApi");
            html.Should().Contain("const NYXID_USER_API = NYXID_API");
            html.Should().NotContain("const NYXID_AUTHORITY = CFG.authority");
        }
        else if (path == "/ai")
        {
            html.Should().Contain("data-aevatar-ai");
            html.Should().Contain("#/overview");
            html.Should().Contain("#/agents");
            html.Should().Contain("#/models");
            html.Should().Contain("#/activity");
            html.Should().Contain("/api/auth/nyxid/finalize");
            html.Should().Contain("/api/ai/overview");
            html.Should().Contain("/api/ai/agents");
            html.Should().Contain("/api/ai/models");
            html.Should().Contain("/api/ai/activity");
            var normalizedHtml = html.ToLowerInvariant();
            normalizedHtml.Should().NotContain("aevatar console");
            normalizedHtml.Should().NotContain("team");
            normalizedHtml.Should().NotContain("scopes");
        }
        if (path == "/admin")
        {
            html.Should().NotContain("\"aiStorageKey\":");
            html.Should().Contain("var NYX_API=BACKEND_CONSOLE_CONFIG.nyxidApi");
            html.Should().Contain("fetch(NYX_API+'/api/v1/admin/users");
            html.Should().Contain("var FLEET_RUN_WINDOW=500;");
            html.Should().Contain("Object.keys(NYX_USERS).forEach(function(sid){ scopeIds[sid]=1; });");
            html.Should().Contain("/api/workflow/observatory/runs?scope=__all__&take='+FLEET_RUN_WINDOW");
            html.Should().NotContain("var NYX_AUTHORITY=BACKEND_CONSOLE_CONFIG.authority");
            // ADR-0018: only the deliberately narrowed voice-realtime purpose keeps
            // explicit resources; the session login sends none.
            html.Should().Contain(
                "var resources=purpose===VOICE_TOKEN_PURPOSE||flow===SERVICE_ACCESS_REVIEW_FLOW?loginResources(requestedResources):[];");
            html.Should().Contain("if(claims && claims.allow_all_services!==false) return true;");
            html.Should().Contain("function observatoryFrameSource()");
            html.Should().Contain("'/admin/workflow-observatory'");
            html.Should().NotContain("'/workflow/observatory'");
            html.Should().NotContain("function bindObservatory(");
            html.Should().Contain("studio:{name:'工作台'");
            html.Should().Contain("suiteFrame('/admin/studio','工作台')");
            html.Should().NotContain("suiteFrame('/workflow/studio','工作台')");
            html.Should().Contain("#frame-dock{flex:1 1 auto;height:100%;min-height:0;");
            html.Should().Contain(".suite-embed{flex:1 1 auto;width:100%;height:100%;min-height:0;");
        }
        else if (path == "/admin/studio")
        {
            html.Should().Contain("\"nyxidWeb\":\"https://web.example.test\"");
            html.Should().Contain("class=\"site-header\"");
            html.Should().Contain("id=\"composerForm\"");
            html.Should().Contain("生产环境 · 操作会影响真实数据，高风险操作需要确认");
            html.Should().Contain("app.js?v=20260823-m62-studio-redesign");
            html.Should().Contain("styles.css?v=20260823-m62-studio-redesign");
            html.Should().Contain("id=\"traceViewButton\"");
            html.Should().Contain("id=\"requestTracePanel\"");
            html.Should().Contain("class=\"trajectory-toolbar\"");
            html.Should().Contain("id=\"traceOperationOverview\"");
            html.Should().Contain("aria-label=\"Input、Model、Tools 时间总览\"");
            html.Should().Contain("id=\"trajectoryOverviewTrack\"");
            html.Should().Contain("id=\"traceOperationList\"");
            html.Should().Contain("id=\"trajectoryDetails\"");
            html.Should().NotContain("class=\"brand-mark\"");
            html.Should().NotContain("Aevatar Studio · 工作流实录");
            html.Should().NotContain("从意图到交付的真实对话");
        }
        else if (path == "/auto/callback")
        {
            html.Should().Contain("\"aiStorageKey\":\"console:test:ai\"");
            // The exchange loops over the PKCE-stored request list; session logins
            // store an empty list (ADR-0018), so only voice-purpose logins append.
            html.Should().Contain("form.append(\"resource\"");
            html.Should().Contain("normalizeResources(pending.resources) : []");
            html.Should().NotContain("normalizeResources(RESOURCES)");
            html.Should().Contain("var parsed = new URL(p, location.origin)");
            html.Should().Contain("parsed.origin !== location.origin");
            html.Should().Contain("return parsed.pathname + parsed.search + parsed.hash");
            html.Should().Contain("var AUTH_STORAGE_CONTEXTS = [");
            html.Should().Contain("hasOAuthState && candidateState.length > 0 && candidateState === oauthState");
            html.Should().Contain("matchingContexts.length === 1");
            html.Should().Contain("callbackTitle.textContent=\"Aevatar AI\"");
        }
        else if (path == "/delivery")
        {
            html.Should().Contain("const BACKEND_CONSOLE_CONFIG = {\"authority\":");
            html.Should().Contain("GET /api/delivery/session");
            html.Should().Contain("/api/delivery/packages");
            html.Should().Contain(":validate-config");
            html.Should().Contain(":publish");
            html.Should().Contain(":retry");
            html.Should().Contain("/connections/");
            html.Should().Contain(":connect");
            html.Should().Contain("/available");
            html.Should().Contain(":attach");
            html.Should().Contain("使用已有连接");
            html.Should().Contain("status === \"ready\"");
            html.Should().Contain("function renderUsageSection(consoleUrl, channelRunCommand, scopeId)");
            html.Should().Contain("renderUsageSection(consoleUrl, channelRunCommand, installationScopeId)");
            html.Should().Contain("打开该 Team 成员的调用工作台，可手动运行已交付工作流并查看运行记录。");
            html.Should().NotContain("打开该 Team 成员的 workflow 页面");
            html.Should().Contain("pendingTeams: Object.create(null)");
            html.Should().Contain("state.customer.pendingTeams[createdTeamId] = createdTeam");
            html.Should().Contain("state.customer.selectedTeamId = createdTeamId");
            html.Should().Contain("NyxID 授权绑定尚未就绪");
            html.Should().NotContain("需要先在 Aevatar Console 完成一次 NyxID 登录");
            html.Should().NotContain("demoMode");
            html.Should().NotContain("MutationObserver");
        }
        else if (path == "/voice")
        {
            html.Should().Contain("async function fetchWithConsoleAuth(");
            html.Should().Contain("requestAdminShellTokenRefresh(");
            html.Should().Contain("rejectedAccessToken");
            // Voice keeps its deliberately narrowed realtime-token flows, but the
            // session login must not request explicit resources.
            html.Should().Contain("purpose===VOICE_TOKEN_PURPOSE ? normalizeResources(CFG.resources,requestedResources) : []");
            html.Should().Contain("normalizeResources(pending.resources) : []");
        }
        else if (path == "/ai")
        {
            html.Should().Contain("async function api(path, options)");
            html.Should().Contain("async function refreshToken(force)");
            html.Should().Contain("resources: []");
        }
        else
        {
            html.Should().NotContain("searchParams.append(\"resource\"");
            html.Should().NotContain("form.append(\"resource\"");
            html.Should().NotContain("f.append(\"resource\"");
            html.Should().Contain("async function fetchWithConsoleAuth(");
            html.Should().Contain("requestAdminShellTokenRefresh(");
            html.Should().Contain("rejectedAccessToken");
        }
    }

    [Fact]
    public async Task AutoCallback_AILogin_ShouldSelectIsolatedPkceByStateAndPreserveAdminSession()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('(function(){');
            const end = source.lastIndexOf('})();');
            const records = new Map([
              ['console:test:token', JSON.stringify({access_token:'admin-access'})],
            ]);
            const pendingRecords = new Map([
              ['console:test:pkce', JSON.stringify({
                verifier:'admin-verifier', state:'admin-state', returnTo:'/admin',
                resources:[], tokenPurpose:'', authFlow:'',
              })],
              ['console:test:ai:pkce', JSON.stringify({
                verifier:'ai-verifier', state:'ai-state', returnTo:'/ai#/agents',
                resources:[], tokenPurpose:'', authFlow:'', storageKey:'attacker-controlled',
              })],
            ]);
            const removedPending = [];
            const fetchCalls = [];
            const elements = new Map();
            const context = {
              URL, URLSearchParams, TextDecoder, Uint8Array,
              atob:value => Buffer.from(value, 'base64').toString('binary'),
              document:{
                title:'',
                getElementById:id => {
                  if (!elements.has(id)) elements.set(id, {style:{},textContent:'',appendChild(){}});
                  return elements.get(id);
                },
                createElement:() => ({href:'',textContent:''}),
              },
              location:{
                origin:'http://127.0.0.1:5080',
                search:'?code=ai-code&state=ai-state',
                replace:value => { context.replaced = value; },
              },
              sessionStorage:{
                getItem:key => pendingRecords.get(key) || null,
                removeItem:key => { removedPending.push(key); pendingRecords.delete(key); },
              },
              localStorage:{
                getItem:key => records.get(key) || null,
                setItem:(key, value) => records.set(key, value),
                removeItem:key => records.delete(key),
              },
              fetch:async (url, options) => {
                fetchCalls.push({url, options});
                return {ok:true,json:async () => ({tokens:{
                  accessToken:'ai-access', refreshToken:'ai-refresh',
                  tokenType:'Bearer', expiresIn:3600, scope:'openid profile',
                }})};
              },
              Date, JSON, Map, Boolean, console,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end + 5), context);
            await new Promise(resolve => setImmediate(resolve));
            await new Promise(resolve => setImmediate(resolve));

            assert.equal(fetchCalls.length, 1);
            assert.equal(fetchCalls[0].url, '/api/auth/nyxid/finalize');
            assert.equal(JSON.parse(fetchCalls[0].options.body).codeVerifier, 'ai-verifier');
            assert.equal(JSON.parse(records.get('console:test:token')).access_token, 'admin-access');
            assert.equal(JSON.parse(records.get('console:test:ai:token')).access_token, 'ai-access');
            assert.equal(records.has('attacker-controlled:token'), false);
            assert.deepEqual(removedPending, ['console:test:ai:pkce']);
            assert.equal(pendingRecords.has('console:test:pkce'), true);
            assert.equal(context.replaced, '/ai#/agents');
            assert.equal(context.document.title, '登录中 · Aevatar AI');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AutoCallback_InvalidOrAmbiguousState_ShouldNotExchangeOrMutateStorage()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('(function(){');
            const end = source.lastIndexOf('})();');

            const scenarios = [
              {
                name:'empty returned and pending state',
                search:'?code=code-alpha&state=',
                pending:[{verifier:'admin-verifier',state:''},{verifier:'ai-verifier',state:'ai-state'}],
              },
              {
                name:'missing returned and pending state',
                search:'?code=code-alpha',
                pending:[{verifier:'admin-verifier'},{verifier:'ai-verifier',state:'ai-state'}],
              },
              {
                name:'duplicate state across fixed storage contexts',
                search:'?code=code-alpha&state=shared-state',
                pending:[
                  {verifier:'admin-verifier',state:'shared-state'},
                  {verifier:'ai-verifier',state:'shared-state'},
                ],
              },
            ];

            for (const scenario of scenarios) {
              const pendingRecords = new Map([
                ['console:test:pkce', JSON.stringify(scenario.pending[0])],
                ['console:test:ai:pkce', JSON.stringify(scenario.pending[1])],
              ]);
              const elements = new Map();
              const mutations = [];
              let fetchCalls = 0;
              const context = {
                URL, URLSearchParams, TextDecoder, Uint8Array,
                atob:value => Buffer.from(value, 'base64').toString('binary'),
                document:{
                  title:'',
                  getElementById:id => {
                    if (!elements.has(id)) elements.set(id, {style:{},textContent:'',appendChild(){}});
                    return elements.get(id);
                  },
                  createElement:() => ({href:'',textContent:''}),
                },
                location:{
                  origin:'http://127.0.0.1:5080', search:scenario.search,
                  replace:value => mutations.push({kind:'replace',value}),
                },
                sessionStorage:{
                  getItem:key => pendingRecords.get(key) || null,
                  removeItem:key => mutations.push({kind:'remove-pending',key}),
                },
                localStorage:{
                  getItem:() => null,
                  setItem:(key, value) => mutations.push({kind:'set-token',key,value}),
                  removeItem:key => mutations.push({kind:'remove-token',key}),
                },
                fetch:async () => { fetchCalls += 1; throw new Error('unexpected token exchange'); },
                Date, JSON, Map, Boolean, console,
              };
              vm.createContext(context);
              vm.runInContext(source.slice(start, end + 5), context);

              assert.equal(fetchCalls, 0, scenario.name);
              assert.deepEqual(mutations, [], scenario.name);
              assert.equal(elements.get('msg').textContent, '登录状态校验失败，请返回重试。', scenario.name);
            }
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AutoCallback_SafePath_ShouldRejectCrossOriginControlCharacterPaths()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function safePath(p, fallback){');
            const end = source.indexOf('\n  function normalizeResources', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const context = {URL,location:{origin:'https://ai.example.test'}};
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            for (const code of [9, 10, 13]) {
              const attack = '/' + String.fromCharCode(code) + '/evil.example';
              assert.equal(context.safePath(attack, '/admin'), '/admin');
            }
            assert.equal(context.safePath('//evil.example/path', '/admin'), '/admin');
            assert.equal(context.safePath('javascript:alert(1)', '/admin'), '/admin');
            assert.equal(
              context.safePath('/ai/../ai?view=models#/models', '/admin'),
              '/ai?view=models#/models');
            assert.equal(
              context.safePath('https://ai.example.test/ai#/agents', '/admin'),
              '/ai#/agents');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AdminShell_ServiceAccessReview_ShouldRequestResourceUnionWithFreshConsent()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function loginResources(requested){');
            const end = source.indexOf('\nfunction decodeJwt(', start);
            assert.notEqual(start, -1, 'loginResources must exist');
            assert.notEqual(end, -1, 'decodeJwt must follow beginLogin');

            const assignments = [];
            const pending = [];
            const popupPending = [];
            const popupNavigations = [];
            const popup = {
              closed:false,
              sessionStorage:{setItem:(key,value) => popupPending.push({key,value:JSON.parse(value)})},
              location:{replace:(value) => popupNavigations.push(value)},
              focus:() => {},
              close:() => { popup.closed = true; },
            };
            const requester = {postMessage:() => {}};
            const context = {
              OIDC:{
                authority:'https://id.example.test', clientId:'client-example',
                redirectUri:'http://127.0.0.1:5080/auto/callback', scope:'openid profile',
                resources:[
                  'https://id.example.test/api/v1/proxy/s/aevatar',
                  'https://id.example.test/api/v1/proxy/s/ornn-api',
                ],
              },
              VOICE_TOKEN_PURPOSE:'voice-realtime',
              SERVICE_ACCESS_REVIEW_FLOW:'service-access-review',
              SERVICE_ACCESS_REVIEW_POPUP_PREFIX:'aevatar-nyxid-service-review-',
              SERVICE_ACCESS_REVIEW_POPUP:null,
              PKCE_KEY:'console:pkce',
              sessionStorage:{
                setItem:(key,value) => pending.push({key,value:JSON.parse(value)}),
                getItem:() => null,
                removeItem:() => {},
              },
              location:{
                pathname:'/admin', hash:'#/studio',
                assign:(value) => assignments.push(value),
              },
              window:{open:(_url,name) => {
                assert.equal(name, 'aevatar-nyxid-service-review-request-alpha');
                return popup;
              }},
              crypto:{getRandomValues:(bytes) => bytes.fill(7),subtle:{}},
              TextEncoder,
              URL,
              URLSearchParams,
              Uint8Array,
              btoa:(value) => Buffer.from(value,'binary').toString('base64'),
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);
            context._rand = () => 'random-alpha';
            context._sha256 = async () => 'challenge-alpha';

            await context.beginLogin();
            const normal = new URL(assignments[0]);
            assert.deepEqual(normal.searchParams.getAll('resource'), []);
            assert.equal(normal.searchParams.has('prompt'), false);
            assert.deepEqual(pending[0].value.resources, []);

            await context.beginLogin([
              'https://id.example.test/api/v1/proxy/s/llm-openai',
              'https://id.example.test/api/v1/proxy/s/api-github',
            ], '', 'service-access-review',
              'aevatar-nyxid-service-review-request-alpha', 'request-alpha', requester);
            const review = new URL(popupNavigations[0]);
            assert.deepEqual(review.searchParams.getAll('resource'), [
              'https://id.example.test/api/v1/proxy/s/aevatar',
              'https://id.example.test/api/v1/proxy/s/ornn-api',
              'https://id.example.test/api/v1/proxy/s/llm-openai',
              'https://id.example.test/api/v1/proxy/s/api-github',
            ]);
            assert.equal(review.searchParams.get('prompt'), 'consent');
            assert.equal(pending[1].value.authFlow, 'service-access-review');
            assert.equal(pending[1].value.authRequestId, 'request-alpha');
            assert.deepEqual(pending[1].value.resources, review.searchParams.getAll('resource'));
            assert.deepEqual(popupPending[0], pending[1]);
            assert.equal(assignments.length, 1, 'service review must not replace the Admin page');
            assert.equal(context.SERVICE_ACCESS_REVIEW_POPUP.popup, popup);
            assert.equal(context.SERVICE_ACCESS_REVIEW_POPUP.requester, requester);

            context.window.open = () => null;
            await context.beginLogin([
              'https://id.example.test/api/v1/proxy/s/api-github',
            ], '', 'service-access-review',
              'aevatar-nyxid-service-review-request-beta', 'request-beta', requester);
            const blockedPopupFallback = new URL(assignments[1]);
            assert.equal(blockedPopupFallback.searchParams.get('prompt'), 'consent');
            assert.equal(pending[2].value.authRequestId, 'request-beta');
            assert.equal(assignments.length, 2, 'a blocked popup must retain the full-page fallback');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        html.Should().Contain("msg.authFlow");
    }

    [Fact]
    public async Task AutoCallback_ServiceAccessReview_ShouldPreserveSessionTokenAndStoreReviewTokenSeparately()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('(function(){');
            const end = source.lastIndexOf('})();');
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const records = new Map();
            records.set('console:test:token', JSON.stringify({access_token:'session-bearer'}));
            const pending = {
              verifier:'verifier-alpha', state:'state-alpha', returnTo:'/admin#/studio',
              resources:['https://api.example.test/api/v1/proxy/s/api-github'],
              tokenPurpose:'', authFlow:'service-access-review', authRequestId:'request-alpha',
            };
            const posted = [];
            const elements = new Map();
            const context = {
              URL,
              URLSearchParams,
              TextDecoder,
              Uint8Array,
              atob:(value) => Buffer.from(value, 'base64').toString('binary'),
              document:{getElementById:(id) => {
                if (!elements.has(id)) elements.set(id, {style:{},textContent:'',innerHTML:''});
                return elements.get(id);
              }},
              location:{
                origin:'http://127.0.0.1:5080',
                search:'?code=code-alpha&state=state-alpha',
                replace:(value) => { context.replaced = value; },
              },
              opener:{
                closed:false,
                postMessage:(message,targetOrigin) => posted.push({message,targetOrigin}),
              },
              close:() => { context.closed = true; },
              sessionStorage:{
                getItem:(key) => key === 'console:test:pkce' ? JSON.stringify(pending) : null,
                removeItem:() => {},
              },
              localStorage:{
                getItem:(key) => records.get(key) || null,
                setItem:(key, value) => records.set(key, value),
                removeItem:(key) => records.delete(key),
              },
              fetch:async () => ({
                ok:true,
                json:async () => ({
                  access_token:'review-bearer',
                  resource:['https://api.example.test/api/v1/proxy/s/api-github'],
                }),
              }),
              Date,
              JSON,
              Map,
              console,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end + 5), context);
            await new Promise((resolve) => setImmediate(resolve));

            assert.equal(
              JSON.parse(records.get('console:test:token')).access_token,
              'session-bearer',
              'service access review must not replace the LLM-capable session bearer');
            assert.equal(
              JSON.parse(records.get('console:test:service-access-review:token')).access_token,
              'review-bearer');
            assert.equal(context.replaced, undefined, 'the Studio page must stay mounted');
            assert.equal(context.closed, true);
            assert.deepEqual(JSON.parse(JSON.stringify(posted)), [{
              message:{
                source:'aevatar-service-access-review-auth',
                type:'service-access-review-result',
                requestId:'request-alpha',
                state:'state-alpha',
                status:'succeeded',
                message:'NyxID 授权已更新，正在恢复 Studio 中的原任务…',
              },
              targetOrigin:'http://127.0.0.1:5080',
            }]);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AutoCallback_OrdinaryLogin_ShouldFinalizeServerSideAndPreserveScopedTokens()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('(function(){');
            const end = source.lastIndexOf('})();');
            const records = new Map([
              ['console:test:voice-realtime:token', JSON.stringify({access_token:'voice-bearer'})],
              ['console:test:service-access-review:token', JSON.stringify({access_token:'review-bearer'})],
            ]);
            const pending = {
              verifier:'verifier-alpha', state:'state-alpha', returnTo:'/admin',
              resources:[], tokenPurpose:'', authFlow:'',
            };
            const fetchCalls = [];
            const elements = new Map();
            const context = {
              URL, URLSearchParams, TextDecoder, Uint8Array,
              atob:(value) => Buffer.from(value, 'base64').toString('binary'),
              document:{getElementById:(id) => {
                if (!elements.has(id)) elements.set(id, {style:{},textContent:'',innerHTML:''});
                return elements.get(id);
              }},
              location:{
                origin:'http://127.0.0.1:5080',
                search:'?code=code-alpha&state=state-alpha',
                replace:(value) => { context.replaced = value; },
              },
              sessionStorage:{
                getItem:(key) => key === 'console:test:pkce' ? JSON.stringify(pending) : null,
                removeItem:() => {},
              },
              localStorage:{
                getItem:(key) => records.get(key) || null,
                setItem:(key, value) => records.set(key, value),
                removeItem:(key) => records.delete(key),
              },
              fetch:async (url, options) => {
                fetchCalls.push({url, options});
                return {
                  ok:true,
                  json:async () => ({tokens:{
                    accessToken:'session-bearer', refreshToken:'session-refresh',
                    tokenType:'Bearer', expiresIn:3600, scope:'openid profile',
                  }}),
                };
              },
              Date, JSON, Map, console,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end + 5), context);
            await new Promise((resolve) => setImmediate(resolve));
            await new Promise((resolve) => setImmediate(resolve));

            assert.equal(fetchCalls.length, 1);
            assert.equal(fetchCalls[0].url, '/api/auth/nyxid/finalize');
            assert.deepEqual(JSON.parse(fetchCalls[0].options.body), {
              code:'code-alpha', codeVerifier:'verifier-alpha',
              redirectUri:'http://127.0.0.1:5080/auto/callback',
            });
            assert.equal(JSON.parse(records.get('console:test:token')).access_token, 'session-bearer');
            assert.equal(JSON.parse(records.get('console:test:voice-realtime:token')).access_token, 'voice-bearer');
            assert.equal(JSON.parse(records.get('console:test:service-access-review:token')).access_token, 'review-bearer');
            assert.equal(context.replaced, '/admin');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AutoCallback_VoiceLogin_ShouldExchangeInBrowserAndPreserveSessionToken()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/auto/callback");
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('(function(){');
            const end = source.lastIndexOf('})();');
            const voiceResource = 'https://api.example.test/api/v1/proxy/s/openai-realtime';
            const records = new Map([
              ['console:test:token', JSON.stringify({access_token:'session-bearer'})],
            ]);
            const pending = {
              verifier:'verifier-alpha', state:'state-alpha', returnTo:'/voice',
              resources:[voiceResource], tokenPurpose:'voice-realtime', authFlow:'',
            };
            const fetchCalls = [];
            const elements = new Map();
            const context = {
              URL, URLSearchParams, TextDecoder, Uint8Array,
              atob:(value) => Buffer.from(value, 'base64').toString('binary'),
              document:{getElementById:(id) => {
                if (!elements.has(id)) elements.set(id, {style:{},textContent:'',innerHTML:''});
                return elements.get(id);
              }},
              location:{
                origin:'http://127.0.0.1:5080',
                search:'?code=code-alpha&state=state-alpha',
                replace:(value) => { context.replaced = value; },
              },
              sessionStorage:{
                getItem:(key) => key === 'console:test:pkce' ? JSON.stringify(pending) : null,
                removeItem:() => {},
              },
              localStorage:{
                getItem:(key) => records.get(key) || null,
                setItem:(key, value) => records.set(key, value),
                removeItem:(key) => records.delete(key),
              },
              fetch:async (url, options) => {
                fetchCalls.push({url, options});
                return {
                  ok:true,
                  json:async () => ({access_token:'voice-bearer', resource:[voiceResource]}),
                };
              },
              Date, JSON, Map, console,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end + 5), context);
            await new Promise((resolve) => setImmediate(resolve));
            await new Promise((resolve) => setImmediate(resolve));

            assert.equal(fetchCalls.length, 1);
            assert.match(fetchCalls[0].url, /\/oauth\/token$/);
            assert.notEqual(fetchCalls[0].url, '/api/auth/nyxid/finalize');
            assert.deepEqual(new URLSearchParams(fetchCalls[0].options.body).getAll('resource'), [voiceResource]);
            assert.equal(JSON.parse(records.get('console:test:token')).access_token, 'session-bearer');
            assert.equal(JSON.parse(records.get('console:test:voice-realtime:token')).access_token, 'voice-bearer');
            assert.equal(context.replaced, '/voice');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_ShouldUseRealApiStateAndKeepAcceptedSeparateFromReady()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        html.Should().Contain("class DeliveryApi");
        html.Should().Contain("session() { return this.request(\"/api/delivery/session\"); }");
        html.Should().Contain("packages() { return this.request(\"/api/delivery/packages\"); }");
        html.Should().Contain("validateAccess(deliveryId)");
        html.Should().Contain("revokeRequest(deliveryId)");
        html.Should().Contain("async function revokeDeliveryRequest(id)");
        html.Should().Contain("HTTP 202 不是完成状态；请刷新列表观察服务端提交结果。");
        html.Should().Contain("createConnectLink(scopeId, deliveryId, slotKey)");
        html.Should().Contain("existingConnections(scopeId, deliveryId, slotKey)");
        html.Should().Contain("attachExistingConnection(scopeId, deliveryId, slotKey, userServiceId)");
        html.Should().Contain("refreshAuthorizationCatalog()");
        html.Should().Contain("/api/auth/nyxid/authorization-catalog:refresh");
        html.Should().Contain("const catalogRefresh = await api.refreshAuthorizationCatalog();");
        html.Should().Contain("NyxID 持久授权目录仍在同步，请稍后重试校验。");
        html.Should().Contain("connectStatus(scopeId, deliveryId, slotKey)");
        html.Should().Contain("const ready = status === \"ready\";");
        html.Should().Contain("HTTP 202 只表示进入处理队列");
        html.Should().Contain("needs_action：尚未创建可用的 NyxID 连接");
        html.Should().Contain("workflowName: text(first(selected, [\"workflowName\"], \"\"))");
        html.Should().Contain("idempotencyKey: state.adminCreateIdempotencyKey || newIdempotencyKey()");
        html.Should().Contain("body.confirmations = riskConfirmations().map");
        html.Should().Contain("attestedRisk: text(first(risk, [\"attestedRisk\"], \"\"))");
        html.Should().Contain("body.idempotencyKey = idempotencyKey");
        html.Should().Contain("triggerOptionKind(option) === \"one_shot\"");
        html.Should().Contain("first(detail, [\"availableTriggerIntents\"], [])");
        html.Should().NotContain("[\"triggerIntents\", \"availableTriggerIntents\"]");
        html.Should().Contain("function deliveryAcceptancePolicy(detail)");
        html.Should().Contain("first(deliveryPackage(detail), [\"acceptancePolicy\"], {})");
        html.Should().Contain("first(policy, [\"automaticAcceptanceSupported\"], false) === true");
        html.Should().Contain("function eligibleTriggerOptions(detail)");
        html.Should().Contain("const availableTriggers = eligibleTriggerOptions(detail);");
        html.Should().Contain("return triggerOptionKind(option) === \"none\";");
        html.Should().Contain("first(policy, [\"limitation\"], \"\")");
        html.Should().Contain("仅支持发布，自动验收不可用");
        html.Should().Contain("Schedule 是独立 trigger intent，不修改 Workflow YAML。");
        html.Should().Contain("connectionRuntime: Object.create(null)");
        html.Should().Contain("verificationStatus");
        html.Should().Contain("verificationReference");
        html.Should().Contain("contentDigest");
        html.Should().NotContain("evidenceItems.map(String)");
        html.Should().NotContain("connectionReferences: connectionReferences()");
        html.Should().Contain("routeHref(\"customer-detail\", id)");
        html.Should().Contain("function restoreConnectReturnRoute()");
        html.Should().Contain("parameters.get(\"deliveryId\")");
        html.Should().NotContain("DemoApi");
        html.Should().NotContain("demoMode");
        html.Should().NotContain("MutationObserver");
        html.Should().NotContain("setInterval(");
        html.Should().NotContain("demo-installation");
        html.Should().NotContain("demo-team");

        var validationStart = html.IndexOf("async function validateCustomerConfig()", StringComparison.Ordinal);
        var catalogRefresh = html.IndexOf(
            "await api.refreshAuthorizationCatalog()",
            validationStart,
            StringComparison.Ordinal);
        var validationRequest = html.IndexOf(
            "await api.validateConfig(",
            validationStart,
            StringComparison.Ordinal);
        catalogRefresh.Should().BeGreaterThan(validationStart);
        validationRequest.Should().BeGreaterThan(catalogRefresh);
    }

    [Fact]
    public async Task DeliveryShell_ConnectionCreate_ShouldRequireCorrelatedAcceptedReceipt()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('async function createConnection(slotKey) {');
            const end = html.indexOf('async function loadExistingConnections(', start);
            assert.notEqual(start, -1, 'connection creation behavior must exist');
            assert.notEqual(end, -1, 'existing connection loader must follow creation behavior');
            const source = html.slice(start, end);

            class ApiError extends Error {
              constructor(status, message, body, code) {
                super(message); this.status = status; this.body = body; this.code = code;
              }
            }
            function first(value, keys, fallback) {
              for (const key of keys) if (value && value[key] !== undefined) return value[key];
              return fallback;
            }
            function text(value, fallback = '') {
              const result = value == null ? '' : String(value).trim();
              return result || fallback;
            }
            function makeContext(result) {
              const context = {
                ApiError, Object,
                api: { async createConnectLink() { return result; } },
                first, text,
                object(value) { return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
                safeHttpUrl(value) { return /^https:\/\//.test(String(value || '')) ? String(value) : ''; },
                customerScopeId(value) { return text(first(value, ['targetScopeId'], '')); },
                customerRouteIsCurrent(deliveryId, sequence) {
                  return context.state.routeSequence === sequence &&
                    context.state.customer.deliveryId === deliveryId;
                },
                invalidateValidation() {}, renderCustomerDetail() {},
                state: {
                  routeSequence: 7, notice: null,
                  customer: {
                    deliveryId:'delivery-alpha',detail:{targetScopeId:'scope-alpha'},busy:'',
                    connectionErrors:Object.create(null),connectionRuntime:Object.create(null)
                  }
                }
              };
              vm.createContext(context);
              vm.runInContext(source, context);
              return context;
            }

            (async function() {
              const statusUrl = '/api/scopes/scope-alpha/delivery-requests/delivery-alpha/connections/lark';
              const accepted = makeContext({
                status:202, location:statusUrl,
                data:{slotKey:'lark',status:'begin_accepted',connectLinkId:'link-created',
                  statusUrl,connectUrl:'https://nyx.example/connect/redacted'}
              });
              await accepted.createConnection('lark');
              assert.equal(accepted.state.customer.connectionRuntime.lark.connectLinkId, 'link-created');
              assert.equal(accepted.state.notice.title, '连接请求已受理');

              const dishonest = makeContext({
                status:200, location:'',
                data:{slotKey:'lark',status:'pending',connectUrl:'https://nyx.example/connect/redacted'}
              });
              await dishonest.createConnection('lark');
              assert.equal(dishonest.state.customer.connectionErrors.lark.code, 'invalid_connection_receipt');
              assert.deepEqual(Object.keys(dishonest.state.customer.connectionRuntime), []);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_ConnectionRefresh_ShouldWaitForTerminalProjectionAndIgnoreLateRoutes()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function customerRouteIsCurrent(deliveryIdValue, sequence) {');
            const end = html.indexOf('function renderConnections()', start);
            assert.notEqual(start, -1, 'customer route guard must exist');
            assert.notEqual(end, -1, 'connection renderer must follow refresh behavior');
            const source = html.slice(start, end);

            class ApiError extends Error {
              constructor(status, message, body, code) {
                super(message); this.status = status; this.body = body; this.code = code;
              }
            }
            function first(value, keys, fallback) {
              for (const key of keys) if (value && value[key] !== undefined) return value[key];
              return fallback;
            }
            function text(value, fallback = '') {
              const result = value == null ? '' : String(value).trim();
              return result || fallback;
            }
            function makeContext(api) {
              const context = {
                ApiError, Array, Boolean, Object, Promise,
                PROJECTION_RETRY_ATTEMPTS: 20,
                PROJECTION_RETRY_MS: 0,
                api,
                first,
                text,
                object(value) { return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
                connectionStatus(value) { return text(first(value, ['status'], ''), 'needs_action').toLowerCase(); },
                customerScopeId(value) { return text(first(value, ['targetScopeId'], '')); },
                invalidateValidation() {},
                renderCount: 0,
                renderCustomerDetail() { context.renderCount += 1; },
                state: {
                  routeSequence: 7,
                  notice: null,
                  customer: {
                    deliveryId: 'delivery-alpha', detail: {targetScopeId: 'scope-alpha'}, busy: '',
                    connectionErrors: Object.create(null), connectionRuntime: Object.create(null)
                  }
                },
                window: { setTimeout(resolve) { resolve(); return 1; } }
              };
              vm.createContext(context);
              vm.runInContext(source, context);
              return context;
            }

            (async function() {
              let reads = 0;
              const observations = [
                {slotKey:'lark',connectLinkId:'link-created',status:'pending',updatedAt:'2026-08-17T00:00:00Z'},
                {slotKey:'lark',connectLinkId:'link-created',status:'completed',userServiceId:'us-lark',updatedAt:'2026-08-17T00:00:01Z'}
              ];
              const terminal = makeContext({
                async refreshConnectStatus() { return {status:202,data:{status:'refresh_accepted'}}; },
                async connectStatus() { reads += 1; return {data:observations.shift()}; }
              });
              await terminal.recheckConnection('lark');
              assert.equal(reads, 2, 'a stale pending projection must not finish the refresh');
              assert.equal(terminal.state.customer.connectionRuntime.lark.status, 'completed');
              assert.equal(terminal.state.customer.connectionRuntime.lark.userServiceId, 'us-lark');

              const mismatch = makeContext({
                async refreshConnectStatus() { return {status:202,data:{status:'refresh_accepted'}}; },
                async connectStatus() { return {data:{slotKey:'lark',connectLinkId:'link-other',status:'completed'}}; }
              });
              mismatch.state.customer.connectionRuntime.lark = {connectLinkId:'link-created'};
              await mismatch.recheckConnection('lark');
              assert.equal(mismatch.state.customer.connectionErrors.lark.code, 'connection_link_mismatch');
              assert.equal(mismatch.state.customer.connectionRuntime.lark.connectLinkId, 'link-created');

              let releaseRefresh;
              const late = makeContext({
                refreshConnectStatus() { return new Promise(resolve => { releaseRefresh = resolve; }); },
                async connectStatus() { throw new Error('late route must not read connection state'); }
              });
              const pending = late.recheckConnection('lark');
              await Promise.resolve();
              const replacement = {
                deliveryId:'delivery-beta',detail:{targetScopeId:'scope-beta'},busy:'replacement-busy',
                connectionErrors:Object.create(null),connectionRuntime:Object.create(null)
              };
              late.state.customer = replacement;
              late.state.routeSequence = 8;
              releaseRefresh({status:202,data:{status:'refresh_accepted'}});
              await pending;
              assert.equal(late.state.customer, replacement);
              assert.equal(replacement.busy, 'replacement-busy');
              assert.deepEqual(Object.keys(replacement.connectionRuntime), []);
              assert.equal(late.renderCount, 1, 'late completion must not render the replacement route');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_NewDeliveryDetail_ShouldRetryAProjectionMiss()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('async function loadCustomerDetail(deliveryIdValue, sequence) {');
            const end = html.indexOf('function normalizeCreatedTeam(', start);
            assert.notEqual(start, -1, 'customer detail loader must exist');
            assert.notEqual(end, -1, 'created Team normalizer must follow detail loading');

            class ApiError extends Error {
              constructor(status, message, body, code) {
                super(message); this.status = status; this.body = body; this.code = code;
              }
            }
            let reads = 0, routeError = null;
            const context = {
              ApiError, Promise,
              PROJECTION_RETRY_ATTEMPTS: 20,
              PROJECTION_RETRY_MS: 0,
              state: {routeSequence:3,customer:null},
              api: { async getRequest() {
                reads += 1;
                if (reads < 3) throw new ApiError(404, 'projection pending', {}, 'not_found');
                return {data:{deliveryId:'delivery-alpha',targetScopeId:'scope-alpha'}};
              }},
              isAdminSession() { return true; },
              renderCustomerDetailLoading() {},
              customerDetailFromResponse(value) { return value; },
              deliveryId(value) { return value.deliveryId || ''; },
              customerScopeId(value) { return value.targetScopeId || ''; },
              initCustomerState(id, detail) { return {deliveryId:id,detail,installationId:''}; },
              renderCustomerDetail() {},
              async loadTeams() {},
              async loadExistingConnections() {},
              async loadInstallation() {},
              renderRouteError(error) { routeError = error; },
              window: { setTimeout(resolve) { resolve(); return 1; } }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async function() {
              await context.loadCustomerDetail('delivery-alpha', 3);
              assert.equal(reads, 3, '404 must be retried until the committed projection appears');
              assert.equal(routeError, null);
              assert.equal(context.state.customer.deliveryId, 'delivery-alpha');
              assert.equal(context.state.customer.detail.targetScopeId, 'scope-alpha');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_Retry_ShouldUseStateVersionAndClearAcceptedNoticeAtTerminalState()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function installationStatus() {');
            const end = html.indexOf('async function createTeam()', start);
            const messageStart = html.indexOf('function installationStatusMessage(');
            const messageEnd = html.indexOf('function renderInstallation()', messageStart);
            assert.notEqual(start, -1, 'installation refresh state machine must exist');
            assert.notEqual(end, -1, 'Team creation must follow installation refresh behavior');
            assert.notEqual(messageStart, -1, 'installation status message mapper must exist');

            function first(value, keys, fallback) {
              for (const key of keys) if (value && value[key] !== undefined) return value[key];
              return fallback;
            }
            function text(value, fallback = '') {
              const result = value == null ? '' : String(value).trim();
              return result || fallback;
            }
            const oldFailure = {
              installationId:'installation-alpha',status:'failed',stage:'provisioning',
              updatedAt:'2026-08-17T00:00:00Z',errorCode:'OLD_FAILURE',errorMessage:'old attempt failed',
              deliveryStateVersion:12
            };
            let observed = oldFailure, scheduled = null, nextTimer = 0;
            const context = {
              Array, Boolean, Date, Object, Promise,
              INSTALLATION_REFRESH_MS: 0,
              PROJECTION_GRACE_MS: 90000,
              installationTimer: null,
              first,
              text,
              object(value) { return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
              deliveryStateVersion(value) {
                const parsed = Number(first(value, ['deliveryStateVersion', 'stateVersion'], 0));
                return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : 0;
              },
              normalizeInstallation(value) { return value; },
              customerScopeId(value) { return value.targetScopeId || ''; },
              customerRouteIsCurrent(id, sequence) {
                return Boolean(context.state.customer) && context.state.routeSequence === sequence &&
                  context.state.customer.deliveryId === id;
              },
              renderCustomerDetail() {},
              errorDescription(error) { return {tone:'danger',title:'error',message:error.message}; },
              state: {
                routeSequence:11,
                notice:null,
                customer: {
                  deliveryId:'delivery-alpha',detail:{targetScopeId:'scope-alpha'},busy:'',
                  installationId:'installation-alpha',deliveryStateVersion:12,
                  installation:oldFailure,installationError:null,acceptedHttpStatus:0,
                  acceptedAtMs:0,installationProjectionBaselineVersion:0
                }
              },
              api: {
                async retry() { return {status:202,data:{installationId:'installation-alpha',status:'accepted'}}; },
                async installation() { return {data:observed}; }
              },
              window: {
                setTimeout(callback) { scheduled = callback; return ++nextTimer; },
                clearTimeout() { scheduled = null; }
              }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);
            vm.runInContext(html.slice(messageStart, messageEnd), context);

            (async function() {
              await context.retryInstallation();
              assert.equal(context.state.customer.installation.status, 'accepted');
              assert.equal(context.state.customer.installation.stage, 'awaiting_projection');
              assert.equal(context.state.customer.installationProjectionBaselineVersion, 12,
                'retry must remember the authoritative pre-acceptance state version');
              assert.equal(typeof scheduled, 'function', 'old failed projection must keep polling');

              observed = {
                installationId:'installation-alpha',status:'ready',stage:'ready',
                updatedAt:'2026-08-17T00:00:01Z',publishedServiceId:'svc-alpha',
                deliveryStateVersion:13
              };
              await context.loadInstallation('scope-alpha', 'installation-alpha', 11, true);
              assert.equal(context.state.customer.installation.status, 'ready');
              assert.equal(context.state.customer.deliveryStateVersion, 13);
              assert.equal(context.state.customer.acceptedHttpStatus, 0);
              assert.equal(context.state.customer.installationProjectionBaselineVersion, 0);
              assert.equal(context.state.notice, null, 'terminal ready must clear the old HTTP 202 notice');
              assert.equal(scheduled, null, 'ready is terminal and must stop polling');

              context.state.customer.installation = Object.assign({}, oldFailure, {deliveryStateVersion:20});
              context.state.customer.deliveryStateVersion = 20;
              observed = context.state.customer.installation;
              await context.retryInstallation();
              assert.equal(context.state.notice.title, '重试请求已受理');
              observed = {
                installationId:'installation-alpha',status:'failed',stage:'provisioning',
                error:'binding was rejected',deliveryStateVersion:21
              };
              await context.loadInstallation('scope-alpha', 'installation-alpha', 11, true);
              assert.equal(context.state.customer.installation.status, 'failed');
              assert.equal(context.state.notice, null, 'terminal failure must clear the old HTTP 202 notice');
              const failureText = context.installationStatusMessage(
                context.state.customer.installation,
                'failed',
                context.state.customer.acceptedHttpStatus);
              assert.equal(failureText, 'binding was rejected',
                'terminal failure detail must outrank the stale 202 receipt');

              context.state.customer.acceptedHttpStatus = 202;
              context.state.customer.acceptedAtMs = Date.now();
              context.state.customer.installationProjectionBaselineVersion = 30;
              observed = {
                installationId:'installation-alpha',status:'ready',stage:'ready',
                publishedServiceId:'svc-alpha',deliveryStateVersion:30
              };
              await context.loadInstallation('scope-alpha', 'installation-alpha', 11, true);
              assert.equal(context.state.customer.installation.status, 'ready',
                'an idempotent ready observation must not be hidden merely because its version is unchanged');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_PublishAndRetry_ShouldIgnoreLateResponsesFromAnotherRoute()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const publishStart = html.indexOf('async function publishCustomerDelivery() {');
            const publishEnd = html.indexOf('function installationStatus() {', publishStart);
            const retryStart = html.indexOf('async function retryInstallation() {');
            const retryEnd = html.indexOf('async function createTeam() {', retryStart);
            assert.notEqual(publishStart, -1, 'publish action must exist');
            assert.notEqual(publishEnd, -1, 'installation state must follow publish');
            assert.notEqual(retryStart, -1, 'retry action must exist');
            assert.notEqual(retryEnd, -1, 'Team creation must follow retry');

            function deferred() {
              let resolve;
              const promise = new Promise(ok => { resolve = ok; });
              return {promise, resolve};
            }
            function first(value, keys, fallback) {
              for (const key of keys) if (value && value[key] !== undefined) return value[key];
              return fallback;
            }
            function text(value, fallback = '') {
              const result = value == null ? '' : String(value).trim();
              return result || fallback;
            }

            const publishGate = deferred();
            const retryGate = deferred();
            let renderCount = 0, installationReads = 0;
            const context = {
              Array, Boolean, Date, Object, Promise,
              first,
              text,
              object(value) { return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
              customerScopeId(value) { return value.targetScopeId || ''; },
              customerRouteIsCurrent(id, sequence) {
                return Boolean(context.state.customer) && context.state.routeSequence === sequence &&
                  context.state.customer.deliveryId === id;
              },
              configurationBody() { return {teamId:'team-alpha',customerConfig:{}}; },
              validationIsPublishable() { return true; },
              riskConfirmations() { return []; },
              absoluteHttpUrl() { return ''; },
              announce() {},
              renderCustomerDetail() { renderCount += 1; },
              errorDescription(error) { return {tone:'danger',title:'error',message:error.message}; },
              async loadInstallation() { installationReads += 1; },
              state: {routeSequence:1,notice:null,customer:null},
              api: {
                publish() { return publishGate.promise; },
                retry() { return retryGate.promise; }
              }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(publishStart, publishEnd), context);
            vm.runInContext(html.slice(retryStart, retryEnd), context);

            function publishCustomer(id) {
              return {
                deliveryId:id,detail:{targetScopeId:'scope-alpha'},busy:'',
                validation:{valid:true},idempotencyKey:'publish-alpha',confirmedRisks:new Set(),
                installation:{},installationId:'',deliveryStateVersion:7,clientError:''
              };
            }
            function retryCustomer(id) {
              return {
                deliveryId:id,detail:{targetScopeId:'scope-alpha'},busy:'',
                installation:{installationId:'installation-alpha',status:'failed'},
                installationId:'installation-alpha',deliveryStateVersion:9
              };
            }

            (async function() {
              context.state.customer = publishCustomer('delivery-alpha');
              const pendingPublish = context.publishCustomerDelivery();
              await Promise.resolve();
              const publishReplacement = {
                deliveryId:'delivery-beta',detail:{targetScopeId:'scope-beta'},busy:'replacement-publish',
                installation:{status:'ready'},installationId:'installation-beta'
              };
              context.state.customer = publishReplacement;
              context.state.routeSequence = 2;
              publishGate.resolve({status:202,data:{installationId:'installation-alpha',status:'accepted'}});
              await pendingPublish;
              assert.equal(context.state.customer, publishReplacement);
              assert.equal(publishReplacement.busy, 'replacement-publish');
              assert.equal(publishReplacement.installationId, 'installation-beta');

              context.state.customer = retryCustomer('delivery-gamma');
              context.state.routeSequence = 3;
              const pendingRetry = context.retryInstallation();
              await Promise.resolve();
              const retryReplacement = {
                deliveryId:'delivery-delta',detail:{targetScopeId:'scope-delta'},busy:'replacement-retry',
                installation:{status:'ready'},installationId:'installation-delta'
              };
              context.state.customer = retryReplacement;
              context.state.routeSequence = 4;
              retryGate.resolve({status:202,data:{installationId:'installation-alpha',status:'accepted'}});
              await pendingRetry;
              assert.equal(context.state.customer, retryReplacement);
              assert.equal(retryReplacement.busy, 'replacement-retry');
              assert.equal(retryReplacement.installationId, 'installation-delta');
              assert.equal(installationReads, 0, 'late responses must not start installation polling');
              assert.equal(renderCount, 2, 'only the two pre-request busy renders are allowed');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_TeamRoster_ShouldKeepCreatedTeamUntilProjectionCatchesUp()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function mergeTeamRoster(authoritativeTeams, pendingTeams) {');
            const end = html.indexOf('async function loadTeams(', start);
            assert.notEqual(start, -1, 'team roster merger must exist');
            assert.notEqual(end, -1, 'team loader must follow roster merger');

            const context = {
              array(value){ return Array.isArray(value) ? value : []; },
              object(value){ return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
              teamId(team){ return String(team && team.teamId || '').trim(); },
              Set, Object
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            const pending = Object.create(null);
            pending['t-new'] = {teamId:'t-new',scopeId:'scope-alpha',displayName:'New Team'};
            const lagging = context.mergeTeamRoster([], pending);
            assert.equal(lagging.length, 1);
            assert.equal(lagging[0].teamId, 't-new');
            assert.ok(pending['t-new'], 'pending summary remains until the read model sees it');

            const projected = context.mergeTeamRoster(
              [{teamId:'t-new',scopeId:'scope-alpha',displayName:'Projected Team'}],
              pending
            );
            assert.equal(projected.length, 1);
            assert.equal(projected[0].displayName, 'Projected Team');
            assert.equal(pending['t-new'], undefined, 'authoritative roster replaces the pending summary');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_TeamSelection_ShouldOnlyAllowActiveWritableTeams()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function teamId(team) {');
            const end = html.indexOf('function connectionSlots()', start);
            assert.notEqual(start, -1, 'Team selection behavior must exist');
            assert.notEqual(end, -1, 'connection rendering must follow Team selection behavior');

            function element(tag, attrs, ...children) {
              return {
                tag,
                attrs: attrs || {},
                children: children.flat(Infinity).filter(value => value !== null && value !== undefined && value !== false)
              };
            }
            function allText(node) {
              if (typeof node === 'string') return node;
              if (!node || typeof node !== 'object') return '';
              return String(node.attrs && node.attrs.text || '') + node.children.map(allText).join('');
            }

            const teams = [
              {teamId:'t-active',displayName:'Active Team',lifecycleStage:'active'},
              {teamId:'t-readonly',displayName:'Read-only Team',lifecycleStage:'active',canPublish:false},
              {teamId:'t-archived',displayName:'Archived Team',lifecycleStage:'archived',canPublish:true},
              {teamId:'t-unknown',displayName:'Unknown Team',lifecycleStage:'created',canPublish:true},
              {teamId:'t-missing',displayName:'Missing Stage Team',canPublish:true}
            ];
            const context = {
              state: {customer: {teams, teamsError:null, selectedTeamId:'t-archived'}},
              array(value){ return Array.isArray(value) ? value : []; },
              object(value){ return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
              first(value, names, fallback){
                for (const name of names) {
                  if (value && value[name] !== undefined && value[name] !== null) return value[name];
                }
                return fallback;
              },
              text(value, fallback){
                return typeof value === 'string' && value.trim() ? value.trim() : (fallback || '');
              },
              h: element,
              badge(status, label){ return element('span', {status, text:label}); },
              errorDescription(){ throw new Error('unexpected error state'); },
              notice(){ throw new Error('unexpected notice state'); },
              emptyBlock(){ throw new Error('unexpected empty state'); },
              invalidateValidation(){},
              renderCustomerDetail(){}
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            assert.equal(context.teamCanPublish(teams[0]), true, 'active Team remains publishable');
            assert.equal(context.teamCanPublish(teams[1]), false, 'read-only active Team is blocked');
            assert.equal(context.teamCanPublish(teams[2]), false, 'archived Team is blocked');
            assert.equal(context.teamCanPublish(teams[3]), false, 'unknown non-active Team fails closed');
            assert.equal(context.teamCanPublish(teams[4]), false, 'missing lifecycle stage fails closed');
            assert.equal(context.selectedTeamCanPublish(), false, 'persisted archived selection cannot validate or publish');

            const rendered = context.renderTeamSection();
            const buttons = rendered.children;
            assert.equal(buttons.length, teams.length);
            assert.equal(buttons[0].attrs.disabled, false);
            assert.equal(buttons[0].attrs['aria-pressed'], 'false');
            assert.equal(buttons[1].attrs.disabled, true);
            assert.match(allText(buttons[1]), /没有发布权限.*只读/);
            assert.equal(buttons[2].attrs.disabled, true);
            assert.equal(buttons[2].attrs['aria-pressed'], 'false', 'archived Team must not render as selected');
            assert.match(allText(buttons[2]), /已归档.*已归档/);
            assert.equal(buttons[3].attrs.disabled, true);
            assert.match(allText(buttons[3]), /当前状态不可发布.*不可发布/);
            assert.equal(buttons[4].attrs.disabled, true);

            context.state.customer.selectedTeamId = 't-active';
            assert.equal(context.selectedTeamCanPublish(), true);
            const activeSelection = context.renderTeamSection();
            assert.equal(activeSelection.children[0].attrs.disabled, false);
            assert.equal(activeSelection.children[0].attrs['aria-pressed'], 'true');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShell_CreatedTeam_ShouldRequireTheAuthoritativeScopeIdentity()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/delivery");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function normalizeCreatedTeam(data, expectedScopeId) {');
            const end = html.indexOf('function mergeTeamRoster(', start);
            assert.notEqual(start, -1, 'created Team normalizer must exist');
            assert.notEqual(end, -1, 'team roster merger must follow the normalizer');

            class ApiError extends Error {
              constructor(status, message, body, code) {
                super(message);
                this.status = status;
                this.body = body;
                this.code = code;
              }
            }
            const context = {
              ApiError,
              object(value){ return value && typeof value === 'object' && !Array.isArray(value) ? value : {}; },
              first(value, names, fallback){
                for (const name of names) {
                  if (value && value[name] !== undefined && value[name] !== null) return value[name];
                }
                return fallback;
              },
              teamId(team){ return String(team && team.teamId || '').trim(); },
              text(value){ return String(value || '').trim(); }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            assert.throws(
              () => context.normalizeCreatedTeam({teamId:'t-new'}, 'scope-alpha'),
              error => error.code === 'missing_team_scope'
            );
            assert.throws(
              () => context.normalizeCreatedTeam({teamId:'t-new',scopeId:'scope-beta'}, 'scope-alpha'),
              error => error.code === 'team_scope_mismatch'
            );
            const created = context.normalizeCreatedTeam(
              {teamId:'t-new',scopeId:'scope-alpha',displayName:'New Team'},
              'scope-alpha'
            );
            assert.equal(created.teamId, 't-new');
            assert.equal(created.scopeId, 'scope-alpha');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task DeliveryShellEndpoint_ShouldRemainGetOnly()
    {
        await using var app = await CreateAppAsync();
        using var response = await app.GetTestClient().PostAsync("/delivery", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task AdminShell_Fleet_ShouldIncludeNyxIdUsersWithoutRuns()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function mapFleetCompanies(runs){');
            const end = html.indexOf('function loadFleet(rerender){', start);
            assert.notEqual(start, -1, 'fleet mapper must exist');
            assert.notEqual(end, -1, 'fleet loader must follow mapper');

            const context = {
              NYX_USERS: {
                'scope-active': {display_name:'Active Org',email:'active@example.test'},
                'scope-idle': {display_name:'Idle Org',email:'idle@example.test'}
              },
              FLEET_RUNS_BY_SCOPE: {},
              fleetRunActive(status){ return status === 'running'; },
              fleetRunFailed(status){ return status === 'failed' || status === 'timed_out'; },
              fleetRunSuccess(status){ return status === 'completed'; },
              fleetHealth(total, failed){ return total ? (failed ? 'red' : 'green') : 'grey'; },
              fleetAgoMins(){ return null; },
              fleetAgo(){ return '—'; },
              fleetOrgProfile(scopeId){
                const user = context.NYX_USERS[scopeId];
                return {name:user.display_name,email:user.email,avatar:null,isAdmin:false};
              }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            const companies = context.mapFleetCompanies([{
              id:'run-active',name:'workflow-active',status:'completed',scope:'scope-active',
              updatedAtUtc:'2026-08-09T00:00:00Z'
            }]);
            assert.equal(companies.length, 2);
            const active = companies.find(company => company.id === 'scope-active');
            const idle = companies.find(company => company.id === 'scope-idle');
            assert.equal(active.runsTotal, 1);
            assert.equal(active.isEmpty, false);
            assert.equal(idle.runsTotal, 0);
            assert.equal(idle.isEmpty, true);
            assert.deepEqual(Object.keys(context.FLEET_RUNS_BY_SCOPE), ['scope-active']);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AdminShell_Fleet_ShouldNotBlockPlatformAdminWhenNyxIdDirectoryForbids()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function loadFleet(rerender){');
            const end = html.indexOf('/* 集团 KPI', start);
            assert.notEqual(start, -1, 'fleet loader must exist');
            assert.notEqual(end, -1, 'fleet KPI must follow loader');

            const calls = [];
            const context = {
              Promise, Date, calls,
              FLEET_ERR:null, FLEET_FORBIDDEN:false, FLEET_LOADING:false, FLEET_LOADED:false, FLEET_STAMP:0,
              FLEET_DIRECTORY_STATUS:'pending', FLEET_RUN_WINDOW:500, FLEET_COMPANIES:[], COMPANIES:[],
              NYX_USERS_ATTEMPTED:false, NYX_USERS:{'stale-user':{}},
              loadNyxUsers(){ return Promise.resolve({forbidden:true}); },
              adminJson(url){ calls.push(url); return Promise.resolve([{scope:'scope-active'}]); },
              mapFleetRuns(runs){ return runs; },
              mapFleetCompanies(runs){ return runs.map(run => ({id:run.scope})); }
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async () => {
              await context.loadFleet();
              assert.equal(context.FLEET_FORBIDDEN, false, 'NyxID directory denial must not deny the board');
              assert.equal(context.FLEET_ERR, null);
              assert.equal(context.FLEET_LOADED, true);
              assert.equal(context.FLEET_DIRECTORY_STATUS, 'forbidden');
              assert.deepEqual(Object.keys(context.NYX_USERS), [], 'stale directory entries must be discarded');
              assert.equal(context.FLEET_COMPANIES.length, 1);
              assert.equal(context.FLEET_COMPANIES[0].id, 'scope-active');
              assert.deepEqual(calls, ['/api/workflow/observatory/runs?scope=__all__&take=500']);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AdminShell_AuditRefresh_ShouldReloadOnEntryAndGlobalRefresh()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        // 进入模块：首访 reset 加载；重进保留已展示行、静默换新（stale-while-revalidate）
        html.Should().Contain("if(!AUDIT_LOADING) loadAuditTrail(!AUDIT_LOADED);");
        html.Should().Contain("async function loadAuditTrail(reset){");
        html.Should().Contain("if(reset){ AUDIT_DATA=[]; AUDIT_CURSOR=null; AUDIT_HAS_MORE=false; AUDIT_WATERMARK=null; }");
        // 头部 ⟳ 统一走 refreshActiveModule：audit 分支真实拉新，不再假装刷新
        html.Should().Contain("refreshActiveModule();");
        html.Should().Contain("if(module==='audit'){ loadAuditTrail(false); toast('正在刷新审计日志'); return; }");
        html.Should().NotContain("toast('已刷新（最终一致 readmodel）')");
        html.Should().NotContain(
            "if(!AUDIT_LOADED||AUDIT_LOADING){ if(!AUDIT_LOADING) loadAuditTrail(); }");
    }

    [Fact]
    public async Task AdminShell_ModelsCatalog_ShouldExposeLoginModuleAndHonestAdminSurface()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("models:{name:'模型目录', auth:'login'");
        html.Should().Contain("items:['models','channels','voice']");
        html.Should().Contain("case 'models': return viewModels();");
        html.Should().Contain(
            "ACCOUNT&&ACCOUNT.admin?'<button type=\"button\" data-models-owner=\"platform\"");
        html.Should().Contain("/api/scopes/'+encodeURIComponent(scope)+'/llm-model-catalog");
        html.Should().Contain("/api/admin/llm-model-catalog");
        html.Should().Contain("base+'/candidates/'+encodeURIComponent(exactIdentity)+'/models'");
        html.Should().Contain("custom_replace，系统不会回退到平台默认");
        html.Should().Contain("模型按公开 ID 稳定排序");
        html.Should().NotContain("来源顺序即返回顺序");
        html.Should().Contain("命令已受理（202 Accepted）");
        html.Should().Contain("currentVersion>baseVersion");
        html.Should().Contain("catalog.lastMutationId===state.pending.mutationId");
        html.Should().Contain("配置已被另一项更新取代");
        html.Should().Contain("candidate.isCallable===true");
        html.Should().Contain("organization_access_denied");
        html.Should().Contain("connection_unavailable");
        html.Should().Contain("if(reason==='invalid_service_slug')return 'Service slug 不规范';");
        html.Should().Contain("if(reason==='provider_service')return 'Provider 服务需要用户绑定';");
        html.Should().Contain("if(reason==='unsupported_service_category')return '服务分类不支持平台直连';");
        html.Should().Contain("if(reason==='user_credential_required')return '需要用户凭据';");
        html.Should().Contain("if(reason==='token_exchange_unsupported')return 'Token exchange 不支持平台直连';");
        html.Should().Contain("if(reason==='unsupported_auth_method')return '认证方式不支持平台直连';");
        html.Should().Contain("平台 catalog 直连");
        html.Should().Contain("modelsCatalogQualifiedId(source,modelId)");
        html.Should().Contain("上游模型 ID");
        html.Should().Contain("/v1/models 发布 qualified ID");
        html.Should().Contain("var MODELS_LIMITS={maxSources:32,maxModelsPerSource:256,maxModelsPerPolicy:2048,maxModelIdUtf8Bytes:256}");
        html.Should().Contain("data-models-reload-latest");
        html.Should().Contain("所有继承平台默认的 scope，其 /v1/models 都将返回空集合");
        html.Should().Contain("root.dataset.modelsCatalogMounted==='true'");
        html.Should().Contain("if(typeof modelsCatalogResetAll==='function')modelsCatalogResetAll('');");
        html.Should().Contain("if(response.forbidden){modelsCatalogClearOwnerState(owner,true);return;}");
        html.Should().Contain("if(response.forbidden){modelsCatalogClearOwnerState('scope',true);return;}");
        html.Should().Contain("data-models-owner=\"scope\" aria-pressed=\"");
        html.Should().Contain("data-models-owner=\"platform\" aria-pressed=\"");
        html.Should().Contain(
            "state.saving?'正在提交':state.loading?'正在读取':state.conflict?'版本冲突':state.pending?'已受理，待物化':state.dirty?'有未保存修改':state.error?'刷新失败':state.loaded?'已同步':'未加载'");
        html.Should().Contain(".models-model-chip button{display:inline-grid;width:24px;height:24px;");
        html.Should().Contain(
            ".models-config-actions .btn-primary:disabled{background:var(--surface-3);border-color:var(--border);color:var(--faint);opacity:.72;cursor:not-allowed;box-shadow:none;}");
        html.Should().Contain(".models-notice{flex-wrap:wrap;}");
        html.Should().Contain("max-height:92dvh;");
        html.Should().Contain("aria-labelledby=\"models-editor-title\" aria-describedby=\"models-editor-description\"");
        html.Should().Contain("label for=\"models-editor-candidate\"");
        html.Should().Contain("label for=\"models-editor-manual-id\"");
        html.Should().Contain("data-models-discover");
        html.Should().Contain("data-models-discovery-search");
        html.Should().Contain("data-models-toggle-filtered aria-checked=\"");
        html.Should().Contain("data-models-clear-discovered");
        html.Should().Contain("role=\"group\" aria-label=\"已发现模型\"");
        html.Should().Contain("role=\"alert\" aria-live=\"assertive\"");
        html.Should().Contain("input.indeterminate=input.getAttribute('aria-checked')==='mixed'");
        html.Should().Contain(".models-discovery-list{display:flex;max-height:230px;");
        html.Should().Contain(".models-discovery-list{max-height:34dvh;}");
        html.Should().Contain("if((curParts()[0]||'')!=='models')modelsCatalogLeavePage()");
        html.Should().Contain("modelsCatalogTrapEditorFocus(root,event)");
        html.Should().Contain("element.inert=true");
        html.Should().Contain("syncAccountFromStoredToken(token)");
        html.Should().Contain("ACCOUNT=next;modelsCatalogSyncAuthority();render();renderAcctW();");
        html.Should().Contain("currentToken.access_token!==expectedAccessToken");
        html.Should().Contain(
            "body:JSON.stringify({expectedStateVersion:baseVersion==null?0:baseVersion,mutationId:mutationId})");

        var start = html.IndexOf("/* ---------------- Models catalog", StringComparison.Ordinal);
        var end = html.IndexOf("/* ---------------- 侧栏 + 账号", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var modelsModule = html[start..end];
        modelsModule.Should().NotContain("serviceUrl");
        modelsModule.Should().NotContain("url.indexOf");
        modelsModule.Should().NotContain("includes('llm')");
        modelsModule.Should().NotContain("includes(\"llm\")");
        modelsModule.Should().NotContain("路由歧义");
    }

    [Fact]
    public async Task AdminShell_ModelsCatalog_ShouldPreserveExactIdentityEmptyReplaceAndObservedVersion()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function sourceBetween(startMarker, endMarker) {
              const start = html.indexOf(startMarker);
              const end = html.indexOf(endMarker, start);
              assert.notEqual(start, -1, startMarker + ' must exist');
              assert.notEqual(end, -1, endMarker + ' must follow ' + startMarker);
              return html.slice(start, end);
            }

            const context = {
              assert, console, Promise, MODELS_REQUEST:0, MODELS_DISCOVERY_REQUEST:0,
              ACCOUNT:{admin:true}, MODELS_STATE:{owner:'scope',editor:null},
              esc:value => String(value), ICON:{empty:''}, modelsCatalogRenderIfActive(){}
            };
            vm.createContext(context);
            vm.runInContext(
              sourceBetween('var MODELS_LIMITS=', 'function modelsCatalogUniqueStrings(') +
              sourceBetween('function modelsCatalogUniqueStrings(', 'function modelsCatalogMutationId(') +
              sourceBetween('function modelsCatalogCandidateIdentity(', 'function modelsCatalogSourceId(') +
              sourceBetween('function modelsCatalogOpenEditor(', 'function modelsCatalogEditorFocusSelector(') +
              sourceBetween('function modelsCatalogEditorFocusSelector(', 'function modelsCatalogAddManualModel(') +
              sourceBetween('function modelsCatalogValidateSources(', 'async function modelsCatalogSave(') +
              sourceBetween('function modelsCatalogModelFilter(', 'function modelsCatalogCandidateOptions(') +
              sourceBetween('function modelsCatalogApplyDiscoveryCheckboxState(', '/* ---------------- 侧栏 + 账号'),
              context);

            vm.runInContext(`
              const scopeCatalog = modelsCatalogNormalize({
                mode:'custom_replace', stateVersion:3,
                sources:[{
                  sourceId:'source-alpha', displayName:'Chrono',
                  serviceSlugSnapshot:'chrono-runtime', catalogServiceId:'catalog-alpha',
                  userServiceId:'user-alpha',
                  modelSelection:{mode:'explicit_models',modelIds:['gpt-5.5','gpt-5.5','o3']}
                }]
              }, 'scope');
              const scopePayload = modelsCatalogBuildPayload('scope', {
                catalog:{stateVersion:4}, draftBaseVersion:3, draft:scopeCatalog
              }, 'mutation-scope');
              assert.equal(scopePayload.mode, 'custom_replace');
              assert.equal(scopePayload.expectedStateVersion, 3,
                'a draft keeps the state version it was based on even after a newer catalog read');
              assert.equal(scopePayload.sources[0].userServiceId, 'user-alpha');
              assert.equal(Object.hasOwn(scopePayload.sources[0], 'catalogServiceId'), false,
                'scope persistence owns exact userServiceId only');
              assert.equal(Object.hasOwn(scopePayload.sources[0], 'sourceId'), false);
              assert.equal(Object.hasOwn(scopePayload.sources[0], 'displayName'), false);
              assert.deepEqual(Array.from(scopePayload.sources[0].modelSelection.modelIds), ['gpt-5.5','o3']);
              assert.equal(modelsCatalogQualifiedId(scopeCatalog.sources[0], 'gpt-5.5'),
                'chrono-runtime/gpt-5.5');
              assert.equal(modelsCatalogUtf8Length('界'.repeat(85)), 255);
              assert.equal(modelsCatalogUtf8Length('界'.repeat(86)), 258);

              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:[{
                  userServiceId:'user-without-snapshot',
                  modelSelection:{mode:'explicit_models',modelIds:['model-a']}
                }]}
              }), /Service slug snapshot/, 'routing requires a persisted canonical slug snapshot');

              function validScopeSource(index, models) {
                return {
                  userServiceId:'user-'+index,serviceSlugSnapshot:'service-'+index,
                  modelSelection:{mode:'explicit_models',modelIds:models || ['model-'+index]}
                };
              }
              const duplicateSlugs=[validScopeSource(1),validScopeSource(2)];
              duplicateSlugs[1].serviceSlugSnapshot='SERVICE-1';
              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:duplicateSlugs}
              }), /Service slug.*重复/, 'slug uniqueness is case-insensitive like the backend');
              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:Array.from({length:33},(_,i)=>validScopeSource(i))}
              }), /最多允许 32 个来源/);
              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:[validScopeSource(1,Array.from({length:257},(_,i)=>'model-'+i))]}
              }), /最多允许 256 个模型 ID/);
              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:Array.from({length:9},(_,i)=>validScopeSource(i,Array.from({length:256},(_,j)=>'model-'+j)))}
              }), /最多允许 2048 个模型 ID/);
              assert.match(modelsCatalogValidateDraft('scope', {
                draft:{mode:'custom_replace',sources:[validScopeSource(1,['界'.repeat(86)])]}
              }), /超过 256 UTF-8 bytes/);

              const enrichedScope = modelsCatalogNormalize({
                mode:'custom_replace',sources:[{
                  sourceId:'source-enriched',serviceSlugSnapshot:'chrono-runtime',
                  userServiceId:'user-enriched',
                  modelSelection:{mode:'explicit_models',modelIds:['model-a']}
                }]
              }, 'scope');
              modelsCatalogEnrichScopeSources(enrichedScope, [
                modelsCatalogNormalizeCandidate({
                  userServiceId:'user-other',catalogServiceId:'catalog-wrong',displayName:'Wrong'
                }),
                modelsCatalogNormalizeCandidate({
                  userServiceId:'user-enriched',catalogServiceId:'catalog-enriched',displayName:'Exact'
                })
              ]);
              assert.equal(enrichedScope.sources[0].catalogServiceId, 'catalog-enriched');
              assert.equal(enrichedScope.sources[0].displayName, 'Exact');

              const explicitEmpty = modelsCatalogNormalize({
                mode:'custom_replace', stateVersion:4, sources:[],
                effectiveSources:[{
                  sourceId:'platform-source', catalogServiceId:'catalog-platform',
                  userServiceId:'must-not-fall-back'
                }]
              }, 'scope');
              const emptyPayload = modelsCatalogBuildPayload('scope', {
                catalog:{stateVersion:4}, draft:explicitEmpty
              }, 'mutation-empty');
              assert.equal(emptyPayload.mode, 'custom_replace');
              assert.equal(emptyPayload.sources.length, 0,
                'explicit empty custom_replace must not copy effective platform sources');

              const platformCatalog = modelsCatalogNormalize({
                mode:'custom_replace', stateVersion:9,
                sources:[{
                  sourceId:'source-platform', displayName:'Platform Chrono',
                  serviceSlugSnapshot:'chrono-public', catalogServiceId:'catalog-platform',
                  userServiceId:'scope-identity-must-be-erased',
                  modelSelection:{mode:'explicit_models',modelIds:['model-a']}
                }]
              }, 'platform');
              assert.equal(Object.hasOwn(platformCatalog.sources[0], 'userServiceId'), false);
              const platformPayload = modelsCatalogBuildPayload('platform', {
                catalog:{stateVersion:9}, draft:platformCatalog
              }, 'mutation-platform');
              assert.equal(Object.hasOwn(platformPayload.sources[0], 'userServiceId'), false,
                'platform defaults must never store a user service identity');
              assert.equal(Object.hasOwn(platformPayload.sources[0], 'sourceId'), false);
              assert.equal(Object.hasOwn(platformPayload.sources[0], 'displayName'), false);
              assert.deepEqual(Array.from(platformPayload.sources[0].modelSelection.modelIds), ['model-a']);

              ACCOUNT={admin:true,scope:'scope-shared',claims:{sub:'admin-a'}};
              modelsCatalogSyncAuthority();
              MODELS_STATE.owner='platform';
              MODELS_STATE.platform.loaded=true;
              MODELS_STATE.platform.catalog={stateVersion:12};
              MODELS_STATE.editor={owner:'platform'};
              ACCOUNT={admin:true,scope:'scope-shared',claims:{sub:'admin-b'}};
              modelsCatalogSyncAuthority();
              assert.equal(MODELS_STATE.owner, 'scope');
              assert.equal(MODELS_STATE.editor, null);
              assert.equal(MODELS_STATE.platform.loaded, false,
                'platform catalog state must be discarded when the authenticated subject changes');

              MODELS_STATE.platform.loaded=true;
              ACCOUNT={admin:false,scope:'scope-shared',claims:{sub:'admin-b'}};
              modelsCatalogSyncAuthority();
              assert.equal(MODELS_STATE.platform.loaded, false,
                'platform catalog state must be discarded when admin authority is lost');

              MODELS_STATE.owner='scope';
              Object.assign(MODELS_STATE.scope, {
                loaded:true,loading:true,saving:true,forbidden:false,error:'stale error',
                catalog:{stateVersion:13},draft:{mode:'custom_replace',sources:[{}]},
                candidates:[{userServiceId:'stale-user-service'}],inventoryFresh:true,
                draftBaseVersion:13,dirty:true,pending:{mutationId:'stale-mutation'},
                notice:{tone:'waiting',text:'stale notice'},conflict:true,request:91
              });
              MODELS_STATE.editor={owner:'scope',source:{userServiceId:'stale-user-service'}};
              const clearedScope=modelsCatalogClearOwnerState('scope',true);
              assert.equal(clearedScope,MODELS_STATE.scope);
              assert.equal(clearedScope.forbidden,true);
              assert.equal(clearedScope.loaded,false);
              assert.equal(clearedScope.loading,false);
              assert.equal(clearedScope.saving,false);
              assert.equal(clearedScope.error,null);
              assert.equal(clearedScope.catalog,null);
              assert.equal(clearedScope.draft,null);
              assert.deepEqual(Array.from(clearedScope.candidates),[]);
              assert.equal(clearedScope.inventoryFresh,false);
              assert.equal(clearedScope.draftBaseVersion,null);
              assert.equal(clearedScope.dirty,false);
              assert.equal(clearedScope.pending,null);
              assert.equal(clearedScope.notice,null);
              assert.equal(clearedScope.conflict,false);
              assert.equal(MODELS_STATE.editor,null,
                'a forbidden response must close an editor backed by now-inaccessible catalog data');

              const exactState = {candidates:[
                modelsCatalogNormalizeCandidate({
                  catalogServiceId:'catalog-alpha',userServiceId:'user-other',
                  displayName:'Same catalog, wrong user service'
                }),
                modelsCatalogNormalizeCandidate({
                  catalogServiceId:'catalog-beta',userServiceId:'user-alpha',
                  displayName:'Exact user service'
                })
              ]};
              const scopeHit = modelsCatalogCandidateForSource({
                catalogServiceId:'catalog-alpha',userServiceId:'user-alpha'
              }, 'scope', exactState);
              assert.equal(scopeHit.catalogServiceId, 'catalog-beta',
                'scope matching must use exact userServiceId rather than catalog or text');
              const platformHit = modelsCatalogCandidateForSource({
                catalogServiceId:'catalog-alpha',userServiceId:'user-alpha'
              }, 'platform', exactState);
              assert.equal(platformHit.userServiceId, 'user-other',
                'platform matching must use exact catalogServiceId');

              exactState.candidates.push(modelsCatalogNormalizeCandidate({
                catalogServiceId:'catalog-alpha',userServiceId:'user-second-binding',
                displayName:'Second exact catalog binding'
              }));
              assert.equal(modelsCatalogCandidateForSource({
                catalogServiceId:'catalog-alpha'
              }, 'platform', exactState), null,
                'an inherited catalog identity must not select the first of multiple scope bindings');
              assert.equal(modelsCatalogCandidatesForSource({
                catalogServiceId:'catalog-alpha'
              }, 'platform', exactState).length, 2);

              const inheritedHtml=modelsCatalogSourcesTable('scope', {
                draft:{mode:'inherit_platform'},candidates:exactState.candidates
              }, [{
                sourceId:'catalog:catalog-alpha',catalogServiceId:'catalog-alpha',
                serviceSlugSnapshot:'chrono-public',displayName:'Chrono Public',
                modelSelection:{mode:'explicit_models',modelIds:['model-a']}
              }], false);
              assert.match(inheritedHtml, /平台 catalog 直连/);
              assert.match(inheritedHtml, /不依赖 scope userServiceId/);
              assert.equal(inheritedHtml.includes('chrono-public/model-a'), true,
                'read-only tables display the qualified ID returned by /v1/models');
              assert.doesNotMatch(inheritedHtml, /路由歧义|候选中不存在/,
                'catalog direct routes do not depend on scope user-service bindings');

              const callableScope = modelsCatalogNormalizeCandidate({
                userServiceId:'user-callable',catalogServiceId:'catalog-callable',
                isCallable:true,availabilityReason:'available',isActive:true,
                serviceType:'http',visibility:'private'
              });
              assert.equal(modelsCatalogCandidateSelectable(callableScope, 'scope'), true,
                'scope additions require an exact userServiceId plus isCallable');
              assert.equal(modelsCatalogStatus(callableScope, 'scope').label, '可用');

              const reasonCases = [
                ['service_inactive', {}, '服务未启用'],
                ['unsupported_service_slug', {}, 'Service slug 不兼容'],
                ['credential_missing', {credentialMissing:true}, '凭据缺失'],
                ['credential_inactive', {credentialStatus:'revoked'}, '凭据状态：revoked'],
                ['connection_expired', {connectionStatus:'expired'}, '连接已过期'],
                ['connection_unavailable', {connectionStatus:'unknown'}, '连接状态：unknown'],
                ['node_unavailable', {nodeStatus:'offline'}, '节点离线'],
                ['organization_access_denied', {
                  credentialSource:{type:'organization',allowed:false}
                }, '组织凭据无权限']
              ];
              reasonCases.forEach(([availabilityReason, extra, expected]) => {
                const candidate = modelsCatalogNormalizeCandidate(Object.assign({
                  userServiceId:'user-'+availabilityReason,isCallable:false,availabilityReason
                }, extra));
                assert.equal(modelsCatalogCandidateSelectable(candidate, 'scope'), false);
                assert.equal(modelsCatalogStatus(candidate, 'scope').label, expected);
              });

              const platformCandidate = modelsCatalogNormalizeCandidate({
                catalogServiceId:'catalog-public',isActive:true,serviceType:'http',visibility:'public',
                isSelectable:true,availabilityReason:'available'
              });
              assert.equal(modelsCatalogCandidateSelectable(platformCandidate, 'platform'), true,
                'platform additions remain restricted to public active HTTP candidates');
              platformCandidate.isSelectable = false;
              platformCandidate.availabilityReason = 'not_public';
              assert.equal(modelsCatalogCandidateSelectable(platformCandidate, 'platform'), false);
              assert.equal(modelsCatalogStatus(platformCandidate, 'platform').label, '不是 public 服务');

              const retainedEditor = {owner:'scope',index:0,candidateKey:'user-expired',persistedKey:'user-expired'};
              const expiredCandidate = modelsCatalogNormalizeCandidate({
                userServiceId:'user-expired',isCallable:false,
                availabilityReason:'credential_inactive',credentialStatus:'expired'
              });
              assert.equal(modelsCatalogEditorRetainsCandidate(retainedEditor, expiredCandidate), true,
                'an already-saved unavailable source remains editable');
              assert.equal(modelsCatalogEditorRetainsCandidate(retainedEditor, null), true,
                'an already-saved source missing from live inventory remains editable');

              const discoveryState = {
                draft:{mode:'custom_replace',sources:[]},
                candidates:[
                  modelsCatalogNormalizeCandidate({
                    userServiceId:'user-alpha',catalogServiceId:'catalog-alpha',
                    serviceSlug:'chrono-alpha',displayName:'Chrono Alpha',isCallable:true
                  }),
                  modelsCatalogNormalizeCandidate({
                    userServiceId:'user-beta',catalogServiceId:'catalog-beta',
                    serviceSlug:'chrono-beta',displayName:'Chrono Beta',isCallable:true
                  })
                ]
              };
              MODELS_STATE.scope=discoveryState;
              MODELS_STATE.editor={
                owner:'scope',index:null,persistedKey:'',candidateKey:'user-alpha',error:null,
                source:modelsCatalogNormalizeSource({
                  sourceId:'source-new',userServiceId:'user-alpha',catalogServiceId:'catalog-alpha',
                  serviceSlugSnapshot:'chrono-alpha',
                  modelSelection:{mode:'explicit_models',modelIds:['alpha-only']}
                },'scope'),
                discovery:{loading:false,loaded:true,error:null,sourceIdentity:'user-alpha',
                  serviceSlug:'chrono-alpha',modelIds:['alpha-only'],search:'alpha',request:17}
              };
              modelsCatalogSelectCandidate('user-beta');
              assert.deepEqual(Array.from(MODELS_STATE.editor.source.modelSelection.modelIds),[],
                'switching an exact service identity must clear models owned by the previous service');
              assert.equal(MODELS_STATE.editor.discovery.loaded,false);
              assert.deepEqual(Array.from(MODELS_STATE.editor.discovery.modelIds),[],
                'switching candidates must discard the previous discovery result');
              assert.equal(MODELS_STATE.editor.source.serviceSlugSnapshot,'chrono-beta');

              const policyFullState={draft:{sources:Array.from({length:8},(_,i)=>
                validScopeSource(i,Array.from({length:256},(_,j)=>'model-'+i+'-'+j)))}};
              MODELS_STATE.scope=policyFullState;
              assert.match(modelsCatalogEditorSelectionProblem({owner:'scope',index:null},['one-more']),
                /最多允许 2048 个模型 ID/,
                'editor additions must enforce the policy limit before source save');

              const filteredEditor={discovery:{search:'codex',modelIds:['gpt-5.4','gpt-5.4-codex','o3-codex']}};
              assert.deepEqual(Array.from(modelsCatalogDiscoveryItems(filteredEditor),item=>item.modelId),
                ['gpt-5.4-codex','o3-codex']);
              modelsCatalogEndpoint=owner=>owner==='platform'
                ?'/api/admin/llm-model-catalog'
                :'/api/scopes/scope-alpha/llm-model-catalog';
              assert.equal(modelsCatalogDiscoveryEndpoint('scope','user/service alpha'),
                '/api/scopes/scope-alpha/llm-model-catalog/candidates/user%2Fservice%20alpha/models');
              assert.equal(modelsCatalogDiscoveryEndpoint('platform','catalog-alpha'),
                '/api/admin/llm-model-catalog/candidates/catalog-alpha/models');
              assert.equal(modelsCatalogDiscoveryErrorMessage({
                status:409,body:{detail:'candidate no longer callable'}
              }),'candidate no longer callable · HTTP 409',
                'discovery 409 must preserve its problem detail rather than report a policy conflict');

              let focused='';
              const first={disabled:false,focus(){focused='first';}};
              const last={disabled:false,focus(){focused='last';}};
              const trapDialog={ownerDocument:{activeElement:last},querySelectorAll(){return [first,last];}};
              const trapRoot={querySelector(selector){return selector==='.models-editor'?trapDialog:null;}};
              const tabEvent={key:'Tab',shiftKey:false,preventDefault(){this.prevented=true;}};
              assert.equal(modelsCatalogTrapEditorFocus(trapRoot,tabEvent),true);
              assert.equal(tabEvent.prevented,true);
              assert.equal(focused,'first','Tab from the final control wraps to the first dialog control');

              const header={inert:false,hidden:false,setAttribute(name,value){if(name==='aria-hidden')this.hidden=value;}};
              const pageContent={inert:false,hidden:false,matches(){return false;},setAttribute(name,value){if(name==='aria-hidden')this.hidden=value;}};
              const candidateFocus={focus(){focused='candidate';}};
              const a11yDialog={
                querySelector(){return candidateFocus;},querySelectorAll(){return [candidateFocus];}
              };
              const a11yRoot={querySelector(selector){
                if(selector==='.models-editor')return a11yDialog;
                if(selector==='.sub-header')return header;
                if(selector==='.models-page')return {children:[pageContent]};
                return null;
              }};
              MODELS_STATE.editor={focusTarget:'candidate'};
              modelsCatalogApplyDialogAccessibility(a11yRoot);
              assert.equal(focused,'candidate','opening the dialog focuses its candidate selector');
              assert.equal(header.inert,true);assert.equal(header.hidden,'true');
              assert.equal(pageContent.inert,true);assert.equal(pageContent.hidden,'true');

              const returnButton={focus(){focused='return';}};
              MODELS_STATE.editor=null;MODELS_RETURN_FOCUS='[data-models-edit="2"]';
              modelsCatalogApplyDialogAccessibility({querySelector(selector){
                if(selector==='.models-editor')return null;
                if(selector==='[data-models-edit="2"]')return returnButton;
                return null;
              }});
              assert.equal(focused,'return','closing the dialog restores the originating row action');
              assert.equal(MODELS_RETURN_FOCUS,'');

              const fakeRoot = {
                dataset:{}, listenerCount:0,
                addEventListener(){ this.listenerCount += 1; }
              };
              mountModelsCatalog(fakeRoot);
              mountModelsCatalog(fakeRoot);
              assert.equal(fakeRoot.listenerCount, 4,
                'the stable view root must receive one click/change/input/keydown listener set');
            `, context);

            const ownerState = {
              loaded:true, loading:false, forbidden:false, error:null, request:0,
              inventoryFresh:true, draftBaseVersion:7, conflict:false,
              catalog:{stateVersion:7}, draft:null, candidates:[], dirty:false,
              pending:{baseVersion:7,mutationId:'mutation-observe'}, notice:null
            };
            context.modelsCatalogSyncAuthority = () => 'scope-alpha';
            context.modelsCatalogOwnerState = () => ownerState;
            context.modelsCatalogEndpoint = (_, candidates) => candidates ? '/candidates' : '/catalog';
            context.modelsCatalogRenderIfActive = () => {};
            context.modelsCatalogErrorMessage = error => String(error && error.body || error);
            context.responses = [];
            context.discoveryRequestPaths = [];
            context.modelsCatalogResponse = async path => {
              context.discoveryRequestPaths.push(path);
              return context.responses.shift();
            };
            vm.runInContext(
              sourceBetween('async function loadModelsCatalog(', 'function modelsCatalogCandidateIdentity('),
              context);

            (async function() {
              context.responses.push(
                {forbidden:false,status:200,body:{mode:'custom_replace',stateVersion:7,lastMutationId:'prior',sources:[]}},
                {forbidden:false,status:200,body:{services:[]}});
              await context.loadModelsCatalog('scope', {keepNotice:true});
              assert.notEqual(ownerState.pending, null,
                'an accepted mutation is not observed while stateVersion is unchanged');
              assert.equal(ownerState.notice.tone, 'waiting');

              context.responses.push(
                {forbidden:false,status:200,body:{mode:'custom_replace',stateVersion:8,lastMutationId:'mutation-other',sources:[]}},
                {forbidden:false,status:200,body:{services:[]}});
              await context.loadModelsCatalog('scope', {keepNotice:true});
              assert.equal(ownerState.pending, null);
              assert.equal(ownerState.notice.tone, 'failed');
              assert.match(ownerState.notice.text, /另一项更新取代/,
                'a newer state with another mutation must not confirm our write');

              ownerState.pending = {baseVersion:8,mutationId:'mutation-observe'};
              context.responses.push(
                {forbidden:false,status:200,body:{mode:'custom_replace',stateVersion:9,lastMutationId:'mutation-observe',sources:[]}},
                {forbidden:false,status:200,body:{services:[]}});
              await context.loadModelsCatalog('scope', {keepNotice:true});
              assert.equal(ownerState.pending, null);
              assert.equal(ownerState.notice.tone, 'success');
              assert.match(ownerState.notice.text, /stateVersion 9/);

              ownerState.dirty = false;
              ownerState.draft = {marker:'server-before-refresh'};
              let resolvePolicy;
              let resolveCandidates;
              context.responses.push(
                new Promise(resolve => { resolvePolicy = resolve; }),
                new Promise(resolve => { resolveCandidates = resolve; }));
              const refresh = context.loadModelsCatalog('scope', {});
              ownerState.dirty = true;
              ownerState.draft = {marker:'local-edit-during-refresh'};
              resolvePolicy({forbidden:false,status:200,body:{mode:'custom_replace',stateVersion:10,lastMutationId:'remote',sources:[]}});
              resolveCandidates({forbidden:false,status:200,body:{services:[]}});
              await refresh;
              assert.equal(ownerState.catalog.stateVersion, 10);
              assert.equal(ownerState.draft.marker, 'local-edit-during-refresh',
                'a refresh response must not overwrite a draft edited while the request was in flight');
              assert.equal(ownerState.dirty, true);
              assert.equal(ownerState.inventoryFresh, true);
              assert.equal(ownerState.draftBaseVersion, 9,
                'the preserved draft must remain based on the version loaded before the refresh');
              assert.equal(ownerState.conflict, true,
                'a newer remote version must force explicit conflict recovery instead of rebasing the draft');

              ownerState.pending = {baseVersion:9,mutationId:'mutation-forbidden'};
              context.MODELS_STATE.editor = {owner:'scope'};
              context.responses.push(
                {forbidden:true,status:403,body:null},
                {forbidden:false,status:200,body:{services:[]}});
              await context.loadModelsCatalog('scope', {discardDraft:true});
              assert.equal(ownerState.forbidden, true);
              assert.equal(ownerState.loaded, false);
              assert.equal(ownerState.catalog, null);
              assert.equal(ownerState.draft, null);
              assert.equal(ownerState.dirty, false,
                'a forbidden reload must not leave an invisible dirty draft that blocks retry');
              assert.equal(ownerState.draftBaseVersion, null);
              assert.equal(ownerState.conflict, false);
              assert.equal(ownerState.pending, null);
              assert.equal(ownerState.notice, null);
              assert.equal(context.MODELS_STATE.editor, null);

              context.responses.push(
                {forbidden:false,status:200,body:{mode:'custom_replace',stateVersion:11,lastMutationId:'remote',sources:[]}},
                {forbidden:false,status:200,body:{services:[]}});
              await context.loadModelsCatalog('scope', {});
              assert.equal(ownerState.forbidden, false);
              assert.equal(ownerState.loaded, true,
                'retry must recover after authorization becomes available again');
              assert.equal(ownerState.draftBaseVersion, 11);

              ownerState.draft = {mode:'custom_replace',sources:[]};
              ownerState.candidates = [
                context.modelsCatalogNormalizeCandidate({
                  userServiceId:'user-alpha',catalogServiceId:'catalog-alpha',serviceSlug:'chrono-alpha',
                  displayName:'Chrono Alpha',isCallable:true,availabilityReason:'available'
                }),
                context.modelsCatalogNormalizeCandidate({
                  userServiceId:'user-beta',catalogServiceId:'catalog-beta',serviceSlug:'chrono-beta',
                  displayName:'Chrono Beta',isCallable:true,availabilityReason:'available'
                })
              ];
              context.MODELS_STATE.editor = {
                owner:'scope',index:null,persistedKey:'',candidateKey:'user-alpha',error:null,
                source:context.modelsCatalogNormalizeSource({
                  sourceId:'source-discovery',userServiceId:'user-alpha',catalogServiceId:'catalog-alpha',
                  serviceSlugSnapshot:'chrono-alpha',
                  modelSelection:{mode:'explicit_models',modelIds:['alpha-manual']}
                },'scope'),
                discovery:context.modelsCatalogNewDiscoveryState()
              };
              let resolveAlphaDiscovery;
              context.responses.push(new Promise(resolve => { resolveAlphaDiscovery = resolve; }));
              const alphaDiscovery = context.modelsCatalogDiscoverModels();
              context.modelsCatalogSelectCandidate('user-beta');
              resolveAlphaDiscovery({forbidden:false,status:200,body:{
                sourceIdentity:'user-alpha',serviceSlug:'chrono-alpha',modelIds:['alpha-remote']
              }});
              await alphaDiscovery;
              assert.equal(context.MODELS_STATE.editor.candidateKey,'user-beta');
              assert.equal(context.MODELS_STATE.editor.discovery.loaded,false);
              assert.deepEqual(Array.from(context.MODELS_STATE.editor.discovery.modelIds),[],
                'a completed request for the prior candidate must not write into the new candidate');
              assert.equal(context.discoveryRequestPaths.at(-1),
                '/catalog/candidates/user-alpha/models');

              context.responses.push({forbidden:false,status:200,body:{
                sourceIdentity:'user-wrong',serviceSlug:'chrono-beta',modelIds:['wrong-model']
              }});
              await context.modelsCatalogDiscoverModels();
              assert.match(context.MODELS_STATE.editor.discovery.error,/sourceIdentity/,
                'discovery errors remain local to the editor discovery state');
              assert.equal(context.MODELS_STATE.editor.error,null);

              context.responses.push({forbidden:false,status:200,body:{
                sourceIdentity:'user-beta',serviceSlug:'chrono-beta',
                defaultModelId:'gpt-5.4',modelIds:['gpt-5.5','gpt-5.4','gpt-5.5']
              }});
              await context.modelsCatalogDiscoverModels();
              assert.equal(context.MODELS_STATE.editor.discovery.error,null);
              assert.equal(context.MODELS_STATE.editor.discovery.loaded,true);
              assert.deepEqual(Array.from(context.MODELS_STATE.editor.discovery.modelIds),
                ['gpt-5.4','gpt-5.5'],
                'retry stores a unique stable model list for the exact candidate');
              assert.equal(context.MODELS_STATE.editor.discovery.defaultModelId,'gpt-5.4');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldExposeStructuredLoginModuleAndHonestStates()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("'agent-profiles':{name:'Agent Profile', auth:'login'");
        html.Should().Contain("items:['studio','agent-profiles','skills','schedules']");
        html.Should().Contain("agentProfileField('显示名称','displayName'");
        html.Should().Contain("agentProfileField('Instructions','instructions'");
        html.Should().Contain("agentProfileSelect('Activation mode','activationMode'");
        html.Should().Contain("data-ap-exact-evidence");
        html.Should().Contain("data-ap-tool-chip");
        html.Should().Contain("<details class=\"ap-disclosure\"");
        html.Should().Contain("AGENT_PROFILE_STATE.skillProofs");
        html.Should().NotContain("agentProfileField('Exact skill GUID','exactSkillGuid'");
        html.Should().Contain("data-ap-open-skills");
        html.Should().Contain("data-ap-replace-skill");
        html.Should().Contain("agentProfileSkillModalHtml()");
        html.Should().Contain("data-ap-skill-confirm");
        html.Should().Contain("agentProfileOpenSkillModal(root,'add'");
        html.Should().Contain("ev.target.matches('[data-ap-skill-choice]')");
        html.Should().NotContain("var skillOption=ev.target.closest('[data-ap-skill-option]')");
        html.Should().NotContain("data-ap-skill-search");
        html.Should().Contain("/api/workflow/skills/'+encodeURIComponent(guid)+'/exact");
        html.Should().Contain("loadAgentProfileBindings(owner,request)");
        html.Should().Contain("AGENT_PROFILE_STATE.systemBinding&&AGENT_PROFILE_STATE.systemBinding.etag");
        html.Should().Contain("agentProfileField('Maximum tools','maximumTools'");
        html.Should().Contain("仅影响新建实例");
        html.Should().Contain("已接受，等待提交/投影");
        html.Should().Contain("其他人已修改此 Profile");
        html.Should().Contain("投影暂时不可用");
        html.Should().Contain("window.addEventListener('beforeunload',agentProfileBeforeUnload)");
        html.Should().Contain("data-ap-start-create");
        html.Should().Contain("data-ap-create-submit");
        html.Should().Contain("data-ap-create-cancel");
        html.Should().Contain("定义职责");
        html.Should().Contain("选择能力");
        html.Should().Contain("检查并创建");
        html.Should().NotContain("data-ap-new-slug");
        html.Should().Contain("@media (max-width:768px)");
        html.Should().NotContain("data-ap-field=\"rawJson\"");
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldSwitchOwnersFromCacheAndIgnoreStaleRefreshes()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            function deferred() {
              let resolve, reject;
              const promise = new Promise((ok, fail) => { resolve = ok; reject = fail; });
              return {promise, resolve, reject};
            }
            async function waitForRequestCount(count) {
              while (requests.length < count) await new Promise(resolve => setImmediate(resolve));
            }

            const requests = [], renders = [];
            const context = {
              structuredClone,
              ACCOUNT:{admin:true,scope:'scope-alpha'},
              AGENT_PROFILE_REQUEST:0,
              AGENT_PROFILE_AUTHORITY:'scope-alpha|true',
              AGENT_PROFILE_OWNER_SNAPSHOTS:{mine:null,system:null},
              AGENT_PROFILE_STATE:{owner:'mine',status:'all',search:'',items:[],selected:null,
                detail:null,loaded:false,loading:false,error:null,forbidden:false,dirty:false,
                createFlow:null,etag:null,binding:null,systemBinding:null,rolloutDraft:null,
                diagnostics:[],skillProofs:{}},
              render() {
                const state = context.AGENT_PROFILE_STATE;
                renders.push({owner:state.owner,selected:state.selected,
                  detail:state.detail && state.detail.displayName});
              },
              agentProfileResetSkillSearch() {},
              agentProfileScope() { return 'scope-alpha'; },
              agentProfileCollectionEndpoint() {
                return context.AGENT_PROFILE_STATE.owner === 'system'
                  ? '/api/admin/agent-profiles'
                  : '/api/scopes/scope-alpha/agent-profiles';
              },
              agentProfileItemEndpoint(item) {
                return item.ownerKind === 'system'
                  ? '/api/admin/agent-profiles/' + item.profileSlug
                  : '/api/scopes/scope-alpha/agent-profiles/' + item.profileSlug;
              },
              agentProfileProblem(status) { return {kind:'error',title:'HTTP ' + status}; },
              agentProfileNormalizeItem(item, owner) {
                return Object.assign({ownerKind:owner === 'system' ? 'system' : 'scope',
                  profileId:'',profileSlug:'',displayName:'',purpose:'',publishedRevision:0,
                  available:true,isDefault:false,etag:null}, item);
              },
              agentProfileReconcilePending() {},
              agentProfileAdvanceCreate() { return Promise.resolve(false); },
              agentProfileCanWrite() { return true; },
              agentProfileRuntime() {
                return {activationMode:'SHADOW',members:[],maximumToolPolicy:{},
                  recoveryToolPolicy:{}};
              },
              agentProfileEmptyDraft() { return {runtimeProfile:{members:[]}}; },
              agentProfileUnionNames(existing, additions) {
                return [...new Set([...(existing || []), ...(additions || [])])];
              },
              agentProfileRolloutFromBinding() {
                return {enabled:false,cohortBasisPoints:0};
              },
              agentProfileStatus() { return ''; },
              agentProfileSkillsSectionHtml(_, disabled) {
                return '<span data-skills-disabled="' + disabled + '"></span>';
              },
              agentProfileDiagnosticsHtml() { return ''; },
              agentProfileLifecycleState() {
                return {label:'idle',tone:'draft',description:'idle'};
              },
              agentProfilePublicSummaryHtml() { return ''; },
              agentProfileCreateHtml() { return ''; },
              esc(value) { return String(value == null ? '' : value); },
              agentProfileJson(path) {
                const request = deferred();
                requests.push(Object.assign({path}, request));
                return request.promise;
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileRequestIsCurrent', 'agentProfileSaveOwnerSnapshot')}
              ${functionSource('agentProfileSaveOwnerSnapshot', 'agentProfileRestoreOwnerSnapshot')}
              ${functionSource('agentProfileRestoreOwnerSnapshot', 'agentProfileSwitchOwner')}
              ${functionSource('agentProfileSwitchOwner', 'agentProfileResetSkillSearch')}
              ${functionSource('loadAgentProfileBindings', 'agentProfileApplyBinding')}
              ${functionSource('agentProfileApplyBinding', 'loadAgentProfiles')}
              ${functionSource('loadAgentProfiles', 'loadAgentProfileDetail')}
              ${functionSource('loadAgentProfileDetail', 'agentProfileRows')}
              ${functionSource('agentProfileField', 'agentProfileSelect')}
              ${functionSource('agentProfileSelect', 'agentProfileHidden')}
              ${functionSource('agentProfileActionBarHtml', 'agentProfileRefreshActionState')}
              ${functionSource('agentProfileEditorHtml', 'agentProfileCollectFields')}
            `, context);

            vm.runInContext(`
              AGENT_PROFILE_STATE.items=[{ownerKind:'scope',profileId:'mine-cached-id',
                profileSlug:'mine-cached',displayName:'Mine cached'}];
              AGENT_PROFILE_STATE.selected='mine-cached';
              AGENT_PROFILE_STATE.detail={ownerKind:'scope',profileId:'mine-cached-id',
                profileSlug:'mine-cached',displayName:'Mine cached'};
              AGENT_PROFILE_STATE.etag='mine-etag';
              AGENT_PROFILE_STATE.binding={target:null};
              AGENT_PROFILE_STATE.loaded=true;
              agentProfileSaveOwnerSnapshot('mine');

              AGENT_PROFILE_STATE.owner='system';
              AGENT_PROFILE_STATE.items=[{ownerKind:'system',profileId:'system-cached-id',
                profileSlug:'system-cached',displayName:'System cached'}];
              AGENT_PROFILE_STATE.selected='system-cached';
              AGENT_PROFILE_STATE.detail={ownerKind:'system',profileId:'system-cached-id',
                profileSlug:'system-cached',displayName:'System cached'};
              AGENT_PROFILE_STATE.etag='system-etag';
              AGENT_PROFILE_STATE.loaded=true;
              agentProfileSaveOwnerSnapshot('system');
              AGENT_PROFILE_STATE.loading=true;
              var refreshingSystemEditor=agentProfileEditorHtml();
              if(!refreshingSystemEditor.includes('data-ap-field="rolloutEnabled" disabled'))
                throw new Error('cached system rollout fields stay read-only while refreshing');
              if(!refreshingSystemEditor.includes('data-ap-field="cohortBasisPoints" disabled'))
                throw new Error('cached cohort field stays read-only while refreshing');
              AGENT_PROFILE_STATE.loading=false;
              agentProfileRestoreOwnerSnapshot('mine');
            `, context);
            context.AGENT_PROFILE_STATE.loading = true;
            const refreshingEditor = vm.runInContext('agentProfileEditorHtml()', context);
            assert.match(refreshingEditor, /data-ap-field="displayName"[^>]* disabled/,
              'cached editor fields stay read-only until the authoritative refresh settles');
            assert.match(refreshingEditor, /data-ap-action="save" disabled/,
              'cached editor actions stay disabled until the authoritative refresh settles');
            context.AGENT_PROFILE_STATE.loading = false;

            (async function() {
              const systemLoad = vm.runInContext("agentProfileSwitchOwner('system')", context);
              assert.equal(context.AGENT_PROFILE_STATE.detail.displayName, 'System cached');
              assert.equal(renders.at(-1).detail, 'System cached');
              assert.deepEqual(requests.slice(0, 3).map(request => request.path), [
                '/api/admin/agent-profiles?take=100',
                '/api/scopes/scope-alpha/agent-profile-bindings/nyxid.chat',
                '/api/admin/agent-profile-bindings/nyxid.chat'
              ]);

              requests[0].resolve({body:{items:[{ownerKind:'system',
                profileId:'stale-system-id',profileSlug:'stale-system',
                displayName:'Stale system'}]}});
              await waitForRequestCount(4);
              assert.equal(requests.length, 4);
              assert.equal(requests[3].path, '/api/admin/agent-profiles/stale-system');

              const mineLoad = vm.runInContext("agentProfileSwitchOwner('mine')", context);
              assert.equal(context.AGENT_PROFILE_STATE.detail.displayName, 'Mine cached');
              assert.equal(requests.length, 7, 'the next owner refresh starts without waiting');

              requests[1].resolve({body:{target:{profileId:'stale-system-id'}}});
              requests[2].resolve({body:{target:{profileId:'stale-system-id'}}});
              requests[3].resolve({body:{displayName:'Stale system detail'},
                etag:'stale-system-etag'});
              await systemLoad;
              assert.equal(context.AGENT_PROFILE_STATE.owner, 'mine');
              assert.equal(context.AGENT_PROFILE_STATE.detail.displayName, 'Mine cached');
              assert.equal(context.AGENT_PROFILE_STATE.items[0].profileSlug, 'mine-cached');

              requests[4].resolve({body:{items:[{ownerKind:'scope',profileId:'mine-fresh-id',
                profileSlug:'mine-fresh',displayName:'Mine fresh'}]}});
              await waitForRequestCount(8);
              assert.equal(requests.length, 8,
                'detail starts after the list without waiting for either binding');
              assert.equal(requests[7].path,
                '/api/scopes/scope-alpha/agent-profiles/mine-fresh');

              requests[7].resolve({body:{displayName:'Mine fresh detail'},etag:'mine-fresh-etag'});
              requests[5].resolve({body:{target:{ownerKind:'scope',profileId:'mine-fresh-id'}}});
              requests[6].resolve({body:{target:null}});
              await mineLoad;

              assert.equal(context.AGENT_PROFILE_STATE.loading, false);
              assert.equal(context.AGENT_PROFILE_STATE.items[0].profileSlug, 'mine-fresh');
              assert.equal(context.AGENT_PROFILE_STATE.items[0].isDefault, true);
              assert.equal(context.AGENT_PROFILE_STATE.detail.displayName, 'Mine fresh detail');
              assert.equal(context.AGENT_PROFILE_STATE.etag, 'mine-fresh-etag');

              context.ACCOUNT.scope = 'scope-beta';
              const staleAuthorityRequest = context.AGENT_PROFILE_REQUEST;
              assert.equal(vm.runInContext('agentProfileSyncAuthority()', context), true);
              assert.equal(vm.runInContext(
                `agentProfileRequestIsCurrent('mine', ${staleAuthorityRequest})`, context),
                false, 'authority changes invalidate already in-flight reads');
              assert.equal(context.AGENT_PROFILE_STATE.items.length, 0);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldResolveOwnerAuthorityAndTypedProblems()
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

            const context = {};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileOwnerEndpoint', 'agentProfileCanWrite')}
              ${functionSource('agentProfileCanWrite', 'agentProfileProblem')}
              ${functionSource('agentProfileProblem', 'agentProfileDraftFromFields')}
              ${functionSource('agentProfileDraftFromFields', 'agentProfileEmptyDraft')}
            `, context);

            assert.equal(
              vm.runInContext("agentProfileOwnerEndpoint('mine','scope-alpha')", context),
              '/api/scopes/scope-alpha/agent-profiles');
            assert.equal(
              vm.runInContext("agentProfileOwnerEndpoint('system','scope-alpha')", context),
              '/api/agent-profiles/system');
            assert.equal(
              vm.runInContext("agentProfileCanWrite({ownerKind:'system'},{admin:false})", context),
              false);
            assert.equal(
              vm.runInContext("agentProfileCanWrite({ownerKind:'system'},{admin:true})", context),
              true);
            assert.equal(
              vm.runInContext("agentProfileCanWrite({ownerKind:'scope'},{admin:false})", context),
              true);
            assert.equal(vm.runInContext('agentProfileProblem(412)', context).kind, 'stale');
            assert.equal(vm.runInContext('agentProfileProblem(409)', context).kind, 'conflict');
            assert.match(vm.runInContext('agentProfileProblem(409)', context).title, /slug/);
            assert.equal(vm.runInContext('agentProfileProblem(422)', context).kind, 'validation');
            assert.equal(vm.runInContext('agentProfileProblem(503)', context).kind, 'unavailable');

            const draft = vm.runInContext(`agentProfileDraftFromFields({
              displayName:'Research', purpose:'Read evidence', instructions:'Cite sources',
              activationMode:'ENFORCED', exactSkillGuid:'11111111-1111-1111-1111-111111111111',
              literalVersion:'1.2', expectedSkillName:'research', reviewedPublisherId:'publisher-alpha',
              maximumTools:'web_search, fetch_url', taskTools:'web_search', maxPlanSteps:'4'
            })`, context);
            assert.equal(draft.displayName, 'Research');
            assert.equal(draft.runtimeProfile.activationMode, 'ENFORCED');
            assert.equal(draft.runtimeProfile.maximumToolPolicy.toolNames.join(','), 'web_search,fetch_url');
            assert.equal(draft.runtimeProfile.members[0].skillRef.literalVersion, '1.2');
            assert.equal(draft.runtimeProfile.members[0].taskToolPolicy.toolNames.join(','), 'web_search');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldPreserveWorkingDraftAndRestoreSystemRollout()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(function(index) { return index !== -1; });
              const start = starts.length ? Math.min.apply(null, starts) : -1;
              const nextStarts = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(function(index) { return index !== -1; });
              const end = nextStarts.length ? Math.min.apply(null, nextStarts) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileDraftFromFields', 'agentProfileEmptyDraft')}
              ${functionSource('agentProfileEmptyDraft', 'agentProfileRolloutFromBinding')}
              ${functionSource('agentProfileRolloutFromBinding', 'agentProfileApplyExactSkill')}
              ${functionSource('agentProfileApplyExactSkill', 'agentProfileCaptureDraft')}
            `, context);

            const rollout = vm.runInContext(
              "agentProfileRolloutFromBinding({enabled:false,cohortBasisPoints:2500,previousReviewedTarget:{profileId:'prof-previous'}})",
              context);
            assert.equal(rollout.enabled, false);
            assert.equal(rollout.cohortBasisPoints, 2500);
            assert.equal(rollout.previousReviewedTarget.profileId, 'prof-previous');

            const draft = vm.runInContext(`agentProfileDraftFromFields({
              displayName:'Unsaved name', instructions:'Unsaved instructions',
              exactSkillGuid:'11111111-1111-1111-1111-111111111111', literalVersion:'1.0',
              expectedSkillName:'old-skill', reviewedPublisherId:'old-publisher',
              maximumTools:'web_search', taskTools:'manual_task', intentId:'primary'
            })`, context);
            context.draft = draft;
            context.exact = {
              guid:'22222222-2222-4222-8222-222222222222', literalVersion:'2.3',
              name:'new-skill', publisher:'new-publisher',
              skillHash:'000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f',
              declaredToolNames:['lookup',' search ','lookup']
            };
            const updated = vm.runInContext('agentProfileApplyExactSkill(draft, exact)', context);
            assert.equal(updated.displayName, 'Unsaved name');
            assert.equal(updated.instructions, 'Unsaved instructions');
            assert.deepEqual(
              Array.from(updated.runtimeProfile.maximumToolPolicy.toolNames),
              ['web_search','lookup','search']);
            assert.deepEqual(
              Array.from(updated.runtimeProfile.members[0].taskToolPolicy.toolNames),
              ['manual_task','lookup','search']);
            assert.equal(updated.runtimeProfile.members[0].skillRef.literalVersion, '2.3');
            assert.equal(updated.runtimeProfile.members[0].expectedSkillName, 'new-skill');

            const openSource = functionSource('agentProfileOpenSkillModal', 'agentProfileCloseSkillModal');
            assert.ok(openSource.indexOf('agentProfileCaptureDraft(root);') >= 0);
            assert.ok(openSource.indexOf('agentProfileCaptureDraft(root);') < openSource.indexOf('render();'));
            const confirmSource = functionSource('agentProfileConfirmSkillSelection', 'agentProfilePublicSummaryHtml');
            assert.ok(confirmSource.indexOf('agentProfileCaptureDraft(root);') >= 0);
            assert.ok(confirmSource.indexOf('agentProfileCaptureDraft(root);') < confirmSource.indexOf('render();'));
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldDiscardStaleExactSkillResponses()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {
              AGENT_PROFILE_REQUEST:0,
              AGENT_PROFILE_STATE:{
                detail:{draft:{runtimeProfile:{maximumToolPolicy:{toolNames:[],toolSetRefs:[]},
                  members:[{intentId:'old',skillRef:{guid:'old-guid',literalVersion:'1.0'},
                    taskToolPolicy:{toolNames:[],toolSetRefs:[]}}]}}},
                skillRequest:0, skillModal:null, skillProofs:{}, skillCardsOpen:{}
              },
              root:{querySelector(){return null;}},
              render() {},
              agentProfileCaptureDraft() {},
              agentProfileWorkingDraft() {
                return context.AGENT_PROFILE_STATE.detail && context.AGENT_PROFILE_STATE.detail.draft;
              },
              agentProfileJson() {
                return new Promise(resolve => { context.resolveExact = resolve; });
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileResetSkillSearch', 'agentProfileOwnerEndpoint')}
              ${functionSource('agentProfileOpenSkillModal', 'agentProfilePublicSummaryHtml')}
            `, context);

            (async function() {
              vm.runInContext("agentProfileOpenSkillModal(root,'replace',0)", context);
              vm.runInContext("agentProfileToggleSkillSelection('new-guid')", context);
              const pending = vm.runInContext('agentProfileConfirmSkillSelection(root)', context);
              vm.runInContext(
                'agentProfileResetSkillSearch(); AGENT_PROFILE_STATE.detail = null', context);
              context.resolveExact({body:{
                guid:'new-guid', literalVersion:'2.3',
                name:'new-skill', publisher:'new-publisher'
              }});
              await pending;

              assert.equal(context.AGENT_PROFILE_STATE.detail, null);
              assert.equal(context.AGENT_PROFILE_STATE.skillModal, null);
              assert.deepEqual(Object.keys(context.AGENT_PROFILE_STATE.skillProofs), []);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldRoundTripEveryMember()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = html.indexOf('function ' + name + '(');
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileDraftFromFields', 'agentProfileEmptyDraft')}
              ${functionSource('agentProfileCollectFields', 'agentProfileLocalDiagnostics')}
            `, context);

            function field(name, value, member) {
              return {
                value,
                getAttribute(attribute) {
                  if (attribute === 'data-ap-field') return name;
                  if (attribute === 'data-ap-member') return member == null ? null : String(member);
                  return null;
                }
              };
            }
            const elements = [
              field('displayName', 'Research team'),
              field('instructions', 'Use the matching specialist'),
              field('activationMode', 'ENFORCED'),
              field('maxPlanSteps', '4'),
              field('intentId', 'research', 0),
              field('exactSkillGuid', '11111111-1111-4111-8111-111111111111', 0),
              field('literalVersion', '1.2', 0),
              field('expectedSkillName', 'research', 0),
              field('reviewedPublisherId', 'publisher-a', 0),
              field('taskTools', 'web_search', 0),
              field('sideEffectClass', 'READ_ONLY', 0),
              field('intentId', 'writer', 1),
              field('exactSkillGuid', '22222222-2222-4222-8222-222222222222', 1),
              field('literalVersion', '2.3', 1),
              field('expectedSkillName', 'writer', 1),
              field('reviewedPublisherId', 'publisher-b', 1),
              field('taskTools', 'document_write', 1),
              field('sideEffectClass', 'SERVICE_CALL', 1)
            ];
            context.root = { querySelectorAll() { return elements; } };
            const fields = vm.runInContext('agentProfileCollectFields(root)', context);
            context.fields = fields;
            const draft = vm.runInContext('agentProfileDraftFromFields(fields)', context);
            assert.equal(fields.members.length, 2);
            assert.equal(draft.runtimeProfile.members.length, 2);
            assert.equal(draft.runtimeProfile.members[0].intentId, 'research');
            assert.equal(draft.runtimeProfile.members[1].intentId, 'writer');
            assert.equal(draft.runtimeProfile.members[1].skillRef.literalVersion, '2.3');
            assert.deepEqual(
              Array.from(draft.runtimeProfile.members[1].taskToolPolicy.toolNames),
              ['document_write']);

            context.AGENT_PROFILE_STATE = {
              detail:{
                ownerKind:'scope', profileSlug:'research-team', displayName:'Research team',
                draft, publishedRevision:1, authorityStateVersion:3
              },
              diagnostics:[], rolloutDraft:null, systemBinding:null, pending:null,
              busy:false, notice:null, error:null, etag:'etag-3', skillMemberIndex:null,
              skillResults:[], skillLoading:false, skillError:null,
              skillProofs:{
                0:{skillHash:'aaaaaaaaaaaa0000',declaredToolNames:['web_search']},
                1:{skillHash:'bbbbbbbbbbbb0000',declaredToolNames:['document_write']}
              }
            };
            context.ACCOUNT = { admin:false };
            context.ICON = { search:'search' };
            context.esc = value => String(value == null ? '' : value);
            vm.runInContext(`
              ${functionSource('agentProfileCanWrite', 'agentProfileProblem')}
              ${functionSource('agentProfileEmptyDraft', 'agentProfileRolloutFromBinding')}
              ${functionSource('agentProfileRolloutFromBinding', 'agentProfileApplyExactSkill')}
              ${functionSource('agentProfileStatus', 'agentProfileListHtml')}
              ${functionSource('agentProfileField', 'agentProfileSelect')}
              ${functionSource('agentProfileSelect', 'agentProfileRuntime')}
              ${functionSource('agentProfileRuntime', 'agentProfileOpenSkillModal')}
              ${functionSource('agentProfileDiagnosticsHtml', 'agentProfileOpenSkillModal')}
              ${functionSource('agentProfileLifecycleState', 'agentProfilePublicSummaryHtml')}
              ${functionSource('agentProfileEditorHtml', 'agentProfileCollectFields')}
              ${functionSource('agentProfileLocalDiagnostics', 'agentProfileMutation')}
            `, context);
            const editor = vm.runInContext('agentProfileEditorHtml()', context);
            assert.match(editor, /data-ap-member-card="0"/);
            assert.match(editor, /data-ap-member-card="1"/);
            assert.match(editor, /id="ap-intentId-0"/);
            assert.match(editor, /id="ap-intentId-1"/);
            assert.match(editor, /data-ap-exact-evidence="0"/);
            assert.match(editor, /data-ap-exact-evidence="1"/);
            assert.match(editor, /type="hidden" data-ap-field="exactSkillGuid" data-ap-member="0"/);
            assert.match(editor, /type="hidden" data-ap-field="literalVersion" data-ap-member="1"/);
            assert.match(editor, /data-ap-tool-chip="web_search"/);
            assert.match(editor, /data-ap-tool-chip="document_write"/);
            assert.match(editor, /aaaaaaaaaaaa/);
            assert.match(editor, /bbbbbbbbbbbb/);
            assert.match(editor, /<details class="ap-disclosure"/);
            assert.doesNotMatch(editor, /id="ap-exactSkillGuid-0" type="text"/);
            assert.match(editor, /id="ap-maxPlanSteps"[^>]*readonly/);

            context.AGENT_PROFILE_STATE.createFlow = {
              owner:'mine',slug:'research-team',slugTouched:true,draft,stage:'catalog'
            };
            context.AGENT_PROFILE_STATE.pending = null;
            vm.runInContext(
              `${functionSource('agentProfileCreateHtml', 'agentProfileEditorHtml')}`, context);
            const timedOutCreate = vm.runInContext('agentProfileCreateHtml()', context);
            assert.match(timedOutCreate, /data-ap-create-cancel(?! disabled)/);
            assert.match(timedOutCreate, /data-ap-create-submit disabled/);

            const invalid = JSON.parse(JSON.stringify(draft));
            invalid.runtimeProfile.members[1].expectedSkillName = '';
            invalid.runtimeProfile.members[1].reviewedPublisherId = '';
            invalid.runtimeProfile.maxPlanSteps = 3;
            context.invalid = invalid;
            const diagnostics = vm.runInContext('agentProfileLocalDiagnostics(invalid)', context);
            assert.ok(diagnostics.some(d =>
              d.code === 'EXPECTED_SKILL_NAME_REQUIRED' && d.field === 'members[1].expectedSkillName'));
            assert.ok(diagnostics.some(d =>
              d.code === 'PUBLISHER_REQUIRED' && d.field === 'members[1].reviewedPublisherId'));
            assert.ok(diagnostics.some(d => d.code === 'PROFILE_MAX_PLAN_STEPS_INVALID'));
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldRenderIntentionalEmptyStateAndCollapsibleSkillCards()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {
              AGENT_PROFILE_STATE:{
                diagnostics:[], skillCardsOpen:{0:false,1:true}, skillProofs:{}
              },
              esc(value) { return String(value == null ? '' : value); },
              agentProfileField(label, name, value) {
                return '<label>' + label + '<input data-ap-field="' + name + '" value="' + value + '"></label>';
              },
              agentProfileSelect(label, name, value) {
                return '<label>' + label + '<select data-ap-field="' + name + '"><option>' + value + '</option></select></label>';
              },
              agentProfileHidden(name, value, index) {
                return '<input type="hidden" data-ap-field="' + name + '" data-ap-member="' + index + '" value="' + value + '">';
              },
              agentProfileExactEvidenceHtml(member, index) {
                return '<div data-ap-exact-evidence="' + index + '">' + member.skillRef.guid + '</div>';
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileDraftFromFields', 'agentProfileEmptyDraft')}
              ${functionSource('agentProfileEmptyDraft', 'agentProfileSlugFromName')}
              ${functionSource('agentProfileSkillCardIsOpen', 'agentProfileSkillCardHtml')}
              ${functionSource('agentProfileSkillCardHtml', 'agentProfileSkillsSectionHtml')}
              ${functionSource('agentProfileSkillsSectionHtml', 'agentProfileDiagnosticsHtml')}
            `, context);

            const emptyDraft = vm.runInContext('agentProfileEmptyDraft()', context);
            assert.equal(emptyDraft.runtimeProfile.members.length, 0);
            const empty = vm.runInContext('agentProfileSkillsSectionHtml([], false)', context);
            assert.match(empty, /还没有添加 Skill/);
            assert.match(empty, /data-ap-open-skills="add"/);

            context.members = [{
              intentId:'research', routingDescription:'Find evidence',
              skillRef:{guid:'11111111-1111-4111-8111-111111111111',literalVersion:'1.2'},
              explicitTriggerAliases:['research'], taskToolPolicy:{toolNames:['web_search'],toolSetRefs:[]},
              sideEffectClass:'READ_ONLY', expectedSkillName:'research', reviewedPublisherId:'publisher-a'
            }, {
              intentId:'writer', routingDescription:'Write a response',
              skillRef:{guid:'22222222-2222-4222-8222-222222222222',literalVersion:'2.3'},
              explicitTriggerAliases:['write'], taskToolPolicy:{toolNames:['document_write'],toolSetRefs:[]},
              sideEffectClass:'SERVICE_CALL', expectedSkillName:'writer', reviewedPublisherId:'publisher-b'
            }];
            const first = vm.runInContext('agentProfileSkillCardHtml(members[0], 0, 2, false)', context);
            const second = vm.runInContext('agentProfileSkillCardHtml(members[1], 1, 2, false)', context);
            assert.match(first, /<details class="ap-skill-card"[^>]*data-ap-member-card="0"/);
            assert.doesNotMatch(first, /<details class="ap-skill-card"[^>]* open/);
            assert.match(second, /<details class="ap-skill-card"[^>]*data-ap-member-card="1"[^>]* open/);
            assert.match(second, /writer/);
            assert.match(second, /2\.3/);
            assert.match(second, /publisher-b/);
            assert.match(second, /data-ap-replace-skill="1"/);
            assert.match(second, /data-ap-remove-member="1"/);
            assert.match(second, /data-ap-exact-evidence="1"/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldReindexCardEvidenceWhenRemovingSkills()
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

            const draft = {runtimeProfile:{members:[
              {expectedSkillName:'research',skillRef:{guid:'guid-a'}},
              {expectedSkillName:'writer',skillRef:{guid:'guid-b'}}
            ]}};
            const context = {
              AGENT_PROFILE_STATE:{
                skillProofs:{0:{skillHash:'hash-a'},1:{skillHash:'hash-b'}},
                skillCardsOpen:{0:false,1:true},dirty:false,diagnostics:[{field:'members[1].intentId'}]
              },
              agentProfileWorkingDraft() { return draft; },
              confirm() { return true; }
            };
            vm.createContext(context);
            vm.runInContext(functionSource('agentProfileRemoveMember', 'agentProfileDiagnosticsHtml'), context);

            assert.equal(vm.runInContext('agentProfileRemoveMember(0)', context), true);
            assert.equal(draft.runtimeProfile.members.length, 1);
            assert.equal(draft.runtimeProfile.members[0].expectedSkillName, 'writer');
            assert.equal(context.AGENT_PROFILE_STATE.skillProofs[0].skillHash, 'hash-b');
            assert.equal(context.AGENT_PROFILE_STATE.skillCardsOpen[0], true);
            assert.deepEqual(Object.keys(context.AGENT_PROFILE_STATE.skillProofs), ['0']);
            assert.deepEqual(Array.from(context.AGENT_PROFILE_STATE.diagnostics), []);
            assert.equal(context.AGENT_PROFILE_STATE.dirty, true);

            assert.equal(vm.runInContext('agentProfileRemoveMember(0)', context), true);
            assert.equal(draft.runtimeProfile.members.length, 0);
            assert.deepEqual(Object.keys(context.AGENT_PROFILE_STATE.skillProofs), []);
            assert.deepEqual(Object.keys(context.AGENT_PROFILE_STATE.skillCardsOpen), []);
            assert.match(html, /root\.addEventListener\('toggle',[\s\S]*data-ap-member-card/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldDiscoverMultipleExactSkillsAndRetryPartialFailures()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const draft = {displayName:'Operator',instructions:'Operate safely',runtimeProfile:{
              maximumToolPolicy:{toolNames:['manual'],toolSetRefs:[]},
              members:[{intentId:'existing',routingDescription:'Existing route',
                skillRef:{guid:'guid-existing',literalVersion:'1.0'},explicitTriggerAliases:[],
                sideEffectClass:'READ_ONLY',expectedSkillName:'existing',reviewedPublisherId:'publisher-existing',
                taskToolPolicy:{toolNames:['manual'],toolSetRefs:[]}}]}};
            let capturedBeforeRender = false, renderCount = 0, failA = true;
            const context = {
              AGENT_PROFILE_STATE:{skillRequest:0,skillModal:null,skillProofs:{},skillCardsOpen:{},
                dirty:false,diagnostics:[]},
              root:{querySelector(){return null;}},
              render(){renderCount += 1;},
              agentProfileCaptureDraft(){capturedBeforeRender = renderCount === 0;},
              agentProfileWorkingDraft(){return draft;},
              agentProfileStoreWorkingDraft(value){assert.equal(value,draft);return value;},
              esc(value){return String(value == null ? '' : value)
                .replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('\"','&quot;');},
              async agentProfileJson(path){
                const guid = decodeURIComponent(path.split('/').at(-2));
                if (guid === 'guid-a' && failA) throw {problem:{title:'A unavailable'}};
                return {body:{guid,literalVersion:guid === 'guid-a'?'2.0':'3.1',name:'research',
                  publisher:'publisher-' + guid,skillHash:'a'.repeat(64),
                  declaredToolNames:guid === 'guid-a'?['search']:['fetch',' search ']}};
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileUnionNames', 'agentProfileApplyExactSkill')}
              ${functionSource('agentProfileApplyExactSkill', 'agentProfileCaptureDraft')}
              ${functionSource('agentProfileSlugFromName', 'agentProfileStartCreate')}
              ${functionSource('agentProfileOpenSkillModal', 'agentProfilePublicSummaryHtml')}
            `, context);

            vm.runInContext("agentProfileOpenSkillModal(root,'add',null)", context);
            assert.equal(context.AGENT_PROFILE_STATE.skillModal.mode, 'add');
            assert.equal(capturedBeforeRender, true);
            context.AGENT_PROFILE_STATE.skillModal.results = [{guid:'guid-b',name:'Research B',
              description:'Fetch evidence',category:'operations',tags:['aevatar'],private:false},
              {guid:'guid-a',name:'Research A',description:'Search evidence',category:'research',
                tags:['search'],private:true},
              {guid:'guid-existing',name:'Existing',description:'Already used',category:'',tags:[],private:false}];
            context.AGENT_PROFILE_STATE.skillModal.total = 3;
            const modal = vm.runInContext('agentProfileSkillModalHtml()', context);
            assert.match(modal, /role="dialog"/);
            assert.match(modal, /aria-modal="true"/);
            assert.match(modal, /type="checkbox"/);
            assert.match(modal, /operations/);
            assert.match(modal, /aevatar/);
            assert.match(modal, /私有/);
            assert.match(modal, /已添加/);
            assert.doesNotMatch(modal, /publisher-from-nowhere/);

            vm.runInContext("agentProfileToggleSkillSelection('guid-b')", context);
            vm.runInContext("agentProfileToggleSkillSelection('guid-a')", context);
            (async function(){
              await vm.runInContext('agentProfileConfirmSkillSelection(root)', context);
              assert.deepEqual(draft.runtimeProfile.members.map(member => member.skillRef.guid),
                ['guid-existing','guid-b']);
              assert.deepEqual(Array.from(context.AGENT_PROFILE_STATE.skillModal.selected), ['guid-a']);
              assert.match(context.AGENT_PROFILE_STATE.skillModal.exactErrors['guid-a'], /unavailable/);
              assert.equal(draft.runtimeProfile.members[1].intentId, 'research');
              assert.deepEqual(Array.from(draft.runtimeProfile.members[1].taskToolPolicy.toolNames),
                ['fetch','search']);
              assert.deepEqual(Array.from(draft.runtimeProfile.maximumToolPolicy.toolNames),
                ['manual','fetch','search']);
              assert.equal(context.AGENT_PROFILE_STATE.skillProofs[1].guid, 'guid-b');

              failA = false;
              await vm.runInContext('agentProfileConfirmSkillSelection(root)', context);
              assert.deepEqual(draft.runtimeProfile.members.map(member => member.skillRef.guid),
                ['guid-existing','guid-b','guid-a']);
              assert.equal(draft.runtimeProfile.members[2].intentId, 'research-2');
              assert.equal(context.AGENT_PROFILE_STATE.skillModal, null);
              assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldReplaceSkillsOnlyAfterExactResolution()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const member = {intentId:'operate',routingDescription:'Handle Aevatar operations',
              skillRef:{guid:'guid-old',literalVersion:'1.0'},explicitTriggerAliases:['operate'],
              sideEffectClass:'SERVICE_CALL',expectedSkillName:'old',reviewedPublisherId:'publisher-old',
              taskToolPolicy:{toolNames:['manual'],toolSetRefs:['set-a']}};
            const draft = {runtimeProfile:{maximumToolPolicy:{toolNames:['manual'],toolSetRefs:[]},members:[member]}};
            let fail = true;
            const context = {
              AGENT_PROFILE_STATE:{skillRequest:0,skillModal:null,skillProofs:{},skillCardsOpen:{},dirty:false},
              root:{querySelector(){return null;}},render(){},agentProfileCaptureDraft(){},
              agentProfileWorkingDraft(){return draft;},agentProfileStoreWorkingDraft(value){return value;},
              async agentProfileJson(){if(fail)throw {problem:{title:'Exact unavailable'}};return {body:{
                guid:'guid-new',literalVersion:'4.2',name:'new',publisher:'publisher-new',
                skillHash:'b'.repeat(64),declaredToolNames:['service_call']}};},
              esc(value){return String(value == null ? '' : value);}
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileUnionNames', 'agentProfileApplyExactSkill')}
              ${functionSource('agentProfileApplyExactSkill', 'agentProfileCaptureDraft')}
              ${functionSource('agentProfileSlugFromName', 'agentProfileStartCreate')}
              ${functionSource('agentProfileOpenSkillModal', 'agentProfilePublicSummaryHtml')}
            `, context);

            vm.runInContext("agentProfileOpenSkillModal(root,'replace',0)", context);
            vm.runInContext("agentProfileToggleSkillSelection('guid-new')", context);
            const before = JSON.stringify(member);
            (async function(){
              await vm.runInContext('agentProfileConfirmSkillSelection(root)', context);
              assert.equal(JSON.stringify(member), before);
              assert.match(context.AGENT_PROFILE_STATE.skillModal.exactErrors['guid-new'], /unavailable/);

              fail = false;
              await vm.runInContext('agentProfileConfirmSkillSelection(root)', context);
              assert.equal(member.intentId, 'operate');
              assert.equal(member.routingDescription, 'Handle Aevatar operations');
              assert.deepEqual(Array.from(member.explicitTriggerAliases), ['operate']);
              assert.equal(member.sideEffectClass, 'SERVICE_CALL');
              assert.equal(member.skillRef.guid, 'guid-new');
              assert.equal(member.skillRef.literalVersion, '4.2');
              assert.equal(member.expectedSkillName, 'new');
              assert.deepEqual(Array.from(member.taskToolPolicy.toolNames), ['manual','service_call']);
              assert.deepEqual(Array.from(member.taskToolPolicy.toolSetRefs), ['set-a']);
              assert.equal(context.AGENT_PROFILE_STATE.skillProofs[0].guid, 'guid-new');
              assert.equal(context.AGENT_PROFILE_STATE.skillModal, null);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldRenderHonestLifecycleActionBar()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(".ap-action-bar{");
        html.Should().Contain("position:sticky;bottom:0");
        html.Should().Contain(".ap-skill-modal{");
        html.Should().Contain("height:min(92dvh,760px)");
        html.Should().Contain("@media (prefers-reduced-motion:reduce)");

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

            const detail = {ownerKind:'scope', profileSlug:'operator', displayName:'Operator',
              purpose:'Operate Aevatar', publishedRevision:4, available:true, draft:{
                displayName:'Operator', purpose:'Operate Aevatar', instructions:'Be careful',
                runtimeProfile:{members:[],maximumToolPolicy:{},recoveryToolPolicy:{}}}};
            const context = {
              detail,
              ACCOUNT:{admin:false},
              AGENT_PROFILE_STATE:{detail,createFlow:null,busy:false,pending:null,dirty:false,
                diagnostics:[],notice:null,error:null,etag:'\"v4\"',rolloutDraft:null,
                systemBinding:null},
              esc(value) { return String(value == null ? '' : value); },
              agentProfileCanWrite() { return true; },
              agentProfileRuntime(value) { return value.draft.runtimeProfile; },
              agentProfileEmptyDraft() { return {runtimeProfile:{members:[]}}; },
              agentProfileRolloutFromBinding() { return {enabled:false,cohortBasisPoints:0}; },
              agentProfileStatus() { return '<span class="ap-status">draft</span>'; },
              agentProfileField() { return ''; }, agentProfileSelect() { return ''; },
              agentProfileSkillsSectionHtml() { return '<div>skills</div>'; },
              agentProfileDiagnosticsHtml() { return ''; }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileLifecycleState', 'agentProfilePublicSummaryHtml')}
              ${functionSource('agentProfileEditorHtml', 'agentProfileCollectFields')}
            `, context);

            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '已发布 r4');
            context.AGENT_PROFILE_STATE.dirty = true;
            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '未保存修改');
            context.actionState = {innerHTML:''};
            context.actionRoot = {querySelector(selector) {
              assert.equal(selector, '.ap-action-state');
              return context.actionState;
            }};
            assert.equal(vm.runInContext(
              'agentProfileRefreshActionState(actionRoot)', context), true);
            assert.match(context.actionState.innerHTML, /未保存修改/);
            context.AGENT_PROFILE_STATE.busy = true;
            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '正在保存');
            context.AGENT_PROFILE_STATE.busy = false;
            context.AGENT_PROFILE_STATE.pending = {kind:'draft'};
            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '已接受，等待提交/投影');
            context.AGENT_PROFILE_STATE.pending = null;
            context.AGENT_PROFILE_STATE.dirty = false;
            context.AGENT_PROFILE_STATE.diagnostics = [{code:'INVALID'}];
            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '校验失败');
            context.AGENT_PROFILE_STATE.diagnostics = [];
            context.AGENT_PROFILE_STATE.notice = '校验通过，可以发布';
            assert.equal(vm.runInContext('agentProfileLifecycleState(detail).label', context),
              '校验通过');
            context.AGENT_PROFILE_STATE.notice = null;

            const writable = vm.runInContext('agentProfileActionBarHtml(detail,true)', context);
            assert.match(writable, /class="ap-action-bar"/);
            assert.match(writable, /aria-live="polite"/);
            assert.match(writable, /data-ap-action="save"/);
            assert.match(writable, /data-ap-action="validate"/);
            assert.match(writable, /data-ap-action="publish"/);
            const readOnly = vm.runInContext('agentProfileActionBarHtml(detail,false)', context);
            assert.doesNotMatch(readOnly, /data-ap-action="(?:save|validate|publish)"/);

            const editor = vm.runInContext('agentProfileEditorHtml()', context);
            assert.ok(editor.indexOf('data-ap-action="save"') > editor.indexOf('</form>'),
              'save/validate/publish belong to the stable bottom action bar, not the header');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldKeepSkillModalKeyboardAndFocusContained()
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

            const handlers = {}, calls = {render:0,close:0,search:0,trap:0,searchFocus:0,statusRefresh:0};
            const searchInput = {focus() { calls.searchFocus += 1; }};
            const root = {
              addEventListener(name, handler) { handlers[name] = handler; },
              querySelector(selector) {
                if (selector === '[data-ap-skill-modal-query]') return searchInput;
                return null;
              }
            };
            const context = {
              root,
              AGENT_PROFILE_STATE:{loaded:true,loading:false,skillRequest:0,skillModal:null,
                skillFocusReturn:null},
              agentProfileCaptureDraft() {},
              render() { calls.render += 1; },
              requestAnimationFrame(callback) { callback(); },
              loadAgentProfiles() {}, toast() {}, confirm() { return true; },
              agentProfileCloseSkillModal() { calls.close += 1; return true; },
              agentProfileSearchSkillModal() { calls.search += 1; },
              agentProfileTrapModalFocus() { calls.trap += 1; return true; },
              agentProfileRefreshActionState() { calls.statusRefresh += 1; return true; }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileOpenSkillModal', 'agentProfileSkillModalHtml')}
            `, context);

            vm.runInContext("agentProfileOpenSkillModal(root,'replace',2)", context);
            assert.equal(context.AGENT_PROFILE_STATE.skillModal.focusSearch, true);
            assert.deepEqual({...context.AGENT_PROFILE_STATE.skillModal.returnFocus},
              {mode:'replace',memberIndex:2});
            vm.runInContext('agentProfileCloseSkillModal()', context);
            assert.equal(context.AGENT_PROFILE_STATE.skillModal, null);
            assert.deepEqual({...context.AGENT_PROFILE_STATE.skillFocusReturn},
              {mode:'replace',memberIndex:2});

            let restored = 0;
            context.AGENT_PROFILE_STATE.skillFocusReturn = {mode:'replace',memberIndex:2};
            context.restoreRoot = {querySelector(selector) {
              assert.equal(selector, '[data-ap-replace-skill="2"]');
              return {focus() { restored += 1; }};
            }};
            assert.equal(vm.runInContext('agentProfileRestoreModalFocus(restoreRoot)', context), true);
            assert.equal(restored, 1);
            assert.equal(context.AGENT_PROFILE_STATE.skillFocusReturn, null);

            const first = {disabled:false,focus() { context.active = first; }};
            const last = {disabled:false,focus() { context.active = last; }};
            const dialog = {ownerDocument:{get activeElement() { return context.active; }},
              querySelectorAll() { return [first,last]; }};
            context.trapRoot = {querySelector() { return dialog; }};
            context.active = last;
            context.tabEvent = {key:'Tab',shiftKey:false,prevented:false,
              preventDefault() { this.prevented = true; }};
            assert.equal(vm.runInContext(
              'agentProfileTrapModalFocus(trapRoot,tabEvent)', context), true);
            assert.equal(context.active, first);
            assert.equal(context.tabEvent.prevented, true);
            context.active = first; context.tabEvent.shiftKey = true; context.tabEvent.prevented = false;
            vm.runInContext('agentProfileTrapModalFocus(trapRoot,tabEvent)', context);
            assert.equal(context.active, last);
            assert.equal(context.tabEvent.prevented, true);
            context.active = {outside:true}; context.tabEvent.shiftKey = false;
            context.tabEvent.prevented = false;
            assert.equal(vm.runInContext(
              'agentProfileTrapModalFocus(trapRoot,tabEvent)', context), true);
            assert.equal(context.active, first, 'rerendered dialog recaptures focus on next Tab');
            assert.equal(context.tabEvent.prevented, true);

            vm.runInContext(`${functionSource('mountAgentProfiles', 'cronFieldMatcher')}`, context);
            context.agentProfileCloseSkillModal = function() { calls.close += 1; return true; };
            context.agentProfileTrapModalFocus = function() { calls.trap += 1; return true; };
            context.AGENT_PROFILE_STATE.skillModal = {focusSearch:true,resolving:false,loading:false};
            vm.runInContext('mountAgentProfiles(root)', context);
            assert.equal(calls.searchFocus, 1);
            assert.equal(context.AGENT_PROFILE_STATE.skillModal.focusSearch, false);
            handlers.input({target:{value:'Changed',matches(selector) {
              return selector === '[data-ap-field]';
            },getAttribute() { return 'instructions'; }}});
            assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
            assert.equal(calls.statusRefresh, 1);

            const keyEvent = (key, matches, shiftKey=false) => ({key,shiftKey,
              target:{matches(selector) { return matches && selector === '[data-ap-skill-modal-query]'; }},
              preventDefault() { this.prevented = true; }});
            handlers.keydown(keyEvent('Enter', true));
            assert.equal(calls.search, 1);
            handlers.keydown(keyEvent('Tab', false));
            assert.equal(calls.trap, 1);
            handlers.keydown(keyEvent('Escape', false));
            assert.equal(calls.close, 1);
            context.AGENT_PROFILE_STATE.skillModal.resolving = true;
            handlers.keydown(keyEvent('Escape', false));
            assert.equal(calls.close, 1, 'resolving exact facts cannot be interrupted');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldRenderPublishedSystemSummaryWithoutFakeDraft()
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

            const context = {
              ACCOUNT:{ admin:false },
              AGENT_PROFILE_STATE:{
                detail:{
                  ownerKind:'system', profileSlug:'public-research', displayName:'Public research',
                  purpose:'Published evidence assistant', publishedRevision:4, available:true
                },
                pending:null, busy:false, notice:null, error:null, etag:null, diagnostics:[]
              },
              esc(value) { return String(value == null ? '' : value); }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileCanWrite', 'agentProfileProblem')}
              ${functionSource('agentProfileStatus', 'agentProfileListHtml')}
              ${functionSource('agentProfileLifecycleState', 'agentProfilePublicSummaryHtml')}
              ${functionSource('agentProfilePublicSummaryHtml', 'agentProfileEditorHtml')}
              ${functionSource('agentProfileEditorHtml', 'agentProfileCollectFields')}
            `, context);
            const editor = vm.runInContext('agentProfileEditorHtml()', context);
            assert.match(editor, /Public research/);
            assert.match(editor, /Published evidence assistant/);
            assert.match(editor, /system\/public-research/);
            assert.match(editor, /已发布 r4/);
            assert.match(editor, /设为我的默认/);
            assert.match(editor, /class="ap-action-bar"/);
            assert.ok(editor.indexOf('data-ap-action="personal-default"') >
              editor.indexOf('Profile 摘要'), 'read-only binding action belongs to the bottom workbench bar');
            assert.doesNotMatch(editor, /data-ap-form/);
            assert.doesNotMatch(editor, /Instructions/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldNotInventMissingCatalogFacts()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const start = html.indexOf('function ' + name + '(');
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {
              AGENT_PROFILE_STATE:{
                search:'', status:'all', selected:'public-research',
                items:[]
              },
              esc(value) { return String(value == null ? '' : value); }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileNormalizeItem', 'loadAgentProfileBindings')}
              ${functionSource('agentProfileRows', 'agentProfileStatus')}
              ${functionSource('agentProfileStatus', 'agentProfileListHtml')}
              ${functionSource('agentProfileListHtml', 'agentProfileField')}
            `, context);
            context.item = {
              profileId:'prof-public', profileSlug:'public-research',
              displayName:'Public research', purpose:'Published evidence assistant',
              publishedRevision:4, available:true, ownerKind:'system'
            };
            context.AGENT_PROFILE_STATE.items = [
              vm.runInContext("agentProfileNormalizeItem(item, 'system')", context)
            ];
            const list = vm.runInContext('agentProfileListHtml()', context);
            assert.match(list, /Public research/);
            assert.match(list, /Published evidence assistant/);
            assert.doesNotMatch(list, /draft r0/);
            assert.doesNotMatch(list, /undefined/);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldNavigateToCanonicalRoute()
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

            const navStart = html.indexOf('var NAV=[');
            const navEnd = html.indexOf('/* 当前账号能否访问该模块', navStart);
            assert.notEqual(navStart, -1);
            assert.notEqual(navEnd, -1);
            const context = {
              location:{ hash:'' }, ACCOUNT:{ name:'Test user' },
              NAV_ICON:{ fleet:'', status:'', audit:'', obs:'', cqrs:'', studio:'',
                agentProfiles:'', skills:'', sched:'', channels:'', voice:'' },
              esc(value) { return String(value); },
              viewAgentProfiles() { return 'agent-profile-view'; }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${html.slice(navStart, navEnd)}
              ${functionSource('canAccessModule', 'defaultModule')}
              ${functionSource('renderRail', 'agentProfileOwnerEndpoint')}
              ${functionSource('buildHash', 'curParts')}
              ${functionSource('navigate', 'breadcrumb')}
            `, context);
            const rail = vm.runInContext("renderRail('agent-profiles')", context);
            const module = rail.match(/class="rail-item on" data-module="([^"]+)"/)[1];
            context.module = module;
            vm.runInContext("navigate([module], {})", context);
            assert.equal(module, 'agent-profiles');
            assert.equal(context.location.hash, '#/agent-profiles');

            const opsStart = html.indexOf('function opsView(');
            const opsEnd = html.indexOf('\n}\n\n</script>', opsStart) + 2;
            assert.notEqual(opsStart, -1);
            assert.ok(opsEnd > opsStart);
            vm.runInContext(html.slice(opsStart, opsEnd), context);
            assert.equal(vm.runInContext("opsView('agent-profiles')", context), 'agent-profile-view');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldReconcileAcceptedReceiptWithCommittedProjection()
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

            const context = {
              AGENT_PROFILE_STATE: {pending:null,notice:null,error:null}
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileTrackAccepted', 'agentProfileOutcomeIsTerminal')}
              ${functionSource('agentProfileOutcomeIsTerminal', 'agentProfileReconcilePending')}
              ${functionSource('agentProfileReconcilePending', 'agentProfileScheduleRefresh')}
            `, context);

            context.result = {status:202,body:{operationId:'op-profile-alpha'}};
            vm.runInContext("agentProfileTrackAccepted(result,'catalog')", context);
            assert.equal(context.AGENT_PROFILE_STATE.pending.operationId, 'op-profile-alpha');
            assert.equal(context.AGENT_PROFILE_STATE.pending.kind, 'catalog');

            context.outcome = {operationId:'op-profile-alpha',status:'SUCCEEDED',code:'PROFILE_PROVISIONING_STARTED'};
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);
            assert.equal(context.AGENT_PROFILE_STATE.pending.operationId, 'op-profile-alpha');

            context.outcome = {operationId:'op-other',status:'SUCCEEDED',code:'PROFILE_ACTIVE'};
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);

            context.outcome = {operationId:'op-profile-alpha',status:'SUCCEEDED',code:'PROFILE_ACTIVE'};
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
            assert.equal(context.AGENT_PROFILE_STATE.pending, null);
            assert.match(context.AGENT_PROFILE_STATE.notice, /完成投影/);

            context.result = {status:202,body:{operationId:'op-publish-alpha'}};
            vm.runInContext("agentProfileTrackAccepted(result,'publish')", context);
            context.outcome = {operationId:'op-publish-alpha',status:'SUCCEEDED',code:'PROFILE_PUBLISHED',executionAvailable:false};
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);
            context.outcome.executionAvailable = true;
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);

            context.result = {status:202,body:{operationId:'op-binding-alpha'}};
            vm.runInContext("agentProfileTrackAccepted(result,'binding')", context);
            context.outcome = {operationId:'op-binding-alpha',status:'REJECTED',code:'AUTHORITY_VERSION_CONFLICT'};
            assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
            assert.equal(context.AGENT_PROFILE_STATE.error.title, 'AUTHORITY_VERSION_CONFLICT');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldStartGuidedCreationLocallyAndSuggestAsciiSlug()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const mutations = [];
            const context = {
              AGENT_PROFILE_STATE:{
                owner:'mine', selected:'existing-profile', detail:{profileSlug:'existing-profile'},
                createFlow:null, completedPending:null, dirty:false, error:{title:'old'},
                notice:'old', diagnostics:[{code:'old'}], skillRequest:0
              },
              agentProfileMutation() { mutations.push(Array.from(arguments)); },
              render() {}
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileResetSkillSearch', 'agentProfileOwnerEndpoint')}
              ${functionSource('agentProfileDraftFromFields', 'agentProfileEmptyDraft')}
              ${functionSource('agentProfileEmptyDraft', 'agentProfileSlugFromName')}
              ${functionSource('agentProfileSlugFromName', 'agentProfileStartCreate')}
              ${functionSource('agentProfileStartCreate', 'agentProfileCancelCreate')}
              ${functionSource('agentProfileCancelCreate', 'agentProfileWorkingDraft')}
            `, context);

            vm.runInContext('agentProfileStartCreate()', context);
            assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'editing');
            assert.equal(context.AGENT_PROFILE_STATE.createFlow.owner, 'mine');
            assert.equal(context.AGENT_PROFILE_STATE.createFlow.draft.runtimeProfile.agentKind, 'nyxid.chat');
            assert.equal(context.AGENT_PROFILE_STATE.detail, null);
            assert.equal(context.AGENT_PROFILE_STATE.completedPending, null);
            assert.equal(mutations.length, 0);
            assert.equal(
              vm.runInContext("agentProfileSlugFromName('Aevatar Operator')", context),
              'aevatar-operator');
            assert.equal(
              vm.runInContext("agentProfileSlugFromName('Crème Ops')", context),
              'creme-ops');
            assert.equal(vm.runInContext("agentProfileSlugFromName('运维助手')", context), '');

            context.AGENT_PROFILE_STATE.dirty = true;
            context.confirm = () => false;
            assert.equal(vm.runInContext('agentProfileCancelCreate()', context), false);
            assert.notEqual(context.AGENT_PROFILE_STATE.createFlow, null);
            context.confirm = () => true;
            assert.equal(vm.runInContext('agentProfileCancelCreate()', context), true);
            assert.equal(context.AGENT_PROFILE_STATE.createFlow, null);
            assert.equal(context.AGENT_PROFILE_STATE.dirty, false);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldKeepExactProofsDuringCreateReadback()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = {
              AGENT_PROFILE_REQUEST:0,
              AGENT_PROFILE_STATE:{
                items:[{ownerKind:'scope',profileSlug:'aevatar-operator'}],
                createFlow:{owner:'mine',slug:'aevatar-operator',stage:'catalog'},
                skillProofs:{0:{skillHash:'0123456789abcdef'}},skillRequest:0,
                diagnostics:[],selected:null,detail:null,etag:null,rolloutDraft:null,dirty:true
              },
              agentProfileItemEndpoint() { return '/profiles/aevatar-operator'; },
              async agentProfileJson() {
                return {etag:'"agent-profile-v3"',body:{ownerKind:'scope',
                  profileSlug:'aevatar-operator',draft:null}};
              },
              agentProfileReconcilePending() {}, render() {}
              ,agentProfileRequestIsCurrent() { return true; }
              ,agentProfileSaveOwnerSnapshot() { return false; }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileResetSkillSearch', 'agentProfileOwnerEndpoint')}
              ${functionSource('loadAgentProfileDetail', 'agentProfileRows')}
            `, context);

            (async function() {
              await vm.runInContext("loadAgentProfileDetail('aevatar-operator', false)", context);
              assert.equal(context.AGENT_PROFILE_STATE.skillProofs[0].skillHash,
                '0123456789abcdef');
              assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldAdvanceGuidedCreationOnlyFromMatchingTerminalOutcomes()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [
                html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')
              ].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [
                html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)
              ].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const draft = {
              displayName:'Aevatar Operator', purpose:'Operate Aevatar',
              instructions:'Require confirmation',
              runtimeProfile:{agentKind:'nyxid.chat',activationMode:'ENFORCED',
                maximumToolPolicy:{toolNames:['aevatar_read'],toolSetRefs:[]},
                recoveryToolPolicy:{toolNames:[],toolSetRefs:[]},members:[]}
            };
            const mutations = [];
            const context = {
              AGENT_PROFILE_STATE:{
                owner:'mine', createFlow:{owner:'mine',slug:'aevatar-operator',slugTouched:true,
                  draft,stage:'editing',catalogOperationId:null,draftOperationId:null},
                items:[],selected:null,detail:null,etag:null,pending:null,completedPending:null,
                pollTimer:null,busy:false,dirty:true,notice:null,error:null,diagnostics:[],skillProofs:{}
              },
              scheduled:0, render() {},
              agentProfileCaptureDraft() {},
              agentProfileWorkingDraft() { return context.AGENT_PROFILE_STATE.createFlow.draft; },
              agentProfileStoreWorkingDraft(value) {
                context.AGENT_PROFILE_STATE.createFlow.draft = value;
                return value;
              },
              agentProfileLocalDiagnostics() { return []; },
              agentProfileCollectionEndpoint() { return '/api/scopes/scope-alpha/agent-profiles'; },
              agentProfileItemEndpoint(item) {
                return '/api/scopes/scope-alpha/agent-profiles/' + item.profileSlug;
              },
              agentProfileProblem() { return {kind:'error',title:'Request failed'}; },
              agentProfileNormalizeItem(item) { return item; },
              agentProfileJson() { throw new Error('unexpected collection read'); },
              async agentProfileMutation(path, method, body, etag) {
                mutations.push({path,method,body,etag});
                if (method === 'POST') {
                  return {status:202,body:{operationId:'op-catalog-alpha'}};
                }
                return {status:202,body:{operationId:'op-draft-alpha'}};
              },
              async loadAgentProfileDetail(slug) {
                context.AGENT_PROFILE_STATE.selected = slug;
                context.AGENT_PROFILE_STATE.detail = {
                  ownerKind:'scope',profileId:'profile-aevatar-operator',profileSlug:slug,draft:null
                };
                context.AGENT_PROFILE_STATE.etag = '"agent-profile-v3"';
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileTrackAccepted', 'agentProfileOutcomeIsTerminal')}
              ${functionSource('agentProfileOutcomeIsTerminal', 'agentProfileReconcilePending')}
              ${functionSource('agentProfileReconcilePending', 'agentProfileScheduleRefresh')}
              ${functionSource('agentProfileScheduleRefresh', 'agentProfileFindCreateItem')}
              ${functionSource('agentProfileFindCreateItem', 'agentProfileHandleAction')}
              agentProfileScheduleRefresh=function(){scheduled+=1;};
            `, context);

            (async function() {
              await vm.runInContext('agentProfileSubmitCreate({})', context);
              assert.equal(mutations.length, 1);
              assert.equal(mutations[0].method, 'POST');
              assert.equal(mutations[0].path, '/api/scopes/scope-alpha/agent-profiles');
              assert.deepEqual(JSON.parse(JSON.stringify(mutations[0].body)),
                {profileSlug:'aevatar-operator'});
              assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'catalog');
              assert.equal(context.AGENT_PROFILE_STATE.pending.operationId, 'op-catalog-alpha');
              assert.equal(context.AGENT_PROFILE_STATE.pending.kind, 'catalog');

              const pending = context.AGENT_PROFILE_STATE.pending;
              context.AGENT_PROFILE_STATE.pending = null;
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), false);
              assert.equal(mutations.length, 1, 'missing pending is not completion');
              context.AGENT_PROFILE_STATE.pending = pending;

              context.outcome = {operationId:'op-catalog-alpha',status:'SUCCEEDED',
                code:'PROFILE_PROVISIONING_STARTED'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), false);

              context.outcome = {operationId:'op-other',status:'SUCCEEDED',code:'PROFILE_ACTIVE'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), false);

              context.AGENT_PROFILE_STATE.items = [{
                ownerKind:'scope',profileId:'profile-aevatar-operator',
                profileSlug:'aevatar-operator',available:true
              }];
              context.outcome = {operationId:'op-catalog-alpha',status:'SUCCEEDED',code:'PROFILE_ACTIVE'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), true);
              assert.equal(mutations.length, 2);
              assert.equal(mutations[1].method, 'PUT');
              assert.equal(mutations[1].path,
                '/api/scopes/scope-alpha/agent-profiles/aevatar-operator/draft');
              assert.equal(mutations[1].etag, '"agent-profile-v3"');
              assert.equal(mutations[1].body.draft, draft);
              assert.equal(context.AGENT_PROFILE_STATE.pending.operationId, 'op-draft-alpha');
              assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'draft');

              context.outcome = {operationId:'op-other',status:'SUCCEEDED',code:'PROFILE_DRAFT_UPDATED'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), false);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), false);
              assert.notEqual(context.AGENT_PROFILE_STATE.createFlow, null);

              context.outcome = {operationId:'op-draft-alpha',status:'NO_CHANGE',code:'PROFILE_DRAFT_UNCHANGED'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), true);
              assert.equal(context.AGENT_PROFILE_STATE.createFlow, null);
              assert.equal(context.AGENT_PROFILE_STATE.selected, 'aevatar-operator');
              assert.equal(context.AGENT_PROFILE_STATE.dirty, false);
              assert.match(context.AGENT_PROFILE_STATE.notice, /草稿已创建/);
              assert.equal(mutations.filter(call => call.method === 'POST').length, 1);
              assert.equal(mutations.filter(call => call.method === 'PUT').length, 1);

              context.AGENT_PROFILE_STATE.createFlow = {owner:'mine',slug:'failed-profile',
                slugTouched:true,draft,stage:'catalog',catalogOperationId:'op-catalog-failed',
                draftOperationId:null};
              context.AGENT_PROFILE_STATE.pending = {operationId:'op-catalog-failed',
                kind:'catalog',attempts:1};
              context.outcome = {operationId:'op-catalog-failed',status:'REJECTED',
                code:'PROFILE_PROVISIONING_FAILED'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), true);
              assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'editing');
              assert.equal(context.AGENT_PROFILE_STATE.error.title, 'PROFILE_PROVISIONING_FAILED');

              context.AGENT_PROFILE_STATE.createFlow = {owner:'mine',slug:'aevatar-operator',
                slugTouched:true,draft,stage:'draft',catalogOperationId:'op-catalog-alpha',
                draftOperationId:'op-draft-failed'};
              context.AGENT_PROFILE_STATE.detail = {ownerKind:'scope',profileId:'profile-aevatar-operator',
                profileSlug:'aevatar-operator',draft:null};
              context.AGENT_PROFILE_STATE.pending = {operationId:'op-draft-failed',
                kind:'draft',attempts:1};
              context.outcome = {operationId:'op-draft-failed',status:'REJECTED',
                code:'AUTHORITY_VERSION_CONFLICT'};
              assert.equal(vm.runInContext('agentProfileReconcilePending(outcome)', context), true);
              assert.equal(await vm.runInContext('agentProfileAdvanceCreate()', context), true);
              assert.equal(context.AGENT_PROFILE_STATE.createFlow, null);
              assert.equal(context.AGENT_PROFILE_STATE.detail.draft, draft);
              assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
              assert.equal(context.AGENT_PROFILE_STATE.error.title, 'AUTHORITY_VERSION_CONFLICT');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
    }

    [Fact]
    public async Task AdminShell_AgentProfiles_ShouldReadBackAmbiguousCreateAndPreserveDraftWhenSaveFails()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const starts = [html.indexOf('function ' + name + '('),
                html.indexOf('async function ' + name + '(')].filter(index => index !== -1);
              const start = starts.length ? Math.min(...starts) : -1;
              const ends = [html.indexOf('\nfunction ' + nextName + '(', start),
                html.indexOf('\nasync function ' + nextName + '(', start)].filter(index => index !== -1);
              const end = ends.length ? Math.min(...ends) : -1;
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const draft = {displayName:'Aevatar Operator',instructions:'Confirm risky work',
              runtimeProfile:{agentKind:'nyxid.chat',members:[]}};
            const mutations = [], reads = [], renderedBusy = [];
            let postFailure = {problem:{title:'Connection lost'}}, detailFailure = false;
            const context = {
              AGENT_PROFILE_STATE:{owner:'mine',createFlow:{owner:'mine',slug:'aevatar-operator',
                slugTouched:true,draft,stage:'editing'},items:[],selected:null,detail:null,etag:null,
                pending:null,completedPending:null,pollTimer:null,busy:false,dirty:true,notice:null,
                error:null,diagnostics:[],skillProofs:{0:{skillHash:'abc'}}},
              render() { renderedBusy.push(context.AGENT_PROFILE_STATE.busy); },
              agentProfileCaptureDraft() {},
              agentProfileWorkingDraft() { return context.AGENT_PROFILE_STATE.createFlow.draft; },
              agentProfileStoreWorkingDraft(value) {
                context.AGENT_PROFILE_STATE.createFlow.draft = value;
                return value;
              },
              agentProfileLocalDiagnostics() { return []; },
              agentProfileCollectionEndpoint() { return '/api/scopes/scope-alpha/agent-profiles'; },
              agentProfileItemEndpoint(item) {
                return '/api/scopes/scope-alpha/agent-profiles/' + item.profileSlug;
              },
              agentProfileProblem(status) { return {kind:'error',title:'HTTP ' + status}; },
              agentProfileNormalizeItem(item) { return item; },
              async agentProfileMutation(path, method, body, etag) {
                mutations.push({path,method,body,etag});
                if (method === 'POST') throw postFailure;
                throw {status:412,problem:{kind:'stale',title:'Other writer changed it'}};
              },
              async agentProfileJson(path) {
                reads.push(path);
                return {body:{items:[{ownerKind:'scope',profileId:'profile-aevatar-operator',
                  profileSlug:'aevatar-operator',available:true}]}};
              },
              async loadAgentProfileDetail(slug) {
                context.AGENT_PROFILE_STATE.selected = slug;
                if (detailFailure) {
                  context.AGENT_PROFILE_STATE.detail = null;
                  context.AGENT_PROFILE_STATE.etag = null;
                  context.AGENT_PROFILE_STATE.error = {kind:'unavailable',title:'Detail unavailable'};
                  return;
                }
                context.AGENT_PROFILE_STATE.detail = {ownerKind:'scope',
                  profileId:'profile-aevatar-operator',profileSlug:slug,draft:null};
                context.AGENT_PROFILE_STATE.etag = '"agent-profile-v3"';
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileTrackAccepted', 'agentProfileOutcomeIsTerminal')}
              ${functionSource('agentProfileOutcomeIsTerminal', 'agentProfileReconcilePending')}
              ${functionSource('agentProfileReconcilePending', 'agentProfileScheduleRefresh')}
              ${functionSource('agentProfileScheduleRefresh', 'agentProfileFindCreateItem')}
              ${functionSource('agentProfileFindCreateItem', 'agentProfileHandleAction')}
              agentProfileScheduleRefresh=function(){};
            `, context);

            (async function() {
              await vm.runInContext('agentProfileSubmitCreate({})', context);
              assert.equal(mutations.filter(call => call.method === 'POST').length, 1);
              assert.equal(mutations.filter(call => call.method === 'PUT').length, 1);
              assert.equal(reads.length, 1, 'ambiguous POST must be read back once');
              assert.match(reads[0], /\?take=100$/);
              assert.equal(context.AGENT_PROFILE_STATE.createFlow, null);
              assert.equal(context.AGENT_PROFILE_STATE.detail.draft, draft);
              assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
              assert.equal(context.AGENT_PROFILE_STATE.error.title, 'Other writer changed it');
              assert.equal(context.AGENT_PROFILE_STATE.skillProofs[0].skillHash, 'abc');

              postFailure = {status:409,problem:{kind:'conflict',title:'Slug already exists'}};
              context.AGENT_PROFILE_STATE.createFlow = {owner:'mine',slug:'aevatar-operator',
                slugTouched:true,draft,stage:'editing'};
              context.AGENT_PROFILE_STATE.detail = null;
              context.AGENT_PROFILE_STATE.pending = null;
              context.AGENT_PROFILE_STATE.busy = false;
              context.AGENT_PROFILE_STATE.error = null;
              const readsBefore = reads.length, putsBefore = mutations.filter(call =>
                call.method === 'PUT').length;
              assert.equal(await vm.runInContext('agentProfileSubmitCreate({})', context), false);
              assert.equal(reads.length, readsBefore, 'explicit 4xx must not use ambiguous readback');
              assert.equal(mutations.filter(call => call.method === 'PUT').length, putsBefore);
              assert.equal(context.AGENT_PROFILE_STATE.createFlow.stage, 'editing');
              assert.equal(context.AGENT_PROFILE_STATE.detail, null);
              assert.equal(context.AGENT_PROFILE_STATE.error.title, 'Slug already exists');
              assert.equal(renderedBusy[renderedBusy.length - 1], false);

              postFailure = {status:503,problem:{kind:'unavailable',title:'Gateway unavailable'}};
              context.AGENT_PROFILE_STATE.createFlow = {owner:'mine',slug:'aevatar-operator',
                slugTouched:true,draft,stage:'editing'};
              context.AGENT_PROFILE_STATE.pending = null;
              context.AGENT_PROFILE_STATE.busy = false;
              const gatewayReads = reads.length, gatewayPuts = mutations.filter(call =>
                call.method === 'PUT').length;
              assert.equal(await vm.runInContext('agentProfileSubmitCreate({})', context), true);
              assert.equal(reads.length, gatewayReads + 1, '5xx may be an ambiguous mutation');
              assert.equal(mutations.filter(call => call.method === 'PUT').length, gatewayPuts + 1);
              assert.equal(context.AGENT_PROFILE_STATE.detail.draft, draft);

              detailFailure = true;
              context.AGENT_PROFILE_STATE.createFlow = {owner:'mine',slug:'aevatar-operator',
                slugTouched:true,draft,stage:'editing'};
              context.AGENT_PROFILE_STATE.detail = null;
              context.AGENT_PROFILE_STATE.etag = null;
              context.AGENT_PROFILE_STATE.pending = null;
              context.AGENT_PROFILE_STATE.busy = false;
              context.AGENT_PROFILE_STATE.error = null;
              const detailReads = reads.length, detailPuts = mutations.filter(call =>
                call.method === 'PUT').length;
              await vm.runInContext('agentProfileSubmitCreate({})', context);
              assert.equal(reads.length, detailReads + 1);
              assert.equal(mutations.filter(call => call.method === 'PUT').length, detailPuts,
                'draft PUT requires a successful detail read and strong ETag');
              assert.equal(context.AGENT_PROFILE_STATE.createFlow, null);
              assert.equal(context.AGENT_PROFILE_STATE.detail.draft, draft);
              assert.equal(context.AGENT_PROFILE_STATE.dirty, true);
              assert.equal(context.AGENT_PROFILE_STATE.error.title, 'Detail unavailable');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
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
    public async Task AdminShell_ChatActivity_ShouldEnforceQueryPolicyAndRenderTypedSafeRecords()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("items:['fleet','status','audit','chat-activity']");
        html.Should().Contain("'chat-activity':{name:'Chat Activity', auth:'login'");
        html.Should().Contain("/api/audit/chat-activity");
        html.Should().Contain("data-act=\"chatActivityRow\"");
        html.Should().Contain("tabindex=\"0\"");
        html.Should().Contain("ev.key==='Enter'||ev.key===' '");
        html.Should().Contain(":focus-visible");
        html.Should().Contain("正在加载 Chat Activity");
        html.Should().Contain("暂无 Chat Activity");
        html.Should().Contain("Chat Activity 加载失败");
        html.Should().Contain("data-act=\"chatActivityMore\"");
        html.Should().Contain("if(a==='chatActivityCopy'){ copyWithToast");
        html.Should().NotContain("/api/chat/history");
        html.Should().NotContain("/api/chat/transcript");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name, nextName) {
              const markers = ['function ' + name + '(', 'async function ' + name + '('];
              const nextMarkers = ['\nfunction ' + nextName + '(', '\nasync function ' + nextName + '('];
              const start = markers.map(marker => html.indexOf(marker)).filter(index => index >= 0).sort((a,b) => a-b)[0] ?? -1;
              const end = nextMarkers.map(marker => html.indexOf(marker, start)).filter(index => index >= 0).sort((a,b) => a-b)[0] ?? -1;
              assert.notEqual(start, -1, name + ' must exist');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const calls = [];
            const response = {
              records: [{
                id:'audit-tool-alpha', occurredAtUtc:'2026-08-01T00:00:00Z',
                operationName:'request_nyxid_connect', operationKind:'Tool',
                lifecyclePhase:'terminal', terminalOutcome:'failed',
                auditActorId:'audit_actor:full-alpha', scopeId:'scope-alpha',
                target:{kind:'tool',id:'request_nyxid_connect'},
                correlation:{callId:'call-alpha',correlationId:'correlation-alpha'},
                failure:{code:'authorization_required',category:'authorization',sanitizedMessage:'Authorization required.'},
                provenance:{chat:{surface:'nyxid_assistant',conversationId:'conversation-alpha',turnId:'turn-alpha',taskId:'task-alpha',stepId:'step-alpha',actionRequestId:null}}
              },{
                id:'audit-tool-zeta', occurredAtUtc:'2026-08-01T00:00:02Z',
                operationName:'search_current_state', operationKind:'Tool',
                lifecyclePhase:'terminal', terminalOutcome:'succeeded',
                auditActorId:'audit_actor:full-zeta', scopeId:'scope-zeta',
                target:{kind:'tool',id:'search_current_state'},
                correlation:{callId:'call-zeta',correlationId:'correlation-zeta'}, failure:null,
                provenance:{chat:{surface:'workflow_chat',conversationId:'conversation-zeta-with-long-id',turnId:'turn-zeta',taskId:'task-zeta',stepId:'step-zeta',actionRequestId:null}}
              },{
                id:'audit-action-alpha', occurredAtUtc:'2026-08-01T00:00:01Z',
                operationName:'chat.action.requested', operationKind:'Authorization',
                lifecyclePhase:'accepted', terminalOutcome:null,
                auditActorId:'audit_actor:full-alpha', scopeId:'scope-alpha',
                target:{kind:'chat_action',id:'request_nyxid_connect'},
                correlation:{correlationId:'correlation-action'}, failure:null,
                provenance:{chat:{surface:'nyxid_assistant',conversationId:'conversation-alpha',turnId:'turn-alpha',taskId:'task-alpha',stepId:'step-alpha',actionRequestId:'action-alpha'}}
              }],
              coverage:{continuationCursor:'cursor-alpha',ingestionWatermark:'2026-08-01T00:00:02Z'}
            };
            const context = {
              URLSearchParams, encodeURIComponent, ACCOUNT:{admin:false}, calls,
              CHAT_ACTIVITY_STATE:{scope:'mine',actor:'',surface:'all',conversation:'',outcome:'all',from:'',to:''},
              CHAT_ACTIVITY_DATA:[], CHAT_ACTIVITY_CURSOR:null, CHAT_ACTIVITY_HAS_MORE:false, CHAT_ACTIVITY_WATERMARK:null,
              CHAT_ACTIVITY_LOADING:false, CHAT_ACTIVITY_LOADED:false, CHAT_ACTIVITY_ERR:null, CHAT_ACTIVITY_FORBIDDEN:false, CHAT_ACTIVITY_RERENDER:null,
              async adminJson(url){ calls.push(url); return response; },
              esc(value){ return String(value == null ? '' : value).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); },
              auditFmtTime(){ return '2026-08-01 08:00:00'; }, auditAgo(){ return '刚刚'; },
              auditResult(r){ return r.terminalOutcome||r.lifecyclePhase||'unspecified'; },
              auditResultTag(o){ return '<span>'+o+'</span>'; },
              ICON:{empty:'',warn:''}
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('chatActivityBuildQuery','chatActivityApplyPage')}
              ${functionSource('chatActivityApplyPage','loadChatActivity')}
              ${functionSource('loadChatActivity','loadChatActivityMore')}
              ${functionSource('chatActivityKind','chatActivityShortId')}
              ${functionSource('chatActivityShortId','chatActivityConversationGroups')}
              ${functionSource('chatActivityConversationGroups','chatActivityTable')}
              ${functionSource('chatActivityTable','chatActivityInspector')}
              ${functionSource('chatActivityInspector','chatActivityGate')}
              ${functionSource('chatActivityFilters','chatActivityRoot')}
            `, context);

            (async () => {
              assert.equal(context.chatActivityBuildQuery({take:50}), '?take=50');
              context.ACCOUNT = {admin:true};
              assert.equal(context.chatActivityBuildQuery({take:50}), '?take=50');
              context.CHAT_ACTIVITY_STATE.scope = 'all';
              context.CHAT_ACTIVITY_STATE.actor = 'audit_actor:exact';
              const all = context.chatActivityBuildQuery({take:50});
              assert.match(all, /scope=__all__/);
              assert.match(all, /auditActorId=audit_actor%3Aexact/);
              assert.doesNotMatch(all, /identityKeyId/);

              context.CHAT_ACTIVITY_STATE.scope = 'mine';
              context.CHAT_ACTIVITY_STATE.actor = 'must-not-leak';
              await context.loadChatActivity();
              assert.equal(calls.length,1);
              assert.match(calls[0], /^\/api\/audit\/chat-activity/);
              assert.doesNotMatch(calls[0], /scope=|auditActorId=|identityKeyId=/);
              assert.equal(context.CHAT_ACTIVITY_CURSOR,'cursor-alpha');
              context.CHAT_ACTIVITY_STATE.outcome = 'failed';
              assert.match(context.chatActivityFilters(), /value="failed" selected/);

              const table = context.chatActivityTable();
              assert.match(table,/Tool/);
              assert.match(table,/Action/);
              assert.match(table,/request_nyxid_connect/);
              assert.match(table,/failed/);
              assert.match(table,/accepted/);
              assert.match(table,/succeeded/);
              assert.match(table,/conversation-alpha/);
              assert.match(table,/conversation-zeta-with-long-id/);
              assert.match(table,/turn-alpha/);
              assert.match(table,/title="conversation-alpha"/);
              assert.match(table,/tabindex="0"/);
              const groups = table.match(/<details class="chat-activity-conversation"[^>]*>/g)||[];
              assert.equal(groups.length,2);
              assert.match(groups[0],/data-conversation="conversation-zeta-with-long-id"/);
              assert.match(groups[0],/ open(?: |>|$)/);
              assert.match(groups[1],/data-conversation="conversation-alpha"/);
              assert.doesNotMatch(groups[1],/ open(?: |>|$)/);
              assert.match(table,/<summary class="chat-activity-conversation-summary">/);
              assert.match(table,/2 个 Conversation/);
              assert.match(table,/2 条 Activity/);
              context.chatActivityApplyPage({records:[{
                ...response.records[0], id:'audit-tool-alpha-older', occurredAtUtc:'2026-07-31T23:59:59Z'
              }],coverage:{}},true);
              const mergedTable = context.chatActivityTable();
              const mergedGroups = mergedTable.match(/<details class="chat-activity-conversation"[^>]*>/g)||[];
              assert.equal(mergedGroups.length,2);
              assert.match(mergedGroups[0],/data-conversation="conversation-zeta-with-long-id"/);
              assert.match(mergedTable,/data-conversation="conversation-alpha"[\s\S]*?3 条 Activity/);
              assert.match(mergedTable,/4 条 Activity/);

              const inspector = context.chatActivityInspector(response.records[2]);
              assert.match(inspector,/task-alpha/);
              assert.match(inspector,/step-alpha/);
              assert.match(inspector,/action-alpha/);
              assert.doesNotMatch(inspector,/prompt|arguments|result_json|params/i);
              assert.equal(calls.every(url => url.startsWith('/api/audit/chat-activity')),true);
            })().catch(error => { console.error(error); process.exitCode=1; });
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, $"Chat Activity behavior should pass. stdout: {result.Output} stderr: {result.Error}");
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
    public async Task AdminShell_Studio_ShouldUseAdminOwnedRouteAndTrimNestedHeader()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(
            "suiteFrame('/admin/studio','工作台'),persistentKey:'admin-studio',frameSource:'/admin/studio'");
        html.Should().Contain(
            "studio=f.getAttribute('data-persistent-view')==='admin-studio'");
        html.Should().Contain("studio?'.site-header,.topbar':'.topbar'");
        html.Should().Contain("data-admin-embed-trim");
        html.Should().NotContain("suiteFrame('/workflow/studio','工作台')");
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
            assert.equal(frame.src, '/admin/workflow-observatory?scope=scope-alpha&status=failed&origin=schedule%2Capi&definition=wf-alpha&schedule=sched-alpha&from=2026-07-29T00%3A00%3A00Z&to=2026-07-30T00%3A00%3A00Z&run=run-alpha&tab=steps');
            assert.equal(vm.runInContext('observatoryHash', context)({scope:'scope-alpha',run:'run-alpha',tab:'steps',ignored:'no'}), '#/observatory?scope=scope-alpha&run=run-alpha&tab=steps');
            assert.equal(vm.runInContext('observatoryHash', context)({scope:'mine',tab:'timeline'}), '#/observatory');
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().NotContain("function bindObservatory(");
    }

    [Fact]
    public async Task AdminShell_ShouldPersistScrollByRouteAndReuseSameEmbeddedView()
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

            const records = new Map();
            const storage = {getItem(key){return records.get(key)||null;},setItem(key,value){records.set(key,value);}};
            const context = {};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('adminRouteKey', 'readAdminViewState')}
              ${functionSource('readAdminViewState', 'writeAdminViewState')}
              ${functionSource('writeAdminViewState', 'adminPaneScrollTop')}
              ${functionSource('adminPaneScrollTop', 'captureAdminViewState')}
              ${functionSource('replaceViewHtml', 'setAdminImmersive')}
              ${functionSource('setAdminImmersive', 'breadcrumb')}
              ${functionSource('activateDockFrame', 'render')}
            `, context);

            vm.runInContext('writeAdminViewState', context)(storage, 'console:test:admin:view', '#/audit?result=failed', 540);
            vm.runInContext('writeAdminViewState', context)(storage, 'console:test:admin:view', '#/fleet', 80);
            assert.equal(vm.runInContext('readAdminViewState', context)(storage, 'console:test:admin:view', '#/audit?result=failed'), 540);
            assert.equal(vm.runInContext('readAdminViewState', context)(storage, 'console:test:admin:view', '#/fleet'), 80);
            assert.equal(vm.runInContext('adminPaneScrollTop', context)({scrollTop:0,scrollHeight:100,clientHeight:100,getAttribute(){return '640';}}), 640);
            assert.equal(vm.runInContext('adminPaneScrollTop', context)({scrollTop:0,scrollHeight:900,clientHeight:300,getAttribute(){return '640';}}), 0);

            // dock 常驻 iframe：裸路径返回不动 src（保留内部状态）；带 query 深链更新 src；切模块只翻 active，不销毁
            function frameStub(key, src){
              const frame = {
                src: src,
                attrs: {'data-persistent-view': key, 'data-frame-source': src},
                getAttribute(name){ return frame.attrs[name] == null ? null : frame.attrs[name]; },
                setAttribute(name, value){ frame.attrs[name] = String(value); },
              };
              frame.classList = { toggle(cls, on){ frame.activeFlag = !!on; } };
              return frame;
            }
            const obsFrame = frameStub('observatory', '/admin/workflow-observatory?run=abc');
            const studioFrame = frameStub('admin-studio', '/admin/studio');
            const dock = {
              querySelector(sel){
                if (sel.indexOf('"observatory"') >= 0) return obsFrame;
                if (sel.indexOf('"admin-studio"') >= 0) return studioFrame;
                return null;
              },
              querySelectorAll(){ return [obsFrame, studioFrame]; },
              insertAdjacentHTML(){ assert.fail('existing dock frames must be reused, not recreated'); }
            };
            const activate = vm.runInContext('activateDockFrame', context);
            activate(dock, {persistentKey:'observatory', frameSource:'/admin/workflow-observatory', html:''});
            assert.equal(obsFrame.src, '/admin/workflow-observatory?run=abc');
            assert.equal(obsFrame.activeFlag, true);
            assert.equal(studioFrame.activeFlag, false);
            activate(dock, {persistentKey:'observatory', frameSource:'/admin/workflow-observatory?run=zzz', html:''});
            assert.equal(obsFrame.src, '/admin/workflow-observatory?run=zzz');
            assert.equal(obsFrame.attrs['data-frame-source'], '/admin/workflow-observatory?run=zzz');
            activate(dock, {persistentKey:'admin-studio', frameSource:'/admin/studio', html:''});
            assert.equal(studioFrame.activeFlag, true);
            assert.equal(obsFrame.activeFlag, false);
            assert.equal(obsFrame.src, '/admin/workflow-observatory?run=zzz');

            const oldScroll = {scrollTop:420};
            const nextScroll = {scrollTop:0};
            let replaced = false;
            const root = {
              matches(){ return false; },
              querySelector(){ return replaced ? nextScroll : oldScroll; },
              set innerHTML(value){ replaced = true; this.value = value; }
            };
            vm.runInContext('replaceViewHtml', context)(root, '<div class="view-scroll"></div>');
            assert.equal(nextScroll.scrollTop, 420);

            const directRoot = {
              scrollTop:85,
              matches(){ return false; },
              querySelector(){ return null; },
              set innerHTML(value){ this.scrollTop = 0; this.value = value; }
            };
            vm.runInContext('replaceViewHtml', context)(directRoot, '<div>updated</div>');
            assert.equal(directRoot.scrollTop, 85);
            """;

        var result = await RunNodeAsync(script, html);

        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("data-persistent-view=\"observatory\"");
        html.Should().Contain(":root{--topbar-h:0px!important}");
    }

    [Fact]
    public async Task AdminShell_ShouldMirrorObservatoryImmersiveState()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('function setAdminImmersive(');
            const end = html.indexOf('\nfunction breadcrumb(', start);
            assert.notEqual(start, -1); assert.notEqual(end, -1);
            const changes = [];
            const context = {document:{body:{classList:{toggle(name,enabled){changes.push([name,enabled]);}}}}};
            vm.createContext(context);
            vm.runInContext(html.slice(start,end),context);
            vm.runInContext('setAdminImmersive',context)(true);
            assert.deepEqual(changes,[['observatory-immersive',true]]);
            """;

        var result = await RunNodeAsync(script, html);
        result.ExitCode.Should().Be(0, result.Error);
        html.Should().Contain("msg.type==='observatory-immersive'");
        html.Should().Contain("body.observatory-immersive .rail");
        html.Should().Contain("body.observatory-immersive .app-header");
        html.Should().Contain("body.observatory-immersive #acctw");
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
        cqrs.Should().Contain("未解决失败");
        cqrs.Should().Contain("singleSourceVersionGap");
        cqrs.Should().Contain("Envelope Inspector");
        cqrs.Should().Contain("function loadScopeIntrospection(scopeActorId)");
        cqrs.Should().Contain("尚无最近 committed envelope 元数据");
        cqrs.Should().Contain("function openAdminObservatory(scopeId)");
        cqrs.Should().Contain("function readDeepLinkFilters()");
        cqrs.Should().Contain("本页回答：投影收到了什么");
        cqrs.Should().Contain("版本差只在同一个权威 source actor 轴上展示");
        cqrs.Should().NotContain("observed − successful");
        cqrs.Should().Contain("if((s.retryExhaustedFailureCount||0) > 0)");
        cqrs.Should().NotContain("if((s.retryExhaustedTotal||0) > 0)");
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
                  receivedEnvelopeTotal:12,
                  attemptedEnvelopeTotal:11,
                  successfulMaterializationTotal:10,
                  failedAttemptTotal:1,
                  retryExhaustedTotal:0,
                  retryExhaustedFailureCount:0,
                  unresolvedFailureCount:1,
                  failureDiagnosticDroppedTotal:0,
                  sourceVersions:[{sourceActorId:'actor-alpha',highestSeenVersion:11,lastSuccessfulVersion:10,versionGap:1}],
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
    public async Task AdminShell_Schedules_ShouldLoadScopeOwnedTeamAutomationsAndUseCanonicalActions()
    {
        await using var app = await CreateAppAsync();
        var admin = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('var SCHEDULES_DATA=[];');
            const end = html.indexOf('/* cron 预览', start);
            assert.notEqual(start, -1, 'schedule state must exist');
            assert.notEqual(end, -1, 'schedule preview must follow schedule actions');

            const listCalls = [];
            const actionCalls = [];
            const current = {
              scheduleId:'sch-current',displayName:'Current automation',cronExpression:'0 9 * * *',
              timezone:'Asia/Singapore',enabled:true,scheduleKind:1,targetKind:1,
              fireCount:6,failureCount:1,lastError:'',lastErrorCode:'',lastAuthorizationErrorCode:'',
              teamAutomationLifecycleStatus:2,teamOwnerScopeId:'scope/alpha',teamId:'team-alpha',
              teamOwnerMemberId:'m-alpha',stateVersion:17
            };
            const legacy = {
              scheduleId:'sch-legacy',displayName:'Legacy generic',cronExpression:'0 8 * * *',
              timezone:'UTC',enabled:true,scheduleKind:1,targetKind:1,fireCount:4,failureCount:2,
              lastError:'legacy failure',lastErrorCode:'LEGACY_FAILURE'
            };
            const context = {
              Promise, Date, JSON, encodeURIComponent,
              ACCOUNT:{scope:'scope/alpha',admin:true},
              adminJson(url) {
                listCalls.push(url);
                return Promise.resolve(url.includes('ownerKind=studio_member_automation')
                  ? {items:[current]} : {items:[legacy]});
              },
              adminApi(url, options) { actionCalls.push({url,options}); return Promise.resolve({ok:true}); },
              toast() {}, _rand(){ return 'nonce-alpha'; }, confirm(){ return true; },
              setInterval(){ return 1; }, clearInterval() {}, document:{hidden:false}
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async () => {
              await context.loadSchedules();
              assert.deepEqual(listCalls, [
                '/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope%2Falpha&take=200&includeTotalCount=true',
                '/api/schedules?take=50'
              ]);
              assert.equal(context.SCHEDULES_DATA.length, 1);
              assert.equal(context.SCHEDULES_DATA[0].ownerScopeId, 'scope/alpha');
              assert.equal(context.SCHEDULES_DATA[0].teamId, 'team-alpha');
              assert.equal(context.SCHEDULES_DATA[0].memberId, 'm-alpha');
              assert.equal(context.SCHEDULES_LEGACY_DATA.length, 1);
              assert.equal(context.SCHEDULES_LEGACY_DATA[0].legacy, true);

              await context.schedAction('sch-current', 'pause');
              assert.equal(actionCalls.length, 1);
              assert.equal(actionCalls[0].url,
                '/api/scopes/scope%2Falpha/teams/team-alpha/members/m-alpha/automations/sch-current/pause');
              assert.equal(actionCalls[0].options.method, 'POST');
              const body = JSON.parse(actionCalls[0].options.body);
              assert.equal(body.operationId, 'admin-schedule-pause-nonce-alpha');
              assert.equal(body.idempotencyKey,
                'admin-schedule:sch-current:admin-schedule-pause-nonce-alpha');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, admin);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        admin.Should().Contain("历史 Generic 任务（只读）");
        admin.Should().Contain("这些资源来自旧 Generic 调度入口，不是当前 Team/member automation");
    }

    [Fact]
    public async Task AdminShell_Schedules_ShouldDistinguishCurrentFailuresFromHistoricalFailuresAndOverdueFires()
    {
        await using var app = await CreateAppAsync();
        var admin = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('var SCHEDULES_DATA=[];');
            const end = html.indexOf('/* cron 预览', start);
            const context = {Promise, Date, JSON, encodeURIComponent};
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            const recovered = context.mapSchedRow({
              scheduleId:'recovered',displayName:'Recovered',enabled:true,
              fireCount:6,failureCount:1,lastError:'',lastErrorCode:'',
              lastAuthorizationErrorCode:'',teamAutomationLifecycleStatus:2
            }, false);
            const runFailed = context.mapSchedRow({
              scheduleId:'run-failed',displayName:'Run failed',enabled:true,
              fireCount:2,failureCount:1,lastError:'safe failure',lastErrorCode:'DISPATCH_FAILED',
              teamAutomationLifecycleStatus:2
            }, false);
            const authorizationFailed = context.mapSchedRow({
              scheduleId:'auth-failed',displayName:'Authorization failed',enabled:true,
              fireCount:0,failureCount:0,lastAuthorizationErrorCode:'AUTH_REQUIRED',
              teamAutomationLifecycleStatus:3
            }, false);

            assert.equal(recovered.currentFailure, false,
              'a lifetime failure counter must not make a recovered schedule currently failed');
            assert.equal(runFailed.currentFailure, true);
            assert.equal(authorizationFailed.currentFailure, true);
            context.SCHED_FILTER = 'failing';
            assert.equal(context._schPass(recovered), false);
            assert.equal(context._schPass(runFailed), true);
            assert.equal(context._schPass(authorizationFailed), true);
            assert.equal(context._schNext('2020-01-01T00:00:00Z'), '已逾期');
            """;

        var result = await RunNodeAsync(script, admin);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
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

    [Fact]
    public async Task AdminShell_Authentication_ShouldRefreshTokensBeforeExpiryAndRetryOneUnauthorizedRequest()
    {
        await using var app = await CreateAppAsync();
        var admin = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');
            const start = html.indexOf('var OIDC=');
            const end = html.indexOf('/* ② 系统状态', start);
            assert.notEqual(start, -1, 'admin auth script must exist');
            assert.notEqual(end, -1, 'admin auth script boundary must exist');

            const stored = new Map();
            const calls = [];
            let phase = 'proactive';
            let releaseRefresh = null;
            function response(status, body) {
              return {
                status,
                ok: status >= 200 && status < 300,
                json: async () => body,
                text: async () => JSON.stringify(body ?? {}),
              };
            }
            const localStorage = {
              getItem: key => stored.has(key) ? stored.get(key) : null,
              setItem: (key, value) => stored.set(key, value),
              removeItem: key => stored.delete(key),
            };
            const context = {
              BACKEND_CONSOLE_CONFIG: {
                authority: 'https://id.example.test',
                clientId: 'client-example',
                scope: 'openid profile',
                resources: [],
                storageKey: 'console:test',
              },
              location: {origin:'https://console.example.test',pathname:'/admin',hash:''},
              localStorage,
              sessionStorage: {setItem(){},removeItem(){}},
              fetch: async (input, init) => {
                calls.push({input:String(input),init});
                if(String(input) === 'https://id.example.test/oauth/token') {
                  if(phase === 'logout') {
                    return await new Promise(resolve => {
                      releaseRefresh = () => resolve(response(200, {
                        access_token: 'late-access',
                        refresh_token: 'late-refresh',
                        expires_in: 900,
                        token_type: 'Bearer',
                      }));
                    });
                  }
                  if(phase === 'inactive') {
                    return response(400, {
                      error:'invalid_client',
                      error_description:'OAuth client is inactive',
                    });
                  }
                  return response(200, {
                    access_token: phase === 'proactive' ? 'proactive-access' : phase === 'embedded' ? 'embedded-access' : 'retry-access',
                    refresh_token: phase === 'proactive' ? 'proactive-refresh' : phase === 'embedded' ? 'embedded-refresh' : 'retry-refresh',
                    expires_in: 900,
                    token_type: 'Bearer',
                  });
                }
                if(phase === 'stale' && calls.filter(call => call.input === '/api/probe').length === 1) {
                  stored.set('console:test:token', JSON.stringify({
                    access_token:'already-refreshed-access',refresh_token:'already-refreshed-refresh',expires_in:900,obtained_at:Date.now()
                  }));
                  return response(401, {});
                }
                if(phase === 'retry' && calls.filter(call => call.input === '/api/probe').length === 1) {
                  return response(401, {});
                }
                return response(200, {ok:true});
              },
              setTimeout: () => 1,
              clearTimeout() {},
              document: {getElementById:()=>null,body:{appendChild(){}},createElement:()=>({classList:{add(){},remove(){}},innerHTML:''})},
              renderAcctW() {},
              renderLoginGate() {},
              crypto: {getRandomValues(){},subtle:{}},
              alert() {},
              URL,
              URLSearchParams,
              TextEncoder,
              Uint8Array,
              console,
            };
            vm.createContext(context);
            vm.runInContext(html.slice(start, end), context);

            (async function(){
              context.setToken({access_token:'expiring-access',refresh_token:'expiring-refresh',expires_in:30,obtained_at:Date.now()});
              const proactive = await context.adminApi('/api/probe');
              assert.equal(proactive.status, 200);
              assert.equal(calls.length, 2);
              assert.equal(calls[0].input, 'https://id.example.test/oauth/token');
              assert.match(calls[0].init.body, /grant_type=refresh_token/);
              assert.match(calls[0].init.body, /refresh_token=expiring-refresh/);
              assert.equal(calls[1].init.headers.Authorization, 'Bearer proactive-access');
              assert.equal(JSON.parse(stored.get('console:test:token')).refresh_token, 'proactive-refresh');

              phase = 'retry';
              calls.length = 0;
              context.setToken({access_token:'active-access',refresh_token:'active-refresh',expires_in:3600,obtained_at:Date.now()});
              const retried = await context.adminApi('/api/probe');
              assert.equal(retried.status, 200);
              assert.equal(calls.length, 3);
              assert.equal(calls[0].init.headers.Authorization, 'Bearer active-access');
              assert.equal(calls[1].input, 'https://id.example.test/oauth/token');
              assert.equal(calls[2].init.headers.Authorization, 'Bearer retry-access');
              assert.equal(JSON.parse(stored.get('console:test:token')).refresh_token, 'retry-refresh');

              phase = 'stale';
              calls.length = 0;
              context.setToken({access_token:'stale-access',refresh_token:'stale-refresh',expires_in:3600,obtained_at:Date.now()});
              const staleRetried = await context.adminApi('/api/probe');
              assert.equal(staleRetried.status, 200);
              assert.equal(calls.length, 2, 'a stale 401 retries directly with the token already in storage');
              assert.equal(calls[1].init.headers.Authorization, 'Bearer already-refreshed-access');
              assert.equal(JSON.parse(stored.get('console:test:token')).access_token, 'already-refreshed-access');

              phase = 'embedded';
              calls.length = 0;
              const posted = [];
              context.setToken({access_token:'embedded-old',refresh_token:'embedded-old-refresh',expires_in:3600,obtained_at:Date.now()});
              await context.handleEmbeddedAuthRefresh({origin:'https://console.example.test',source:{postMessage(message,origin){posted.push({message,origin});}}},
                {requestId:'request-alpha',rejectedAccessToken:'embedded-old'});
              assert.equal(posted.length, 1);
              assert.equal(posted[0].message.type, 'auth-refresh-result');
              assert.equal(posted[0].message.refreshed, true);
              assert.equal(JSON.parse(stored.get('console:test:token')).access_token, 'embedded-access');

              assert.equal(context.showLoginGate('stale rejection', 'embedded-old'), false);
              assert.equal(JSON.parse(stored.get('console:test:token')).access_token, 'embedded-access', 'stale auth-required must preserve the new token');
              assert.equal(context.showLoginGate('current rejection', 'embedded-access'), true);
              assert.equal(stored.has('console:test:token'), false, 'only the currently rejected token may be cleared');

              phase = 'inactive';
              posted.length = 0;
              context.setToken({access_token:'inactive-old',refresh_token:'inactive-refresh',expires_in:3600,obtained_at:Date.now()});
              await context.handleEmbeddedAuthRefresh({origin:'https://console.example.test',source:{postMessage(message,origin){posted.push({message,origin});}}},
                {requestId:'request-inactive',rejectedAccessToken:'inactive-old'});
              assert.equal(posted.length, 1);
              assert.equal(posted[0].message.refreshed, false);
              assert.equal(posted[0].message.errorCode, 'OAUTH_CLIENT_INACTIVE');
              assert.match(posted[0].message.reason, /登录客户端已停用/);
              assert.match(posted[0].message.reason, /重复登录不会恢复/);

              phase = 'logout';
              context.setToken({access_token:'logout-access',refresh_token:'logout-refresh',expires_in:3600,obtained_at:Date.now()});
              const pendingRefresh = context.refreshAccessToken(true);
              while(!releaseRefresh) await new Promise(resolve => setImmediate(resolve));
              context.clearToken();
              releaseRefresh();
              assert.equal(await pendingRefresh, null);
              assert.equal(stored.has('console:test:token'), false, 'a late refresh response must not restore a logged-out session');
            })().catch(error => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, admin);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
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
        builder.Configuration["Aevatar:BackendConsole:NyxWebBaseUrl"] = "https://web.example.test";
        builder.Configuration["Aevatar:BackendConsole:StorageKey"] = "console:test";
        builder.Configuration["Aevatar:BackendConsole:DefaultReturnPath"] = "/admin";
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        var app = builder.Build();
        app.MapAdminConsoleEndpoints();
        app.MapAIPageEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapDeliveryConsoleEndpoints();
        app.MapCqrsObservatoryPageEndpoints();
        app.MapVoiceConsoleEndpoints();
        app.MapWorkflowSkillsEndpoints();
        await app.StartAsync();
        return app;
    }
}
