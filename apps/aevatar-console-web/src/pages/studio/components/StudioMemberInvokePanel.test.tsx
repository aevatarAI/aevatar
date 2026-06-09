import { AGUIEventType } from '@aevatar-react-sdk/types';
import {
  fireEvent,
  render,
  screen,
  waitFor,
} from '@testing-library/react';
import { message } from 'antd';
import React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { createIdleInvokeResult } from './StudioMemberInvokePanel.currentRun';
import StudioMemberInvokePanel from './StudioMemberInvokePanel';
import StudioMemberInvokeHistoryPanel from './StudioMemberInvokeHistoryPanel';

jest.mock('antd', () => {
  const actual = jest.requireActual('antd');
  return {
    ...actual,
    message: {
      ...actual.message,
      info: jest.fn(),
      success: jest.fn(),
      warning: jest.fn(),
    },
  };
});

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
  },
}));

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getMemberEndpointContract: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    bindScopeGAgent: jest.fn(),
  },
}));

describe('StudioMemberInvokePanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    Element.prototype.scrollTo = jest.fn();
    Element.prototype.scrollIntoView = jest.fn();
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText: jest.fn().mockResolvedValue(undefined) },
    });
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValue({
      accepted: true,
      commandId: 'cmd-1',
      requestId: 'run-1',
      targetActorId: 'actor-1',
    });
    (scopeRuntimeApi.getMemberEndpointContract as jest.Mock).mockResolvedValue({
      defaultSmokeInputMode: 'typed-payload',
      defaultSmokePrompt: null,
      deploymentStatus: 'Active',
      endpointId: 'submit',
      fetchExample: null,
      curlExample: null,
      invokePath: '/api/scopes/scope-1/members/default/invoke/submit',
      method: 'POST',
      publishedServiceId: 'default',
      requestContentType: 'application/json',
      requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
      responseContentType: 'application/json',
      responseTypeUrl: 'type.googleapis.com/example.ContractSubmitResult',
      revisionId: 'contract-rev',
      sampleRequestJson: '{"message":"hello"}',
      scopeId: 'scope-1',
      serviceId: 'default',
      smokeTestSupported: true,
      streamFrameFormat: null,
      supportsAguiFrames: false,
      supportsSse: false,
      supportsWebSocket: false,
      invocationReadiness: {
        canInvoke: true,
        status: 'ready',
        reasonCode: 'ready',
        message: 'Member endpoint is ready for invocation.',
        revisionId: 'contract-rev',
        deploymentId: 'dep-2',
        observedAtUtc: '2026-03-26T07:02:00Z',
      },
    });
    (parseBackendSSEStream as jest.Mock).mockImplementation(
      async function* () {},
    );
  });

  it('renders the invoke workbench skeleton with a compact contract and a persistent console', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        memberRevision: {
          allocationWeight: 100,
          artifactHash: 'hash-2',
          createdAt: '2026-03-26T07:00:00Z',
          deploymentId: 'dep-2',
          failureReason: '',
          implementationKind: 'workflow',
          inlineWorkflowCount: 1,
          isActiveServing: true,
          isDefaultServing: true,
          isServingTarget: true,
          preparedAt: '2026-03-26T07:01:00Z',
          primaryActorId: 'actor-default',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
          revisionId: 'rev-2',
          scriptDefinitionActorId: '',
          scriptId: '',
          scriptRevision: '',
          scriptSourceHash: '',
          servingState: 'Active',
          staticActorTypeName: '',
          status: 'Published',
          workflowDefinitionActorId: 'scope-workflow:scope-1:default',
          workflowName: 'workspace-demo',
        },
        scopeId: 'scope-1',
        selectedMemberLabel: 'workspace-demo',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    expect(
      await screen.findByTestId('studio-member-invoke-panel'),
    ).toBeTruthy();
    const targetSummary = screen.getByTestId('studio-invoke-target-summary');
    expect(targetSummary).toHaveTextContent('workspace-demo');
    expect(targetSummary).toHaveTextContent('Member: default');
    expect(targetSummary).toHaveTextContent('Service: workspace-demo');
    expect(targetSummary).toHaveTextContent('Endpoint: Submit (submit)');
    expect(targetSummary).toHaveTextContent('Lifecycle: Active');
    expect(targetSummary).toHaveTextContent('Ready');
    expect(targetSummary).not.toHaveTextContent('Command ID');
    expect(targetSummary).not.toHaveTextContent('Actor ID');
    expect(targetSummary).not.toHaveTextContent('Member ID');
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.getByText('Run output')).toBeTruthy();
    expect(screen.queryByText('Conversation')).toBeNull();
    expect(screen.getAllByText('Output').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Timeline')).toBeTruthy();
    expect(screen.getByText('Events')).toBeTruthy();
    expect(screen.getByText('Metadata')).toBeTruthy();
    expect(screen.getByText('Advanced typed payload')).toBeTruthy();
    expect(screen.queryByLabelText('Payload base64')).toBeNull();
    expect(screen.getByTestId('studio-invoke-playground-actions')).toBeTruthy();
    const invokeWorkspace = screen.getByTestId('studio-invoke-workspace');
    const mainDebugArea = screen.getByTestId('studio-invoke-main-debug-area');
    const invokeComposerDock = screen.getByTestId(
      'studio-invoke-composer-dock',
    );
    const runOutputSection = screen.getByTestId(
      'studio-invoke-run-output-section',
    );
    const historyPanel = screen.getByTestId('studio-invoke-history-panel');
    const currentRunViewport = screen.getByTestId(
      'studio-invoke-current-run-viewport',
    );
    expect(invokeWorkspace).toContainElement(targetSummary);
    expect(invokeWorkspace).toContainElement(mainDebugArea);
    expect(invokeWorkspace).toContainElement(invokeComposerDock);
    expect(invokeWorkspace.children[1]).toBe(invokeComposerDock);
    expect(mainDebugArea).not.toContainElement(invokeComposerDock);
    expect(invokeComposerDock).toContainElement(
      screen.getByLabelText('Invocation request input'),
    );
    expect(mainDebugArea.style.overflow).toBe('visible');
    expect(mainDebugArea.style.minHeight).toBe('0');
    expect(runOutputSection.style.flex).toBe('0 0 auto');
    expect(runOutputSection.style.minHeight).toBe('0');
    expect(historyPanel.style.flex).toBe('0 0 auto');
    expect(historyPanel.style.minHeight).toBe('0');
    expect(currentRunViewport.style.overflow).toBe('visible');
    expect(invokeComposerDock.style.flex).toBe('0 0 auto');
    expect(screen.getByRole('button', { name: 'Invoke' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Stop' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Clear' })).toBeTruthy();
    expect(
      screen.getByText('Send a prompt above to create the first run.'),
    ).toBeTruthy();
    expect(screen.getByText('Run history (0)')).toBeTruthy();
    expect(screen.getAllByText('No runs yet').length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();
    expect(screen.queryByText('调用契约')).toBeNull();
    expect(screen.queryByRole('button', { name: /Details|详情|展开/ })).toBeNull();
    expect(screen.queryByTestId('studio-invoke-selected-run-detail')).toBeNull();
  });

  it('keeps prompt validation local and does not create a failed run for empty chat input', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'joker',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Invoke' }));

    expect(await screen.findByText('Please enter Prompt before initiating Invoke.')).toBeTruthy();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(screen.queryByText('调用契约')).toBeNull();
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.queryByText('Conversation')).toBeNull();
    expect(screen.getByText('Run output')).toBeTruthy();
    expect(
      screen.getByText('Send a prompt above to create the first run.'),
    ).toBeTruthy();
    expect(screen.getByText('Run history (0)')).toBeTruthy();
    expect(screen.getAllByText('No runs yet').length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('Run failed')).toBeNull();
  });

  it('shows the missing target reason when a member has no invokable endpoint', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        scopeId: 'scope-1',
        selectedMemberLabel: 'Unbound member',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'Unbound service',
            endpoints: [],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    expect(await screen.findByTestId('studio-invoke-target-summary')).toHaveTextContent(
      'Select an endpoint before invoking.',
    );
    expect(screen.getAllByText('Select an endpoint before invoking.').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('button', { name: 'Invoke' })).toBeDisabled();
  });

  it('prefers final run output over intermediate assistant text for chat invoke results', async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({});
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        delta: '可以拆成这些重点词：',
        type: AGUIEventType.TEXT_MESSAGE_CONTENT,
      };
      yield {
        result: '핵심 단어로 나누면:\n- 빠른 요약',
        type: AGUIEventType.RUN_FINISHED,
      };
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'joker',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'joker',
            endpoints: [
              {
                description: 'Chat with joker.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-joker',
            serviceId: 'joker',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Give me a quick summary of what this member can do.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-1',
        {
          prompt: 'Give me a quick summary of what this member can do.',
        },
        expect.any(AbortSignal),
        {
          serviceId: 'joker',
        },
      );
    });

    expect(await screen.findByText(/핵심 단어로 나누면/)).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-observe-handoff')).toHaveTextContent(
      'This run is ready for Observe. Switch to Observe to inspect backend events, audit frames, and the runtime trail for this member.',
    );
    expect(Element.prototype.scrollTo).toHaveBeenCalledWith(
      expect.objectContaining({ top: expect.any(Number) }),
    );
    expect(
      (Element.prototype.scrollTo as jest.Mock).mock.calls.every(
        ([options]) =>
          !(
            options &&
            typeof options === 'object' &&
            'block' in (options as Record<string, unknown>)
          ),
      ),
    ).toBe(true);
    expect(
      (Element.prototype.scrollTo as jest.Mock).mock.instances.every(
        (target) =>
          target === screen.getByTestId('studio-invoke-chat-transcript'),
      ),
    ).toBe(true);
    expect(screen.getByText(/빠른 요약/)).toBeTruthy();
    expect(screen.queryByText('可以拆成这些重点词：')).toBeNull();
    expect(screen.getByText('Latest run')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-run-status-summary')).toHaveTextContent(
      'Succeeded',
    );
  });

  it('shows a recovery path when the latest Invoke run fails', async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockRejectedValueOnce(
      new Error('GAgent draft-run timed out.'),
    );

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'gagent-1',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'gagent-1',
            endpoints: [
              {
                description: 'Chat with gagent-1.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-gagent',
            serviceId: 'gagent-1',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Classify this support ticket.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    expect(await screen.findByText('Run failed')).toBeTruthy();
    expect(screen.getByText('GAgent draft-run timed out.')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-recovery-path')).toHaveTextContent(
      'This failed only the Invoke run. Retry with a smaller prompt, inspect Events for backend signals, or return to Build/Bind if the member contract needs changes.',
    );
    expect(screen.getByRole('button', { name: 'Retry as new run' })).toBeTruthy();
  });

  it('lets Run history expand in document flow when many runs are rendered', () => {
    const longErrorDetail = Array.from(
      { length: 12 },
      (_, index) => `detail-${index}-abcdefghijklmnopqrstuvwxyz`,
    ).join('\n');
    const idleResult = createIdleInvokeResult();

    render(
      React.createElement(StudioMemberInvokeHistoryPanel, {
        entries: Array.from({ length: 12 }, (_, index) => ({
          completedAt: 1_790_000_000_000 + index * 1000,
          createdAt: 1_790_000_000_000 + index * 1000,
          endpointId: 'chat',
          endpointLabel: `Chat endpoint ${index}`,
          errorDetail: index === 0 ? longErrorDetail : '',
          eventCount: index + 1,
          id: `run-${index}`,
          mode: 'stream' as const,
          payloadBase64: '',
          payloadTypeUrl: '',
          prompt: `Run prompt ${index}`,
          runId: `run-${index}`,
          serviceId: 'member-service',
          startedAt: 1_790_000_000_000 + index * 1000,
          status: index === 0 ? ('error' as const) : ('success' as const),
          summary: `Run summary ${index}`,
          snapshot: {
            chatMessages: [],
            result: {
              ...idleResult,
              actorId: `actor-${index}`,
              commandId: `cmd-${index}`,
              runId: `run-${index}`,
              status: index === 0 ? ('error' as const) : ('success' as const),
            },
          },
        })),
        selectedHistoryId: 'run-0',
        onCopyInput: jest.fn(),
        onCopyOutput: jest.fn(),
        onCopyRunId: jest.fn(),
        onRetryAsNewRun: jest.fn(),
        onSelectEntry: jest.fn(),
      }),
    );

    const historyScroll = screen.getByTestId('studio-invoke-history-scroll');
    expect(historyScroll.style.overflow).toBe('visible');
    expect(historyScroll.style.minHeight).toBe('0');
    expect(historyScroll.style.display).toBe('flex');
    expect(screen.getByText('Run prompt 11')).toBeTruthy();
    expect(screen.getByText('Run history (12)')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy input' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy output' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy run id' })).toBeTruthy();
    expect(
      screen.getByTestId('studio-invoke-history-readonly-guidance'),
    ).toHaveTextContent(
      'Historical run is read-only. Retry as new run restores the prompt without changing this record.',
    );
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
    expect(screen.queryByTestId('studio-invoke-selected-run-detail')).toBeNull();
    expect(screen.queryByText('Member ID')).toBeNull();
    expect(screen.queryByRole('button', { name: /Details|详情|展开/ })).toBeNull();
  });

  it('wires selected failed history run actions with disabled unavailable copies', () => {
    const idleResult = createIdleInvokeResult();
    const copyInput = jest.fn();
    const copyOutput = jest.fn();
    const copyRunId = jest.fn();
    const retryAsNewRun = jest.fn();

    render(
      React.createElement(StudioMemberInvokeHistoryPanel, {
        entries: [
          {
            completedAt: 1_790_000_000_000,
            createdAt: 1_790_000_000_000,
            endpointId: 'chat',
            endpointLabel: 'chat',
            errorDetail: 'Provider failed',
            eventCount: 0,
            id: 'failed-run',
            mode: 'stream' as const,
            payloadBase64: '',
            payloadTypeUrl: '',
            prompt: 'hello',
            runId: '',
            serviceId: 'member-service',
            startedAt: 1_790_000_000_000,
            status: 'error' as const,
            summary: 'Provider failed',
            snapshot: {
              chatMessages: [],
              result: {
                ...idleResult,
                error: 'Provider failed',
                runId: '',
                status: 'error',
              },
            },
          },
        ],
        getEntryOutputText: () => 'Provider failed',
        selectedHistoryId: 'failed-run',
        onCopyInput: copyInput,
        onCopyOutput: copyOutput,
        onCopyRunId: copyRunId,
        onRetryAsNewRun: retryAsNewRun,
        onSelectEntry: jest.fn(),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Copy input' }));
    expect(copyInput).toHaveBeenCalledWith('failed-run');

    fireEvent.click(screen.getByRole('button', { name: 'Copy output' }));
    expect(copyOutput).toHaveBeenCalledWith('failed-run');

    expect(screen.getByRole('button', { name: 'Copy run id' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Retry as new run' }));
    expect(retryAsNewRun).toHaveBeenCalledWith('failed-run');
  });

  it('routes GAgent chat invokes through the team stream endpoint', async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({});

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'joker',
        memberRevision: {
          allocationWeight: 100,
          artifactHash: 'hash-gagent',
          createdAt: '2026-03-26T07:00:00Z',
          deploymentId: 'dep-gagent',
          failureReason: '',
          implementationKind: 'gagent',
          inlineWorkflowCount: 0,
          isActiveServing: true,
          isDefaultServing: true,
          isServingTarget: true,
          preparedAt: '2026-03-26T07:01:00Z',
          primaryActorId: 'actor-gagent',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
          revisionId: 'rev-gagent',
          scriptDefinitionActorId: '',
          scriptId: '',
          scriptRevision: '',
          scriptSourceHash: '',
          servingState: 'Active',
          staticActorTypeName: 'Aevatar.GAgents.GAgent',
          status: 'Published',
          workflowDefinitionActorId: '',
          workflowName: '',
        },
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'joker',
            endpoints: [
              {
                description: 'Chat with joker.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-joker',
            serviceId: 'joker',
          },
        ],
        teamId: 'team-1',
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Run the gagent team.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-1',
        {
          prompt: 'Run the gagent team.',
        },
        expect.any(AbortSignal),
        {
          teamId: 'team-1',
        },
      );
    });
  });

  it('routes workflow chat invokes through the team stream endpoint when team context is present', async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({});

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'workspace-demo',
        memberRevision: {
          allocationWeight: 100,
          artifactHash: 'hash-workflow',
          createdAt: '2026-03-26T07:00:00Z',
          deploymentId: 'dep-workflow',
          failureReason: '',
          implementationKind: 'workflow',
          inlineWorkflowCount: 1,
          isActiveServing: true,
          isDefaultServing: true,
          isServingTarget: true,
          preparedAt: '2026-03-26T07:01:00Z',
          primaryActorId: 'actor-workflow',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
          revisionId: 'rev-workflow',
          scriptDefinitionActorId: '',
          scriptId: '',
          scriptRevision: '',
          scriptSourceHash: '',
          servingState: 'Active',
          staticActorTypeName: '',
          status: 'Published',
          workflowDefinitionActorId: 'scope-workflow:scope-1:workspace-demo',
          workflowName: 'workspace-demo',
        },
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-workflow',
            serviceId: 'member-workspace-demo',
          },
        ],
        teamId: 'team-1',
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Run the team member.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-1',
        {
          prompt: 'Run the team member.',
        },
        expect.any(AbortSignal),
        {
          teamId: 'team-1',
        },
      );
    });
  });

  it('records runs into read-only Run history and keeps technical fields in Metadata', async () => {
    const onObserveSessionChange = jest.fn();

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        onObserveSessionChange,
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledWith(
      'scope-1',
      'default',
      'submit',
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
          prompt: 'Route this escalation to billing review.',
        }),
        {
          serviceId: 'default',
        },
      );
    });
    await waitFor(() => {
      expect(onObserveSessionChange).toHaveBeenLastCalledWith(
        expect.objectContaining({
          actorId: 'actor-1',
          completedAtUtc: expect.any(String),
          endpointId: 'submit',
          prompt: 'Route this escalation to billing review.',
          runId: 'run-1',
          serviceId: 'default',
          status: 'success',
        }),
      );
    });

    expect(await screen.findByText('Run history (1)')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-observe-handoff')).toHaveTextContent(
      'Invoke receipt was captured. Switch to Observe to watch backend events and read-model materialization catch up for this member.',
    );
    expect(
      screen.getByTestId('studio-invoke-history-scroll').style.overflow,
    ).toBe('visible');
    expect(
      screen.getAllByText('Route this escalation to billing review.').length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Latest run')).toBeTruthy();
    expect(screen.getByText('Status summary')).toBeTruthy();
    expect(screen.getByText('Input')).toBeTruthy();
    expect(screen.getAllByText('Output').length).toBeGreaterThanOrEqual(1);
    expect(
      screen.getByText('No displayable content returned.'),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Details|详情|展开/ })).toBeNull();
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();

    fireEvent.click(screen.getByRole('tab', { name: 'Metadata' }));
    expect(screen.getByText('Full Run ID')).toBeTruthy();
    expect(screen.getByText('run-1')).toBeTruthy();
    expect(screen.getByText('Command ID')).toBeTruthy();
    expect(screen.getByText('cmd-1')).toBeTruthy();
    expect(screen.getByText('Actor ID')).toBeTruthy();
    expect(screen.getByText('actor-1')).toBeTruthy();
    expect(screen.getByText('Member ID')).toBeTruthy();
    expect(screen.getByText('default')).toBeTruthy();
    expect(screen.getByText('Advanced details')).toBeTruthy();
    expect(screen.queryByTestId('studio-invoke-selected-run-detail')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(
      screen.getByText('Send a prompt above to create the first run.'),
    ).toBeTruthy();

    const historyScroll = screen.getByTestId('studio-invoke-history-scroll');
    fireEvent.click(
      screen.getByRole('button', {
        name: /View historical run Route this escalation to billing review./,
      }),
    );
    expect(historyScroll).toBeTruthy();
    expect(screen.getByText('Historical run · Read-only')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-observe-handoff')).toHaveTextContent(
      'Historical runs are read-only. Retry as a new run when you need a fresh Observe handoff.',
    );
    expect(screen.getByTestId('studio-invoke-composer-guidance')).toHaveTextContent(
      'Historical run is read-only. Sending this prompt creates a new independent Run and fresh Observe handoff.',
    );
    expect(screen.getByRole('button', { name: 'Copy input' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy output' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy run id' })).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Continue|Resume|Edit/ })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Copy input' }));
    expect(window.navigator.clipboard.writeText).toHaveBeenCalledWith(
      'Route this escalation to billing review.',
    );
    expect(message.success).toHaveBeenCalledWith('Input copied.');

    expect(screen.getByRole('button', { name: 'Copy output' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Copy run id' }));
    expect(window.navigator.clipboard.writeText).toHaveBeenCalledWith('run-1');
    expect(message.success).toHaveBeenCalledWith('Run id copied.');

    fireEvent.click(screen.getByRole('button', { name: 'Retry as new run' }));
    expect(screen.getByLabelText('Invocation request input')).toHaveValue(
      'Route this escalation to billing review.',
    );
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({
      behavior: 'smooth',
      block: 'start',
    });
    expect(message.info).toHaveBeenCalledWith(
      'Prompt restored. Click Invoke to create a new Run.',
    );

    fireEvent.change(screen.getByLabelText('Invocation request input'), {
      target: {
        value: 'Overwrite prompt',
      },
    });

    expect(screen.getByLabelText('Invocation request input')).toHaveValue(
      'Overwrite prompt',
    );
  });

  it('requires base64 for non text typed payloads and sends it for structured invoke endpoints', async () => {
    (scopeRuntimeApi.getMemberEndpointContract as jest.Mock).mockResolvedValueOnce({
      defaultSmokeInputMode: 'typed-payload',
      defaultSmokePrompt: null,
      deploymentStatus: 'Active',
      endpointId: 'submit',
      fetchExample: null,
      curlExample: null,
      invokePath: '/api/scopes/scope-1/members/default/invoke/submit',
      method: 'POST',
      publishedServiceId: 'default',
      requestContentType: 'application/json',
      requestTypeUrl: 'type.googleapis.com/example.ContractSubmit',
      responseContentType: 'application/json',
      responseTypeUrl: 'type.googleapis.com/example.ContractSubmitResult',
      revisionId: 'contract-rev',
      sampleRequestJson: '{"message":"hello"}',
      scopeId: 'scope-1',
      serviceId: 'default',
      smokeTestSupported: true,
      streamFrameFormat: null,
      supportsAguiFrames: false,
      supportsSse: false,
      supportsWebSocket: false,
      invocationReadiness: {
        canInvoke: true,
        status: 'ready',
        reasonCode: 'ready',
        message: 'Member endpoint is ready for invocation.',
        revisionId: 'contract-rev',
        deploymentId: 'dep-2',
        observedAtUtc: '2026-03-26T07:02:00Z',
      },
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    expect(
      await screen.findByText('Advanced typed payload'),
    ).toBeTruthy();
    fireEvent.click(screen.getByText('Advanced typed payload'));
    await waitFor(() => {
      expect(screen.getByLabelText('Payload type URL')).toHaveValue(
        'type.googleapis.com/example.ContractSubmit',
      );
    });

    fireEvent.change(screen.getByLabelText('Invocation request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    expect(
      await screen.findByText(
        "payloadBase64 is required for payloadTypeUrl 'type.googleapis.com/example.ContractSubmit'.",
      ),
    ).toBeTruthy();
    expect(runtimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
    expect(screen.getByText('Run history (0)')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Payload base64'), {
      target: {
        value: 'CgVIZWxsbw==',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
          payloadBase64: 'CgVIZWxsbw==',
          payloadTypeUrl: 'type.googleapis.com/example.ContractSubmit',
          prompt: 'Route this escalation to billing review.',
        }),
        {
          serviceId: 'default',
        },
      );
    });
  });

  it('wraps long run error text inside the failure card', async () => {
    const longError =
      'LLM request failed\n[tools=chrono_diff,chrono_tree,chrono_file_edit,chrono_file_write,chrono_file_read,chrono_grep,chrono_glob,telegram_chats_tool_with_a_really_long_unbroken_name] NyxID authentication required for provider nyxid. Please sign in.';
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockRejectedValueOnce(
      new Error(longError),
    );

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'hello',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    expect(await screen.findByText('Run failed')).toBeTruthy();
    const errorNode = screen.getByText(/telegram_chats_tool_with_a_really_long/);
    expect(errorNode).toHaveStyle({
      overflowWrap: 'anywhere',
      whiteSpace: 'pre-wrap',
      wordBreak: 'break-word',
    });
    expect(screen.getByRole('button', { name: 'View events' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy error' })).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
  });

  it('does not enable Runs navigation when invoke only returns a command id', async () => {
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValueOnce({
      accepted: true,
      commandId: 'cmd-only',
      targetActorId: 'actor-1',
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invocation request input'), {
      target: {
        value: 'Dispatch this typed command.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalled();
    });

    expect(screen.getByText('Run history (1)')).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'Metadata' }));
    expect(screen.getByText('Command ID')).toBeTruthy();
    expect(screen.getByText('cmd-only')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '打开运行记录' })).toBeNull();
  });

  it('renders a clear empty state when no selected member is available for invoke', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        emptyState: {
          description:
            '请先在“团队成员”里选择成员，或从绑定页面继续进入，这样调用页面才会稳定固定到单个成员。',
          message: '请选择要调用的成员。',
          type: 'info',
        },
        scopeId: 'scope-1',
        services: [],
      }),
    );

    expect(await screen.findByText('请选择要调用的成员。')).toBeTruthy();
    expect(
      screen.getByText(
        '请先在“团队成员”里选择成员，或从绑定页面继续进入，这样调用页面才会稳定固定到单个成员。',
      ),
    ).toBeTruthy();
    expect(screen.queryByText('调用契约')).toBeNull();
  });
});
