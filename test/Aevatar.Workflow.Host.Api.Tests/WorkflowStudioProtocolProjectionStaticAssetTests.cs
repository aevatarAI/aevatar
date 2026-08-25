using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed partial class WorkflowConsoleStaticAssetEndpointTests
{
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
    public async Task WorkflowStudio_Protocol_ShouldStopSseConsumptionWhenTheFrameHandlerReturnsFalse()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const payload = new TextEncoder().encode(
              'data: {"type":"CUSTOM","custom":{"name":"aevatar.nyxid_chat.keepalive",' +
              '"payload":{"status":"running"}}}\n\n');
            let reads = 0;
            let cancelled = false;
            const reader = {
              async read() {
                reads += 1;
                if (reads === 1) return {value:payload,done:false};
                throw new Error('consumeSse read again after the handler requested stop');
              },
              async cancel() { cancelled = true; },
            };
            let frames = 0;
            await context.consumeSse({body:{getReader:() => reader}}, async () => {
              frames += 1;
              return false;
            });

            assert.equal(frames, 1);
            assert.equal(reads, 1);
            assert.equal(cancelled, true);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
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
    public async Task WorkflowStudio_Protocol_ShouldPreserveReadOnlyPlanProgressWithoutAGate()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8').replace(/^export /gm, '');
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);

            const snapshot = context.normalizeFrame({
              type:'CUSTOM', sequence:61, custom:{name:'nyxid.task.snapshot', payload:{
                schemaVersion:4, actorId:'conversation-alpha', turnId:'turn-alpha',
                taskId:'task-alpha', planId:'plan-alpha', planRevision:2,
                title:'Post the update', status:'active',
                steps:[{stepId:'step-plan',order:1,kind:'llm',status:'done',
                  externalEffect:'not_started',addedBy:'initial'}]
              }}
            });

            assert.equal(snapshot.type, 'task_snapshot');
            assert.equal(snapshot.payload.planId, 'plan-alpha');
            assert.equal(snapshot.payload.planRevision, 2);
            assert.equal(snapshot.payload.steps[0].status, 'done');
            assert.equal(Object.hasOwn(snapshot.payload, 'gate'), false);
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_Plan_ShouldBeReadOnlyWhileExactApprovalsAndBrowserActionsRemain()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);

        app.Should().NotContain("需要你确认计划");
        app.Should().NotContain("确认执行");

        app.Should().Contain("type: \"approval.resolve\"");
        app.Should().Contain("async function submitApproval(");
        app.Should().Contain("async function submitActionContinuation(");
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
            version == "20260823-m62-studio-redesign");
        transitiveVersions.Should().NotBeEmpty();
        transitiveVersions.Should().OnlyContain(static version =>
            version == "20260823-m62-studio-redesign");
        html.Should().Contain("styles.css?v=");
        html.Should().Contain("app.js?v=");
        app.Should().Contain("transport.js?v=");
        app.Should().Contain("readiness.js?v=");
        transport.Should().Contain("readiness.js?v=");
        actorState.Should().Contain("protocol.js?v=");
        blocks.Should().Contain("protocol.js?v=");
    }

    [Fact]
    public async Task WorkflowStudio_ActorProjection_ShouldRehydrateTypedActionRequestsFromCurrentState()
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

            const request = {
              schemaVersion:4,
              actorId:'action-actor-alpha',
              originTurnId:'turn-alpha',
              taskId:'task-alpha',
              stepId:'step-connect',
              actionRequestId:'action-alpha',
              action:'service.connect',
              params:{catalogService:{serviceSlug:'github',requestedScopes:['repo']}}
            };
            const live = context.reduceActorEvent(
              context.createActorProjection('action-actor-alpha'), {
                type:'action_request', sequence:41, actionRequest:request
              });
            const liveAction = live.actions.get('action-alpha');
            assert.deepEqual(JSON.parse(JSON.stringify(liveAction.request)), request);
            assert.deepEqual(JSON.parse(JSON.stringify(liveAction.params)), request.params);
            assert.equal(liveAction.executable, true);
            assert.equal(liveAction.conflicted, false);

            const result = context.applyCurrentStateResult(
              context.createActorProjection('action-actor-alpha'), {
                status:'current', stateVersion:42,
                snapshot:{
                  actorId:'action-actor-alpha', scopeId:'scope-alpha',
                  stateVersion:42, progressSequence:42,
                  activeTurn:null, latestTurn:null, recentTerminalTurns:[],
                  activeTask:null, pendingInput:null, pendingApproval:null,
                  latestInputResolution:null, latestApprovalResolution:null,
                  taskStatus:'active', attentionKind:'none', attentionSince:null,
                  activeStepSummary:null,
                  pendingActions:[{
                    schemaVersion:4, originTurnId:'turn-alpha', taskId:'task-alpha',
                    stepId:'step-connect', actionRequestId:'action-alpha',
                    action:'service.connect', reports:[], postconditionResult:null,
                    request
                  }],
                  controlFence:null, latestControlResult:null,
                  continuationAdmission:null
                }
              });
            const action = result.projection.actions.get('action-alpha');
            assert.deepEqual(JSON.parse(JSON.stringify(action.request)), request);
            assert.deepEqual(JSON.parse(JSON.stringify(action.params)), request.params);
            assert.equal(action.executable, true);
            assert.equal(action.conflicted, false);

            const mismatched = context.applyCurrentStateResult(
              context.createActorProjection('action-actor-alpha'), {
                status:'current', stateVersion:43,
                snapshot:{
                  actorId:'action-actor-alpha', scopeId:'scope-alpha',
                  stateVersion:43, progressSequence:43,
                  activeTurn:null, latestTurn:null, recentTerminalTurns:[],
                  activeTask:null, pendingInput:null, pendingApproval:null,
                  latestInputResolution:null, latestApprovalResolution:null,
                  taskStatus:'active', attentionKind:'none', attentionSince:null,
                  activeStepSummary:null,
                  pendingActions:[{
                    schemaVersion:4, originTurnId:'turn-other', taskId:'task-alpha',
                    stepId:'step-connect', actionRequestId:'action-alpha',
                    action:'service.connect', reports:[], postconditionResult:null,
                    request
                  }],
                  controlFence:null, latestControlResult:null,
                  continuationAdmission:null
                }
              }).projection.actions.get('action-alpha');
            assert.equal(mismatched.request, null);
            assert.equal(mismatched.params, null);
            assert.equal(mismatched.executable, false);
            assert.equal(mismatched.conflicted, true);

            const parent = context.reduceActorEvent(
              context.createActorProjection('conversation-alpha'), {
                type:'action_request', sequence:44, actionRequest:request
              });
            assert.equal(parent.actions.size, 0);
            assert.equal(parent.conflicts.at(-1).code, 'NYXID_ACTOR_ID_CONFLICT');
            """;

        var result = await RunNodeAsync(script, actorState);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActionEvents_ShouldRouteToOwningActorWithoutReplacingParentConversation()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function actorProjectionFor(entry, actorId = entry?.actorId) {');
            const end = source.indexOf('\ninitializeConversationStates();', start);
            assert.notEqual(start, -1, 'actorProjectionFor must exist in the served Studio app');
            assert.notEqual(end, -1, 'initializeConversationStates must follow actor routing helpers');

            const state = { actorId:'conversation-alpha' };
            const context = {
              Map,
              state,
              createActorProjection:(actorId) => ({
                actorId, progressSequence:0, stateVersion:0, events:[]
              }),
              reduceActorEvent:(projection, event) => ({
                ...projection,
                progressSequence:event.sequence,
                events:[...projection.events, event]
              })
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const parentProjection = context.createActorProjection('conversation-alpha');
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:parentProjection,
              actionActorProjections:new Map()
            };
            const request = {
              actorId:'action-actor-alpha', actionRequestId:'action-alpha'
            };

            const routedAction = context.reduceActorEventForEntry(entry, {
              type:'action_request', sequence:41, actionRequest:request
            });
            assert.equal(routedAction.actorId, 'action-actor-alpha');
            assert.strictEqual(entry.actorProjection, parentProjection);
            assert.equal(entry.actorProjection.events.length, 0);
            assert.strictEqual(
              entry.actionActorProjections.get('action-actor-alpha'),
              routedAction.projection);
            assert.equal(routedAction.projection.events[0].sequence, 0);

            const routedActionTask = context.reduceActorEventForEntry(entry, {
              type:'task_snapshot', sequence:5,
              payload:{actorId:'action-actor-alpha'}
            }, {streamActorId:'action-actor-alpha'});
            assert.equal(routedActionTask.actorId, 'action-actor-alpha');
            assert.equal(routedActionTask.projection.events.at(-1).sequence, 5);
            assert.equal(entry.actorProjection.events.length, 0);

            const routedParentTask = context.reduceActorEventForEntry(entry, {
              type:'task_snapshot', sequence:7,
              payload:{actorId:'conversation-alpha'}
            });
            assert.equal(routedParentTask.actorId, 'conversation-alpha');
            assert.strictEqual(entry.actorProjection, routedParentTask.projection);
            assert.equal(entry.actorProjection.events.at(-1).sequence, 7);

            const preserved = context.adoptRunStartedConversationActor(
              entry,
              'action-actor-alpha',
              {preserveConversationActor:true});
            assert.equal(preserved, false);
            assert.equal(entry.actorId, 'conversation-alpha');
            assert.equal(state.actorId, 'conversation-alpha');
            """;

        var result = await RunNodeAsync(script, app);

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
    public async Task WorkflowStudio_StoredMessageProtocol_ShouldPreserveTurnIdentity()
    {
        var protocol = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantProtocol);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('export function normalizeStoredMessages(');
            assert.notEqual(start, -1);
            const context = {};
            vm.createContext(context);
            vm.runInContext(source.slice(start).replace(/^export /, ''), context);
            const result = context.normalizeStoredMessages([{
              id:'turn-alpha-assistant', role:'assistant', content:'done',
              timestamp:42, status:'completed', turnId:'turn-alpha'
            }]);
            assert.equal(result.length, 1);
            assert.equal(result[0].turnId, 'turn-alpha');
            """;

        var result = await RunNodeAsync(script, protocol);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

}
