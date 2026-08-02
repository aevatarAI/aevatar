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
              if(!refreshingSystemEditor.includes('data-ap-field="cohortBasisPoints" type="number" value="0" disabled'))
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
              "agentProfileRolloutFromBinding({enabled:false,cohortBasisPoints:2750})",
              context);
            assert.equal(rollout.enabled, false);
            assert.equal(rollout.cohortBasisPoints, 2750);

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
            const obsFrame = frameStub('observatory', '/workflow/observatory?run=abc');
            const studioFrame = frameStub('workflow-studio', '/workflow/studio');
            const dock = {
              querySelector(sel){
                if (sel.indexOf('"observatory"') >= 0) return obsFrame;
                if (sel.indexOf('"workflow-studio"') >= 0) return studioFrame;
                return null;
              },
              querySelectorAll(){ return [obsFrame, studioFrame]; },
              insertAdjacentHTML(){ assert.fail('existing dock frames must be reused, not recreated'); }
            };
            const activate = vm.runInContext('activateDockFrame', context);
            activate(dock, {persistentKey:'observatory', frameSource:'/workflow/observatory', html:''});
            assert.equal(obsFrame.src, '/workflow/observatory?run=abc');
            assert.equal(obsFrame.activeFlag, true);
            assert.equal(studioFrame.activeFlag, false);
            activate(dock, {persistentKey:'observatory', frameSource:'/workflow/observatory?run=zzz', html:''});
            assert.equal(obsFrame.src, '/workflow/observatory?run=zzz');
            assert.equal(obsFrame.attrs['data-frame-source'], '/workflow/observatory?run=zzz');
            activate(dock, {persistentKey:'workflow-studio', frameSource:'/workflow/studio', html:''});
            assert.equal(studioFrame.activeFlag, true);
            assert.equal(obsFrame.activeFlag, false);
            assert.equal(obsFrame.src, '/workflow/observatory?run=zzz');

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
                  return response(200, {
                    access_token: phase === 'proactive' ? 'proactive-access' : 'retry-access',
                    refresh_token: phase === 'proactive' ? 'proactive-refresh' : 'retry-refresh',
                    expires_in: 900,
                    token_type: 'Bearer',
                  });
                }
                if(phase === 'retry' && calls.filter(call => call.input === '/api/probe').length === 1) {
                  return response(401, {});
                }
                return response(200, {ok:true});
              },
              setTimeout: () => 1,
              clearTimeout() {},
              document: {getElementById:()=>null,body:{appendChild(){}},createElement:()=>({classList:{add(){},remove(){}},innerHTML:''})},
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
