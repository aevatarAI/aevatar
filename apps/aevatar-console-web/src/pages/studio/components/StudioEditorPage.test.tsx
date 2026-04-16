import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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
  runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
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
    workflows: {
      isLoading: false,
      isError: false,
      error: null,
      data: [
        {
          workflowId: 'workflow-1',
          name: 'workspace-demo',
          description: 'Workspace workflow',
          fileName: 'workspace-demo.yaml',
          filePath: '/tmp/workflows/workspace-demo.yaml',
          directoryId: 'dir-1',
          directoryLabel: 'Workspace',
          stepCount: 1,
          hasLayout: true,
          updatedAtUtc: '2026-03-25T00:00:00Z',
        },
      ],
    },
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
    canAskAiGenerate: true,
    askAiUnavailableMessage: '',
    runPrompt: '',
    recentPromptHistory: [],
    promptHistoryCount: 0,
    runPending: false,
    canOpenRunWorkflow: true,
    canRunWorkflow: true,
    runNotice: null,
    resolvedScopeId: undefined,
    publishPending: false,
    canPublishWorkflow: true,
    publishNotice: null,
    scopeBinding: null,
    scopeBindingLoading: false,
    scopeBindingError: null,
    gAgentTypes: [],
    gAgentTypesLoading: false,
    gAgentTypesError: null,
    bindingActivationRevisionId: '',
    bindingRetirementRevisionId: '',
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
    onPublishWorkflow: jest.fn(),
    onOpenProjectOverview: jest.fn(),
    onOpenProjectInvoke: jest.fn(),
    onOpenWorkflow: jest.fn(),
    onStartBlankDraft: jest.fn(),
    onBindGAgent: jest.fn(async () => undefined),
    onActivateBindingRevision: jest.fn(),
    onRetireBindingRevision: jest.fn(),
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

    fireEvent.click(screen.getByRole('button', { name: /添加步骤/i }));

    expect(await screen.findByText('步骤库')).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: /插\s*入/ })[0]);

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

    fireEvent.click(screen.getByRole('button', { name: /AI 辅助/i }));

    expect(await screen.findByText('行为描述')).toBeInTheDocument();
    expect(screen.getByText('校验后的 YAML')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /生\s*成/ }));

    expect(onAskAiGenerate).toHaveBeenCalledTimes(1);
  });

  it('disables Ask AI when workflow generation is unavailable', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          canAskAiGenerate: false,
          askAiUnavailableMessage: '当前环境暂时无法连接 Studio 服务，请稍后再试。',
        }) as any,
      ),
    );

    expect(screen.getByRole('button', { name: /AI 辅助/i })).toBeDisabled();
  });

  it('keeps the inspector column in a constrained scroll shell', () => {
    render(React.createElement(StudioEditorPage, createBaseProps() as any));

    expect(screen.getByTestId('studio-editor-shell')).toHaveStyle({
      height: 'calc(100vh - 176px)',
      overflow: 'hidden',
    });
    expect(screen.getByTestId('studio-inspector-scroll')).toHaveStyle({
      overflowY: 'auto',
      minHeight: '0',
    });
  });

  it('hides legacy recommendation notices for dirty drafts', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
        }) as any,
      ),
    );

    expect(await screen.findByText('行为定义')).toBeInTheDocument();
    expect(screen.queryByText('下一步：保存定义')).not.toBeInTheDocument();
  });

  it('keeps scope binding details collapsed until requested', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
        }) as any,
      ),
    );

    expect(await screen.findByText('未发布默认入口')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /收起详情/i })).not.toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: /查看详情/i })[0]);

    expect(await screen.findByRole('button', { name: /收起详情/i })).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: /收起详情/i })[0]);

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: /收起详情/i })).not.toBeInTheDocument();
    });
  });

  it('keeps the published team entry panel visible without recommendation cards', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          scopeBinding: {
            available: true,
            scopeId: 'scope-a',
            serviceId: 'default',
            displayName: 'Workspace Demo',
            serviceKey: 'scope-a:default',
            defaultServingRevisionId: 'rev-2',
            activeServingRevisionId: 'rev-2',
            deploymentId: 'deploy-2',
            deploymentStatus: 'Active',
            primaryActorId: 'actor://scope-a/default',
            updatedAt: '2026-03-26T08:00:00Z',
            revisions: [],
          },
        }) as any,
      ),
    );

    expect(await screen.findByText('Workspace Demo')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /查看详情/i })).toBeInTheDocument();
    expect(screen.queryByText('下一步：打开测试台')).not.toBeInTheDocument();
  });

  it('adds another GAgent endpoint before binding', async () => {
    const onBindGAgent = jest.fn(async () => undefined);

    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          gAgentTypes: [
            {
              typeName: 'OrdersGAgent',
              fullName: 'Tests.OrdersGAgent',
              assemblyName: 'Tests',
            },
          ],
          onBindGAgent,
        }) as any,
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: '绑定团队入口' }));

    expect(
      await screen.findByRole('dialog', { name: '绑定团队入口' }),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /添加入口/i }));
    fireEvent.change(screen.getByLabelText('入口 ID 2'), {
      target: { value: 'chat' },
    });

    fireEvent.click(screen.getByRole('button', { name: '仅绑定' }));

    await waitFor(() => {
      expect(onBindGAgent).toHaveBeenCalledWith(
        expect.objectContaining({
          endpoints: expect.arrayContaining([
            expect.objectContaining({
              endpointId: 'run',
            }),
            expect.objectContaining({
              endpointId: 'chat',
            }),
          ]),
        }),
        {
          openRuns: false,
        },
      );
    });
  });
});
