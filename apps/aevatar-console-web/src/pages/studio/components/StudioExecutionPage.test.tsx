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
        frames: [],
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
    savePending: false,
    canSaveWorkflow: true,
    runPending: false,
    canOpenRunWorkflow: true,
    canRunWorkflow: true,
    executionCanStop: true,
    executionStopPending: false,
    runPrompt: '',
    executionNotice: null,
    onSwitchStudioView: jest.fn(),
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
  it('reuses the shared workflow header chrome on the runs view', () => {
    render(
      React.createElement(StudioExecutionPage, createBaseProps() as any),
    );

    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Runs' })).toBeInTheDocument();
    expect(screen.getByDisplayValue('workspace-demo')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Export' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeInTheDocument();
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
});
