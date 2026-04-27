import { AGUIEventType } from '@aevatar-react-sdk/types';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
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
import { StudioWorkflowBuildPanel } from './StudioBuildPanels';

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamDraftRun: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
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
    value = '',
    onChange,
  }: {
    readonly value?: string;
    readonly onChange?: (nextValue: string) => void;
  }) => (
    <textarea
      aria-label="Mock script code editor"
      value={value}
      onChange={(event) => onChange?.(event.target.value)}
    />
  ),
}));

const mockedRuntimeRunsApi = runtimeRunsApi as unknown as {
  streamDraftRun: jest.Mock;
};
const mockedParseBackendSSEStream = parseBackendSSEStream as jest.Mock;

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
