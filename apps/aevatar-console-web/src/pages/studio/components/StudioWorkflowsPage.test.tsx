import { render, screen } from '@testing-library/react';
import React from 'react';
import {
  dedupeStudioWorkflowSummaries,
  StudioWorkflowsPage,
} from './StudioWorkbenchSections';

function createWorkflowSummary(overrides = {}) {
  return {
    workflowId: 'workflow-1',
    name: 'workflow-demo',
    description: '',
    fileName: 'workflow-demo.yaml',
    filePath: '/tmp/workflows/workflow-demo.yaml',
    directoryId: 'scope:scope-1',
    directoryLabel: 'scope-1',
    stepCount: 0,
    hasLayout: false,
    updatedAtUtc: '2026-04-09T09:00:00Z',
    ...overrides,
  };
}

function createProps(overrides = {}) {
  return {
    workflows: {
      isLoading: false,
      isError: false,
      error: null,
      data: [],
    },
    workspaceSettings: {
      isLoading: false,
      isError: false,
      error: null,
      data: {
        runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
        directories: [],
      },
    },
    workflowStorageMode: 'workspace',
    selectedWorkflowId: '',
    selectedDirectoryId: '',
    templateWorkflow: '',
    draftMode: '',
    activeWorkflowName: '',
    activeWorkflowDescription: '',
    activeWorkflowSourceKey: '',
    workflowSearch: '',
    showDirectoryForm: false,
    directoryPath: '',
    directoryLabel: '',
    workflowImportPending: false,
    workflowImportInputRef: React.createRef<HTMLInputElement>(),
    onOpenWorkflow: jest.fn(),
    onStartBlankDraft: jest.fn(),
    onOpenCurrentDraft: jest.fn(),
    onSelectDirectoryId: jest.fn(),
    onSetWorkflowSearch: jest.fn(),
    onToggleDirectoryForm: jest.fn(),
    onSetDirectoryPath: jest.fn(),
    onSetDirectoryLabel: jest.fn(),
    onAddDirectory: jest.fn(),
    onRemoveDirectory: jest.fn(),
    onWorkflowImportClick: jest.fn(),
    onWorkflowImportChange: jest.fn(),
    ...overrides,
  } as any;
}

describe('StudioWorkflowsPage', () => {
  it('deduplicates same-name workflows and keeps the selected item visible', () => {
    const deduped = dedupeStudioWorkflowSummaries(
      [
        createWorkflowSummary({
          workflowId: 'legacy-id',
          name: 'hello-chat',
          updatedAtUtc: '2026-04-09T09:00:00Z',
        }),
        createWorkflowSummary({
          workflowId: 'hello-chat',
          name: 'hello-chat',
          updatedAtUtc: '2026-04-09T10:00:00Z',
        }),
      ],
      'legacy-id',
    );

    expect(deduped).toHaveLength(1);
    expect(deduped[0]?.workflowId).toBe('legacy-id');
  });

  it('keeps the browser panel stretched and centers the empty state', () => {
    render(React.createElement(StudioWorkflowsPage, createProps()));

    const browserSection = screen
      .getByPlaceholderText('Search workflows')
      .closest('section');
    expect(browserSection).toHaveStyle('height: 100%');
    expect(browserSection).toHaveStyle('min-height: 0');

    const emptyContainer = screen
      .getByText('Create a workflow with the New workflow button above.')
      .closest('.ant-empty')?.parentElement?.parentElement;
    expect(emptyContainer).toHaveStyle('display: flex');
    expect(emptyContainer).toHaveStyle('justify-content: center');
    expect(emptyContainer).toHaveStyle('width: 100%');
  });

  it('renders only the newest workflow card when duplicate names are returned', () => {
    render(
      React.createElement(
        StudioWorkflowsPage,
        createProps({
          workflows: {
            isLoading: false,
            isError: false,
            error: null,
            data: [
              createWorkflowSummary({
                workflowId: 'legacy-id',
                name: 'hello-chat',
                updatedAtUtc: '2026-04-09T09:00:00Z',
              }),
              createWorkflowSummary({
                workflowId: 'hello-chat',
                name: 'hello-chat',
                updatedAtUtc: '2026-04-09T10:00:00Z',
              }),
            ],
          },
        }),
      ),
    );

    expect(screen.getAllByText('hello-chat')).toHaveLength(1);
  });
});
