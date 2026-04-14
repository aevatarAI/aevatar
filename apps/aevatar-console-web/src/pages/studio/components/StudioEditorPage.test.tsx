import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { StudioEditorPage } from './StudioWorkbenchSections';

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement(
      'div',
      { 'data-testid': 'graph-canvas' },
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
        props.overlayContent
          ? React.createElement(
              'div',
              { key: 'overlay', 'data-testid': 'graph-canvas-overlay' },
              props.overlayContent,
            )
          : null,
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
    resolvedScopeId: undefined,
    publishPending: false,
    canPublishWorkflow: true,
    publishNotice: null,
    scopeBinding: null,
    scopeBindingLoading: false,
    scopeBindingError: null,
    projectEntryReadyForCurrentWorkflow: true,
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

    fireEvent.click(screen.getByRole('button', { name: /Add node/i }));

    const graphCanvas = screen.getByTestId('graph-canvas');

    expect(await within(graphCanvas).findByText('Node library')).toBeInTheDocument();
    expect(graphCanvas.querySelector('.ant-drawer-left')).not.toBeNull();
    expect(graphCanvas.querySelector('.ant-drawer-right')).toBeNull();

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

    const graphCanvas = screen.getByTestId('graph-canvas');

    expect(await within(graphCanvas).findByText('Workflow prompt')).toBeInTheDocument();
    expect(within(graphCanvas).getByText('Validated YAML')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

    expect(onAskAiGenerate).toHaveBeenCalledTimes(1);
  });

  it('opens the workflow inspector drawer inside the graph canvas from the left', async () => {
    render(
      React.createElement(StudioEditorPage, createBaseProps() as any),
    );

    fireEvent.click(screen.getByRole('button', { name: /Open YAML inspector/i }));

    const graphCanvas = screen.getByTestId('graph-canvas');

    expect(await within(graphCanvas).findByText('Inspector content')).toBeInTheDocument();
    expect(graphCanvas.querySelector('.ant-drawer-left')).not.toBeNull();
    expect(graphCanvas.querySelector('.ant-drawer-right')).toBeNull();
  });

  it('guides dirty drafts toward save before later project steps', async () => {
    const onSaveDraft = jest.fn();

    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
          onSaveDraft,
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Save asset')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Expand guidance' }));

    fireEvent.click(screen.getByRole('button', { name: 'Save asset' }));

    expect(onSaveDraft).toHaveBeenCalledTimes(1);
  });

  it('keeps save success feedback out of the inline editor chrome', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          saveNotice: {
            type: 'success',
            message: 'Saved workspace-demo to Workspace.',
          },
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Run draft')).toBeInTheDocument();
    expect(screen.queryByText('Workflow saved')).not.toBeInTheDocument();
    expect(screen.queryByText('Saved workspace-demo to Workspace.')).not.toBeInTheDocument();
  });

  it('opens the workflow guide drawer from the compact guidance bar', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
        }) as any,
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Expand guidance' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Why?' }));

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(await screen.findByText('Draft path')).toBeInTheDocument();
    expect(screen.getByText('Published project path')).toBeInTheDocument();
  });

  it('lets users dismiss and restore the floating guidance', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
        }) as any,
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Close guidance' }));

    expect(screen.queryByText('Next: Save asset')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show guidance' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Show guidance' }));

    expect(await screen.findByRole('button', { name: 'Collapse guidance' })).toBeInTheDocument();
    expect(screen.getByText('Next: Save asset')).toBeInTheDocument();
  });

  it('animates the floating guidance when the next step changes', async () => {
    const view = render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Save asset')).toBeInTheDocument();
    expect(screen.getByTestId('studio-guidance-floating-card')).toHaveAttribute(
      'data-guidance-updated',
      'false',
    );

    view.rerender(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: false,
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Bind project')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByTestId('studio-guidance-floating-card')).toHaveAttribute(
        'data-guidance-updated',
        'true',
      );
    });
  });

  it('lets users drag the floating guidance to a new position', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          isDraftDirty: true,
        }) as any,
      ),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Expand guidance' }));

    const guidanceCard = await screen.findByTestId('studio-guidance-floating-card');
    fireEvent.mouseDown(screen.getByRole('button', { name: 'Drag guidance' }), {
      clientX: 520,
      clientY: 120,
    });
    fireEvent.mouseMove(window, {
      clientX: 440,
      clientY: 176,
    });
    fireEvent.mouseUp(window);

    await waitFor(() => {
      expect(guidanceCard.style.transform).toContain('translate3d(-80px, 56px, 0)');
    });
  });

  it('opens scope binding details in a drawer when requested', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
        }) as any,
      ),
    );

    expect(await screen.findByText('No active binding')).toBeInTheDocument();
    expect(screen.queryByText('No published binding')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /show details/i }));

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(await screen.findByText('No published binding')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Close panel' }));

    await waitFor(() => {
      expect(screen.queryByText('No published binding')).not.toBeInTheDocument();
    });
  });

  it('guides published teams toward project invoke', async () => {
    const onOpenProjectInvoke = jest.fn();

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
          onOpenProjectInvoke,
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Open Project Invoke')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Expand guidance' }));

    fireEvent.click(screen.getByRole('button', { name: 'Open Project Invoke' }));

    expect(onOpenProjectInvoke).toHaveBeenCalledTimes(1);
  });

  it('guides published chat-ready projects toward Chat', async () => {
    const onOpenProjectInvoke = jest.fn();

    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          projectEntrySurface: 'chat',
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
          onOpenProjectInvoke,
        }) as any,
      ),
    );

    expect(await screen.findByText('Next: Open Chat')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Expand guidance' }));

    fireEvent.click(screen.getByRole('button', { name: 'Open Chat' }));

    expect(onOpenProjectInvoke).toHaveBeenCalledTimes(1);
  });

  it('keeps Chat unavailable until the current workflow is rebound', async () => {
    render(
      React.createElement(
        StudioEditorPage,
        createBaseProps({
          resolvedScopeId: 'scope-a',
          projectEntrySurface: 'chat',
          projectEntryReadyForCurrentWorkflow: false,
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

    expect(await screen.findByText('Next: Bind project')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Expand guidance' }));
    expect(screen.queryByRole('button', { name: 'Open Chat' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Bind project' }).length).toBeGreaterThan(0);
  });

  it('keeps published binding revision details inside the details drawer', async () => {
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
            defaultServingRevisionId: 'rev-2026040208260375-9752a1bbf42643469087df61787e9f45',
            activeServingRevisionId: 'rev-2026040208260375-9752a1bbf42643469087df61787e9f45',
            deploymentId: 'deploy-2',
            deploymentStatus: 'Active',
            primaryActorId: 'actor://scope-a/default',
            updatedAt: '2026-03-26T08:00:00Z',
            revisions: [
              {
                revisionId: 'rev-2026040208260375-9752a1bbf42643469087df61787e9f45',
                implementationKind: 'workflow',
                status: 'Active',
                artifactHash: 'hash-1',
                failureReason: '',
                isDefaultServing: true,
                isActiveServing: true,
                isServingTarget: true,
                allocationWeight: 100,
                servingState: 'serving',
                deploymentId: 'deploy-2',
                primaryActorId: 'actor://scope-a/default',
                createdAt: '2026-03-26T08:00:00Z',
                preparedAt: '2026-03-26T08:00:00Z',
                publishedAt: '2026-03-26T08:00:00Z',
                retiredAt: null,
                workflowName: 'draft',
                workflowDefinitionActorId: 'workflow://draft',
                inlineWorkflowCount: 0,
                scriptId: '',
                scriptRevision: '',
                scriptDefinitionActorId: '',
                scriptSourceHash: '',
                staticActorTypeName: '',
                staticPreferredActorId: '',
              },
            ],
          },
        }) as any,
      ),
    );

    expect(await screen.findByText('Workspace Demo')).toBeInTheDocument();
    expect(screen.queryByText('Target: draft')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open GAgents' })).not.toBeInTheDocument();
    expect(
      screen.queryByText('rev-2026040208260375-9752a1bbf42643469087df61787e9f45'),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /show details/i }));

    await waitFor(() => {
      expect(
        screen.getAllByText(
          'rev-2026040208260375-9752a1bbf42643469087df61787e9f45',
        ).length,
      ).toBeGreaterThan(0);
    });
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

    fireEvent.click(screen.getByRole('button', { name: /GAgent service/i }));

    expect(await screen.findByText('Bind GAgent service')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Add endpoint/i }));
    fireEvent.change(screen.getByLabelText('GAgent endpoint id 2'), {
      target: { value: 'chat' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Bind' }));

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
