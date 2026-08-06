import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import WorkflowStudioExecutionPanel from './WorkflowStudioExecutionPanel';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

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

describe('WorkflowStudioExecutionPanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
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
});
