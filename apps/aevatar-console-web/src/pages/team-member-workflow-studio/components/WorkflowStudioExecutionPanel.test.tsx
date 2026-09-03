import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import WorkflowStudioExecutionPanel from './WorkflowStudioExecutionPanel';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};
const scrollIntoView = jest.fn();

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

const executionDetail = {
  actorId: 'actor-alpha',
  completedAtUtc: '2026-08-06T00:00:01.000Z',
  error: null,
  executionId: 'run-alpha',
  frames: [
    {
      payload: JSON.stringify({
        runStarted: {
          runId: 'run-alpha',
        },
      }),
      receivedAtUtc: '2026-08-06T00:00:00.000Z',
    },
  ],
  prompt: 'Summarize the workflow.',
  startedAtUtc: '2026-08-06T00:00:00.000Z',
  status: 'succeeded',
  workflowName: 'Workflow Alpha',
};

type WorkflowNodes = ReadonlyArray<{
  readonly stepId: string;
  readonly stepType: string;
  readonly subtitle: string;
  readonly targetRole: string;
  readonly title: string;
}>;

const submittedWorkflowNodes: WorkflowNodes = [
  {
    stepId: 'step-alpha',
    stepType: 'llm_call',
    subtitle: 'LLM call',
    targetRole: 'assistant',
    title: 'step-alpha',
  },
  {
    stepId: 'step-beta',
    stepType: 'transform',
    subtitle: 'Transform',
    targetRole: '',
    title: 'step-beta',
  },
  {
    stepId: 'step-gamma',
    stepType: 'emit',
    subtitle: 'Emit',
    targetRole: '',
    title: 'step-gamma',
  },
];

function createExecutionFrame(
  payload: Record<string, unknown>,
  receivedAtUtc: string,
) {
  return {
    payload: JSON.stringify(payload),
    receivedAtUtc,
  };
}

function createRunStartedFrame() {
  return createExecutionFrame(
    { runStarted: { runId: 'run-live-alpha' } },
    '2026-09-03T06:45:00.000Z',
  );
}

function createStepStartedFrame(
  stepId: string,
  input: string,
  receivedAtUtc: string,
) {
  return createExecutionFrame(
    {
      custom: {
        name: 'aevatar.step.request',
        payload: {
          input,
          stepId,
          stepType: 'llm_call',
          targetRole: 'assistant',
        },
      },
    },
    receivedAtUtc,
  );
}

function createStepCompletedFrame(
  stepId: string,
  output: string,
  receivedAtUtc: string,
) {
  return createExecutionFrame(
    {
      custom: {
        name: 'aevatar.step.completed',
        payload: {
          output,
          stepId,
          success: true,
        },
      },
    },
    receivedAtUtc,
  );
}

function createDiagnosticFrame(receivedAtUtc: string) {
  return createExecutionFrame(
    {
      custom: {
        name: 'aevatar.raw.observed',
        payload: { source: 'runtime-observer' },
      },
    },
    receivedAtUtc,
  );
}

function createRunningExecution(
  frames: ReadonlyArray<{ payload: string; receivedAtUtc: string }>,
) {
  return {
    actorId: 'actor-live-alpha',
    completedAtUtc: null,
    error: null,
    executionId: 'run-live-alpha',
    frames: [...frames],
    prompt: 'Process the request.',
    startedAtUtc: '2026-09-03T06:45:00.000Z',
    status: 'running',
    workflowName: 'Live workflow',
  };
}

function ControlledExecutionPanel({
  detail,
  workflowNodes,
}: Readonly<{
  detail: ReturnType<typeof createRunningExecution>;
  workflowNodes?: WorkflowNodes;
}>) {
  const [activeLogIndex, setActiveLogIndex] = React.useReducer(
    (_current: number | null, next: number | null) => next,
    null,
  );

  return (
    <WorkflowStudioExecutionPanel
      activeLogIndex={activeLogIndex}
      detail={detail}
      onSelectLog={setActiveLogIndex}
      workflowNodes={workflowNodes}
    />
  );
}

describe('WorkflowStudioExecutionPanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: undefined,
    });
  });

  it('reports unavailable clipboard access instead of a copy success', async () => {
    render(
      React.createElement(WorkflowStudioExecutionPanel, {
        detail: executionDetail,
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Copy all logs' }));

    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not copy logs.',
      );
    });
    expect(mockConsoleToast.success).not.toHaveBeenCalled();
  });

  it('keeps raw events out of Nodes and promotes the first running node', async () => {
    const runStartedFrame = createRunStartedFrame();
    const { rerender } = render(
      <ControlledExecutionPanel
        detail={createRunningExecution([runStartedFrame])}
      />,
    );

    expect(screen.getByRole('radio', { name: 'Nodes' })).toBeChecked();
    expect(
      screen.queryByTestId('workflow-execution-log-row-run-0'),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Log details')).not.toBeInTheDocument();

    rerender(
      <ControlledExecutionPanel
        detail={createRunningExecution([
          runStartedFrame,
          createStepStartedFrame(
            'step-alpha',
            'First node input',
            '2026-09-03T06:45:01.000Z',
          ),
        ])}
      />,
    );

    const runningRow = await screen.findByTestId(
      'workflow-execution-log-row-node-step-alpha',
    );
    await waitFor(() =>
      expect(runningRow).toHaveAttribute('aria-pressed', 'true'),
    );
    expect(runningRow).toHaveTextContent('Running');
    expect(
      runningRow.querySelectorAll(
        '[data-testid="workflow-execution-running-indicator"] .aevatar-loading-dot',
      ),
    ).toHaveLength(3);
    expect(screen.getByLabelText('Log details')).toHaveTextContent(
      'step-alpha',
    );
    expect(screen.getByLabelText('Log details')).toHaveTextContent(
      'First node input',
    );
  });

  it('shows submitted nodes before they start and promotes only the running node', async () => {
    const runStartedFrame = createRunStartedFrame();
    const { rerender } = render(
      <ControlledExecutionPanel
        detail={createRunningExecution([runStartedFrame])}
        workflowNodes={submittedWorkflowNodes}
      />,
    );

    const overview = screen.getByRole('listbox', { name: 'Logs overview' });
    const initialRows = within(overview).getAllByRole('button');
    expect(initialRows.map((row) => row.dataset.testid)).toEqual([
      'workflow-execution-log-row-node-step-alpha',
      'workflow-execution-log-row-node-step-beta',
      'workflow-execution-log-row-node-step-gamma',
    ]);
    initialRows.forEach((row) => {
      expect(row).toHaveTextContent('Pending');
      expect(row).toBeDisabled();
    });

    rerender(
      <ControlledExecutionPanel
        detail={createRunningExecution([
          runStartedFrame,
          createStepStartedFrame(
            'step-alpha',
            'First node input',
            '2026-09-03T06:45:01.000Z',
          ),
        ])}
        workflowNodes={submittedWorkflowNodes}
      />,
    );

    const alphaRow = within(overview).getByTestId(
      'workflow-execution-log-row-node-step-alpha',
    );
    await waitFor(() =>
      expect(alphaRow).toHaveAttribute('aria-pressed', 'true'),
    );
    expect(alphaRow).toHaveTextContent('Running');
    expect(alphaRow).toBeEnabled();
    for (const stepId of ['step-beta', 'step-gamma']) {
      const pendingRow = within(overview).getByTestId(
        `workflow-execution-log-row-node-${stepId}`,
      );
      expect(pendingRow).toHaveTextContent('Pending');
      expect(pendingRow).toBeDisabled();
    }
  });

  it('marks unentered nodes as not run after execution finishes', () => {
    render(
      <ControlledExecutionPanel
        detail={{
          ...createRunningExecution([
            createRunStartedFrame(),
            createStepStartedFrame(
              'step-alpha',
              'First node input',
              '2026-09-03T06:45:01.000Z',
            ),
            createStepCompletedFrame(
              'step-alpha',
              'First node output',
              '2026-09-03T06:45:02.000Z',
            ),
          ]),
          completedAtUtc: '2026-09-03T06:45:03.000Z',
          status: 'succeeded',
        }}
        workflowNodes={submittedWorkflowNodes}
      />,
    );

    expect(
      screen.getByTestId('workflow-execution-log-row-node-step-alpha'),
    ).toHaveTextContent('Success');
    for (const stepId of ['step-beta', 'step-gamma']) {
      const notRunRow = screen.getByTestId(
        `workflow-execution-log-row-node-${stepId}`,
      );
      expect(notRunRow).toHaveTextContent('Not run');
      expect(notRunRow).toBeDisabled();
    }
  });

  it('follows each newly running node while preserving manual inspection within a node', async () => {
    const initialFrames = [
      createRunStartedFrame(),
      createStepStartedFrame(
        'step-alpha',
        'Alpha input',
        '2026-09-03T06:45:01.000Z',
      ),
      createStepCompletedFrame(
        'step-alpha',
        'Alpha output',
        '2026-09-03T06:45:02.000Z',
      ),
      createStepStartedFrame(
        'step-beta',
        'Beta input',
        '2026-09-03T06:45:03.000Z',
      ),
    ];
    const { rerender } = render(
      <ControlledExecutionPanel
        detail={createRunningExecution(initialFrames)}
      />,
    );

    const alphaRow = screen.getByTestId(
      'workflow-execution-log-row-node-step-alpha',
    );
    const betaRow = screen.getByTestId(
      'workflow-execution-log-row-node-step-beta',
    );
    await waitFor(() =>
      expect(betaRow).toHaveAttribute('aria-pressed', 'true'),
    );
    expect(alphaRow).toHaveAttribute('aria-pressed', 'false');
    expect(scrollIntoView).toHaveBeenLastCalledWith({ block: 'nearest' });
    expect(scrollIntoView.mock.contexts.at(-1)).toBe(betaRow);
    expect(betaRow).not.toHaveFocus();

    fireEvent.click(alphaRow);
    expect(alphaRow).toHaveAttribute('aria-pressed', 'true');
    expect(betaRow).toHaveAttribute('aria-pressed', 'false');

    rerender(
      <ControlledExecutionPanel
        detail={createRunningExecution([
          ...initialFrames,
          createDiagnosticFrame('2026-09-03T06:45:04.000Z'),
        ])}
      />,
    );
    expect(alphaRow).toHaveAttribute('aria-pressed', 'true');

    rerender(
      <ControlledExecutionPanel
        detail={createRunningExecution([
          ...initialFrames,
          createDiagnosticFrame('2026-09-03T06:45:04.000Z'),
          createStepStartedFrame(
            'step-gamma',
            'Gamma input',
            '2026-09-03T06:45:05.000Z',
          ),
        ])}
      />,
    );

    const gammaRow = screen.getByTestId(
      'workflow-execution-log-row-node-step-gamma',
    );
    await waitFor(() =>
      expect(gammaRow).toHaveAttribute('aria-pressed', 'true'),
    );
    expect(alphaRow).toHaveAttribute('aria-pressed', 'false');
    expect(scrollIntoView.mock.contexts.at(-1)).toBe(gammaRow);
    expect(gammaRow).not.toHaveFocus();
  });
});
