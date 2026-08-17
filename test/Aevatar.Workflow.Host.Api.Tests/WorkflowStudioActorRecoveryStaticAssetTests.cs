using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed partial class WorkflowConsoleStaticAssetEndpointTests
{
    [Fact]
    public async Task WorkflowStudio_NeedsYouDecision_ShouldRefreshTheAuthoritativeStateVersionBeforeDispatch()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('async function submitNeedsYouDecision(');
            const end = source.indexOf('\nfunction scheduleActorStateRefresh(', start);
            assert.notEqual(start, -1, 'submitNeedsYouDecision must exist in the served Studio app');
            assert.notEqual(end, -1, 'scheduleActorStateRefresh must follow submitNeedsYouDecision');

            const requests = [];
            const timeline = [];
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:{actorId:'conversation-alpha',stateVersion:3},
              needsYouSubmissions:new Map(),
            };
            const context = {
              actorProjectionFor:(target) => target.actorProjection,
              actorStateVersion:(target) => target.actorProjection.stateVersion,
              createId:() => 'client-approval-alpha',
              demoHeaders:() => ({}),
              fetch:async (_url, init) => {
                timeline.push('dispatch');
                requests.push(JSON.parse(init.body));
                return {ok:true,json:async () => ({status:'accepted'})};
              },
              needsYouKey:(kind, requestId, actorId) => `${kind}:${actorId}:${requestId}`,
              refreshActorStateFor:async (target, actorId, options) => {
                assert.equal(actorId, 'conversation-alpha');
                timeline.push(options?.uncursored === true ? 'refresh-authoritative' : 'refresh');
                target.actorProjection = {actorId,stateVersion:12};
                return target.actorProjection;
              },
              renderActorProjection:() => {},
              responseError:async () => new Error('request failed'),
              scheduleActorStateRefresh:() => {},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const accepted = await context.submitNeedsYouDecision(
              entry,
              'approval',
              'approval-alpha',
              {
                type:'approval.resolve', approved:true
              },
              {actorId:'conversation-alpha',projection:entry.actorProjection});

            assert.equal(accepted, true);
            assert.equal(requests.length, 1);
            assert.equal(timeline[0], 'refresh-authoritative');
            assert.equal(requests[0].expectedStateVersion, 12);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_NeedsYouDecision_ShouldFollowActorStateAfterTheChatStreamTimesOut()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function actorStateFollowNeeded(');
            const end = source.indexOf('\nfunction actorTaskElementFor(', start);
            assert.notEqual(start, -1, 'actorStateFollowNeeded must exist in the served Studio app');
            assert.notEqual(end, -1, 'actorTaskElementFor must follow actor state recovery');

            const timers = [];
            const terminalRecoveries = [];
            const active = {
              actorId:'conversation-alpha', stateVersion:40,
              task:{taskId:'task-alpha',turnId:'turn-alpha',status:'active',gate:{mode:'auto',status:'satisfied'}},
              activeTurn:{turnId:'turn-alpha',taskId:'task-alpha',status:'active'},
              latestTurn:{turnId:'turn-alpha',taskId:'task-alpha',status:'active'},
              pendingInput:null, pendingApproval:null,
            };
            const succeeded = {
              ...active, stateVersion:44,
              task:{...active.task,status:'succeeded'},
              activeTurn:null,
              latestTurn:{turnId:'turn-alpha',taskId:'task-alpha',status:'succeeded'},
            };
            const projections = [active, succeeded];
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:{...active,stateVersion:39},
              actorStateRefreshTimer:null,
              actionStateRefreshTimers:new Map(),
              needsYouSubmissions:new Map(),
              run:{status:'error',completedAt:1,assistantText:''},
            };
            let refreshCount = 0;
            const context = {
              window:{
                setTimeout(callback) { timers.push(callback); return timers.length; },
                clearTimeout() {},
              },
              actorTerminalRunStatus:(projection) =>
                projection?.task?.status === 'succeeded' ? 'complete' : null,
              actorStateTurnId:(projection) => projection?.latestTurn?.turnId || '',
              needsYouKey:(kind, requestId, actorId) => `${actorId}:${kind}:${requestId}`,
              refreshActorState:async (target) => {
                target.actorProjection = projections[Math.min(refreshCount, projections.length - 1)];
                refreshCount += 1;
                return target.actorProjection;
              },
              refreshActionActorState:async () => null,
              recoverTerminalConversation:async (target, projection, terminalStatus) => {
                terminalRecoveries.push({target,projection,terminalStatus});
                target.run.status = terminalStatus;
                return true;
              },
              renderActiveConversationState:() => {},
              setActorStateNotice:() => {},
              renderActorProjection:() => {},
              withConversationState:(_target, callback) => callback(),
              state:{activeConversation:entry},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            context.scheduleActorStateRefresh(entry, 'conversation-alpha', 0, 4);
            assert.equal(timers.length, 1);
            await timers.shift()();

            assert.equal(refreshCount, 1);
            assert.equal(entry.run.status, 'running');
            assert.equal(entry.run.completedAt, null);
            assert.equal(timers.length, 1, 'an active Actor must schedule another authoritative refresh');

            await timers.shift()();

            assert.equal(refreshCount, 2);
            assert.equal(terminalRecoveries.length, 1);
            assert.equal(terminalRecoveries[0].terminalStatus, 'complete');
            assert.equal(timers.length, 0, 'terminal recovery must stop the follow loop');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_TerminalActorRecovery_ShouldWaitForTheCanonicalAssistantReply()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function terminalConversationHistoryReady(');
            const end = source.indexOf('\nfunction actorStateFollowNeeded(', start);
            assert.notEqual(start, -1, 'terminalConversationHistoryReady must exist in the served Studio app');
            assert.notEqual(end, -1, 'actorStateFollowNeeded must follow terminal history recovery');

            const previousMessages = [
              {id:'turn-previous-user',role:'user',content:'previous request',turnId:'turn-previous'},
              {id:'turn-previous-assistant',role:'assistant',content:'previous answer',turnId:'turn-previous'},
            ];
            const currentUser = {
              id:'turn-current-user',role:'user',content:'retrieve one issue',turnId:'turn-current'
            };
            const payloads = [
              previousMessages,
              [...previousMessages, currentUser],
              [
                ...previousMessages,
                currentUser,
                {
                  id:'turn-current-assistant',role:'assistant',turnId:'turn-current',
                  content:'Assigned issue: aevatarAI/aevatar#123 - Repair actor state recovery.'
                },
              ],
            ];
            const replacements = [];
            let requestCount = 0;
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:{
                actorId:'conversation-alpha',stateVersion:44,
                latestTurn:{turnId:'turn-current',status:'succeeded'}
              },
              historyRecoveredTurnId:null,
            };
            const context = {
              fetch:async (url) => {
                assert.equal(url, '/history/conversation-alpha');
                const payload = payloads[requestCount++];
                return {ok:true,json:async () => payload};
              },
              historyUrl:(actorId) => `/history/${actorId}`,
              demoHeaders:() => ({}),
              responseError:async () => new Error('history failed'),
              normalizeStoredMessages:(value) => value,
              actorStateTurnId:(projection) => projection.latestTurn?.turnId || '',
              replaceConversationHistory:(target, messages, projection, terminalStatus) => {
                replacements.push({target,messages,projection,terminalStatus});
                return true;
              },
              loadConversations:async () => {},
              setActorStateNotice:() => {},
              renderActorProjection:() => {},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);
            context.replaceConversationHistory = (target, messages, projection, terminalStatus) => {
              replacements.push({target,messages,projection,terminalStatus});
              return true;
            };

            const pending = await context.recoverTerminalConversation(
              entry, entry.actorProjection, 'complete');
            assert.equal(pending, false);
            assert.equal(replacements.length, 0);

            const awaitingAssistant = await context.recoverTerminalConversation(
              entry, entry.actorProjection, 'complete');
            assert.equal(awaitingAssistant, false);
            assert.equal(replacements.length, 0);

            const recovered = await context.recoverTerminalConversation(
              entry, entry.actorProjection, 'complete');
            assert.equal(recovered, true);
            assert.equal(replacements.length, 1);
            assert.equal(replacements[0].messages.at(-1).role, 'assistant');
            assert.equal(replacements[0].messages.at(-1).turnId, 'turn-current');
            assert.match(replacements[0].messages.at(-1).content, /aevatar#123/);
            assert.equal(entry.historyRecoveredTurnId, 'turn-current');
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_TerminalActorRecovery_ShouldNotReplaceANewerRun()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function terminalConversationHistoryReady(');
            const end = source.indexOf('\nfunction actorStateFollowNeeded(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const oldRun = {status:'error'};
            const newRun = {status:'running'};
            const newController = {kind:'new-controller'};
            let domReplacementCount = 0;
            const entry = {
              actorId:'conversation-alpha',
              actorProjection:{
                actorId:'conversation-alpha',stateVersion:44,
                latestTurn:{turnId:'turn-current',status:'succeeded'}
              },
              historyRecoveredTurnId:null,
              run:oldRun,
              controller:null,
              thread:{},
              actionActorTaskElements:new Map(),
              meta:{messageCount:1},
            };
            const state = {
              activeConversation:entry,
              activeController:null,
              run:oldRun,
            };
            const context = {
              state,
              dom:{thread:{replaceChildren(){ domReplacementCount += 1; }}},
              actorStateTurnId:(projection) => projection.latestTurn?.turnId || '',
              loadConversations:async () => {},
              fetch:async () => {
                entry.run = newRun;
                entry.controller = newController;
                state.run = newRun;
                state.activeController = newController;
                return {ok:true,json:async () => [{
                  id:'turn-current-assistant',role:'assistant',turnId:'turn-current',content:'done'
                }]};
              },
              historyUrl:() => '/history/conversation-alpha',
              demoHeaders:() => ({}),
              responseError:async () => new Error('history failed'),
              normalizeStoredMessages:(value) => value,
              withConversationState:(_target, callback) => callback(),
              createRunState:() => ({}),
              renderStoredMessage:() => {},
              renderActorProjection:() => {},
              renderActiveConversationState:() => {},
              scrollThread:() => {},
              refreshIcons:() => {},
              setActorStateNotice:() => {},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const settled = await context.recoverTerminalConversation(
              entry, entry.actorProjection, 'complete');

            assert.equal(settled, true, 'a superseded recovery must stop its old retry chain');
            assert.strictEqual(entry.run, newRun);
            assert.strictEqual(entry.controller, newController);
            assert.strictEqual(state.activeController, newController);
            assert.equal(domReplacementCount, 0);
            assert.equal(entry.historyRecoveredTurnId, null);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActorStateFollow_ShouldNotLetAnOlderRefreshReplaceANewerTimer()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function actorStateFollowNeeded(');
            const end = source.indexOf('\nfunction actorTaskElementFor(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            let nextTimerId = 0;
            const timers = new Map();
            let resolveFirstRefresh;
            let refreshCount = 0;
            const active = {
              actorId:'conversation-alpha',stateVersion:40,
              task:{taskId:'task-alpha',status:'active',gate:{mode:'auto',status:'satisfied'}},
              pendingInput:null,pendingApproval:null,
            };
            const entry = {
              actorId:'conversation-alpha',actorProjection:active,
              actorStateRefreshTimer:null,actionStateRefreshTimers:new Map(),
              actorStateRefreshGeneration:0,actionStateRefreshGenerations:new Map(),
              needsYouSubmissions:new Map(),run:{status:'error',completedAt:1},
            };
            const notices = [];
            const context = {
              window:{
                setTimeout(callback) {
                  const id = ++nextTimerId;
                  timers.set(id, callback);
                  return id;
                },
                clearTimeout(id) { timers.delete(id); },
              },
              actorTerminalRunStatus:() => null,
              needsYouKey:(kind, requestId, actorId) => `${actorId}:${kind}:${requestId}`,
              refreshActorState:async () => {
                refreshCount += 1;
                if (refreshCount === 1) {
                  return new Promise((resolve) => { resolveFirstRefresh = resolve; });
                }
                return active;
              },
              refreshActionActorState:async () => null,
              recoverTerminalConversation:async () => true,
              renderActiveConversationState:() => {},
              setActorStateNotice:(_entry, _actorId, message) => notices.push(message),
              renderActorProjection:() => {},
              state:{activeConversation:entry},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            context.scheduleActorStateRefresh(entry, 'conversation-alpha', 0, 2);
            const oldTimerId = entry.actorStateRefreshTimer;
            const oldCallback = timers.get(oldTimerId);
            timers.delete(oldTimerId);
            const oldFollow = oldCallback();

            context.scheduleActorStateRefresh(entry, 'conversation-alpha', 0, 300);
            const newTimerId = entry.actorStateRefreshTimer;
            assert.notEqual(newTimerId, oldTimerId);
            assert.equal(timers.has(newTimerId), true);

            resolveFirstRefresh(active);
            await oldFollow;

            assert.equal(entry.actorStateRefreshTimer, newTimerId);
            assert.equal(timers.has(newTimerId), true);
            assert.equal(notices.length, 0);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActorStateFollow_ShouldReportTerminalHistoryRecoveryTimeout()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            (async () => {
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function actorStateFollowNeeded(');
            const end = source.indexOf('\nfunction actorTaskElementFor(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const timers = [];
            const terminal = {
              actorId:'conversation-alpha',stateVersion:44,
              task:{taskId:'task-alpha',status:'succeeded'},
              latestTurn:{turnId:'turn-alpha',status:'succeeded'},
            };
            const entry = {
              actorId:'conversation-alpha',actorProjection:terminal,
              actorStateRefreshTimer:null,actionStateRefreshTimers:new Map(),
              actorStateRefreshGeneration:0,actionStateRefreshGenerations:new Map(),
              needsYouSubmissions:new Map(),historyRecoveredTurnId:null,
              run:{status:'error',completedAt:1,assistantText:''},
            };
            const notices = [];
            const context = {
              window:{
                setTimeout(callback) { timers.push(callback); return timers.length; },
                clearTimeout() {},
              },
              actorTerminalRunStatus:() => 'complete',
              actorStateTurnId:(projection) => projection.latestTurn?.turnId || '',
              needsYouKey:(kind, requestId, actorId) => `${actorId}:${kind}:${requestId}`,
              refreshActorState:async () => terminal,
              refreshActionActorState:async () => null,
              recoverTerminalConversation:async () => false,
              renderActiveConversationState:() => {},
              setActorStateNotice:(_entry, _actorId, message) => notices.push(message),
              renderActorProjection:() => {},
              state:{activeConversation:entry},
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            context.scheduleActorStateRefresh(entry, 'conversation-alpha', 0, 1);
            await timers.shift()();

            assert.equal(timers.length, 0);
            assert.equal(notices.some((message) => /最终回复/.test(message)), true);
            })().catch((error) => { console.error(error); process.exitCode = 1; });
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ActorStateFollow_ShouldPauseUntilAttentionIsLocallySubmitted()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const start = source.indexOf('function actorStateFollowNeeded(');
            const end = source.indexOf('\nfunction markActorRunFollowing(', start);
            assert.notEqual(start, -1);
            assert.notEqual(end, -1);

            const entry = {
              actorId:'conversation-alpha',
              needsYouSubmissions:new Map(),
            };
            const base = {
              actorId:'conversation-alpha',
              task:{status:'active'},
              pendingInput:null,
              pendingApproval:null,
            };
            const context = {
              actorTerminalRunStatus:() => null,
              needsYouKey:(kind, requestId, actorId) => `${actorId}:${kind}:${requestId}`,
            };
            vm.createContext(context);
            vm.runInContext(source.slice(start, end), context);

            const input = {...base,pendingInput:{requestId:'input-alpha'}};
            assert.equal(context.actorStateFollowNeeded(entry, entry.actorId, input), false);
            entry.needsYouSubmissions.set('conversation-alpha:input:input-alpha', {status:'pending'});
            assert.equal(context.actorStateFollowNeeded(entry, entry.actorId, input), true);

            entry.needsYouSubmissions.clear();
            const approval = {...base,pendingApproval:{approvalRequestId:'approval-alpha'}};
            assert.equal(context.actorStateFollowNeeded(entry, entry.actorId, approval), false);
            entry.needsYouSubmissions.set('conversation-alpha:approval:approval-alpha', {status:'accepted'});
            assert.equal(context.actorStateFollowNeeded(entry, entry.actorId, approval), true);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
    }

    [Fact]
    public async Task WorkflowStudio_ConversationStateVersion_ShouldPreferTheLoadedConversationMetadata()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');
            const versionStart = source.indexOf('function conversationStateVersion(entry) {');
            const versionEnd = source.indexOf('\ninitializeConversationStates();', versionStart);
            assert.notEqual(versionStart, -1, 'conversationStateVersion must exist in the served Studio app');
            assert.notEqual(versionEnd, -1, 'initializeConversationStates must follow conversationStateVersion');
            const context = {
              createActorProjection: (actorId) => ({
                actorId,
                stateVersion: 0,
                task: null,
                steps: new Map(),
                pendingInput: null,
                pendingApproval: null,
                actions: new Map(),
                conflicts: [],
              }),
            };
            vm.createContext(context);
            vm.runInContext(source.slice(versionStart, versionEnd), context);

            const createEntry = (stateVersion, projectionVersion = 0) => ({
              actorId: 'conversation-alpha',
              meta: { stateVersion },
              actorProjection: { actorId: 'conversation-alpha', stateVersion: projectionVersion },
            });

            assert.equal(context.conversationStateVersion(createEntry(39, 0)), 39);
            assert.equal(context.conversationStateVersion(createEntry(39, 17)), 39);
            assert.equal(context.conversationStateVersion(createEntry(0, 17)), 17);
            assert.equal(context.conversationStateVersion(createEntry(0, 0)), 0);
            assert.equal(context.ensureConversationProjectionVersion(createEntry(39, 0)).stateVersion, 39);
            assert.equal(context.ensureConversationProjectionVersion(createEntry(39, 17)).stateVersion, 39);
            assert.equal(context.ensureConversationProjectionVersion(createEntry(0, 17)).stateVersion, 17);
            assert.equal(context.ensureConversationProjectionVersion(createEntry(0, 0)).stateVersion, 0);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("function conversationStateVersion(entry)");
        app.Should().Contain("function ensureConversationProjectionVersion(entry)");
        app.Should().Contain("const reliableVersion = reliableConversationStateVersion(entry);");
        app.Should().Contain("expectedStateVersion: reliableVersion,");
        app.Should().Contain("dom.sendButton.disabled = !state.auth.authenticated || locked || reliableVersion <= 0 || !hasAnswer;");
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

}
