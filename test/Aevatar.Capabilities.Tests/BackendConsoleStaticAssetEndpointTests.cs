using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
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
        html.Should().Contain("\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\"]");
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
            html.Should().Contain("id=\"obs-run-in\"");
            html.Should().Contain("/api/workflow/observatory/admin/runs/");
            html.Should().Contain("/api/workflow/observatory/runs/");
            html.Should().NotContain("obsLooksLikeRunId");
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
    public async Task AdminShell_ObservatoryPolling_ShouldKeepCachedDetailVisible()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("function loadObsDetail(runId,rerender,refresh)");
        html.Should().Contain("detail:previousDetail");
        html.Should().Contain("if(cache&&cache.loading&&!d)");
        html.Should().Contain("loadObsDetail(selected.id,function(){ reList();");
        html.Should().NotContain("delete OBS_DETAIL[selected.id]");
    }

    [Fact]
    public async Task AdminShell_ObservatoryPolling_ShouldStopAfterHandled404UntilExplicitReloadRecovers()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const html = require('node:fs').readFileSync(0, 'utf8');

            (async function() {
            function functionSource(name, nextName) {
              const start = html.indexOf('function ' + name + '(');
              const end = html.indexOf('\nfunction ' + nextName + '(', start);
              assert.notEqual(start, -1, name + ' must exist in the served admin asset');
              assert.notEqual(end, -1, nextName + ' must follow ' + name);
              return html.slice(start, end);
            }

            const context = { assert, setImmediate };
            vm.createContext(context);
            vm.runInContext(`
              var runId = 'run-a';
              var OBS_DETAIL = {}, OBS_DETAIL_REQUESTS = {}, OBS_DETAIL_SCOPE_VERSION = 0;
              var OBS_RUNS = [], OBS_RUNS_ERR = null, OBS_POLL_TIMER = null;
              var OBS_STATE = { selectedId: runId, immersive: false };
              var detailRequests = 0, intervalCallback = null, clickHandler = null;
              var detailResponses = [
                function() { return Promise.reject({ status: 404 }); },
                function() { return Promise.resolve({ runId: runId, visible: true }); }
              ];

              function adminJson(path) {
                if (path === '/graph') return Promise.resolve(null);
                assert.equal(path, '/detail');
                detailRequests++;
                return detailResponses.shift()();
              }
              function obsDetailRequestBase() { return '/detail'; }
              function obsGraphRequestUrl() { return '/graph'; }
              function mapObsDetail(detail) { return detail; }
              function obsUpsertRunFromDetail() {}
              function obsDetailsEqual(left, right) { return JSON.stringify(left) === JSON.stringify(right); }
              function loadObsRuns(rerender) { if (rerender) rerender(); return Promise.resolve(); }
              function obsSelected() { return { id: OBS_STATE.selectedId }; }
              function obsRunsFiltered() { return OBS_RUNS; }
              function obsSetImmersive() {}
              function curParts() { return ['observatory']; }
              function defaultModule() { return 'observatory'; }
              function setInterval(callback) { intervalCallback = callback; return 1; }
              function clearInterval() {}
              var document = { hidden: false };

              ${functionSource('obsNextDetailRequest', 'obsInvalidateDetail')}
              ${functionSource('obsDetailRequestCurrent', 'obsDetailRequestBase')}
              ${functionSource('loadObsDetail', 'loadObsGraph')}
              ${functionSource('loadObsGraph', 'loadObsResolveScope')}
              ${functionSource('bindObservatory', 'cqrsPipeline')}
            `, context);

            await vm.runInContext(`(async function() {
              var root = {
                querySelector: function() { return null; },
                addEventListener: function(type, handler) {
                  if (type === 'click') clickHandler = handler;
                }
              };
              bindObservatory(root);
              await new Promise(setImmediate);

              assert.equal(detailRequests, 1);
              assert.equal(OBS_DETAIL[runId].notFound, true);

              intervalCallback();
              await new Promise(setImmediate);
              assert.equal(detailRequests, 1, 'automatic polling must stop after the handled 404');

              var reload = { getAttribute: function() { return 'obsReload'; } };
              clickHandler({ target: {
                closest: function(selector) { return selector === '[data-act]' ? reload : null; }
              }});
              await new Promise(setImmediate);

              assert.equal(detailRequests, 2, 'explicit reload must retry the detail request');
              assert.equal(OBS_DETAIL[runId].notFound, undefined);
              assert.equal(OBS_DETAIL[runId].detail.runId, runId);
              assert.equal(OBS_DETAIL[runId].detail.visible, true);
            })()`, context);
            })().catch(function(error) {
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
        process.Should().NotBeNull("Node.js is required to execute the shipped admin polling behavior");
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(html);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        process.ExitCode.Should().Be(0, $"the causal polling regression should pass. stdout: {output} stderr: {error}");
    }

    [Fact]
    public async Task AdminShell_ObservatoryRouteState_ShouldDefaultToMineAndBuildSupportedFilters()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("scope:'mine',status:'',origin:'',definition:'',schedule:'',from:'',to:''");
        html.Should().Contain("if(OBS_STATE.scope==='all') p.set('scope','__all__')");
        html.Should().Contain("['status','origin','definition','schedule','from','to'].forEach");
        html.Should().Contain("p.set('take','100')");
        html.Should().Contain("if((key==='from'||key==='to')&&value&&!obsValidTimestamp(value)) return");
        html.Should().NotContain("scope:'__all__'");
        html.Should().NotContain("statusFilter:[]");
    }

    [Fact]
    public async Task AdminShell_ObservatoryRouteState_ShouldUseCanonicalHashKeys()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(
            "var OBS_QUERY_KEYS=['scope','status','origin','definition','schedule','from','to','run','tab']");
        html.Should().Contain("OBS_STATUS_VALUES.indexOf(q.status)>=0");
        html.Should().Contain("OBS_TAB_VALUES.indexOf(q.tab)>=0");
        html.Should().Contain("selectedId:q.run||null");
    }

    [Fact]
    public async Task AdminShell_ObservatoryDeepLinks_ShouldPreserveExactScopeAndSchedule()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("data-scope=\"'+esc(scopeId)+'\"");
        html.Should().Contain("var runScope=runEl.getAttribute('data-scope')");
        html.Should().Contain("obsNavigate({run:rid,scope:runScope||'mine'})");
        html.Should().Contain("obsNavigate({schedule:act.getAttribute('data-id'),run:null})");
        html.Should().NotContain("OBS_STATE.scope='__all__'");
    }

    [Fact]
    public async Task AdminShell_ObservatoryDetail_ShouldFollowObservationIntentAndPinFilteredRun()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("if(OBS_STATE.scope==='all'||OBS_DIRECT_RUNS[runId])");
        html.Should().Contain("if(OBS_STATE.scope&&OBS_STATE.scope!=='mine') p.set('scope',OBS_STATE.scope)");
        html.Should().Contain("return base+(p.toString()?'?'+p.toString():'')");
        html.Should().Contain("function obsPinnedRun()");
        html.Should().Contain("return obsRunsFiltered().some(function(r){return r.id===OBS_STATE.selectedId;})");
        html.Should().Contain("data-obs-pinned=\"true\"");
        html.Should().Contain("不在当前筛选结果中");
        html.Should().NotContain("obsUpsertRunFromDetail(OBS_STATE.selectedId");
    }

    [Fact]
    public async Task AdminShell_ObservatoryNavigation_ShouldClearDirectLookupIntentForNormalRunSelection()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(
            "var targetScope=('scope' in overrides)?(overrides.scope||'mine'):OBS_STATE.scope;");
        html.Should().Contain(
            "if(overrides.run&&targetScope!=='all'&&OBS_DIRECT_RUNS[overrides.run])");
        html.Should().Contain("delete OBS_DIRECT_RUNS[overrides.run]");
        html.Should().Contain("obsInvalidateDetail(overrides.run)");
        html.Should().Contain("if(directIntentCleared&&location.hash===next){ render(); return; }");
        html.Should().Contain("if(row.getAttribute('data-obs-pinned')==='true') return;");

        // Selecting a run while observing every scope keeps the admin-endpoint intent, because a
        // cross-scope list row proves nothing about the current account's own scope.
        html.Should().NotContain(
            "if(overrides.run&&overrides.scope!=='all'&&OBS_DIRECT_RUNS[overrides.run])");
    }

    [Fact]
    public async Task AdminShell_ObservatoryDetail_ShouldIgnoreResponsesFromAnOlderScopeOrRequest()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("var OBS_DETAIL_REQUESTS={};");
        html.Should().Contain("var scopeVersion=OBS_DETAIL_SCOPE_VERSION");
        html.Should().Contain("var requestId=obsNextDetailRequest(runId)");
        html.Should().Contain("OBS_DETAIL_SCOPE_VERSION++");

        // Both the fulfilled and the rejected detail handler must drop stale responses, otherwise a
        // late failure from an older scope or request overwrites the current run detail.
        const string staleGuard =
            "if(!obsDetailRequestCurrent(runId,requestId,scopeVersion)) return false;";
        Regex.Matches(html, Regex.Escape(staleGuard)).Count.Should().Be(2);
    }

    [Fact]
    public async Task AdminShell_ObservatoryNavigation_ShouldNotTreatEveryRunAttributeAsFleetLink()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("t.closest('[data-run][data-scope]')");
        html.Should().Contain("obsNavigate({run:or.getAttribute('data-run'),scope:'mine'})");
        html.Should().Contain("obsNavigate({run:act.getAttribute('data-run'),scope:'mine'})");
    }

    [Fact]
    public async Task AdminShell_ObservatoryStatus_ShouldPresentStoppedRunsHonestly()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("stopped:'stopped'");
        html.Should().Contain("stopped:['tag-idle','已停止','■']");
    }

    [Fact]
    public async Task AdminShell_ObservatoryEmptyState_ShouldDistinguishFiltersAndLocalSearch()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("function obsEmptyList()");
        html.Should().Contain("当前加载结果中没有匹配运行");
        html.Should().Contain("当前服务端筛选下没有运行");
        html.Should().Contain("当前 scope 暂无运行记录");
        html.Should().Contain("data-act=\"obsLocalSearchClear\"");
        html.Should().NotContain("该员工还没有执行记录。',null]");
    }

    [Fact]
    public async Task AdminShell_ObservatoryWorkspace_ShouldExposeScopeRailAndAdminTools()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("class=\"obs-scope-switch\" role=\"group\" aria-label=\"观测 scope\"");
        html.Should().Contain("aria-pressed=\"'+(OBS_STATE.scope===");
        html.Should().Contain("data-act=\"obsRailToggle\"");
        html.Should().Contain("class=\"obs-admin-tools\"");
        html.Should().Contain("data-act=\"obsLocalSearch\"");
        html.Should().Contain("显示 '+visible+' / 已加载 '+loaded");
        html.Should().NotContain("class=\"obs-adminbar\"");
    }

    [Fact]
    public async Task AdminShell_ObservatoryMobileFilters_ShouldOverlayInsteadOfCompressingDetail()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain(".obs-filterbar{position:relative;z-index:40;");
        html.Should().Contain(
            ".filter-body{position:absolute;top:100%;left:8px;right:8px;max-height:min(56vh,430px);overflow:auto;");
        html.Should().Contain(".obs-filter-search input{height:100%;");
        html.Should().Contain(".obs-scope-notice button{min-height:24px;");
        html.Should().Contain(".obs-clear-all{height:24px;");
    }

    [Fact]
    public async Task AdminShell_ObservatoryImmersiveMode_ShouldBeExplicitSessionState()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("data-act=\"obsImmersive\"");
        html.Should().Contain("sessionStorage.setItem(OBS_SESSION_IMMERSIVE,enabled?'1':'0')");
        html.Should().Contain("if(OBS_STATE.immersive){ obsSetImmersive(false); render(); }");
        html.Should().Contain("body.obs-immersive .rail");
        html.Should().Contain("body.obs-immersive .app-header");
        html.Should().Contain("class=\"obs-immersive-bar\"");
    }

    [Fact]
    public async Task AdminShell_ObservatoryGraph_ShouldPreserveEdgesAndDeriveNodeStatus()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("function obsMapGraph(graph,steps,runStatus)");
        html.Should().Contain("rootNodeId:graph.rootNodeId||''");
        html.Should().Contain("edges:validEdges");
        html.Should().Contain("st:step?step.status:topologyStatus");
        html.Should().NotContain(".join('<div class=\"dag-link\"></div>')");
    }

    [Fact]
    public async Task AdminShell_ObservatoryGraph_ShouldExposeInteractiveDagControlsAndNodeDetails()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("function obsGraphView(r)");
        html.Should().Contain("function obsBindGraph(root)");
        html.Should().Contain("addEventListener('wheel'");
        html.Should().Contain("addEventListener('pointerdown'");
        html.Should().Contain("data-obs-graph-act=\"fit\"");
        html.Should().Contain("data-obs-node=\"");
        html.Should().Contain("function obsOpenGraphNode(nodeId)");
    }

    [Fact]
    public async Task AdminShell_ObservatoryDetail_ShouldSurfaceExecutionEvidence()
    {
        await using var app = await CreateAppAsync();
        var html = await app.GetTestClient().GetStringAsync("/admin");

        html.Should().Contain("function obsNarrativeView(r)");
        html.Should().Contain("function obsRenderToolCall(tc,forceOpen)");
        html.Should().Contain("argumentsJson");
        html.Should().Contain("resultJson");
        html.Should().Contain("最终输出 · finalOutput");
        html.Should().Contain("输入 · input");
        html.Should().Contain("outputPreview 为 240 字预览");
        html.Should().Contain("派生视图：由 diagnostics + committed timeline 组装");
        html.Should().Contain("promptTokens:obsNum(ut.promptTokens)");
        html.Should().Contain("completionTokens:obsNum(ut.completionTokens)");
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

    private static string ScheduleRequestSnippet(string html, string scheduleCall)
    {
        var index = html.IndexOf(scheduleCall, StringComparison.Ordinal);
        index.Should().BeGreaterThanOrEqualTo(0, $"static producer should call {scheduleCall}");
        var start = Math.Max(0, index - 300);
        var length = Math.Min(html.Length - start, 700);
        return html.Substring(start, length);
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
