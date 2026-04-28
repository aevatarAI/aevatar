import { AGUIEventType } from '@aevatar-react-sdk/types';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import {
  applyStepInspectorDraft,
  connectStepToTarget,
  insertStepByType,
  removeStep,
  suggestBranchLabelForStep,
} from '@/shared/studio/document';
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
} from '@/shared/studio/graph';
import {
  StudioScriptBuildPanel,
  StudioWorkflowBuildPanel,
} from './StudioBuildPanels';

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamDraftRun: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock('@/shared/studio/scriptsApi', () => ({
  scriptsApi: {
    validateDraft: jest.fn(),
    saveScript: jest.fn(),
    observeSaveScript: jest.fn(),
    runDraftScript: jest.fn(),
    proposeEvolution: jest.fn(),
  },
}));

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: ({
    nodes = [],
    selectedNodeId,
    onConnectNodes,
    onNodeLayoutChange,
    onNodeSelect,
  }: any) => (
    <div data-testid="mock-graph-canvas">
      <div data-testid="mock-graph-selected-node">{selectedNodeId || ''}</div>
      <div data-testid="mock-graph-node-count">{String(nodes.length)}</div>
      {nodes.map((node: any) => (
        <button
          key={node.id}
          type="button"
          onClick={() => onNodeSelect?.(node.id)}
        >
          {node.data?.stepId || node.id}
        </button>
      ))}
      <button
        type="button"
        onClick={() => onConnectNodes?.('step:draft_step', 'step:approve_step')}
      >
        Mock connect
      </button>
      <button type="button" onClick={() => onNodeLayoutChange?.(nodes)}>
        Mock layout change
      </button>
    </div>
  ),
}));

jest.mock('@/modules/studio/scripts/ScriptCodeEditor', () => ({
  __esModule: true,
  default: ({
    focusTarget,
    value = '',
    onChange,
  }: {
    readonly focusTarget?: { readonly filePath: string; readonly startLineNumber: number } | null;
    readonly value?: string;
    readonly onChange?: (nextValue: string) => void;
  }) => (
    <div>
      <textarea
        aria-label="Mock script code editor"
        value={value}
        onChange={(event) => onChange?.(event.target.value)}
      />
      <div data-testid="mock-script-focus-target">
        {focusTarget ? `${focusTarget.filePath}:${focusTarget.startLineNumber}` : ''}
      </div>
    </div>
  ),
}));

const mockedRuntimeRunsApi = runtimeRunsApi as unknown as {
  streamDraftRun: jest.Mock;
};
const mockedParseBackendSSEStream = parseBackendSSEStream as jest.Mock;
const mockedScriptsApi = scriptsApi as unknown as {
  validateDraft: jest.Mock;
  saveScript: jest.Mock;
  observeSaveScript: jest.Mock;
  runDraftScript: jest.Mock;
  proposeEvolution: jest.Mock;
};

const scriptDetail = {
  available: true,
  scopeId: 'scope-1',
  script: {
    scopeId: 'scope-1',
    scriptId: 'script-alpha',
    catalogActorId: 'catalog-1',
    definitionActorId: 'definition-1',
    activeRevision: 'rev-1',
    activeSourceHash: 'hash-1',
    updatedAt: '2026-04-27T00:00:00Z',
  },
  source: {
    sourceText: 'using System;',
    definitionActorId: 'definition-1',
    revision: 'rev-1',
    sourceHash: 'hash-1',
  },
};

const initialDocument = {
  name: 'workflow-demo',
  description: 'Workflow demo',
  roles: [
    {
      id: 'assistant',
      name: 'Assistant',
      systemPrompt: 'Help the customer.',
      provider: 'tornado',
      model: 'gpt-test',
      connectors: ['web-search'],
    },
  ],
  steps: [
    {
      id: 'draft_step',
      type: 'llm_call',
      targetRole: 'assistant',
      parameters: {
        prompt_prefix: 'Draft the response',
      },
      next: 'approve_step',
      branches: {},
    },
    {
      id: 'approve_step',
      type: 'human_approval',
      targetRole: '',
      parameters: {
        reviewer: 'operator',
      },
      next: null,
      branches: {},
    },
  ],
};

function cloneValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function buildWorkflowYaml(document: typeof initialDocument): string {
  const roleLines = document.roles.flatMap((role) => {
    const lines = [`  - id: ${role.id}`];
    if (role.name) {
      lines.push(`    name: ${role.name}`);
    }
    if (role.provider) {
      lines.push(`    provider: ${role.provider}`);
    }
    if (role.model) {
      lines.push(`    model: ${role.model}`);
    }
    return lines;
  });

  const stepLines = document.steps.flatMap((step) => {
    const lines = [`  - id: ${step.id}`, `    type: ${step.type}`];
    if (step.targetRole) {
      lines.push(`    targetRole: ${step.targetRole}`);
    }
    if (step.next) {
      lines.push(`    next: ${step.next}`);
    }
    return lines;
  });

  return [
    `name: ${document.name}`,
    'roles:',
    ...roleLines,
    'steps:',
    ...stepLines,
    '',
  ].join('\n');
}

function WorkflowBuildHarness({
  onApplyStepDraftOverride,
  onContinueToBind,
  onSaveDraft,
}: {
  readonly onApplyStepDraftOverride?: (draft: any) => Promise<void>;
  readonly onContinueToBind: jest.Mock;
  readonly onSaveDraft: jest.Mock;
}) {
  const [document, setDocument] = React.useState(() => cloneValue(initialDocument));
  const [draftYaml, setDraftYaml] = React.useState(() =>
    buildWorkflowYaml(initialDocument),
  );
  const [selectedGraphNodeId, setSelectedGraphNodeId] = React.useState(
    'step:draft_step',
  );
  const [layout, setLayout] = React.useState<unknown>(null);
  const [runPrompt, setRunPrompt] = React.useState('Please triage the refund request.');
  const workflowGraph = React.useMemo(
    () => buildStudioGraphElements(document, layout),
    [document, layout],
  );

  const commitDocument = React.useCallback(
    async (
      nextDocument: typeof initialDocument,
      options?: {
        readonly nextSelectedNodeId?: string;
      },
    ) => {
      setDocument(cloneValue(nextDocument));
      setDraftYaml(buildWorkflowYaml(nextDocument));
      if (options?.nextSelectedNodeId !== undefined) {
        setSelectedGraphNodeId(options.nextSelectedNodeId);
      }
    },
    [],
  );

  return (
    <StudioWorkflowBuildPanel
      availableStepTypes={['llm_call', 'human_approval', 'connector_call']}
      buildWorkflowYamls={async () => [draftYaml]}
      canSaveWorkflow
      draftYaml={draftYaml}
      dryRunModelLabel="gpt-5.4-mini"
      dryRunRouteLabel="OpenAI"
      onApplyStepDraft={
        onApplyStepDraftOverride
          ? onApplyStepDraftOverride
          : async (draft) => {
              const currentStepId = selectedGraphNodeId.replace(/^step:/, '');
              const result = applyStepInspectorDraft(document, currentStepId, draft);
              await commitDocument(result.document as typeof initialDocument, {
                nextSelectedNodeId: result.nodeId,
              });
            }
      }
      onAutoLayout={() => setLayout(null)}
      onConnectNodes={(sourceNodeId: string, targetNodeId: string) => {
        const sourceStepId = sourceNodeId.replace(/^step:/, '');
        const targetStepId = targetNodeId.replace(/^step:/, '');
        const sourceStep = document.steps.find((step) => step.id === sourceStepId);
        const result = connectStepToTarget(
          document,
          sourceStepId,
          targetStepId,
          suggestBranchLabelForStep(
            sourceStep?.type || '',
            sourceStep?.branches || {},
          ),
        );
        void commitDocument(result.document as typeof initialDocument, {
          nextSelectedNodeId: result.nodeId,
        });
      }}
      onContinueToBind={() => onContinueToBind(draftYaml)}
      onInsertStep={async (stepType: string) => {
        const result = insertStepByType(document, stepType, {
          afterStepId: selectedGraphNodeId.replace(/^step:/, ''),
          targetRoleId: 'assistant',
        });
        await commitDocument(result.document as typeof initialDocument, {
          nextSelectedNodeId: result.nodeId,
        });
      }}
      onNodeLayoutChange={(nodes) => {
        setLayout(
          buildStudioWorkflowLayout(
            document.name || 'workflow-demo',
            nodes as any,
            layout ?? undefined,
          ),
        );
      }}
      onRemoveSelectedStep={async () => {
        const currentStepId = selectedGraphNodeId.replace(/^step:/, '');
        const result = removeStep(document, currentStepId);
        await commitDocument(result.document as typeof initialDocument, {
          nextSelectedNodeId: result.nodeId,
        });
      }}
      onDeleteWorkflowNodes={async (nodeIds: string[]) => {
        const selectedNodeId = nodeIds[0]?.replace(/^step:/, '') || '';
        if (!selectedNodeId) {
          return;
        }

        const result = removeStep(document, selectedNodeId);
        await commitDocument(result.document as typeof initialDocument, {
          nextSelectedNodeId: result.nodeId,
        });
      }}
      runMetadata={{
        'aevatar.model_override': 'gpt-5.4-mini',
        'nyxid.route_preference': '/api/v1/proxy/s/openai',
      }}
      onRunPromptChange={setRunPrompt}
      onSaveDraft={() => onSaveDraft(draftYaml)}
      onSetDraftYaml={setDraftYaml}
      runtimePrimitives={[
        {
          name: 'llm_call',
          aliases: [],
          description: 'Call the LLM.',
          category: 'ai',
          parameters: [
            {
              name: 'prompt_prefix',
              type: 'string',
              required: false,
              description: 'Prompt prefix',
              default: '',
              enumValues: [],
            },
          ],
          exampleWorkflows: ['workflow-demo'],
        },
        {
          name: 'human_approval',
          aliases: [],
          description: 'Pause for approval.',
          category: 'human',
          parameters: [
            {
              name: 'reviewer',
              type: 'string',
              required: false,
              description: 'Reviewer',
              default: '',
              enumValues: [],
            },
          ],
          exampleWorkflows: ['workflow-demo'],
        },
        {
          name: 'connector_call',
          aliases: [],
          description: 'Call an external connector.',
          category: 'integration',
          parameters: [
            {
              name: 'connector',
              type: 'string',
              required: true,
              description: 'Connector name',
              default: '',
              enumValues: [],
            },
          ],
          exampleWorkflows: ['workflow-demo'],
        },
      ]}
      runPrompt={runPrompt}
      savePending={false}
      saveNotice={null}
      scopeId="scope-1"
      selectedGraphNodeId={selectedGraphNodeId}
      workflowGraph={workflowGraph}
      workflowName={document.name || 'workflow-demo'}
      workflowRoles={[
        {
          id: 'assistant',
          name: 'Assistant',
        },
      ]}
      onSelectGraphNode={setSelectedGraphNodeId}
    />
  );
}

describe('StudioWorkflowBuildPanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockedRuntimeRunsApi.streamDraftRun.mockResolvedValue({} as Response);
    mockedScriptsApi.validateDraft.mockResolvedValue({
      success: true,
      errorCount: 0,
      warningCount: 0,
      diagnostics: [],
    });
    mockedScriptsApi.saveScript.mockResolvedValue({
      acceptedScript: {
        scriptId: 'script-alpha',
        revisionId: 'rev-2',
        definitionActorId: 'definition-1',
        sourceHash: 'hash-2',
        proposalId: 'proposal-1',
        expectedBaseRevision: 'rev-1',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });
    mockedScriptsApi.observeSaveScript.mockResolvedValue({
      status: 'applied',
    });
    mockedScriptsApi.runDraftScript.mockResolvedValue({
      ok: true,
    });
    mockedScriptsApi.proposeEvolution.mockResolvedValue({
      accepted: true,
      proposalId: 'proposal-1',
      scriptId: 'script-alpha',
      baseRevision: 'rev-1',
      candidateRevision: 'rev-2',
      status: 'accepted',
      failureReason: '',
      definitionActorId: 'definition-1',
      catalogActorId: 'catalog-1',
    });
    mockedParseBackendSSEStream.mockImplementation(async function* () {
      yield {
        type: AGUIEventType.RUN_STARTED,
        runId: 'run-1',
        threadId: 'actor-1',
      };
      yield {
        type: AGUIEventType.TEXT_MESSAGE_CONTENT,
        delta: 'workflow draft output',
      };
    });
  });

  it('keeps the Script bind CTA stable and uses the dry-run panel as the run entry', () => {
    const handleContinueToBind = jest.fn();

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [scriptDetail],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="script-alpha"
        onContinueToBind={handleContinueToBind}
        onSelectScriptId={jest.fn()}
      />,
    );

    expect(screen.getByText('Ready to bind')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Dry-run' })).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Run' })).toHaveLength(1);

    fireEvent.change(screen.getByLabelText('Mock script code editor'), {
      target: {
        value: 'using System;\n// changed',
      },
    });

    expect(screen.getByText('Validate current source')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save revision' })).toBeDisabled();
  });

  it('offers Add script from the empty Script build state', () => {
    const handleCreateScriptDraft = jest.fn();

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId=""
        onContinueToBind={jest.fn()}
        onCreateScriptDraft={handleCreateScriptDraft}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add script' }));
    expect(handleCreateScriptDraft).toHaveBeenCalled();
  });

  it('starts a named Script draft without a saved catalog script', async () => {
    const handleDraftSaved = jest.fn();
    mockedScriptsApi.saveScript.mockResolvedValueOnce({
      acceptedScript: {
        scriptId: 'orders-script',
        revisionId: 'draft-1',
        definitionActorId: 'definition-draft',
        sourceHash: 'hash-draft',
        proposalId: 'proposal-draft',
        expectedBaseRevision: '',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="orders-script"
        pendingScriptDraft={{
          scriptId: 'orders-script',
          displayName: 'Orders Script',
        }}
        onContinueToBind={jest.fn()}
        onRefreshScripts={jest.fn()}
        onScriptDraftSaved={handleDraftSaved}
        onSelectScriptId={jest.fn()}
      />,
    );

    expect(screen.getByText('orders-script (draft)')).toBeInTheDocument();
    expect(
      (screen.getByLabelText('Mock script code editor') as HTMLTextAreaElement).value,
    ).toContain('DraftBehavior');
    expect(screen.getByText('Validate current source')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save revision' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));

    await waitFor(() => {
      expect(mockedScriptsApi.validateDraft).toHaveBeenCalledWith(
        expect.objectContaining({
          scriptId: 'orders-script',
        }),
      );
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save revision' }));

    await waitFor(() => {
      expect(mockedScriptsApi.saveScript).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          scriptId: 'orders-script',
          expectedBaseRevision: undefined,
        }),
      );
    });
    await waitFor(() => {
      expect(handleDraftSaved).toHaveBeenCalledWith('orders-script');
    });
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeEnabled();
    });
    expect(screen.getByText('Ready to bind')).toBeInTheDocument();
  });

  it('renders validation diagnostics and focuses the selected problem', async () => {
    mockedScriptsApi.validateDraft.mockResolvedValueOnce({
      success: false,
      scriptId: 'script-alpha',
      scriptRevision: 'rev-1',
      primarySourcePath: 'Behavior.cs',
      errorCount: 1,
      warningCount: 0,
      diagnostics: [
        {
          severity: 'error',
          code: 'CS1002',
          message: '; expected',
          filePath: 'Behavior.cs',
          startLine: 12,
          startColumn: 8,
          endLine: 12,
          endColumn: 9,
          origin: 'compiler',
        },
      ],
    });

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [scriptDetail],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="script-alpha"
        onContinueToBind={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Mock script code editor'), {
      target: {
        value: 'using System;\n// changed',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));

    expect(await screen.findByText('; expected')).toBeInTheDocument();
    expect(screen.getByText('Behavior.cs:12:8')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /CS1002/ }));

    expect(screen.getByTestId('mock-script-focus-target')).toHaveTextContent(
      'Behavior.cs:12',
    );
  });

  it('shows structured dry-run facts after running a Script draft', async () => {
    mockedScriptsApi.runDraftScript.mockResolvedValueOnce({
      accepted: true,
      scopeId: 'scope-1',
      scriptId: 'script-alpha',
      scriptRevision: 'rev-1',
      definitionActorId: 'definition-run',
      runtimeActorId: 'runtime-run',
      runId: 'run-script-1',
      sourceHash: 'hash-run',
      commandTypeUrl: 'type.googleapis.com/AppScriptCommand',
      readModelUrl: 'type.googleapis.com/AppScriptReadModel',
    });

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [scriptDetail],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="script-alpha"
        onContinueToBind={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(await screen.findByLabelText('Script dry run facts')).toBeInTheDocument();
    expect(screen.getByText('run-script-1')).toBeInTheDocument();
    expect(screen.getByText('runtime-run')).toBeInTheDocument();
    expect(screen.getByText('type.googleapis.com/AppScriptReadModel')).toBeInTheDocument();
  });

  it('keeps save observation pending honest and exposes catalog refresh', async () => {
    mockedScriptsApi.saveScript.mockResolvedValueOnce({
      acceptedScript: {
        scriptId: 'orders-script',
        revisionId: 'draft-1',
        definitionActorId: 'definition-draft',
        sourceHash: 'hash-draft',
        proposalId: 'proposal-draft',
        expectedBaseRevision: '',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });
    mockedScriptsApi.observeSaveScript.mockResolvedValue({
      scopeId: 'scope-1',
      scriptId: 'orders-script',
      status: 'pending',
      message: 'pending',
      currentScript: null,
      isTerminal: false,
    });
    const handleRefreshScripts = jest.fn();

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="orders-script"
        pendingScriptDraft={{
          scriptId: 'orders-script',
          displayName: 'Orders Script',
        }}
        onContinueToBind={jest.fn()}
        onRefreshScripts={handleRefreshScripts}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save revision' }));

    expect(await screen.findByText(/Waiting for catalog/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh catalog' }));
    expect(handleRefreshScripts).toHaveBeenCalled();
  });

  it('surfaces rejected Script save observations and keeps Bind disabled', async () => {
    mockedScriptsApi.saveScript.mockResolvedValueOnce({
      acceptedScript: {
        scriptId: 'orders-script',
        revisionId: 'draft-1',
        definitionActorId: 'definition-draft',
        sourceHash: 'hash-draft',
        proposalId: 'proposal-draft',
        expectedBaseRevision: '',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });
    mockedScriptsApi.observeSaveScript.mockResolvedValueOnce({
      scopeId: 'scope-1',
      scriptId: 'orders-script',
      status: 'rejected',
      message: 'Catalog rejected the revision.',
      currentScript: null,
      isTerminal: true,
    });

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="orders-script"
        pendingScriptDraft={{
          scriptId: 'orders-script',
          displayName: 'Orders Script',
        }}
        onContinueToBind={jest.fn()}
        onRefreshScripts={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save revision' }));

    expect(await screen.findByText('Catalog rejected the revision.')).toBeInTheDocument();
    expect(screen.getByText('Save needs attention')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeDisabled();
  });

  it('polls save observation until a pending Script save is applied', async () => {
    const handleDraftSaved = jest.fn();
    mockedScriptsApi.saveScript.mockResolvedValueOnce({
      acceptedScript: {
        scriptId: 'orders-script',
        revisionId: 'draft-1',
        definitionActorId: 'definition-draft',
        sourceHash: 'hash-draft',
        proposalId: 'proposal-draft',
        expectedBaseRevision: '',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });
    mockedScriptsApi.observeSaveScript
      .mockResolvedValueOnce({
        scopeId: 'scope-1',
        scriptId: 'orders-script',
        status: 'pending',
        message: 'pending',
        currentScript: null,
        isTerminal: false,
      })
      .mockResolvedValueOnce({
        scopeId: 'scope-1',
        scriptId: 'orders-script',
        status: 'applied',
        message: 'applied',
        currentScript: {
          scopeId: 'scope-1',
          scriptId: 'orders-script',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-draft',
          activeRevision: 'draft-1',
          activeSourceHash: 'hash-draft',
          updatedAt: '2026-04-27T00:00:01Z',
        },
        isTerminal: true,
      });

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="orders-script"
        pendingScriptDraft={{
          scriptId: 'orders-script',
          displayName: 'Orders Script',
        }}
        onContinueToBind={jest.fn()}
        onRefreshScripts={jest.fn()}
        onScriptDraftSaved={handleDraftSaved}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save revision' }));

    expect(await screen.findByText(/checking again in 1s/)).toBeInTheDocument();
    await waitFor(
      () => {
        expect(mockedScriptsApi.observeSaveScript).toHaveBeenCalledTimes(2);
        expect(handleDraftSaved).toHaveBeenCalledWith('orders-script');
        expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeEnabled();
      },
      { timeout: 2500 },
    );
  });

  it('ignores a save observation that resolves after the source changes', async () => {
    mockedScriptsApi.saveScript.mockResolvedValueOnce({
      acceptedScript: {
        scriptId: 'orders-script',
        revisionId: 'draft-1',
        definitionActorId: 'definition-draft',
        sourceHash: 'hash-draft',
        proposalId: 'proposal-draft',
        expectedBaseRevision: '',
        acceptedAt: '2026-04-27T00:00:00Z',
      },
    });
    let resolveObservation: (value: {
      scopeId: string;
      scriptId: string;
      status: 'applied';
      message: string;
      currentScript: {
        scopeId: string;
        scriptId: string;
        catalogActorId: string;
        definitionActorId: string;
        activeRevision: string;
        activeSourceHash: string;
        updatedAt: string;
      };
      isTerminal: true;
    }) => void = () => undefined;
    mockedScriptsApi.observeSaveScript.mockReturnValueOnce(
      new Promise((resolve) => {
        resolveObservation = resolve;
      }),
    );

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="orders-script"
        pendingScriptDraft={{
          scriptId: 'orders-script',
          displayName: 'Orders Script',
        }}
        onContinueToBind={jest.fn()}
        onRefreshScripts={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Validate' }));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save revision' }));
    await waitFor(() => {
      expect(mockedScriptsApi.observeSaveScript).toHaveBeenCalled();
    });

    fireEvent.change(screen.getByLabelText('Mock script code editor'), {
      target: {
        value: 'using System;\n// edited while save is observing',
      },
    });
    await act(async () => {
      resolveObservation({
        scopeId: 'scope-1',
        scriptId: 'orders-script',
        status: 'applied',
        message: 'applied',
        currentScript: {
          scopeId: 'scope-1',
          scriptId: 'orders-script',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-draft',
          activeRevision: 'draft-1',
          activeSourceHash: 'hash-draft',
          updatedAt: '2026-04-27T00:00:01Z',
        },
        isTerminal: true,
      });
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save revision' })).toBeDisabled();
    });
    expect(screen.queryByText(/Save applied/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeDisabled();
  });

  it('edits a multi-file package and entry settings', () => {
    const promptSpy = jest
      .spyOn(window, 'prompt')
      .mockReturnValueOnce('Support.cs')
      .mockReturnValueOnce('SupportRenamed.cs');

    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [scriptDetail],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="script-alpha"
        onContinueToBind={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    expect(screen.getByLabelText('Script package tree')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Advanced package'));
    fireEvent.click(screen.getByRole('button', { name: 'Add C#' }));
    expect(screen.getByRole('button', { name: /Support\.cs/ })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Rename' }));
    expect(screen.getByRole('button', { name: /SupportRenamed\.cs/ })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Entry behavior type'), {
      target: {
        value: 'SupportBehavior',
      },
    });
    expect(screen.getByText(/Behavior: SupportBehavior/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Set entry source' }));
    expect(screen.getByText(/Entry: SupportRenamed\.cs/)).toBeInTheDocument();
    promptSpy.mockRestore();
  });

  it('records promotion decisions for the current Script revision', async () => {
    render(
      <StudioScriptBuildPanel
        scopeId="scope-1"
        scriptsQuery={{
          data: [scriptDetail],
          error: null,
          isError: false,
          isLoading: false,
        }}
        selectedScriptId="script-alpha"
        onContinueToBind={jest.fn()}
        onSelectScriptId={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByText('Promotion'));
    fireEvent.change(screen.getByLabelText('Promotion reason'), {
      target: {
        value: 'ready for binding',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Propose evolution' }));

    await waitFor(() => {
      expect(mockedScriptsApi.proposeEvolution).toHaveBeenCalledWith(
        'scope-1',
        'script-alpha',
        expect.objectContaining({
          baseRevision: 'rev-1',
          reason: 'ready for binding',
        }),
      );
    });
    expect(await screen.findByText(/Promotion accepted/)).toBeInTheDocument();
    expect(screen.getByText('Accepted')).toBeInTheDocument();
  });

  it('walks a complete workflow build loop', async () => {
    const handleContinueToBind = jest.fn();
    const handleSaveDraft = jest.fn();

    render(
      <WorkflowBuildHarness
        onContinueToBind={handleContinueToBind}
        onSaveDraft={handleSaveDraft}
      />,
    );

    expect(await screen.findByText('DAG Canvas')).toBeInTheDocument();
    expect(screen.getByTestId('workflow-stage-actions')).toBeInTheDocument();
    const workflowEditorWorkspace = screen.getByTestId('workflow-editor-workspace');
    const workflowDryRunPanel = screen.getByTestId('workflow-dry-run-panel');
    expect(workflowEditorWorkspace).toBeInTheDocument();
    expect(workflowDryRunPanel).toBeInTheDocument();
    expect(within(workflowEditorWorkspace).queryByText('Dry-run')).not.toBeInTheDocument();
    expect(
      workflowEditorWorkspace.compareDocumentPosition(workflowDryRunPanel) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Add step' }));
    expect(await screen.findByTestId('workflow-step-type-picker')).toBeInTheDocument();
    expect(screen.getByTestId('workflow-step-type-picker-grid')).toHaveStyle({
      overflowY: 'auto',
    });
    fireEvent.click(screen.getByRole('button', { name: /llm_call/i }));

    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('llm_call');
    });

    fireEvent.change(screen.getByLabelText('Step ID'), {
      target: {
        value: 'review_step',
      },
    });
    fireEvent.change(screen.getByLabelText('Step parameters'), {
      target: {
        value: JSON.stringify(
          {
            prompt_prefix: 'Inspect the order and summarize the risk.',
          },
          null,
          2,
        ),
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Apply changes' }));

    await waitFor(() => {
      expect(screen.getByLabelText('Step ID')).toHaveValue('review_step');
    });

    fireEvent.click(screen.getByRole('button', { name: 'YAML' }));

    await waitFor(() => {
      expect(
        (screen.getByLabelText('定义 YAML') as HTMLTextAreaElement).value,
      ).toContain('review_step');
    });
    expect(screen.getByTestId('workflow-step-detail-panel')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue to Bind' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));

    expect(handleSaveDraft).toHaveBeenCalledWith(
      expect.stringContaining('review_step'),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          metadata: {
            'aevatar.model_override': 'gpt-5.4-mini',
            'nyxid.route_preference': '/api/v1/proxy/s/openai',
          },
          prompt: 'Please triage the refund request.',
        }),
        expect.any(AbortSignal),
      );
    });

    expect(await screen.findByText('workflow draft output')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Bind' }));

    expect(handleContinueToBind).toHaveBeenCalledWith(
      expect.stringContaining('review_step'),
    );
  });

  it('keeps runtime metadata out of output and only exposes it in debug details', async () => {
    mockedParseBackendSSEStream.mockImplementationOnce(async function* () {
      yield {
        type: AGUIEventType.RUN_STARTED,
        actorId: 'actor-1',
        runId: 'run-1',
      };
      yield {
        type: AGUIEventType.CUSTOM,
        name: 'run_context',
      } as any;
      yield {
        type: AGUIEventType.TEXT_MESSAGE_END,
        message: 'final workflow answer',
      };
    });

    render(
      <WorkflowBuildHarness
        onContinueToBind={jest.fn()}
        onSaveDraft={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(await screen.findByText('final workflow answer')).toBeInTheDocument();

    const outputSection = screen.getByText('Output');
    const outputPanel = outputSection.parentElement;
    expect(outputPanel).not.toBeNull();
    expect(within(outputPanel as HTMLElement).getByText('final workflow answer')).toBeInTheDocument();
    expect(within(outputPanel as HTMLElement).queryByText(/runId:/i)).not.toBeInTheDocument();
    expect(within(outputPanel as HTMLElement).queryByText(/actorId:/i)).not.toBeInTheDocument();
    expect(screen.queryByText('Run summary')).not.toBeInTheDocument();

    const debugDetailsToggle = await screen.findByText('Debug details');
    fireEvent.click(debugDetailsToggle);
    expect(await screen.findByText(/runId: run-1/i)).toBeInTheDocument();
    expect(screen.getByText(/actorId: actor-1/i)).toBeInTheDocument();
  });

  it('prefers the final workflow output over earlier streamed node text', async () => {
    mockedParseBackendSSEStream.mockImplementationOnce(async function* () {
      yield {
        type: AGUIEventType.RUN_STARTED,
        runId: 'run-2',
        threadId: 'actor-2',
      };
      yield {
        type: AGUIEventType.TEXT_MESSAGE_CONTENT,
        delta: 'first node answer',
      };
      yield {
        type: AGUIEventType.RUN_FINISHED,
        result: {
          output: 'second node final answer',
        },
      };
    });

    render(
      <WorkflowBuildHarness
        onContinueToBind={jest.fn()}
        onSaveDraft={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(await screen.findByText('second node final answer')).toBeInTheDocument();
    const outputSection = screen.getByText('Output');
    const outputPanel = outputSection.parentElement;
    expect(outputPanel).not.toBeNull();
    expect(within(outputPanel as HTMLElement).queryByText('first node answer')).toBeNull();
    expect(within(outputPanel as HTMLElement).getByText('second node final answer')).toBeInTheDocument();
  });

  it('shows a friendly provider guidance message when draft run backend rejects the route', async () => {
    mockedRuntimeRunsApi.streamDraftRun.mockRejectedValueOnce(
      new Error(
        "Service request failed. Status: 400 (Bad Request) | NyxID response: {\"message\":\"Bad request: Provider 'openai' not connected. Connect at /providers.\"}",
      ),
    );

    render(
      <WorkflowBuildHarness
        onContinueToBind={jest.fn()}
        onSaveDraft={jest.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));

    expect(
      await screen.findByText(/provider 还没有连好/i),
    ).toBeInTheDocument();
  });

  it('locks apply changes while the step mutation is pending', async () => {
    let resolveApply: (() => void) | null = null;
    const handleApplyStepDraft = jest.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveApply = resolve;
        }),
    );

    render(
      <WorkflowBuildHarness
        onApplyStepDraftOverride={handleApplyStepDraft}
        onContinueToBind={jest.fn()}
        onSaveDraft={jest.fn()}
      />,
    );

    const applyButton = screen.getByRole('button', { name: 'Apply changes' });
    fireEvent.click(applyButton);
    fireEvent.click(applyButton);

    await waitFor(() => {
      expect(handleApplyStepDraft).toHaveBeenCalledTimes(1);
      expect(applyButton).toBeDisabled();
    });

    act(() => {
      resolveApply?.();
    });

    await waitFor(() => {
      expect(applyButton).not.toBeDisabled();
    });
  });
});
