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
        html.Should().Contain("data-ap-skill-search");
        html.Should().Contain("/api/workflow/skills/'+encodeURIComponent(guid)+'/exact");
        html.Should().Contain("loadAgentProfileBindings()");
        html.Should().Contain("AGENT_PROFILE_STATE.systemBinding&&AGENT_PROFILE_STATE.systemBinding.etag");
        html.Should().Contain("agentProfileField('Maximum tools','maximumTools'");
        html.Should().Contain("仅影响新建实例");
        html.Should().Contain("已接受，等待提交/投影");
        html.Should().Contain("其他人已修改此 Profile");
        html.Should().Contain("投影暂时不可用");
        html.Should().Contain("window.addEventListener('beforeunload',agentProfileBeforeUnload)");
        html.Should().Contain("@media (max-width:768px)");
        html.Should().NotContain("data-ap-field=\"rawJson\"");
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

            const searchSource = functionSource('agentProfileSearchSkills', 'agentProfileSelectSkill');
            assert.ok(searchSource.indexOf('agentProfileCaptureDraft(root);') >= 0);
            assert.ok(searchSource.indexOf('agentProfileCaptureDraft(root);') < searchSource.indexOf('render();'));
            const selectSource = functionSource('agentProfileSelectSkill', 'agentProfileEditorHtml');
            assert.ok(selectSource.indexOf('agentProfileCaptureDraft(root);') >= 0);
            assert.ok(selectSource.indexOf('agentProfileCaptureDraft(root);') < selectSource.indexOf('render();'));
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
              AGENT_PROFILE_STATE:{
                detail:{draft:{runtimeProfile:{members:[{skillRef:{}}]}}},
                skillRequest:0, skillMemberIndex:null, skillQuery:'', skillResults:[],
                skillLoading:false, skillError:null, skillProofs:{}
              },
              root:{},
              render() {},
              agentProfileCaptureDraft() {},
              agentProfileJson() {
                return new Promise(resolve => { context.resolveExact = resolve; });
              }
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('agentProfileResetSkillSearch', 'agentProfileOwnerEndpoint')}
              ${functionSource('agentProfileApplyExactSkill', 'agentProfileCaptureDraft')}
              ${functionSource('agentProfileSelectSkill', 'agentProfilePublicSummaryHtml')}
            `, context);

            (async function() {
              const pending = vm.runInContext(
                "agentProfileSelectSkill('skill-guid', root, '0')", context);
              vm.runInContext(
                'agentProfileResetSkillSearch(); AGENT_PROFILE_STATE.detail = null', context);
              context.resolveExact({body:{
                guid:'22222222-2222-4222-8222-222222222222', literalVersion:'2.3',
                name:'new-skill', publisher:'new-publisher'
              }});
              await pending;

              assert.equal(context.AGENT_PROFILE_STATE.detail, null);
              assert.equal(context.AGENT_PROFILE_STATE.skillMemberIndex, null);
              assert.equal(context.AGENT_PROFILE_STATE.skillLoading, false);
              assert.equal(context.AGENT_PROFILE_STATE.skillError, null);
              assert.deepEqual(Object.keys(context.AGENT_PROFILE_STATE.skillProofs), []);
              assert.deepEqual(Array.from(context.AGENT_PROFILE_STATE.skillResults), []);
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
              ${functionSource('agentProfileRuntime', 'agentProfileMemberHtml')}
              ${functionSource('agentProfileMemberHtml', 'agentProfileDiagnosticsHtml')}
              ${functionSource('agentProfileDiagnosticsHtml', 'agentProfileSkillSearchHtml')}
              ${functionSource('agentProfileSkillSearchHtml', 'agentProfileSearchSkills')}
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
              ${functionSource('agentProfilePublicSummaryHtml', 'agentProfileEditorHtml')}
              ${functionSource('agentProfileEditorHtml', 'agentProfileCollectFields')}
            `, context);
            const editor = vm.runInContext('agentProfileEditorHtml()', context);
            assert.match(editor, /Public research/);
            assert.match(editor, /Published evidence assistant/);
            assert.match(editor, /system\/public-research/);
            assert.match(editor, /已发布 r4/);
            assert.match(editor, /设为我的默认/);
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
