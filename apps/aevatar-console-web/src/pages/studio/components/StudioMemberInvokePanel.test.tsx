import { AGUIEventType } from '@aevatar-react-sdk/types';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import StudioMemberInvokeHistoryPanel from './StudioMemberInvokeHistoryPanel';
import StudioMemberInvokePanel from './StudioMemberInvokePanel';
import { createIdleInvokeResult } from './StudioMemberInvokePanel.currentRun';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamEndpoint: jest.fn(),
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
    expect(targetSummary).toHaveTextContent('Member: workspace-demo');
    expect(targetSummary).toHaveTextContent('Service: workspace-demo');
    expect(targetSummary).toHaveTextContent('Endpoint: Submit');
    expect(targetSummary).toHaveTextContent('Lifecycle: Active');
    expect(targetSummary).toHaveTextContent('Ready');
    expect(targetSummary).not.toHaveTextContent('Member: default');
    expect(targetSummary).not.toHaveTextContent('Endpoint: Submit (submit)');
    expect(targetSummary).not.toHaveTextContent('Command ID');
    expect(targetSummary).not.toHaveTextContent('Actor ID');
    expect(targetSummary).not.toHaveTextContent('Member ID');
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.getByText('Response')).toBeTruthy();
    expect(screen.queryByText('Conversation')).toBeNull();
    expect(screen.getByText('No run yet')).toBeTruthy();
    expect(screen.queryByText('Timeline')).toBeNull();
    expect(screen.queryByText('Events')).toBeNull();
    expect(screen.queryByText('Run diagnostics')).toBeNull();
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.queryByLabelText('Payload base64')).toBeNull();
    expect(screen.getByRole('button', { name: 'Details' })).toBeTruthy();
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
      screen.getByLabelText('Workflow request input'),
    );
    expect(mainDebugArea.style.overflow).toBe('visible');
    expect(mainDebugArea.style.minHeight).toBe('0');
    expect(runOutputSection.style.flex).toBe('0 0 auto');
    expect(runOutputSection.style.minHeight).toBe('0');
    expect(historyPanel.style.flex).toBe('0 0 auto');
    expect(historyPanel.style.minHeight).toBe('0');
    expect(currentRunViewport.style.overflow).toBe('visible');
    expect(invokeComposerDock.style.flex).toBe('0 0 auto');
    expect(screen.getByRole('button', { name: 'Run workflow' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Stop' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Clear' })).toBeTruthy();
    expect(
      screen.getByText('Send a request above to create the first run.'),
    ).toBeTruthy();
    expect(screen.getByText('Run history (0)')).toBeTruthy();
    expect(screen.getAllByText('No runs yet').length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();
    expect(screen.queryByText('调用契约')).toBeNull();
    expect(screen.queryByText('Run diagnostics')).toBeNull();
    expect(
      screen.queryByTestId('studio-invoke-selected-run-detail'),
    ).toBeNull();
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

    fireEvent.click(
      await screen.findByRole('button', { name: 'Run workflow' }),
    );

    expect(
      await screen.findByText('Enter a request before running this workflow.'),
    ).toBeTruthy();
    expect(runtimeRunsApi.streamEndpoint).not.toHaveBeenCalled();
    expect(screen.queryByText('调用契约')).toBeNull();
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.queryByText('Conversation')).toBeNull();
    expect(screen.getByText('Response')).toBeTruthy();
    expect(
      screen.getByText('Send a request above to create the first run.'),
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

    expect(
      await screen.findByTestId('studio-invoke-target-summary'),
    ).toHaveTextContent('Select an endpoint before running.');
    expect(
      screen.getAllByText('Select an endpoint before running.').length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getByRole('button', { name: 'Run workflow' })).toBeDisabled();
  });

  it('uses the member-run surface as an isolated SaaS run page', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        result: 'Member-run answer',
        type: AGUIEventType.RUN_FINISHED,
      };
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'member-with-a-very-long-stable-identifier-1234567890',
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
        selectedMemberLabel:
          'Extremely long member display name that should truncate visually but remain available on hover',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo-service',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Primary chat endpoint with a long label',
                endpointId: 'chat',
                kind: 'chat',
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
        targetSummaryVariant: 'member-run',
        teamId: 'team-1',
      }),
    );

    const targetSummary = await screen.findByTestId(
      'studio-invoke-target-summary',
    );
    expect(targetSummary).toHaveTextContent(
      'Extremely long member display name that should truncate visually but remain available on hover',
    );
    expect(targetSummary).toHaveTextContent(
      'Endpoint: Primary chat endpoint with a long label',
    );
    expect(targetSummary).toHaveTextContent('Status: Active');
    expect(targetSummary).not.toHaveTextContent(
      'member-with-a-very-long-stable-identifier-1234567890',
    );
    expect(targetSummary).not.toHaveTextContent('(chat)');
    expect(targetSummary).not.toHaveTextContent('Member:');
    expect(targetSummary).not.toHaveTextContent(
      'Service: workspace-demo-service',
    );
    expect(targetSummary).not.toHaveTextContent('Workflow');
    expect(targetSummary).not.toHaveTextContent('Team: team-1');
    expect(
      screen.getByTestId('studio-invoke-member-run-workbench'),
    ).toBeTruthy();
    expect(screen.getByText('Launch run')).toBeTruthy();
    expect(
      screen.getByText('One input creates one isolated run.'),
    ).toBeTruthy();
    expect(screen.getByText('Run launcher')).toBeTruthy();
    expect(screen.getByLabelText('Run input')).toBeTruthy();
    expect(screen.queryByLabelText('Workflow request input')).toBeNull();
    expect(
      screen.getByText(
        'Each run is isolated. Previous runs are not sent as context.',
      ),
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Start run' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Run workflow' })).toBeNull();
    expect(
      screen.getByRole('button', { name: 'Technical details' }),
    ).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-target-actions')).toHaveStyle({
      flex: '0 1 auto',
      flexWrap: 'wrap',
      maxWidth: '100%',
      minWidth: 0,
    });
    expect(screen.queryByRole('button', { name: 'Details' })).toBeNull();
    expect(screen.getByText('Current run')).toBeTruthy();
    expect(screen.getByText('No run result yet')).toBeTruthy();
    expect(
      screen.getByText('Start a run to see the result here.'),
    ).toBeTruthy();
    expect(screen.queryByText('New run')).toBeNull();
    expect(screen.queryByText('Run result')).toBeNull();
    expect(screen.queryByText('Observe handoff')).toBeNull();

    fireEvent.change(screen.getByLabelText('Run input'), {
      target: {
        value: 'Summarize the team member status.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    expect(
      (await screen.findAllByText('Member-run answer')).length,
    ).toBeGreaterThanOrEqual(1);
    expect(
      screen.getByTestId('studio-invoke-observe-handoff'),
    ).toHaveTextContent(
      'This run is ready for Observe. Switch to Observe when you need backend events, audit frames, or the runtime trail for this member.',
    );
  });

  it('locks the member-run input and keeps the submitted task visible while a run is in progress', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* (
      _response,
      options?: { signal?: AbortSignal },
    ) {
      await new Promise<void>((resolve) => {
        if (options?.signal?.aborted) {
          resolve();
          return;
        }

        options?.signal?.addEventListener('abort', () => resolve(), {
          once: true,
        });
      });
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'member-run-chat',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'member-run-chat',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-member-run-chat',
            serviceId: 'member-run-chat',
          },
        ],
        targetSummaryVariant: 'member-run',
      }),
    );

    const runInput = await screen.findByLabelText('Run input');
    fireEvent.change(runInput, {
      target: {
        value: 'Summarize the latest support case for billing.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          prompt: 'Summarize the latest support case for billing.',
        },
        expect.any(AbortSignal),
        {
          serviceId: 'member-run-chat',
        },
      );
    });

    expect(screen.queryByRole('textbox', { name: 'Run input' })).toBeNull();
    const submittedReceipt = screen.getByTestId(
      'studio-invoke-submitted-input-receipt',
    );
    expect(submittedReceipt).toHaveTextContent('Submitted input');
    expect(submittedReceipt).toHaveTextContent(
      'Summarize the latest support case for billing.',
    );
    expect(screen.getByText('In progress')).toBeTruthy();
    expect(
      screen.getByText(
        'This submitted input is locked while the run is in progress.',
      ),
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Stop run' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Clear' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Start run' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Stop run' }));
  });

  it('sends member-run attachments through the selected stream endpoint', async () => {
    const image = new File(['image-bytes'], 'cat.png', { type: 'image/png' });
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* (
      _response,
      options?: { signal?: AbortSignal },
    ) {
      await new Promise<void>((resolve) => {
        if (options?.signal?.aborted) {
          resolve();
          return;
        }

        options?.signal?.addEventListener('abort', () => resolve(), {
          once: true,
        });
      });
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        enableFileAttachments: true,
        memberId: 'member-alpha',
        runtimeTarget: 'member',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'member-alpha',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-member-alpha',
            serviceId: 'svc-alpha',
          },
        ],
        targetSummaryVariant: 'member-run',
        teamId: 'team-1',
      }),
    );

    expect(await screen.findByText('No files attached')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Attach files'), {
      target: {
        files: [image],
      },
    });
    expect(
      screen.getByTestId('studio-invoke-attachment-chip'),
    ).toHaveTextContent('cat.png');

    fireEvent.change(screen.getByLabelText('Run input'), {
      target: {
        value: 'Describe this image.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          files: [image],
          prompt: 'Describe this image.',
        },
        expect.any(AbortSignal),
        {
          memberId: 'member-alpha',
        },
      );
    });

    expect(
      screen.getByTestId('studio-invoke-submitted-input-receipt'),
    ).toHaveTextContent('Files: cat.png');
    expect(
      screen.getByTestId('studio-invoke-attachment-chip'),
    ).toHaveTextContent('cat.png');

    fireEvent.click(screen.getByRole('button', { name: 'Stop run' }));
  });

  it('keeps empty member-run attachments local instead of submitting invalid files', async () => {
    const emptyFile = new File([], 'empty.png', { type: 'image/png' });

    render(
      React.createElement(StudioMemberInvokePanel, {
        enableFileAttachments: true,
        memberId: 'member-alpha',
        runtimeTarget: 'member',
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'member-alpha',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-member-alpha',
            serviceId: 'svc-alpha',
          },
        ],
        targetSummaryVariant: 'member-run',
        teamId: 'team-1',
      }),
    );

    fireEvent.change(await screen.findByLabelText('Attach files'), {
      target: {
        files: [emptyFile],
      },
    });
    fireEvent.change(screen.getByLabelText('Run input'), {
      target: {
        value: 'Describe this image.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    expect(
      await screen.findByText(
        'Remove empty file empty.png before starting the run.',
      ),
    ).toBeTruthy();
    expect(runtimeRunsApi.streamEndpoint).not.toHaveBeenCalled();
  });

  it('prefers final run output over intermediate assistant text for chat invoke results', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Give me a quick summary of what this member can do.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          prompt: 'Give me a quick summary of what this member can do.',
        },
        expect.any(AbortSignal),
        {
          serviceId: 'joker',
        },
      );
    });

    expect(
      (await screen.findAllByText(/핵심 단어로 나누면/)).length,
    ).toBeGreaterThanOrEqual(1);
    expect(
      screen.getByTestId('studio-invoke-observe-handoff'),
    ).toHaveTextContent(
      'This run is ready for Observe. Switch to Observe when you need backend events, audit frames, or the runtime trail for this member.',
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
    expect(screen.getByText('Latest response')).toBeTruthy();
    expect(
      screen.getByTestId('studio-invoke-run-status-summary'),
    ).toHaveTextContent('Succeeded');
  });

  it('shows workflow node logs on Invoke runs instead of surfacing transient response chunks', async () => {
    const originalClientHeightDescriptor = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'clientHeight',
    );
    const originalScrollHeightDescriptor = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return this.getAttribute('data-testid') ===
          'studio-invoke-run-log-scroll'
          ? 120
          : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute('data-testid') ===
          'studio-invoke-run-log-scroll'
          ? 480
          : 0;
      },
    });
    try {
      (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
      (parseBackendSSEStream as jest.Mock).mockImplementation(
        async function* () {
          yield {
            runId: 'run-node-log',
            threadId: 'actor-node-log',
            timestamp: Date.parse('2026-06-08T00:00:00Z'),
            type: AGUIEventType.RUN_STARTED,
          };
          yield {
            name: 'aevatar.step.request',
            payload: {
              input: 'Classify ticket severity',
              stepId: 'triage-ticket',
              stepType: 'llm_call',
              targetRole: 'support-analyst',
            },
            timestamp: Date.parse('2026-06-08T00:00:01Z'),
            type: AGUIEventType.CUSTOM,
          };
          yield {
            delta: 'Thinking through severity...',
            type: AGUIEventType.TEXT_MESSAGE_CONTENT,
          };
          yield {
            name: 'aevatar.step.completed',
            payload: {
              output: 'Severity: high',
              stepId: 'triage-ticket',
              success: true,
            },
            timestamp: Date.parse('2026-06-08T00:00:02Z'),
            type: AGUIEventType.CUSTOM,
          };
          yield {
            result: 'Final answer: route to priority support.',
            timestamp: Date.parse('2026-06-08T00:00:03Z'),
            type: AGUIEventType.RUN_FINISHED,
          };
        },
      );

      render(
        React.createElement(StudioMemberInvokePanel, {
          memberId: 'workflow-member',
          scopeId: 'scope-1',
          services: [
            {
              deploymentStatus: 'Active',
              displayName: 'workflow-member',
              endpoints: [
                {
                  description: 'Chat with workflow-member.',
                  displayName: 'Chat',
                  endpointId: 'chat',
                  kind: 'invoke',
                  requestTypeUrl: '',
                  responseTypeUrl: '',
                },
              ],
              kind: 'service',
              namespace: 'default',
              primaryActorId: 'actor-workflow-member',
              serviceId: 'workflow-member',
            },
          ],
        }),
      );

      fireEvent.change(await screen.findByLabelText('Workflow request input'), {
        target: {
          value: 'Classify this ticket.',
        },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

      const runLogs = await screen.findByTestId('studio-invoke-run-logs');
      expect(runLogs).toHaveTextContent('Run logs');
      expect(runLogs).toHaveTextContent('triage-ticket');
      expect(runLogs).toHaveTextContent('llm_call');
      expect(runLogs).toHaveTextContent('Input / Output');
      expect(runLogs).toHaveTextContent('Input · Output');
      expect(
        screen.getByTestId('studio-invoke-run-log-scroll').style.maxHeight,
      ).toBe('min(520px, 58vh)');
      expect(
        screen.getByTestId('studio-invoke-run-log-scroll').style.overflowY,
      ).toBe('auto');
      const runLogScroll = screen.getByTestId('studio-invoke-run-log-scroll');
      await waitFor(() => {
        expect(runLogScroll.scrollTop).toBe(480);
      });
      const nodeDetails = screen.getByTestId(
        'studio-invoke-run-log-details-triage-ticket',
      );
      expect(nodeDetails).not.toHaveAttribute('open');
      fireEvent.click(nodeDetails.querySelector('summary') as HTMLElement);
      expect(nodeDetails).toHaveAttribute('open');
      expect(runLogs).toHaveTextContent('Classify ticket severity');
      expect(runLogs).toHaveTextContent('Severity: high');
      expect(
        await screen.findByText('Final answer: route to priority support.'),
      ).toBeTruthy();
      expect(screen.queryByText('Thinking through severity...')).toBeNull();
    } finally {
      if (originalClientHeightDescriptor) {
        Object.defineProperty(
          HTMLElement.prototype,
          'clientHeight',
          originalClientHeightDescriptor,
        );
      } else {
        delete (HTMLElement.prototype as unknown as { clientHeight?: unknown })
          .clientHeight;
      }
      if (originalScrollHeightDescriptor) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeightDescriptor,
        );
      } else {
        delete (HTMLElement.prototype as unknown as { scrollHeight?: unknown })
          .scrollHeight;
      }
    }
  });

  it('shows a recovery path when the latest Invoke run fails', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockRejectedValueOnce(
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Classify this support ticket.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    expect(await screen.findByText('Run failed')).toBeTruthy();
    expect(screen.getByText('GAgent draft-run timed out.')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-recovery-path')).toHaveTextContent(
      'This run failed. Retry with a smaller request, open diagnostics for backend signals, or edit the member contract from its owning member surface.',
    );
    expect(
      screen.getByRole('button', { name: 'Open diagnostics' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
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
    expect(screen.queryByRole('button', { name: 'Copy run id' })).toBeNull();
    expect(
      screen.getByTestId('studio-invoke-history-readonly-guidance'),
    ).toHaveTextContent(
      'Historical run is read-only. Retry as new run restores the prompt without changing this record.',
    );
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
    expect(
      screen.queryByTestId('studio-invoke-selected-run-detail'),
    ).toBeNull();
    expect(screen.queryByText('Member ID')).toBeNull();
  });

  it('wires selected failed history run actions with disabled unavailable copies', () => {
    const idleResult = createIdleInvokeResult();
    const copyInput = jest.fn();
    const copyOutput = jest.fn();
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
        onRetryAsNewRun: retryAsNewRun,
        onSelectEntry: jest.fn(),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Copy input' }));
    expect(copyInput).toHaveBeenCalledWith('failed-run');

    fireEvent.click(screen.getByRole('button', { name: 'Copy output' }));
    expect(copyOutput).toHaveBeenCalledWith('failed-run');

    expect(screen.queryByRole('button', { name: 'Copy run id' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Retry as new run' }));
    expect(retryAsNewRun).toHaveBeenCalledWith('failed-run');
  });

  it('routes GAgent chat invokes through the team stream endpoint', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});

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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Run the gagent team.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
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
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});

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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Run the team member.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          prompt: 'Run the team member.',
        },
        expect.any(AbortSignal),
        {
          teamId: 'team-1',
        },
      );
    });
  });

  it('routes workflow chat invokes through the member stream endpoint when member target is explicit', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});

    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'workspace-demo',
        runtimeTarget: 'member',
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Run the bound member workflow.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          prompt: 'Run the bound member workflow.',
        },
        expect.any(AbortSignal),
        {
          memberId: 'workspace-demo',
        },
      );
    });
  });

  it('allows bound member-run chat endpoints to start without prompt or files', async () => {
    (runtimeRunsApi.streamEndpoint as jest.Mock).mockResolvedValue({});
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce({
      defaultSmokeInputMode: 'prompt',
      defaultSmokePrompt: null,
      deploymentStatus: 'Active',
      endpointId: 'chat',
      fetchExample: null,
      curlExample: null,
      invokePath: '/api/scopes/scope-1/members/m-alpha/invoke/chat:stream',
      memberId: 'm-alpha',
      method: 'POST',
      publishedServiceId: 'svc-alpha',
      requestContentType: 'application/json',
      requestTypeUrl: '',
      responseContentType: 'text/event-stream',
      responseTypeUrl: '',
      revisionId: 'rev-workflow',
      sampleRequestJson: null,
      scopeId: 'scope-1',
      serviceId: 'svc-alpha',
      smokeTestSupported: true,
      streamFrameFormat: 'workflow-run-event',
      supportsAguiFrames: false,
      supportsSse: true,
      supportsWebSocket: false,
    });

    render(
      React.createElement(StudioMemberInvokePanel, {
        enableFileAttachments: true,
        memberId: 'm-alpha',
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
          workflowDefinitionActorId: 'wf-alpha',
          workflowName: 'status-report',
        },
        presentation: 'member-run',
        runtimeTarget: 'member',
        scopeId: 'scope-1',
        selectedMemberLabel: 'Status reporter',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'Status reporter service',
            endpoints: [
              {
                description: 'Run the bound workflow.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-workflow',
            serviceId: 'svc-alpha',
          },
        ],
        teamId: 'team-alpha',
      }),
    );

    await waitFor(() => {
      expect(scopeRuntimeApi.getMemberEndpointContract).toHaveBeenCalledWith(
        'scope-1',
        'm-alpha',
        'chat',
      );
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-1',
        {
          endpointId: 'chat',
          prompt: '',
        },
        expect.any(AbortSignal),
        {
          memberId: 'm-alpha',
        },
      );
    });
    expect(
      screen.queryByText('Enter a request before running this workflow.'),
    ).toBeNull();
  });

  it('records runs into read-only Run history without exposing internal identifiers', async () => {
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

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
    expect(
      screen.getByTestId('studio-invoke-observe-handoff'),
    ).toHaveTextContent(
      'The workflow run was accepted. Switch to Observe to watch backend events and read-model materialization catch up for this member.',
    );
    expect(
      screen.getByTestId('studio-invoke-history-scroll').style.overflow,
    ).toBe('visible');
    expect(
      screen.getAllByText('Route this escalation to billing review.').length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Latest response')).toBeTruthy();
    expect(screen.getByText('Run status')).toBeTruthy();
    expect(screen.getAllByText('Request').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Response').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('No readable response returned.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Details' })).toBeTruthy();
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();
    expect(screen.queryByRole('tablist')).toBeNull();
    expect(screen.queryByText('Run details')).toBeNull();
    expect(screen.queryByText('Event payload')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(
      await screen.findByTestId('studio-invoke-diagnostics-drawer'),
    ).toBeTruthy();
    expect(screen.getByText('Run details')).toBeTruthy();
    expect(screen.getByText('Status')).toBeTruthy();
    expect(screen.getAllByText('Succeeded').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Endpoint')).toBeTruthy();
    expect(screen.getAllByText('Submit').length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('Run ID')).toBeNull();
    expect(screen.queryByText('run-1')).toBeNull();
    expect(screen.queryByText('Command ID')).toBeNull();
    expect(screen.queryByText('cmd-1')).toBeNull();
    expect(screen.queryByText('Actor ID')).toBeNull();
    expect(screen.queryByText('actor-1')).toBeNull();
    expect(screen.queryByText('Member ID')).toBeNull();
    expect(screen.getByText('Event payload')).toBeTruthy();
    expect(
      screen.queryByTestId('studio-invoke-selected-run-detail'),
    ).toBeNull();

    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByRole('button', { name: 'Clear' }));
    expect(
      screen.getByText('Send a request above to create the first run.'),
    ).toBeTruthy();

    const historyScroll = screen.getByTestId('studio-invoke-history-scroll');
    fireEvent.click(
      screen.getByRole('button', {
        name: /View historical run Route this escalation to billing review./,
      }),
    );
    expect(historyScroll).toBeTruthy();
    expect(screen.queryByText('Historical run · Read-only')).toBeNull();
    expect(screen.queryByTestId('studio-invoke-observe-handoff')).toBeNull();
    expect(screen.getByTestId('studio-invoke-diagnostics-drawer')).toBeTruthy();
    expect(
      screen.getByTestId('studio-invoke-composer-guidance'),
    ).toHaveTextContent(
      'Historical run is read-only. Sending this request starts a new run and fresh Observe handoff.',
    );
    expect(screen.getByRole('button', { name: 'Copy input' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy output' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Copy run id' })).toBeNull();
    expect(
      screen.getByRole('button', { name: 'Retry as new run' }),
    ).toBeTruthy();
    expect(
      screen.queryByRole('button', { name: /Continue|Resume|Edit/ }),
    ).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Copy input' }));
    expect(window.navigator.clipboard.writeText).toHaveBeenCalledWith(
      'Route this escalation to billing review.',
    );
    await waitFor(() => {
      expect(mockConsoleToast.success).toHaveBeenCalledWith('Input copied.');
    });

    expect(screen.getByRole('button', { name: 'Copy output' })).toBeDisabled();

    (window.navigator.clipboard.writeText as jest.Mock).mockRejectedValueOnce(
      new Error('Clipboard access was rejected.'),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Copy input' }));
    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not copy this value.',
      );
    });
    expect(mockConsoleToast.success).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Retry as new run' }));
    expect(screen.getByLabelText('Workflow request input')).toHaveValue(
      'Route this escalation to billing review.',
    );
    expect(Element.prototype.scrollIntoView).toHaveBeenCalledWith({
      behavior: 'smooth',
      block: 'start',
    });
    expect(mockConsoleToast.info).toHaveBeenCalledWith(
      'Request restored. Run workflow to create a new run.',
    );

    fireEvent.change(screen.getByLabelText('Workflow request input'), {
      target: {
        value: 'Overwrite prompt',
      },
    });

    expect(screen.getByLabelText('Workflow request input')).toHaveValue(
      'Overwrite prompt',
    );

    fireEvent.click(screen.getByLabelText('Close'));
    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(await screen.findByText('Latest run detail')).toBeTruthy();
    expect(screen.queryByText('Historical run detail')).toBeNull();
    expect(screen.queryByText('History detail')).toBeNull();
    expect(
      screen.getAllByText('No run is selected yet.').length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText('run-1')).toBeNull();
  });

  it('routes structured endpoint invokes through the member endpoint when member target is explicit', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        memberId: 'default',
        runtimeTarget: 'member',
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
          prompt: 'Route this escalation to billing review.',
        }),
        {
          memberId: 'default',
        },
      );
    });
  });

  it('requires base64 for non text typed payloads and sends it for structured invoke endpoints', async () => {
    (
      scopeRuntimeApi.getMemberEndpointContract as jest.Mock
    ).mockResolvedValueOnce({
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

    expect(await screen.findByRole('button', { name: 'Details' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(
      await screen.findByTestId('studio-invoke-diagnostics-drawer'),
    ).toBeTruthy();
    expect(screen.getByText('Advanced typed payload')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByLabelText('Payload type URL')).toHaveValue(
        'type.googleapis.com/example.ContractSubmit',
      );
    });

    fireEvent.change(screen.getByLabelText('Workflow request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

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
    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'hello',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    expect(await screen.findByText('Run failed')).toBeTruthy();
    const errorNode = screen.getByText(
      /telegram_chats_tool_with_a_really_long/,
    );
    expect(errorNode).toHaveStyle({
      overflowWrap: 'anywhere',
      whiteSpace: 'pre-wrap',
      wordBreak: 'break-word',
    });
    expect(
      screen.getByRole('button', { name: 'Open diagnostics' }),
    ).toBeTruthy();
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

    fireEvent.change(await screen.findByLabelText('Workflow request input'), {
      target: {
        value: 'Dispatch this typed command.',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run workflow' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalled();
    });

    expect(screen.getByText('Run history (1)')).toBeTruthy();
    expect(screen.queryByText('Run details')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Details' }));
    expect(
      await screen.findByTestId('studio-invoke-diagnostics-drawer'),
    ).toBeTruthy();
    expect(screen.getByText('Run details')).toBeTruthy();
    expect(screen.queryByText('Command ID')).toBeNull();
    expect(screen.queryByText('cmd-only')).toBeNull();
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
