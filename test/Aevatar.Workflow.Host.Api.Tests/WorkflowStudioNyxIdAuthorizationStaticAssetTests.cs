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
        transport.Should().Contain("authorizedFetch(\"/api/chat\"");
        transport.Should().NotContain("setToken(token);\n}",
            "the standalone OAuth callback must branch before storing a review token");
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
    public async Task WorkflowStudio_ActionProtocol_ShouldAdmitExactKeyActionsAndFailClosed()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const identity = {
              schemaVersion:4,
              actorId:'conversation-alpha',
              originTurnId:'turn-alpha',
              taskId:'task-alpha',
              stepId:'step-alpha',
              actionRequestId:'action-alpha'
            };
            const create = context.validateActionRequest({
              ...identity,
              action:'key.create',
              params:{
                name:'studio-agent',
                platform:'codex',
                allowedServiceIds:['service-alpha','service-beta']
              }
            });
            assert.deepEqual(JSON.parse(JSON.stringify(create)), {
              ...identity,
              action:'key.create',
              params:{
                name:'studio-agent',
                platform:'codex',
                allowedServiceIds:['service-alpha','service-beta']
              }
            });
            assert.equal(Object.isFrozen(create), true);
            assert.equal(Object.isFrozen(create.params.allowedServiceIds), true);

            const rotate = context.validateActionRequest({
              ...identity,
              actionRequestId:'action-rotate',
              action:'key.rotate',
              params:{keyId:'key-predecessor'}
            });
            assert.deepEqual(JSON.parse(JSON.stringify(rotate.params)), {keyId:'key-predecessor'});

            const invalid = [
              {...identity,action:'key.create',params:{name:'studio-agent',platform:'codex',allowedServiceIds:[]}},
              {...identity,action:'key.create',params:{name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha','service-alpha']}},
              {...identity,action:'key.create',params:{name:'studio-agent',platform:'codex',allowedServiceIds:['service/alpha']}},
              {...identity,action:'key.create',params:{name:' studio-agent',platform:'codex',allowedServiceIds:['service-alpha']}},
              {...identity,action:'key.create',params:{name:'studio-agent',platform:'Bearer secret-value',allowedServiceIds:['service-alpha']}},
              {...identity,action:'key.create',params:{name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha'],allowAllServices:true}},
              {...identity,action:'key.rotate',params:{keyId:'key-predecessor',replacementKeyId:'key-successor'}},
              {...identity,action:'key.rotate',params:{keyId:'key/predecessor'}},
              {...identity,action:'key.delete',params:{keyId:'key-predecessor'}}
            ];
            for (const value of invalid) {
              assert.throws(() => context.validateActionRequest(value), context.ProtocolValidationError);
            }
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActionContinuation_ShouldRequireActionSpecificCompletedResources()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const continuation = (disposition, resource) => ({
              type:'action.continue',
              clientRequestId:'client-action-alpha',
              originTurnId:'turn-alpha',
              actions:[{
                actionRequestId:'action-alpha',
                originTurnId:'turn-alpha',
                disposition,
                ...(resource ? {resource} : {})
              }]
            });

            for (const action of ['key.create','key.rotate']) {
              const accepted = context.validateActionContinuation(
                continuation('completed', {key:{keyId:'key-successor'}}),
                {expectedAction:action}
              );
              assert.equal(accepted.actions[0].resource.key.keyId, 'key-successor');
              assert.throws(
                () => context.validateActionContinuation(
                  continuation('completed', {userService:{userServiceId:'service-alpha'}}),
                  {expectedAction:action}
                ),
                context.ProtocolValidationError
              );
              assert.throws(
                () => context.validateActionContinuation(continuation('completed'), {expectedAction:action}),
                context.ProtocolValidationError
              );
              const declined = context.validateActionContinuation(
                continuation('declined'),
                {expectedAction:action}
              );
              assert.equal(declined.actions[0].disposition, 'declined');
              assert.equal(Object.prototype.hasOwnProperty.call(declined.actions[0], 'resource'), false);
            }

            assert.throws(
              () => context.validateActionContinuation(
                continuation('completed', {key:{keyId:'key-successor'}}),
                {expectedAction:'service.connect'}
              ),
              context.ProtocolValidationError
            );
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_KeyActionTransport_ShouldConstructAllowlistedMutationsAndNormalizeEffects()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('const KEY_ACTION_SECRET_FIELD');
            const end = source.indexOf('\nfunction proxyResourceForSlug(', start);
            assert.notEqual(start, -1, 'key action transport helpers must exist');
            assert.notEqual(end, -1, 'key action helper boundary must exist');
            const context = { URL, Date, structuredClone };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end).replace(/^export /gm, ''), context);

            const created = context.buildKeyActionMutation('key.create', {
              actionRequestId:'action-create',
              name:'studio-agent',
              platform:'codex',
              allowedServiceIds:['service-alpha','service-beta']
            });
            assert.deepEqual(JSON.parse(JSON.stringify(created)), {
              path:'/api/v1/assistant/actions/key-create',
              body:{
                actionRequestId:'action-create',
                name:'studio-agent',
                platform:'codex',
                allowedServiceIds:['service-alpha','service-beta']
              }
            });

            const rotated = context.buildKeyActionMutation('key.rotate', {
              actionRequestId:'action-rotate', keyId:'key-predecessor'
            });
            assert.deepEqual(JSON.parse(JSON.stringify(rotated)), {
              path:'/api/v1/assistant/actions/key-rotate',
              body:{actionRequestId:'action-rotate',keyId:'key-predecessor'}
            });
            assert.throws(
              () => context.buildKeyActionMutation('key.create', {
                actionRequestId:'action-create', name:'studio-agent', platform:'codex',
                allowedServiceIds:['service-alpha'], rawBearer:'forbidden'
              }),
              context.KeyActionVerificationError
            );
            assert.throws(
              () => context.buildKeyActionMutation('key.delete', {actionRequestId:'action-delete'}),
              context.KeyActionVerificationError
            );

            const effect = context.normalizeKeyActionEffect('key.create', {
              resource:{keyId:'key-created'}, replayed:false, fullKey:'nyxid_ag_one_time_value'
            });
            assert.deepEqual(JSON.parse(JSON.stringify(effect)), {
              resource:{keyId:'key-created'}, replayed:false, fullKey:'nyxid_ag_one_time_value'
            });
            const replay = context.normalizeKeyActionEffect('key.create', {
              resource:{keyId:'key-created'}, replayed:true
            });
            assert.deepEqual(JSON.parse(JSON.stringify(replay)), {
              resource:{keyId:'key-created'}, replayed:true
            });
            const rotateEffect = context.normalizeKeyActionEffect('key.rotate', {
              resource:{keyId:'key-successor'}, replayed:false,
              requestedAt:'2026-08-13T08:00:00Z', fullKey:'nyxid_ag_rotated_once'
            });
            assert.equal(rotateEffect.requestedAt, '2026-08-13T08:00:00Z');
            assert.throws(
              () => context.normalizeKeyActionEffect('key.create', {
                resource:{keyId:'key-created'}, replayed:true, fullKey:'must-not-replay'
              }),
              context.KeyActionVerificationError
            );
            assert.throws(
              () => context.normalizeKeyActionEffect('key.rotate', {
                resource:{keyId:'key-successor'}, replayed:false,
                requestedAt:'not-a-time', fullKey:'nyxid_ag_rotated_once'
              }),
              context.KeyActionVerificationError
            );
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        transport.Should().Contain("/api/nyxid/assistant-actions/key-create");
        transport.Should().Contain("/api/nyxid/assistant-actions/key-rotate");
        transport.Should().Contain("/api/nyxid/api-keys/");
        transport.Should().Contain("/api/nyxid/keys/");
    }

    [Fact]
    public async Task WorkflowStudio_KeyCreateReadBack_ShouldRequirePersonalServicesAndExactLeastScope()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('const KEY_ACTION_SECRET_FIELD');
            const end = source.indexOf('\nfunction proxyResourceForSlug(', start);
            assert.notEqual(start, -1, 'key action transport helpers must exist');
            assert.notEqual(end, -1, 'key action helper boundary must exist');
            const context = { URL, Date, structuredClone };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end).replace(/^export /gm, ''), context);

            const request = {
              params:{
                name:'studio-agent', platform:'codex',
                allowedServiceIds:['service-alpha','service-beta']
              }
            };
            const effect = {resource:{keyId:'key-created'},replayed:false,fullKey:'one-time'};
            const service = id => ({
              id, is_active:true, credential_source:{type:'personal'},
              name:'Service', status:'connected'
            });
            assert.equal(
              context.verifyPersonalServiceReadBack('service-alpha', service('service-alpha')).verified,
              true
            );
            for (const invalid of [
              null,
              service('service-other'),
              {...service('service-alpha'),is_active:false},
              {...service('service-alpha'),credential_source:{type:'org',org_id:'org-alpha'}},
              {...service('service-alpha'),oauth_client_secret:'forbidden'}
            ]) {
              assert.throws(
                () => context.verifyPersonalServiceReadBack('service-alpha', invalid),
                context.KeyActionVerificationError
              );
            }

            const exact = {
              id:'key-created', name:'studio-agent', platform:'codex', scopes:'proxy',
              is_active:true,
              allowed_service_ids:['service-beta','service-alpha'],
              allowed_node_ids:[], allow_all_services:false, allow_all_nodes:false,
              state_version:1
            };
            const verified = context.verifyKeyCreateReadBack(request, effect, exact);
            assert.deepEqual(JSON.parse(JSON.stringify(verified)), {
              verified:true,keyId:'key-created'
            });

            const invalidKeys = [
              null,
              {...exact,id:'key-other'},
              {...exact,name:'other-agent'},
              {...exact,platform:'other-platform'},
              {...exact,scopes:'proxy read'},
              {...exact,is_active:false},
              {...exact,allowed_service_ids:['service-alpha']},
              {...exact,allowed_service_ids:['service-alpha','service-beta','service-beta']},
              {...exact,allowed_node_ids:['node-alpha']},
              {...exact,allow_all_services:true},
              {...exact,allow_all_nodes:true},
              {...exact,key_hash:'forbidden'}
            ];
            for (const invalid of invalidKeys) {
              assert.throws(
                () => context.verifyKeyCreateReadBack(request, effect, invalid),
                context.KeyActionVerificationError
              );
            }
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_KeyRotateReadBack_ShouldRequireFreshExactLineage()
    {
        var transport = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantTransport);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('const KEY_ACTION_SECRET_FIELD');
            const end = source.indexOf('\nfunction proxyResourceForSlug(', start);
            assert.notEqual(start, -1, 'key action transport helpers must exist');
            assert.notEqual(end, -1, 'key action helper boundary must exist');
            const context = { URL, Date, structuredClone };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end).replace(/^export /gm, ''), context);

            const request = {params:{keyId:'key-predecessor'}};
            const effect = {
              resource:{keyId:'key-successor'}, replayed:false,
              requestedAt:'2026-08-13T08:00:00Z', fullKey:'one-time'
            };
            const exact = {
              id:'key-successor', is_active:true,
              rotation_predecessor_id:'key-predecessor', state_version:2,
              created_at:'2026-08-13T08:00:01Z', updated_at:'2026-08-13T08:00:02Z'
            };
            const verified = context.verifyKeyRotateReadBack(request, effect, exact);
            assert.deepEqual(JSON.parse(JSON.stringify(verified)), {
              verified:true,keyId:'key-successor'
            });

            const invalid = [
              null,
              {...exact,id:'key-other'},
              {...exact,id:'key-predecessor'},
              {...exact,rotation_predecessor_id:null},
              {...exact,rotation_predecessor_id:'key-other'},
              {...exact,state_version:0},
              {...exact,state_version:'2'},
              {...exact,is_active:false},
              {...exact,created_at:'2026-08-13T07:59:59Z'},
              {...exact,updated_at:'2026-08-13T07:59:59Z'},
              {...exact,created_at:'2026-08-13T08:00:03Z',updated_at:'2026-08-13T08:00:02Z'},
              {...exact,created_at:'invalid'},
              {...exact,refresh_token:'forbidden'}
            ];
            for (const value of invalid) {
              assert.throws(
                () => context.verifyKeyRotateReadBack(request, effect, value),
                context.KeyActionVerificationError
              );
            }
            """;

        var result = await RunNodeAsync(script, transport);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_KeyActionCard_ShouldExposeOnlySafeFactsAndAwaitActorVerification()
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

            const identity = {
              schemaVersion:4, actorId:'conversation-alpha', originTurnId:'turn-alpha',
              taskId:'task-alpha', stepId:'step-alpha', actionRequestId:'action-alpha'
            };
            const created = context.buildKeyActionCardBlock({
              ...identity,
              action:'key.create',
              params:{name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha','service-beta']}
            });
            assert.deepEqual(JSON.parse(JSON.stringify(created)), {
              type:'key_action_card', block_id:'action-alpha', action:'key.create',
              identity:{
                actorId:'conversation-alpha',originTurnId:'turn-alpha',taskId:'task-alpha',
                stepId:'step-alpha',actionRequestId:'action-alpha'
              },
              title:'创建 API key', subtitle:'studio-agent · codex',
              facts:[
                {label:'名称',value:'studio-agent'},
                {label:'平台',value:'codex'},
                {label:'允许的 Services',value:'service-alpha, service-beta'}
              ],
              state:'ready', steps:[
                {title:'执行 NyxID 密钥操作',body:'浏览器直接调用 NyxID；完整密钥不会发送给 Aevatar。',done:false},
                {title:'精确读取并确认密钥',body:'读取同一 key identity，验证最小权限，并确认一次性密钥已安全保存。',done:false},
                {title:'报告 key reference 并等待 Actor 验证',body:'仅报告 keyId；Actor postcondition 精确匹配后才显示成功。',done:false}
              ],
              footer:'完整密钥仅在当前对话框显示一次 · Aevatar 只接收 keyId'
            });

            const rotated = context.buildKeyActionCardBlock({
              ...identity, actionRequestId:'action-rotate', action:'key.rotate',
              params:{keyId:'key-predecessor'}
            });
            assert.equal(rotated.title, '轮换 API key');
            assert.deepEqual(JSON.parse(JSON.stringify(rotated.facts)), [
              {label:'原 Key ID',value:'key-predecessor'}
            ]);
            const serialized = JSON.stringify({created,rotated});
            for (const forbidden of ['fullKey','full_key','keyHash','accessToken','refreshToken']) {
              assert.equal(serialized.includes(forbidden), false);
            }
            """;

        var result = await RunNodeAsync(script, blocks);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_KeyActionCompletion_ShouldRequireBrowserVerificationAndExactActorProof()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('const KEY_ACTION_CARD_ACTIONS');
            const end = source.indexOf('\nfunction connectDeepLink(', start);
            assert.notEqual(start, -1, 'key action card helpers must exist');
            assert.notEqual(end, -1, 'key action helper boundary must exist');
            const context = { JSON };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end).replace(/^export /gm, ''), context);

            assert.equal(context.keyActionResourceId({key:{keyId:'key-created'}}), 'key-created');
            assert.equal(context.keyActionResourceId({keyId:'key-created'}), 'key-created');
            assert.equal(context.keyActionResourceId({
              key:{keyId:'key-created'},userService:{userServiceId:'service-alpha'}
            }), '');
            assert.equal(context.keyActionResourceId({
              keyId:'key-created',userServiceId:'service-alpha'
            }), '');

            const request = {
              schemaVersion:4,actorId:'action-actor-alpha',originTurnId:'turn-alpha',
              taskId:'task-alpha',stepId:'step-alpha',actionRequestId:'action-alpha',
              action:'key.create',
              params:{name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha']}
            };
            const effect = {
              resource:{keyId:'key-created'},replayed:false,fullKey:'nyxid_ag_one_time_value'
            };
            assert.throws(
              () => context.buildKeyActionCompletedResource(request, effect, {browserVerified:false,savedConfirmed:true}),
              context.KeyActionCardError
            );
            assert.throws(
              () => context.buildKeyActionCompletedResource(request, effect, {browserVerified:true,savedConfirmed:false}),
              context.KeyActionCardError
            );
            assert.deepEqual(JSON.parse(JSON.stringify(
              context.buildKeyActionCompletedResource(request, effect, {browserVerified:true,savedConfirmed:true})
            )), {key:{keyId:'key-created'}});
            assert.deepEqual(JSON.parse(JSON.stringify(
              context.buildKeyActionCompletedResource(request, {...effect,replayed:true,fullKey:undefined}, {
                browserVerified:true,savedConfirmed:false
              })
            )), {key:{keyId:'key-created'}});

            const card = {
              request,report:{disposition:'completed',resource:{key:{keyId:'key-created'}}},
              status:'awaiting_verification',busy:false,error:'',note:''
            };
            const confirmedStep = {steps:new Map([['postcondition',{
              actionRequestId:'action-alpha',kind:'postcondition',status:'done',externalEffect:'confirmed'
            }]])};
            assert.equal(context.applyActorActionProof(card, {
              postconditionResult:null
            }, confirmedStep), false);
            assert.equal(card.status, 'awaiting_verification');

            const invalidProofs = [
              {verified:false,actionRequestId:'action-alpha',disposition:'completed',resource:{key:{keyId:'key-created'}}},
              {verified:true,actionRequestId:'action-other',disposition:'completed',resource:{key:{keyId:'key-created'}}},
              {verified:true,actionRequestId:'action-alpha',disposition:'failed',resource:{key:{keyId:'key-created'}}},
              {verified:true,actionRequestId:'action-alpha',disposition:'completed',resource:{userService:{userServiceId:'key-created'}}},
              {verified:true,actionRequestId:'action-alpha',disposition:'completed',resource:{key:{keyId:'key-other'}}}
            ];
            for (const proof of invalidProofs) {
              card.status = 'awaiting_verification';
              assert.equal(context.applyActorActionProof(card, {postconditionResult:proof}, {steps:new Map()}), false);
              assert.equal(card.status, 'awaiting_verification');
            }

            assert.equal(context.applyActorActionProof(card, {postconditionResult:{
              verified:true,actionRequestId:'action-alpha',disposition:'completed',
              resource:{key:{keyId:'key-created'}}
            }}, {steps:new Map()}), true);
            assert.equal(card.status, 'verified');
            assert.equal(JSON.stringify(card).includes('nyxid_ag_one_time_value'), false);
            const journeyCard = {
              request,status:'ready',busy:false,error:'',note:'',report:null,
              effectKeyId:'',replayed:null,requestedAt:'',browserVerified:false
            };
            const requests = [];
            const ioAdapter = context.createKeyActionIo(async (url, init = {}) => {
              requests.push({url,method:init.method || 'GET',body:init.body || ''});
              if (url.endsWith('/keys/service-alpha')) {
                return {ok:true,async json(){return {id:'service-alpha'};}};
              }
              if (url.endsWith('/assistant-actions/key-create')) {
                return {ok:true,async json(){return {resource:{keyId:'key-created'}};}};
              }
              if (url.endsWith('/api-keys/key-created')) {
                return {ok:true,async json(){return {id:'key-created'};}};
              }
              return {ok:false,async json(){throw new Error('upstream secret body');}};
            });
            assert.deepEqual(JSON.parse(JSON.stringify(await ioAdapter.readService('service-alpha'))), {
              id:'service-alpha'
            });
            assert.deepEqual(JSON.parse(JSON.stringify(await ioAdapter.mutate(request))), {
              resource:{keyId:'key-created'}
            });
            assert.deepEqual(JSON.parse(JSON.stringify(await ioAdapter.readKey('key-created'))), {
              id:'key-created'
            });
            assert.deepEqual(requests, [
              {url:'/api/nyxid/keys/service-alpha',method:'GET',body:''},
              {url:'/api/nyxid/assistant-actions/key-create',method:'POST',body:JSON.stringify({
                actionRequestId:'action-alpha',name:'studio-agent',platform:'codex',
                allowedServiceIds:['service-alpha']
              })},
              {url:'/api/nyxid/api-keys/key-created',method:'GET',body:''}
            ]);
            await assert.rejects(
              ioAdapter.readKey('key-unavailable'),
              error => error?.name === 'KeyActionCardError' &&
                error?.code === 'NYXID_KEY_ACTION_IO_UNAVAILABLE' &&
                error.message.includes('upstream secret body') === false
            );
            const journey = context.createKeyActionDialogState(journeyCard);
            let mutationCalls = 0;
            let serviceReads = 0;
            let keyReads = 0;
            const io = {
              async readService(serviceId) {
                serviceReads += 1;
                return {id:serviceId,is_active:true,credential_source:{type:'personal'}};
              },
              verifyService(serviceId, snapshot) {
                assert.equal(snapshot.id, serviceId);
              },
              async mutate(candidate) {
                mutationCalls += 1;
                assert.equal(candidate.action, 'key.create');
                return {
                  resource:{keyId:'key-created'},replayed:false,
                  fullKey:'nyxid_ag_one_time_value'
                };
              },
              async readKey(keyId) {
                keyReads += 1;
                assert.equal(keyId, 'key-created');
                if (keyReads === 1) throw new Error('raw upstream secret detail');
                return {id:keyId};
              },
              verifyCreate(candidate, effectValue, snapshot) {
                assert.equal(candidate.actionRequestId, 'action-alpha');
                assert.equal(effectValue.resource.keyId, snapshot.id);
              },
              verifyRotate() {
                throw new Error('wrong verifier');
              }
            };
            await assert.rejects(
              context.runKeyActionMutation(journey, io),
              context.KeyActionCardError
            );
            assert.equal(mutationCalls, 1);
            assert.equal(serviceReads, 1);
            assert.equal(keyReads, 1);
            assert.equal(journey.effect.resource.keyId, 'key-created');
            assert.equal(journey.phase, 'verification_error');
            assert.equal(journey.error.includes('raw upstream secret detail'), false);
            assert.equal(JSON.stringify(journeyCard).includes('nyxid_ag_one_time_value'), false);
            await context.runKeyActionReadBack(journey, io);
            assert.equal(mutationCalls, 1, 'read-back retry must not repeat mutation');
            assert.equal(serviceReads, 1, 'read-back retry must not repeat service validation');
            assert.equal(keyReads, 2);
            assert.equal(journey.browserVerified, true);
            assert.throws(
              () => context.keyActionDialogCompletedResource(journey),
              context.KeyActionCardError
            );
            assert.equal(context.keyActionDialogCanClose(journey), false);
            journey.savedConfirmed = true;
            assert.deepEqual(JSON.parse(JSON.stringify(
              context.keyActionDialogCompletedResource(journey)
            )), {key:{keyId:'key-created'}});
            assert.equal(context.keyActionDialogCanClose(journey), true);
            context.clearKeyActionDialogState(journey);
            assert.equal(journey.effect, null);
            assert.equal(journey.browserVerified, false);
            assert.equal(JSON.stringify(journey).includes('nyxid_ag_one_time_value'), false);

            const rotateRequest = {
              ...request,actionRequestId:'action-rotate',action:'key.rotate',
              params:{keyId:'key-predecessor'}
            };
            const rotateJourney = context.createKeyActionDialogState({
              request:rotateRequest,status:'ready',busy:false,error:'',note:'',report:null,
              effectKeyId:'',replayed:null,requestedAt:'',browserVerified:false
            });
            let rotateVerifierCalls = 0;
            await context.runKeyActionMutation(rotateJourney, {
              async readService() { throw new Error('rotate must not read services'); },
              verifyService() { throw new Error('rotate must not verify services'); },
              async mutate() {
                return {resource:{keyId:'key-successor'},replayed:true,
                  requestedAt:'2026-08-13T08:00:00Z'};
              },
              async readKey(keyId) { return {id:keyId}; },
              verifyCreate() { throw new Error('wrong verifier'); },
              verifyRotate(candidate, effectValue, snapshot) {
                rotateVerifierCalls += 1;
                assert.equal(candidate.params.keyId, 'key-predecessor');
                assert.equal(effectValue.resource.keyId, snapshot.id);
              }
            });
            assert.equal(rotateVerifierCalls, 1);
            assert.equal(rotateJourney.effect.replayed, true);
            assert.equal(Object.hasOwn(rotateJourney.effect, 'fullKey'), false);
            assert.equal(rotateJourney.savedConfirmed, false);
            assert.equal(rotateJourney.browserVerified, true);
            assert.equal(context.keyActionDialogCanClose(rotateJourney), true);
            assert.deepEqual(JSON.parse(JSON.stringify(
              context.keyActionDialogCompletedResource(rotateJourney)
            )), {key:{keyId:'key-successor'}});
            })().catch((error) => {
              console.error(error);
              process.exitCode = 1;
            });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_KeyActionRecovery_ShouldProjectPendingAndRecentSafeActions()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        var actorState = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantActorState);
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const assets = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
            const context = {
              structuredClone,
              TextDecoder,
              URL,
              console,
              actionEntryKey:(actorId, actionRequestId) => `${actorId}:${actionRequestId}`,
            };
            vm.createContext(context);
            vm.runInContext(assets.protocol.replace(/^export /gm, ''), context);
            vm.runInContext(assets.actorState
              .replace(/^import[^;]+;\s*/m, '')
              .replace(/^export /gm, ''), context);
            const start = assets.app.indexOf('function actorStateWithActionHistory(');
            const end = assets.app.indexOf('\nasync function refreshActorState(', start);
            assert.notEqual(start, -1, 'action recovery helper must exist');
            assert.notEqual(end, -1, 'action recovery helper boundary must exist');
            vm.runInContext(assets.app.slice(start, end), context);

            const pending = {
              schemaVersion:4,actionRequestId:'action-create',originTurnId:'turn-alpha',
              taskId:'task-alpha',stepId:'step-create',action:'key.create',
              request:{schemaVersion:4,actorId:'action-actor-alpha',originTurnId:'turn-alpha',
                taskId:'task-alpha',stepId:'step-create',actionRequestId:'action-create',
                action:'key.create',params:{
                name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha']
              }},reports:[],postconditionResult:null
            };
            const recent = {
              schemaVersion:4,actionRequestId:'action-rotate',originTurnId:'turn-alpha',
              taskId:'task-alpha',stepId:'step-rotate',action:'key.rotate',
              request:{schemaVersion:4,actorId:'action-actor-alpha',originTurnId:'turn-alpha',
                taskId:'task-alpha',stepId:'step-rotate',actionRequestId:'action-rotate',
                action:'key.rotate',params:{keyId:'key-predecessor'}},
              reports:[{disposition:'completed',resource:{key:{keyId:'key-successor'}}}],
              postconditionResult:{verified:true,actionRequestId:'action-rotate',disposition:'completed',
                resource:{key:{keyId:'key-successor'}}}
            };
            const envelope = context.actorStateWithActionHistory({
              status:'current',stateVersion:7,snapshot:{actorId:'action-actor-alpha',
                pendingActions:[pending],recentActions:[recent]}
            });
            assert.deepEqual(JSON.parse(JSON.stringify(
              envelope.snapshot.pendingActions.map(action => action.actionRequestId)
            )), ['action-create','action-rotate']);
            assert.deepEqual(JSON.parse(JSON.stringify(envelope.snapshot.recentActions)), [recent]);
            assert.equal(JSON.stringify(envelope).includes('fullKey'), false);
            const entry = {actorId:'conversation-alpha',actionFrameCache:new Map()};
            context.restoreCurrentStateActionRequests(entry, envelope);
            assert.equal(
              entry.actionFrameCache.get('action-actor-alpha:action-create').action,
              'key.create'
            );
            assert.deepEqual(JSON.parse(JSON.stringify(
              entry.actionFrameCache.get('action-actor-alpha:action-create').params
            )), {name:'studio-agent',platform:'codex',allowedServiceIds:['service-alpha']});
            assert.equal(
              entry.actionFrameCache.get('action-actor-alpha:action-rotate').action,
              'key.rotate'
            );

            const mismatched = context.actorStateWithActionHistory({
              status:'current',stateVersion:8,snapshot:{actorId:'action-actor-alpha',pendingActions:[{
                ...pending,actionRequestId:'action-other'
              }],recentActions:[]}
            });
            context.restoreCurrentStateActionRequests(entry, mismatched);
            assert.equal(entry.actionFrameCache.has('action-actor-alpha:action-other'), false);
            const wrongOwner = context.actorStateWithActionHistory({
              status:'current',stateVersion:9,snapshot:{actorId:'conversation-other',
                pendingActions:[pending],recentActions:[]}
            });
            const wrongOwnerEntry = {actorId:'conversation-alpha',actionFrameCache:new Map()};
            context.restoreCurrentStateActionRequests(wrongOwnerEntry, wrongOwner);
            assert.equal(wrongOwnerEntry.actionFrameCache.size, 0);
            assert.equal(JSON.stringify([...entry.actionFrameCache.values()]).includes('fullKey'), false);

            const refreshStart = assets.app.indexOf('async function refreshActorStateFor(');
            const refreshEnd = assets.app.indexOf('\nfunction actorStateNotice(', refreshStart);
            const refreshSource = assets.app.slice(refreshStart, refreshEnd);
            const historyIndex = refreshSource.indexOf(
              'const envelope = actorStateWithActionHistory(await response.json());'
            );
            const applyIndex = refreshSource.indexOf('applyCurrentStateResult(projection, envelope)');
            const setIndex = refreshSource.indexOf(
              'setActorProjectionFor(entry, actorId, result.projection);'
            );
            assert.ok(historyIndex !== -1 && historyIndex < applyIndex);
            assert.ok(applyIndex < setIndex);
            assert.equal(refreshSource.includes('entry.actorProjection = result.projection'), false);
            """;

        var input = System.Text.Json.JsonSerializer.Serialize(new { app, actorState, protocol });
        var result = await RunNodeAsync(script, input);

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

        app.Should().Contain("verifyKeyCreateReadBack,");
        app.Should().Contain("verifyKeyRotateReadBack,");
        app.Should().Contain("verifyPersonalServiceReadBack,");
        app.Should().Contain("from \"./transport.js?v=20260817-p0-key-actions-integrated\"");
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
        app.Should().Contain("function renderTraceOperationInspector(trace)");
        app.Should().Contain("function renderActionCard(card)");
        app.Should().Contain("let terminalObserved = false;");
        app.Should().Contain("function actionActorJourneyReady(entry, projection)");
        app.Should().Contain("actionEntryKey(action.request.actorId, action.actionRequestId)");
        app.Should().NotContain("cardElements.get(action.actionRequestId)");
        app.Should().Contain("const KEY_ACTION_CARD_ACTIONS = Object.freeze([\"key.create\", \"key.rotate\"])");
        protocol.Should().Contain("export function normalizeFrame(");
        protocol.Should().Contain("export function validateActionContinuation(");
        protocol.Should().Contain("schemaVersion !== 4");
        protocol.Should().Contain("\"service.connect\", \"service.access_review\", \"key.create\", \"key.rotate\"");
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
        blocks.Should().Contain("export function buildKeyActionCardBlock(");
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
        html.Should().Contain("id=\"traceOperationSection\"");
        html.Should().Contain("id=\"traceOperationInputFact\"");
        html.Should().Contain("id=\"traceOperationOutputFact\"");
        html.Should().Contain("id=\"traceClientRequestFact\"");
        html.Should().Contain("id=\"conversationViewButton\"");
        html.Should().Contain("id=\"copyKeyActionSecretButton\"");
        html.Should().Contain("id=\"keyActionDialog\"");
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
        html.Should().Contain("app.js?v=20260817-p0-key-actions-integrated");
        html.Should().Contain("styles.css?v=20260817-p0-key-actions-integrated");
        html.Should().Contain("lucide.min.js?v=20260817-p0-key-actions-integrated");
        html.Should().Contain("marked.min.js?v=20260817-p0-key-actions-integrated");
        html.Should().Contain("purify.min.js?v=20260817-p0-key-actions-integrated");
        app.Should().Contain("protocol.js?v=20260817-p0-key-actions-integrated");
        app.Should().Contain("blocks.js?v=20260817-p0-key-actions-integrated");
        app.Should().Contain("actor-state.js?v=20260817-p0-key-actions-integrated");
        app.Should().Contain("readiness.js?v=20260817-p0-key-actions-integrated");
        transport.Should().Contain("readiness.js?v=20260817-p0-key-actions-integrated");
        actorState.Should().Contain("protocol.js?v=20260817-p0-key-actions-integrated");
        blocks.Should().Contain("protocol.js?v=20260817-p0-key-actions-integrated");
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
              renderActionCard:() => {},
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
              renderActionCard:() => {},
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
            vm.runInContext(source.slice(proofStart, proofEnd).replace(/^export /gm, ''), context);
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
              renderActionCard:() => {},
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
