using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed partial class WorkflowConsoleStaticAssetEndpointTests
{
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
              setComposerStatus: (message) => { dom.composerStatus.textContent = message; },
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
    public async Task WorkflowStudio_StoredOperations_ShouldRestoreTurnScopedTrajectoryWithoutSynthesizingTiming()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name) {
              const start = source.indexOf('function ' + name + '(');
              assert.notEqual(start, -1, name + ' must exist in the served Studio app');
              const end = source.indexOf('\n}\n', start);
              assert.notEqual(end, -1, name + ' must close at column zero');
              return source.slice(start, end + 3);
            }

            const context = {
              Map, Set, Number, String, Object, Array, JSON, Boolean,
              createRunState: () => ({ status: 'idle', events: [], tools: new Map(),
                startedAt: null, completedAt: null, context: {}, request: null }),
              createId: prefix => prefix + '-generated',
              mergeUsage: (left, right) => right ?? left,
              traceOperationKindLabel: kind => kind.toUpperCase(),
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('ensureTraceOperationState')}
              ${functionSource('orderedTraceOperations')}
              ${functionSource('traceOperationKey')}
              ${functionSource('upsertTraceOperation')}
              ${functionSource('requestTraceInput')}
              ${functionSource('createInputTraceOperation')}
              ${functionSource('traceServerTimestamp')}
              ${functionSource('traceOperationDurationMs')}
              ${functionSource('restoredTraceKey')}
              ${functionSource('ensureRestoredRequestTrace')}
              ${functionSource('restoredOperationTimestamp')}
              ${functionSource('restoredOperationUsage')}
              ${functionSource('normalizedToolText')}
              ${functionSource('containsOpaqueToolInvocation')}
              ${functionSource('readableToolInvocationName')}
              ${functionSource('nyxIdToolPresentationSource')}
              ${functionSource('describeToolOperation')}
              ${functionSource('applyRestoredOperation')}
              ${functionSource('restoreTrajectoryFromStoredOperations')}
            `, context);

            const entry = { actorId: 'conversation-a', traces: new Map(), traceOrder: [] };
            const operations = [
              { turnId: 'turn-a', operationId: 'op-1', order: 1, kind: 'model',
                title: 'deepseek-v4-pro', status: 'done', model: 'deepseek-v4-pro',
                startedAt: '2026-08-20T08:00:00.000Z', completedAt: '2026-08-20T08:00:02.000Z',
                totalTokens: 4397, outputPreview: 'plan', previewsTruncated: true,
                availableToolNames: ['github.get_issue', 'nyxid.require_service'],
                toolCatalogCaptured: true },
              { turnId: 'turn-a', operationId: 'op-2', order: 2, kind: 'tool',
                title: 'service.reconnect', status: 'error',
                startedAt: '2026-08-20T08:00:03.000Z', completedAt: null,
                safeMessage: 'NYXID_REFRESH_REQUIRED' },
              { turnId: 'turn-b', operationId: 'op-3', order: 1, kind: 'model',
                title: 'deepseek-v4-pro', status: 'done', startedAt: null, completedAt: null },
            ];
            const messages = [
              { role: 'user', turnId: 'turn-a', content: 'reconnect the service' },
              { role: 'assistant', turnId: 'turn-a', content: 'done' },
            ];

            context.restoreTrajectoryFromStoredOperations(entry, operations, messages);

            assert.deepEqual([...entry.traces.keys()], ['turn:turn-a', 'turn:turn-b']);
            assert.deepEqual(entry.traceOrder, ['turn:turn-b', 'turn:turn-a'],
              'newest container first so the ledger renders oldest to newest');

            const turnA = entry.traces.get('turn:turn-a');
            assert.equal(turnA.serverTurnId, 'turn-a');
            assert.equal(turnA.restored, true);
            const records = context.orderedTraceOperations(turnA);
            // Join before comparing: the ledger array is built inside the vm realm.
            assert.equal(records.map(record => record.kind).join('|'), 'input|model|tool');
            assert.equal(records[0].input, 'reconnect the service');
            assert.equal(records[1].previewsTruncated, true);
            assert.equal(records[1].usage.totalTokens, 4397);
            assert.equal(records[1].tools.join('|'), 'github.get_issue|nyxid.require_service');
            assert.equal(records[1].toolCatalogCaptured, true);
            assert.equal(context.traceOperationDurationMs(records[1]), 2000);
            assert.equal(records[2].error, 'NYXID_REFRESH_REQUIRED');
            assert.equal(records[2].completedAt, null);
            assert.equal(context.traceOperationDurationMs(records[2]), null,
              'a tool that never reported completion keeps no duration');

            const turnB = context.orderedTraceOperations(entry.traces.get('turn:turn-b'));
            assert.equal(turnB.at(-1).startedAt, null,
              'absent timing must not be replaced with the load time');

            // Re-running the restore must not duplicate the recovered containers.
            context.restoreTrajectoryFromStoredOperations(entry, operations, messages);
            assert.equal(entry.traces.size, 2);
            assert.equal(context.orderedTraceOperations(turnA).length, 3);
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("restoreTrajectoryFromStoredOperations(entry, storedOperations, messages);");
        app.Should().Contain("if (isConversationActor) {");
        app.Should().Contain("restoreTrajectoryFromActorProjection(entry, result.projection);");
        app.Should().Contain("restoreWorkflowSignalFromActorProjection(entry, result.projection);");
        // Tool result bodies are never archived, so the restore path must not read one.
        app.Should().NotContain("operation?.resultPreview");
    }

    [Fact]
    public async Task WorkflowStudio_OperationDetails_ShouldStayIsolatedFromTheDefaultConversationView()
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

            function node(tag) {
              const self = {
                tag, children: [], textContent: '', title: '', hidden: false,
                dataset: {}, style: { setProperty() {} }, attributes: {},
                className: '', parentElement: null,
                classList: {
                  toggle(name, force) { self.toggles.push([name, force]); },
                  add() {}, remove() {},
                },
                toggles: [],
                append(...items) { self.children.push(...items); },
                replaceChildren(...items) { self.children = items; },
                setAttribute(name, value) { self.attributes[name] = value; },
                addEventListener() {},
                getBoundingClientRect() { return { width: 400 }; },
              };
              Object.defineProperty(self, 'childElementCount', { get: () => self.children.length });
              return self;
            }

            const panel = node('aside');
            panel.parentElement = node('div');
            const dom = {
              trajectoryDetails: panel,
              trajectoryDetailsKind: node('span'),
              trajectoryDetailsLocation: node('span'),
              trajectoryDetailsTabs: node('div'),
              trajectoryDetailsBody: node('div'),
            };
            const record = {
              key: 'model:model-0', id: 'model-0', kind: 'model', title: 'deepseek-chat',
              status: 'done', model: 'deepseek-chat', provider: 'deepseek', round: 0,
              sessionId: 'session-alpha', finishReason: 'stop', usage: { totalTokens: 12 },
              input: 'Prompt', output: 'Answer', reasoning: '', error: '', tools: ['search'],
              toolCatalogCaptured: true,
              startedAt: 1700000000000, completedAt: 1700000000100,
            };
            const trace = { key: 'client-request-one', clientRequestId: 'client-request-one', selected: record };
            const entry = { mainView: 'conversation' };
            const trajectory = {
              detailsOpen: false, detailsTab: null, detailsWidth: null,
              rows: [{ type: 'operation', trace, number: 2, record, collapsedCalls: [] }],
            };
            const context = {
              TRAJECTORY_DETAILS_DEFAULT_WIDTH: 340,
              trajectory,
              state: { activeConversation: entry },
              dom,
              document: { createElement: node },
              el(tag, className, text) {
                const created = node(tag);
                if (className) created.className = className;
                if (text !== undefined) created.textContent = text;
                return created;
              },
              selectedRequestTrace: () => trace,
              selectedTraceOperation: candidate => candidate?.selected || null,
              inspectorRequestTrace: () => trace,
              trajectoryRowTitle: () => 'deepseek-chat',
              traceOperationKindLabel: kind => kind.toUpperCase(),
              traceOperationStatusLabel: status => status,
              traceOperationStartedAt: () => '22:13:20.000',
              traceOperationDuration: () => '100ms',
            };
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('selectedTrajectoryRecord', 'selectTraceOperation')}
              ${functionSource('trajectoryDetailTabs', 'trajectoryFactList')}
              ${functionSource('trajectoryFactList', 'trajectoryPayloadGroup')}
              ${functionSource('trajectoryPayloadGroup', 'renderTrajectoryDetailBody')}
              ${functionSource('renderTrajectoryDetailBody', 'setTrajectoryDetailsWidth')}
              ${functionSource('setTrajectoryDetailsWidth', 'renderTrajectoryDetails')}
              ${functionSource('renderTrajectoryDetails', 'updateTrajectoryToolbar')}
              ${functionSource('inspectorTraceOperation', 'paintRunStatus')}
            `, context);

            assert.equal(context.inspectorTraceOperation(entry, trace), null,
              'the default conversation view must not select an operation');

            context.renderTrajectoryDetails(entry);
            assert.deepEqual(panel.toggles.at(-1), ['hidden', true],
              'a closed details pane stays hidden');
            assert.equal(dom.trajectoryDetailsLocation.textContent, '',
              'hidden trajectory facts must not be written');

            entry.mainView = 'traces';
            trajectory.detailsOpen = true;
            assert.equal(context.inspectorTraceOperation(entry, trace), record);
            context.renderTrajectoryDetails(entry);
            assert.deepEqual(panel.toggles.at(-1), ['hidden', false]);
            assert.equal(dom.trajectoryDetailsKind.textContent, 'MODEL');
            assert.equal(dom.trajectoryDetailsKind.dataset.kind, 'model');
            assert.equal(dom.trajectoryDetailsLocation.textContent, 'Req 2 · deepseek-chat');
            assert.deepEqual(
              dom.trajectoryDetailsTabs.children.map(tab => tab.textContent),
              ['概览', '输入', '输出', '计时'],
              'tabs appear only when the operation captured those facts');
            assert.equal(trajectory.detailsTab, 'overview');

            const facts = dom.trajectoryDetailsBody.children[0];
            const readFacts = () => facts.children.map(row => row.children.map(cell => cell.textContent));
            assert.deepEqual(readFacts().slice(0, 4), [
              ['状态', 'done'], ['内部 Operation', 'model-0'], ['开始', '22:13:20.000'], ['Duration', '100ms'],
            ]);
            assert.ok(readFacts().some(([term, value]) => term === 'Total tokens' && value === '12'));

            trajectory.detailsTab = 'input';
            context.renderTrajectoryDetails(entry);
            const input = dom.trajectoryDetailsBody.children[0];
            assert.deepEqual(
              input.children.map(group => group.children[0].textContent),
              ['Input', '本轮加载工具']);

            trajectory.detailsTab = 'output';
            context.renderTrajectoryDetails(entry);
            assert.deepEqual(
              dom.trajectoryDetailsBody.children[0].children.map(group => group.children[1].textContent),
              ['Answer']);

            const bare = { ...record, input: '', output: '', reasoning: '', error: '', tools: [],
              toolCatalogCaptured: false, startedAt: null };
            trace.selected = bare;
            trajectory.detailsTab = null;
            context.renderTrajectoryDetails(entry);
            assert.deepEqual(dom.trajectoryDetailsTabs.children.map(tab => tab.textContent), ['概览'],
              'missing facts must not create empty inspector tabs');
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("const operation = inspectorTraceOperation(entry, trace);");
        app.Should().Contain("const record = trajectory.detailsOpen ? selectedTrajectoryRecord(entry) : null;");
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
            const end = source.indexOf('\nfunction trajectoryRequestSummary(', start);
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
              availableToolNames: ['github.get_issue', 'nyxid.require_service'],
              timestamp: 1700000000100,
            });
            const firstModel = trace.recordIndex.get('model:model-round-0');
            assert.ok(firstModel, 'model_start creates a record before any text exists');
            assert.equal(firstModel.output, '');
            assert.equal(firstModel.model, 'deepseek-chat');
            assert.equal(firstModel.provider, 'deepseek');
            assert.equal(firstModel.tools.join('|'), 'github.get_issue|nyxid.require_service');
            assert.equal(firstModel.toolCatalogCaptured, true);
            assert.equal(firstModel.round, 0);
            assert.equal(firstModel.serverSequence, 10);
            assert.equal(context.traceOperationDurationMs(firstModel), null);
            assert.equal(context.trajectoryLoadedToolsSummary(firstModel),
              '已加载 2 · github.get_issue, nyxid.require_service');
            assert.equal(context.trajectoryLoadedToolsSummary({
              kind: 'model', tools: [], toolCatalogCaptured: true,
            }), '已加载 0');
            assert.equal(context.trajectoryLoadedToolsSummary({
              kind: 'model', tools: [], toolCatalogCaptured: false,
            }), null, 'legacy records must not guess that an uncaptured catalog was empty');

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
              type: 'tool_start', toolCallId: 'call-search',
              toolName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
              presentation: {
                invocationName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
                displayName: 'Search deployments', kind: 'nyxIdOperation',
                sourceRef: {nyxIdOperation: {connectionLabel: 'Production GitHub'}},
              },
              argumentsJson: '{"token":"raw-secret-must-not-leak"}',
              sequence: 12, timestamp: 1700000000800,
            });
            const tool = trace.recordIndex.get('tool:call-search');
            assert.ok(tool);
            assert.equal(tool.input, '', 'tool_start never exposes raw arguments');
            assert.equal(tool.title, 'Production GitHub · Search deployments');
            assert.equal(tool.invocationName,
              'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd');
            assert.equal(tool.presentation.displayName, 'Search deployments');
            assert.equal(tool.serverSequence, 12);
            assert.equal(context.traceOperationDurationMs(tool), null);

            apply(entry, run, {
              type: 'tool_start', toolCallId: 'call-search',
              toolName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
              presentation: {
                invocationName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
                displayName: 'Search deployments', kind: 'nyxIdOperation',
                sourceRef: {nyxIdOperation: {connectionLabel: 'Production GitHub'}},
              },
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
              type: 'waiting_signal', stepId: 'wait_for_post_timeout_choice',
              signalName: 'dinner_date_user_choice_after_timeout',
              prompt: 'All three venues are held. Pick one to keep.',
              sequence: 14, timestamp: 1700000001250,
            });
            const waitingStep = trace.recordIndex.get('workflow:wait_for_post_timeout_choice');
            assert.ok(waitingStep, 'waiting signals are shown in the same request trajectory');
            assert.equal(waitingStep.kind, 'workflow');
            assert.equal(waitingStep.title, 'wait_for_post_timeout_choice');
            assert.equal(waitingStep.status, 'running');
            assert.equal(waitingStep.output, 'All three venues are held. Pick one to keep.');
            assert.equal(waitingStep.serverSequence, 14);
            assert.equal(context.traceOperationDurationMs(waitingStep), null);

            apply(entry, run, {
              type: 'step_completed', stepId: 'hold_candidate_option_1',
              displayName: 'Hold candidate option 1', message: 'Pasta Bar held.',
              success: true, sequence: 15, timestamp: 1700000001300,
            });
            const holdStep = trace.recordIndex.get('workflow:hold_candidate_option_1');
            assert.ok(holdStep, 'workflow primitive steps are first-class trajectory records');
            assert.equal(holdStep.kind, 'workflow');
            assert.equal(holdStep.title, 'Hold candidate option 1');
            assert.equal(holdStep.status, 'done');
            assert.equal(holdStep.output, 'Pasta Bar held.');
            assert.equal(holdStep.serverSequence, 15);

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
        app.Should().Contain("function trajectoryLoadedToolsSummary(record, limit = 3)");
        app.Should().Contain("const tools = el(\"span\", \"trajectory-content-tools\")");
        app.Should().Contain("fields.tools.title = loadedTools === null ? \"\" : record.tools.join(\"\\n\");");
        app.Should().Contain("applyWorkflowStepTraceOperation(trace, step);");
        app.Should().Contain("const workflows = records.filter((record) => record.kind === \"workflow\").length;");
    }

    [Fact]
    public async Task WorkflowStudio_RunActivity_ShouldRenderTypedToolPresentationInsteadOfOpaqueInvocationNames()
    {
        var app = await GetStudioAssetAsync(WorkflowStudioEndpoints.GetAssistantApp);
        const string script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const source = require('node:fs').readFileSync(0, 'utf8');

            function functionSource(name) {
              const start = source.indexOf('function ' + name + '(');
              assert.notEqual(start, -1, name + ' must exist in the served Studio app');
              const end = source.indexOf('\n}\n', start);
              assert.notEqual(end, -1, name + ' must close at column zero');
              return source.slice(start, end + 3);
            }

            const context = {String};
            vm.createContext(context);
            vm.runInContext(`
              ${functionSource('normalizedToolText')}
              ${functionSource('containsOpaqueToolInvocation')}
              ${functionSource('readableToolInvocationName')}
              ${functionSource('nyxIdToolPresentationSource')}
              ${functionSource('describeToolOperation')}
              ${functionSource('toolActivityRunningCopy')}
              ${functionSource('trajectoryPreview')}
              ${functionSource('trajectoryRowTitle')}
              ${functionSource('actorStepSourceLabel')}
              ${functionSource('actorStepDisplayName')}
            `, context);

            const opaqueName = 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd';
            const presentation = {
              invocationName: opaqueName,
              displayName: 'Get repository',
              description: "Read 'Get repository' from connected service 'GitHub'.",
              kind: 'nyxIdOperation',
              sourceRef: {
                type: 'nyxIdOperation',
                nyxIdOperation: {
                  connectionLabel: 'Work GitHub',
                  connectorDisplayName: 'GitHub',
                  operationId: 'get_repository',
                },
              },
            };
            const tool = context.describeToolOperation({toolName: opaqueName, presentation});
            assert.equal(tool.invocationName, opaqueName,
              'the exact invocation identity remains available for dispatch and diagnostics');
            assert.equal(tool.displayName, 'Get repository');
            assert.equal(tool.serviceLabel, 'Work GitHub');
            assert.equal(tool.title, 'Work GitHub · Get repository');
            assert.equal(context.toolActivityRunningCopy(tool),
              '正在通过 Work GitHub 执行 Get repository…');
            assert.equal(context.readableToolInvocationName(opaqueName), '连接服务操作',
              'an old event without presentation still cannot leak the opaque invocation name');

            const record = {kind: 'tool', invocationName: opaqueName, title: opaqueName, presentation};
            assert.equal(context.trajectoryRowTitle(record), 'Work GitHub · Get repository');
            assert.equal(context.trajectoryRowTitle(record).includes('nyxop_'), false);

            const step = {
              kind: 'tool',
              description: `Run authorized tool ${opaqueName}.`,
              source: {tool: {toolName: opaqueName, presentation}},
            };
            assert.equal(context.actorStepDisplayName(step),
              '通过 Work GitHub 执行 Get repository');
            assert.equal(context.actorStepSourceLabel(step), 'Work GitHub · NyxID 连接服务');
            assert.equal(context.actorStepDisplayName(step).includes('nyxop_'), false);

            const directStatePresentation = {
              ...presentation,
              sourceRef: undefined,
              nyxIdOperation: presentation.sourceRef.nyxIdOperation,
            };
            assert.equal(context.describeToolOperation({toolName: opaqueName,
              presentation: directStatePresentation}).title, 'Work GitHub · Get repository',
              'SSE and actor current-state presentation shapes render identically');
            """;

        var result = await RunNodeAsync(script, app);

        result.ExitCode.Should().Be(0, result.Error + result.Output);
        app.Should().Contain("const label = el(\"span\", \"\", \"AI 执行\")");
        app.Should().Contain("const presentation = describeToolOperation(event);");
        app.Should().Contain("el(\"strong\", \"\", presentation.title)");
        app.Should().Contain("toolActivityRunningCopy(presentation)");
        var addToolStart = app.IndexOf("function addTool(event)", StringComparison.Ordinal);
        var addToolEnd = app.IndexOf("\nfunction updateActivityProgress()", addToolStart, StringComparison.Ordinal);
        addToolStart.Should().BeGreaterThanOrEqualTo(0);
        addToolEnd.Should().BeGreaterThan(addToolStart);
        app[addToolStart..addToolEnd].Should().NotContain("el(\"strong\", \"\", name)");
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
                availableToolNames: ['github.get_issue', 'nyxid.require_service'],
              },
            });
            assert.equal(modelStart.type, 'model_start');
            assert.equal(modelStart.operationId, 'model-round-0');
            assert.equal(modelStart.sessionId, 'session-shared');
            assert.equal(modelStart.round, 0);
            assert.equal(modelStart.model, 'deepseek-chat');
            assert.equal(modelStart.availableToolNames.join('|'), 'github.get_issue|nyxid.require_service');
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
              toolCallStart: {
                toolCallId: 'call-search',
                toolName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
                presentation: {
                  invocationName: 'nyxop_f9abd921a0cdeb24004a13c914d961dcee4bb6d2f8eff2dd',
                  displayName: 'Search deployments',
                  kind: 'nyxIdOperation',
                  sourceRef: {nyxIdOperation: {connectionLabel: 'Production GitHub'}},
                },
              },
            });
            assert.equal(toolStart.type, 'tool_start');
            assert.equal(toolStart.toolCallId, 'call-search');
            assert.equal(toolStart.presentation.displayName, 'Search deployments');
            assert.equal(toolStart.presentation.sourceRef.nyxIdOperation.connectionLabel,
              'Production GitHub');
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
}
