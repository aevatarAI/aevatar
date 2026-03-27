import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { StudioEditorPage } from './StudioWorkbenchSections';

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement(
      'div',
      null,
      [
        React.createElement('div', { key: 'canvas' }, 'Graph canvas'),
        React.createElement(
          'button',
          {
            key: 'context',
            type: 'button',
            onClick: () =>
              props.onCanvasContextMenu?.({
                clientX: 24,
                clientY: 24,
                flowX: 160,
                flowY: 240,
              }),
          },
          'Open canvas palette',
        ),
      ].filter(Boolean),
    );
  },
}));

const connectorsCatalog = {
  homeDirectory: '/tmp/.aevatar',
  filePath: '/tmp/.aevatar/connectors.json',
  fileExists: true,
  connectors: [
    {
      name: 'search',
      type: 'http',
      enabled: true,
      timeoutMs: 10000,
      retry: 0,
    },
  ],
};

const workspaceSettings = {
  runtimeBaseUrl: 'http://127.0.0.1:5100',
  directories: [
    {
      directoryId: 'dir-1',
      label: 'Workspace',
      path: '/tmp/workflows',
      isBuiltIn: false,
    },
  ],
};

const activeWorkflowFile = {
  workflowId: 'workflow-1',
  name: 'workspace-demo',
  fileName: 'workspace-demo.yaml',
  filePath: '/tmp/workflows/workspace-demo.yaml',
  directoryId: 'dir-1',
  directoryLabel: 'Workspace',
  yaml: 'name: workspace-demo\nsteps:\n  - id: review_step\n',
  findings: [],
  updatedAtUtc: '2026-03-25T00:00:00Z',
};

const workflowRoles = [
  {
    id: 'assistant',
    name: 'Assistant',
    provider: 'openai',
    model: 'gpt-5.4',
    systemPrompt: 'Help with review tasks.',
    connectors: ['search'],
  },
];

const workflowSteps = [
  {
    id: 'review_step',
    type: 'llm_call',
    targetRole: 'assistant',
    parameters: { prompt: 'Review' },
    next: null,
    branches: {},
  },
];

function createBaseProps(overrides = {}) {
  return {
    selectedWorkflow: {
      isLoading: false,
      isError: false,
      error: null,
      data: {},
    },
    templateWorkflow: {
      isLoading: false,
      isError: false,
      error: null,
      data: {},
    },
    connectors: {
      isLoading: false,
      isError: false,
      error: null,
      data: connectorsCatalog,
    },
    draftYaml: 'name: workspace-demo\nsteps:\n  - id: review_step\n',
    draftWorkflowName: 'workspace-demo',
    draftDirectoryId: 'dir-1',
    draftFileName: 'workspace-demo.yaml',
    draftMode: '',
    selectedWorkflowId: 'workflow-1',
    templateWorkflowName: '',
    activeWorkflowDescription: 'A Studio workflow used in tests.',
    activeWorkflowFile,
    isDraftDirty: false,
    workflowGraph: {
      roles: workflowRoles,
      steps: workflowSteps,
      nodes: [],
      edges: [],
    },
    parseYaml: {
      isLoading: false,
      isError: false,
      error: null,
      data: {
        document: { name: 'workspace-demo' },
        findings: [],
      },
    },
    selectedGraphNodeId: '',
    selectedGraphEdge: null,
    workflowRoleIds: ['assistant'],
    workflowStepIds: ['review_step'],
    inspectorTab: 'yaml',
    inspectorContent: React.createElement('div', null, 'Inspector content'),
    workspaceSettings: {
      isLoading: false,
      isError: false,
      error: null,
      data: workspaceSettings,
    },
    savePending: false,
    canSaveWorkflow: true,
    saveNotice: null,
    workflowImportPending: false,
    workflowImportNotice: null,
    workflowImportInputRef: React.createRef<HTMLInputElement>(),
    askAiPrompt: 'Draft a support workflow.',
    askAiPending: false,
    askAiNotice: null,
    askAiReasoning: '',
    askAiAnswer: '',
    runPrompt: '',
    recentPromptHistory: [],
    promptHistoryCount: 0,
    runPending: false,
    canOpenRunWorkflow: true,
    canRunWorkflow: true,
    runNotice: null,
    onSwitchStudioView: jest.fn(),
    onExportDraft: jest.fn(),
    onSelectGraphNode: jest.fn(),
    onSelectGraphEdge: jest.fn(),
    onClearGraphSelection: jest.fn(),
    onAddGraphNode: jest.fn(),
    onConnectGraphNodes: jest.fn(),
    onUpdateGraphLayout: jest.fn(),
    onDeleteSelectedGraphEdge: jest.fn(),
    onSetWorkflowDescription: jest.fn(),
    onSetDraftYaml: jest.fn(),
    onSetDraftWorkflowName: jest.fn(),
    onSetDraftDirectoryId: jest.fn(),
    onSetDraftFileName: jest.fn(),
    onSetInspectorTab: jest.fn(),
    onValidateDraft: jest.fn(),
    onWorkflowImportClick: jest.fn(),
    onWorkflowImportChange: jest.fn(),
    onResetDraft: jest.fn(),
    onSaveDraft: jest.fn(),
    onInspectPublishedWorkflow: jest.fn(),
    onRunInConsole: jest.fn(),
    onAskAiPromptChange: jest.fn(),
    onAskAiGenerate: jest.fn(),
    onRunPromptChange: jest.fn(),
    onClearPromptHistory: jest.fn(),
    onReusePrompt: jest.fn(),
    onOpenWorkflowFromHistory: jest.fn(),
    onStartExecution: jest.fn(),
    onOpenExecutions: jest.fn(),
    ...overrides,
  };
}

describe('StudioEditorPage', () => {
  it('opens the node library drawer from the toolbar and inserts a node', async () => {
    const onAddGraphNode = jest.fn();

    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          onAddGraphNode,
        }) as any,
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: /Add node/i }));

    expect(await screen.findByText('Node library')).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: 'Insert' })[0]);

    expect(onAddGraphNode).toHaveBeenCalledWith('llm_call', undefined, {
      x: 420,
      y: 220,
    });
  });

  it('opens the Ask AI drawer from the toolbar and keeps generation wired', async () => {
    const onAskAiGenerate = jest.fn();

    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          onAskAiGenerate,
          askAiAnswer: 'name: ai_workflow\nsteps: []\n',
        }) as any,
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: /Ask AI/i }));

    expect(await screen.findByText('Workflow prompt')).toBeInTheDocument();
    expect(screen.getByText('Validated YAML')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

    expect(onAskAiGenerate).toHaveBeenCalledTimes(1);
  });
});
