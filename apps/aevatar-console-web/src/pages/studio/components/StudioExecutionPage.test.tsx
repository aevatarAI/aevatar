import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { StudioExecutionPage } from './StudioWorkbenchSections';

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: () => {
    const React = require('react');
    return React.createElement('div', null, 'Graph canvas');
  },
}));

function createBaseProps(overrides = {}) {
  return {
    executions: {
      isLoading: false,
      isError: false,
      error: null,
      data: [
        {
          executionId: 'execution-1',
          workflowName: 'workspace-demo',
          prompt: 'Run the demo workflow.',
          status: 'running',
          startedAtUtc: '2026-03-18T00:00:00Z',
          completedAtUtc: null,
          actorId: 'actor-1',
          error: null,
        },
      ],
    },
    selectedExecution: {
      isLoading: false,
      isError: false,
      error: null,
      data: {
        executionId: 'execution-1',
        workflowName: 'workspace-demo',
        prompt: 'Run the demo workflow.',
        status: 'running',
        startedAtUtc: '2026-03-18T00:00:00Z',
        completedAtUtc: null,
        actorId: 'actor-1',
        error: null,
        frames: [
          {
            receivedAtUtc: '2026-03-18T00:00:01Z',
            payload: JSON.stringify({
              custom: {
                name: 'aevatar.step.request',
                payload: {
                  stepId: 'triage',
                  stepType: 'llm_call',
                  targetRole: 'support',
                  input: 'Route the ticket.',
                },
              },
            }),
          },
          {
            receivedAtUtc: '2026-03-18T00:00:02Z',
            payload: JSON.stringify({
              custom: {
                name: 'aevatar.human_input.request',
                payload: {
                  runId: 'execution-1',
                  stepId: 'triage',
                  suspensionType: 'human_approval',
                  prompt: 'Need L2 approval before refund.',
                  timeoutSeconds: 120,
                },
              },
            }),
          },
          {
            receivedAtUtc: '2026-03-18T00:00:03Z',
            payload: JSON.stringify({
              custom: {
                name: 'studio.human.resume',
                payload: {
                  stepId: 'triage',
                  suspensionType: 'human_approval',
                  approved: true,
                  userInput: 'Approved by operator.',
                },
              },
            }),
          },
        ],
      },
    },
    workflowGraph: {
      roles: [],
      steps: [],
      nodes: [],
      edges: [],
    },
    draftWorkflowName: 'workspace-demo',
    activeWorkflowName: 'workspace-demo',
    activeWorkflowDescription: 'A Studio workflow used in tests.',
    activeDirectoryLabel: 'Workspace',
    selectedMemberLabel: 'workspace-demo',
    currentImplementationLabel: 'workspace-demo',
    currentImplementationKind: 'workflow',
    emptyState: null,
    savePending: false,
    canSaveWorkflow: true,
    runPending: false,
    canOpenRunWorkflow: true,
    canRunWorkflow: true,
    executionCanStop: true,
    executionStopPending: false,
    runPrompt: '',
    executionNotice: null,
    onOpenExecution: jest.fn(),
    onSaveDraft: jest.fn(),
    onExportDraft: jest.fn(),
    onSetDraftWorkflowName: jest.fn(),
    onSetWorkflowDescription: jest.fn(),
    onRunPromptChange: jest.fn(),
    onStartExecution: jest.fn(),
    onResumeExecution: jest.fn(async () => {}),
    onStopExecution: jest.fn(),
    ...overrides,
  };
}

describe('StudioExecutionPage', () => {
  it('renders the current execution runtime chrome', () => {
    render(
      React.createElement(
        StudioExecutionPage,
        createBaseProps({
          workflowGraph: {
            roles: [],
            steps: [],
            nodes: [
              {
                id: 'node-1',
                type: 'role',
                position: { x: 0, y: 0 },
                data: {
                  stepId: 'triage',
                  label: 'Triage',
                  nodeType: 'role',
                },
              },
            ],
            edges: [],
          },
        }) as any,
      ),
    );

    expect(screen.getByText('Run Compare')).toBeInTheDocument();
    expect(screen.getByText('Health & Trust')).toBeInTheDocument();
    expect(screen.getByText('Member Snapshot')).toBeInTheDocument();
    expect(screen.getByText('Human Escalation Playback')).toBeInTheDocument();
    expect(screen.getByText('运行中')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '重新运行' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /停\s*止/ })).toBeInTheDocument();
    expect(screen.getByText('执行日志')).toBeInTheDocument();
    expect(screen.getByLabelText('选择测试运行')).toBeInTheDocument();
    expect(screen.getByText('Graph canvas')).toBeInTheDocument();
  });

  it('shows the selected execution actor id and lets users copy it', async () => {
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });

    render(
      React.createElement(StudioExecutionPage, createBaseProps() as any),
    );

    expect(screen.getAllByText('Actor ID').length).toBeGreaterThan(0);
    expect(screen.getAllByText('actor-1').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Copy Actor ID.' }));

    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith('actor-1');
    });
  });

  it('surfaces approval playback details from the selected execution trace', () => {
    render(
      React.createElement(StudioExecutionPage, createBaseProps() as any),
    );

    expect(screen.getAllByText('triage waiting for approval').length).toBeGreaterThan(0);
    expect(screen.getAllByText('triage approved').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Need L2 approval before refund.').length).toBeGreaterThan(0);
  });

  it('allows sending a signal when the selected run is waiting on wait_signal', async () => {
    const onResumeExecution = jest.fn(async () => {});

    render(
      React.createElement(
        StudioExecutionPage,
        createBaseProps({
          onResumeExecution,
          selectedExecution: {
            isLoading: false,
            isError: false,
            error: null,
            data: {
              ...createBaseProps().selectedExecution.data,
              frames: [
                {
                  receivedAtUtc: '2026-03-18T00:00:01Z',
                  payload: JSON.stringify({
                    custom: {
                      name: 'aevatar.step.request',
                      payload: {
                        stepId: 'wait_external',
                        stepType: 'wait_signal',
                        targetRole: 'support',
                        input: 'Wait for external confirmation.',
                      },
                    },
                  }),
                },
                {
                  receivedAtUtc: '2026-03-18T00:00:02Z',
                  payload: JSON.stringify({
                    custom: {
                      name: 'aevatar.wait_signal.request',
                      payload: {
                        runId: 'execution-1',
                        stepId: 'wait_external',
                        suspensionType: 'wait_signal',
                        prompt: 'Need external confirmation.',
                        signalName: 'customer_confirmed',
                      },
                    },
                  }),
                },
              ],
            },
          },
        }) as any,
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: '发送信号' }));

    await waitFor(() => {
      expect(onResumeExecution).toHaveBeenCalledWith(
        expect.objectContaining({
          kind: 'wait_signal',
          runId: 'execution-1',
          stepId: 'wait_external',
          signalName: 'customer_confirmed',
        }),
        'signal',
        '',
      );
    });
  });

  it('shows an honest graph downgrade for script members', () => {
    render(
      React.createElement(
        StudioExecutionPage,
        createBaseProps({
          currentImplementationKind: 'script',
          workflowGraph: {
            roles: [],
            steps: [],
            nodes: [],
            edges: [],
          },
        }) as any,
      ),
    );

    expect(
      screen.getByText('Script members do not expose a workflow graph.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Graph canvas')).toBeNull();
    expect(screen.getByText('执行日志')).toBeInTheDocument();
  });

  it('shows a clear member-first empty state when Observe has no selected member', () => {
    render(
      React.createElement(
        StudioExecutionPage,
        createBaseProps({
          currentImplementationLabel: '',
          emptyState: {
            title: 'Select a member to observe.',
            description:
              'Choose a member from Team members first so Observe stays pinned to one member context.',
          },
          executions: {
            isLoading: false,
            isError: false,
            error: null,
            data: [],
          },
          selectedExecution: {
            isLoading: false,
            isError: false,
            error: null,
            data: undefined,
          },
          selectedMemberLabel: '',
        }) as any,
      ),
    );

    expect(screen.getByText('Select a member to observe.')).toBeInTheDocument();
  });
});
