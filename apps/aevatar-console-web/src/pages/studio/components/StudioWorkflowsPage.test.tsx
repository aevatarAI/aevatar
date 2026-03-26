import { render, screen } from '@testing-library/react';
import React from 'react';
import { StudioWorkflowsPage } from './StudioWorkbenchSections';

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
        runtimeBaseUrl: 'http://127.0.0.1:5100',
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
    workflowLayout: 'grid',
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
    onSetWorkflowLayout: jest.fn(),
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
});
