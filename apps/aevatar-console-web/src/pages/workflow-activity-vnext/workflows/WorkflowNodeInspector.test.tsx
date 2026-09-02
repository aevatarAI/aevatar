import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
import type { StudioStepInspectorDraft } from '@/shared/studio/document';
import type {
  StudioWorkflowCapabilityList,
  StudioWorkflowCapabilityReadiness,
} from '@/shared/studio/models';
import WorkflowNodeInspector from './WorkflowNodeInspector';

const capabilityList: StudioWorkflowCapabilityList = {
  capabilities: [
    {
      displayName: 'PostHog / Update dashboard',
      readOnly: false,
      destructive: false,
      selector: {
        kind: 'nyxid_operation',
        userServiceId: 'us-posthog-alpha',
        endpointId: 'update-dashboard',
      },
      source: {
        kind: 'nyxid_user_services',
        sourceId: 'source-posthog-alpha',
        sourceVersion: 7,
        observedAt: '2026-09-02T08:00:00Z',
        freshUntil: '2026-09-02T08:05:00Z',
      },
    },
  ],
  candidateCount: 1,
  rejectedCount: 0,
  diagnostics: [],
};

const readyCapability: StudioWorkflowCapabilityReadiness = {
  executionMode: 'interactive',
  status: 'ready',
  selectedSelector: {
    kind: 'nyxid_operation',
    userServiceId: 'us-posthog-alpha',
    endpointId: 'update-dashboard',
  },
  selectedOperation: {
    userServiceId: 'us-posthog-alpha',
    endpointId: 'update-dashboard',
    serviceSlug: 'posthog',
    httpMethod: 'PATCH',
    pathTemplate: '/api/dashboards/{dashboard_id}',
    parameters: [],
    requestBody: null,
    responsePolicy: {
      textAllowed: true,
      fileArtifactAllowed: false,
      mediaTypes: ['application/json'],
    },
    executionPolicy: {
      risk: 'write',
      approval: 'required',
      enforcementOwner: 'aevatar',
      allowedExecutionModes: ['interactive'],
    },
  },
  blockers: [],
  remediations: [],
  sources: [],
};

const dashboardIdParameter = {
  name: 'dashboard_id',
  location: 'path',
  required: true,
  schema: {
    valueKind: 'integer',
    properties: [],
    requiredProperties: [],
    items: null,
    allowedValues: [],
    additionalPropertiesAllowed: false,
  },
} as const;

const baseDraft: StudioStepInspectorDraft = {
  kind: 'step',
  id: 'step-alpha',
  type: 'llm_call',
  targetRole: 'assistant',
  next: 'step-beta',
  branchesText: '{}',
  parametersText: '{\n  "prompt_prefix": "Summarize the input"\n}',
  capability: null,
};

function renderInspector(
  stepDraft: StudioStepInspectorDraft,
  input?: {
    readonly onClose?: jest.Mock;
    readonly onConfigurationChange?: jest.Mock;
  },
) {
  const onClose = input?.onClose ?? jest.fn();
  const onConfigurationChange =
    input?.onConfigurationChange ?? jest.fn().mockReturnValue(true);
  const queryClient = new QueryClient({
    defaultOptions: { queries: { gcTime: 0, retry: false } },
  });
  return {
    ...render(
      <QueryClientProvider client={queryClient}>
        <WorkflowNodeInspector
          onClose={onClose}
          onConfigurationChange={onConfigurationChange}
          onConfigurationErrorChange={jest.fn()}
          scopeId="scope-alpha"
          stepDraft={stepDraft}
        />
      </QueryClientProvider>,
    ),
    onClose,
    onConfigurationChange,
  };
}

describe('WorkflowNodeInspector', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue(readyCapability);
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('leads with the step purpose and keeps runtime identity secondary', () => {
    renderInspector(baseDraft);

    const inspector = screen.getByRole('complementary', {
      name: 'Configure step-alpha',
    });
    const purpose = within(inspector).getByText(
      'Send an instruction and workflow input to an AI model.',
    );
    const settings = within(inspector).getByRole('heading', {
      name: 'Settings',
    });

    expect(
      purpose.compareDocumentPosition(settings) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(within(inspector).getByText('LLM call')).toBeVisible();
    expect(within(inspector).getByText('Technical details')).toBeVisible();
    expect(within(inspector).getByText('Advanced JSON')).toBeVisible();
    expect(within(inspector).queryByText('step-alpha')).not.toBeInTheDocument();
    expect(
      within(inspector).getByRole('button', { name: 'Apply step' }),
    ).toBeVisible();
  });

  it('shows an external action instead of the nyxid runtime adapter', async () => {
    renderInspector({
      ...baseDraft,
      type: 'tool_call',
      parametersText: '{\n  "tool": "nyxid_proxy",\n  "arguments": "{}"\n}',
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
    });

    const inspector = screen.getByRole('complementary', {
      name: 'Configure step-alpha',
    });
    expect(await within(inspector).findByLabelText('Action')).toBeVisible();
    expect(within(inspector).queryByLabelText('Tool')).not.toBeInTheDocument();
    expect(
      within(inspector).queryByText('nyxid_proxy'),
    ).not.toBeInTheDocument();
    expect(
      within(inspector).getByText(
        'Run an action from a connected service or registered tool.',
      ),
    ).toBeVisible();

    fireEvent.click(within(inspector).getByText('Technical details'));
    expect(within(inspector).getByText('step-alpha')).toBeVisible();
    expect(within(inspector).getByText('nyxid_proxy')).toBeVisible();
    expect(within(inspector).getByText('us-posthog-alpha')).toBeVisible();
    expect(within(inspector).getByText('update-dashboard')).toBeVisible();
  });

  it('keeps the generic editor for an existing non-external tool', () => {
    renderInspector({
      ...baseDraft,
      type: 'tool_call',
      parametersText: '{\n  "tool": "web_search"\n}',
    });

    expect(screen.getByLabelText('Tool')).toHaveValue('web_search');
    expect(screen.queryByLabelText('Action')).not.toBeInTheDocument();
    expect(studioApi.listWorkflowCapabilities).not.toHaveBeenCalled();
  });

  it('applies the selected capability and its runtime parameters atomically', async () => {
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(
      {
        ...baseDraft,
        type: 'tool_call',
        parametersText: '{}',
      },
      { onConfigurationChange },
    );

    const actionPicker = await screen.findByRole('combobox', {
      name: 'Action',
    });
    await waitFor(() => expect(actionPicker).not.toBeDisabled());
    fireEvent.mouseDown(actionPicker);
    fireEvent.click(await screen.findByText('PostHog / Update dashboard'));
    fireEvent.click(screen.getByRole('button', { name: 'Apply step' }));

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalled());
    const change = onConfigurationChange.mock.calls.at(-1)?.[0];
    expect(change.capability).toEqual({
      nyxid_operation: {
        user_service_id: 'us-posthog-alpha',
        endpoint_id: 'update-dashboard',
      },
    });
    expect(JSON.parse(change.parametersText)).toEqual({
      tool: 'nyxid_proxy',
      arguments: '{}',
    });
  });

  it('keeps an incomplete but representable action draft applyable', async () => {
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue({
        ...readyCapability,
        selectedOperation: readyCapability.selectedOperation
          ? {
              ...readyCapability.selectedOperation,
              parameters: [dashboardIdParameter],
            }
          : null,
      });
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(
      {
        ...baseDraft,
        type: 'tool_call',
        parametersText: '{}',
      },
      { onConfigurationChange },
    );

    const actionPicker = await screen.findByRole('combobox', {
      name: 'Action',
    });
    await waitFor(() => expect(actionPicker).not.toBeDisabled());
    fireEvent.mouseDown(actionPicker);
    fireEvent.click(await screen.findByText('PostHog / Update dashboard'));

    expect(await screen.findByText('Dashboard id is required.')).toBeVisible();
    const applyStep = screen.getByRole('button', { name: 'Apply step' });
    expect(applyStep).toBeEnabled();
    fireEvent.click(applyStep);

    await waitFor(() => expect(onConfigurationChange).toHaveBeenCalled());
    expect(
      JSON.parse(onConfigurationChange.mock.calls.at(-1)?.[0].parametersText),
    ).toEqual({
      tool: 'nyxid_proxy',
      arguments: '{"response_mode":"text"}',
    });
  });

  it('blocks an invalid saved response mode after selecting an action', async () => {
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue({
        ...readyCapability,
        selectedOperation: readyCapability.selectedOperation
          ? {
              ...readyCapability.selectedOperation,
              responsePolicy: {
                textAllowed: true,
                fileArtifactAllowed: true,
                mediaTypes: ['application/json', 'application/pdf'],
              },
            }
          : null,
      });
    const onConfigurationChange = jest.fn().mockReturnValue(true);
    renderInspector(
      {
        ...baseDraft,
        type: 'tool_call',
        parametersText: '{"arguments":"{\\"response_mode\\":\\"binary\\"}"}',
      },
      { onConfigurationChange },
    );

    const actionPicker = await screen.findByRole('combobox', {
      name: 'Action',
    });
    await waitFor(() => expect(actionPicker).not.toBeDisabled());
    fireEvent.mouseDown(actionPicker);
    fireEvent.click(await screen.findByText('PostHog / Update dashboard'));

    const responseMode = await screen.findByRole('combobox', {
      name: 'Result format',
    });
    await waitFor(() =>
      expect(responseMode).toHaveAttribute('aria-invalid', 'true'),
    );
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeDisabled();
    expect(onConfigurationChange).not.toHaveBeenCalled();
  });

  it('clears a generated-field error after Advanced JSON repairs the value', async () => {
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue({
        ...readyCapability,
        selectedOperation: readyCapability.selectedOperation
          ? {
              ...readyCapability.selectedOperation,
              parameters: [dashboardIdParameter],
            }
          : null,
      });
    renderInspector({
      ...baseDraft,
      type: 'tool_call',
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      parametersText:
        '{"tool":"nyxid_proxy","arguments":"{\\"path_params\\":{\\"dashboard_id\\":42}}"}',
    });

    const dashboardId = await screen.findByLabelText('Dashboard id');
    fireEvent.change(dashboardId, { target: { value: 'not-a-number' } });
    expect(
      await screen.findAllByText('Dashboard id must be a whole number.'),
    ).toHaveLength(2);

    fireEvent.click(screen.getByText('Advanced JSON'));
    fireEvent.change(screen.getByLabelText('Raw configuration'), {
      target: {
        value: JSON.stringify(
          {
            tool: 'nyxid_proxy',
            arguments: '{"path_params":{"dashboard_id":43}}',
          },
          null,
          2,
        ),
      },
    });

    await waitFor(() =>
      expect(
        screen.queryByText('Dashboard id must be a whole number.'),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText('Dashboard id')).toHaveValue('43');
    expect(screen.getByRole('button', { name: 'Apply step' })).toBeEnabled();
  });

  it('confirms before closing with unapplied guided changes', async () => {
    const { onClose } = renderInspector({
      ...baseDraft,
      type: 'tool_call',
      parametersText: '{}',
    });

    const actionPicker = await screen.findByRole('combobox', {
      name: 'Action',
    });
    await waitFor(() => expect(actionPicker).not.toBeDisabled());
    fireEvent.mouseDown(actionPicker);
    fireEvent.click(await screen.findByText('PostHog / Update dashboard'));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onClose).not.toHaveBeenCalled();
    expect(
      screen.getByRole('dialog', { name: 'Discard node changes?' }),
    ).toBeInTheDocument();
  });
});
