import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import StudioInspectorPane from './StudioInspectorPane';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

const workflowRole = {
  id: 'assistant',
  name: 'Assistant',
  provider: 'openai',
  model: 'gpt-5.4',
  systemPrompt: 'Help with review tasks.',
  connectors: ['search'],
};

const workflowStep = {
  id: 'review_step',
  type: 'connector_call' as const,
  targetRole: 'assistant',
  parameters: { connector: 'search', query: 'hello' },
  next: 'publish_step',
  branches: { retry: 'retry_step' },
};

const connector = {
  name: 'search',
  type: 'http',
  enabled: true,
  timeoutMs: 10000,
  retry: 0,
};

const savedRole = {
  id: 'assistant_catalog',
  name: 'Catalog assistant',
  provider: 'openai',
  model: 'gpt-5.4-mini',
  systemPrompt: 'Catalog prompt',
  connectors: ['search'],
};

function createBaseProps<const Overrides extends object>(
  overrides?: Overrides,
) {
  const nodeInspectorDraft = {
    kind: 'step' as const,
    capability: null,
    id: 'review_step',
    type: 'connector_call',
    targetRole: 'assistant',
    next: 'publish_step',
    branchesText: '{\n  "retry": "retry_step"\n}',
    parametersText: '{\n  "connector": "search"\n}',
  };

  return {
    draftYaml: 'name: studio_demo\nsteps:\n  - id: review_step\n',
    inspectorTab: 'yaml' as const,
    workflowRoleIds: ['assistant'],
    workflowStepIds: ['review_step', 'publish_step', 'retry_step'],
    workflowRoles: [workflowRole],
    workflowSteps: [workflowStep],
    connectors: [connector],
    savedRoles: [savedRole],
    selectedGraphRole: null,
    selectedGraphStep: null,
    nodeInspectorDraft,
    inspectorPending: false,
    inspectorNotice: null,
    validationLoading: false,
    validationError: null,
    validationFindings: [],
    parsedWorkflowName: 'studio_demo',
    activeWorkflowName: 'studio_demo',
    activeWorkflowDescription:
      'A demo workflow used for Studio inspector tests.',
    onSetInspectorTab: jest.fn(),
    onSetDraftYaml: jest.fn(),
    onValidateDraft: jest.fn(),
    onChangeNodeInspectorDraft: jest.fn(),
    onApplyNodeChanges: jest.fn(),
    onInsertStep: jest.fn(),
    onAddWorkflowRole: jest.fn(),
    onUseSavedRole: jest.fn(),
    onUpdateWorkflowRole: jest.fn(),
    onDeleteConnection: jest.fn(),
    onDeleteWorkflowRole: jest.fn(),
    onDeleteStep: jest.fn(),
    onResetSelectedNode: jest.fn(),
    ...(overrides ?? {}),
  };
}

describe('StudioInspectorPane', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders the YAML summary and validation digest', () => {
    const validationFindings = [
      {
        level: 'warning' as const,
        path: '/steps/0',
        code: 'step-warning',
        message: 'Step should define a more specific timeout.',
      },
    ];

    render(
      <StudioInspectorPane
        {...createBaseProps({
          inspectorTab: 'yaml',
          validationFindings,
        })}
      />,
    );

    expect(screen.getByText('YAML workspace')).toBeInTheDocument();
    expect(
      screen.queryByText(
        'Edit the source document directly, then validate it before saving or running.',
      ),
    ).not.toBeInTheDocument();
    expect(
      screen.getAllByRole('button', { name: 'Show help' }).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText('Validation digest')).toBeInTheDocument();
    expect(screen.getByText('1 validation finding(s)')).toBeInTheDocument();
    expect(screen.getByText('Parsed workflow')).toBeInTheDocument();
    expect(screen.getByText('studio_demo')).toBeInTheDocument();
  });

  it('renders step summary cards and keeps node actions wired', () => {
    const onApplyNodeChanges = jest.fn();
    const onChangeNodeInspectorDraft = jest.fn();

    render(
      <StudioInspectorPane
        {...createBaseProps({
          inspectorTab: 'node',
          selectedGraphStep: workflowStep,
          nodeInspectorDraft: {
            kind: 'step',
            capability: null,
            id: 'review_step',
            type: 'connector_call',
            targetRole: 'assistant',
            next: 'publish_step',
            branchesText: '{\n  "retry": "retry_step"\n}',
            parametersText: '{\n  "connector": "search"\n}',
          },
          onChangeNodeInspectorDraft,
          onApplyNodeChanges,
        })}
      />,
    );

    expect(screen.getByText('Step summary')).toBeInTheDocument();
    expect(screen.getByText('Outgoing connections')).toBeInTheDocument();
    expect(screen.getAllByText('publish_step').length).toBeGreaterThan(0);
    expect(screen.getByText('retry_step')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Apply node changes' }));

    expect(onApplyNodeChanges).toHaveBeenCalledTimes(1);

    fireEvent.change(screen.getByLabelText('Parameter Operation'), {
      target: { value: 'query' },
    });
    expect(onChangeNodeInspectorDraft).toHaveBeenCalledWith(
      expect.objectContaining({
        parametersText: expect.stringContaining('"operation": "query"'),
      }),
    );
  });

  it('blocks node apply when raw parameter JSON is invalid', () => {
    const onApplyNodeChanges = jest.fn();

    render(
      <StudioInspectorPane
        {...createBaseProps({
          inspectorTab: 'node',
          selectedGraphStep: workflowStep,
          nodeInspectorDraft: {
            kind: 'step',
            capability: null,
            id: 'review_step',
            type: 'connector_call',
            targetRole: 'assistant',
            next: 'publish_step',
            branchesText: '{\n  "retry": "retry_step"\n}',
            parametersText: '{ "connector": ',
          },
          onApplyNodeChanges,
        })}
      />,
    );

    expect(
      screen.getByText('Unexpected end of JSON input'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Apply node changes' }),
    ).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Apply node changes' }));

    expect(onApplyNodeChanges).not.toHaveBeenCalled();
  });

  it('reports node action failures with a safe toast', async () => {
    render(
      <StudioInspectorPane
        {...createBaseProps({
          inspectorNotice: {
            message: 'PATCH /api/studio/node returned 500',
            type: 'error',
          },
          inspectorTab: 'node',
          selectedGraphStep: workflowStep,
        })}
      />,
    );

    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
    });
    expect(mockConsoleToast.error).not.toHaveBeenCalledWith(
      'PATCH /api/studio/node returned 500',
    );
    expect(
      screen.queryByText('PATCH /api/studio/node returned 500'),
    ).not.toBeInTheDocument();
  });
});
