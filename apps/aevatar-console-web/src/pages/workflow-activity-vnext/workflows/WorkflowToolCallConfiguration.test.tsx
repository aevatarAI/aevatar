import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
import type {
  StudioWorkflowCapability,
  StudioWorkflowCapabilityList,
  StudioWorkflowCapabilityReadiness,
} from '@/shared/studio/models';
import { capabilitySelectorKey } from './toolCallConfiguration';
import WorkflowToolCallConfiguration, {
  type WorkflowToolCallConfigurationChange,
} from './WorkflowToolCallConfiguration';

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
    parameters: [
      {
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
      },
      {
        name: 'include_archived',
        location: 'query',
        required: false,
        schema: {
          valueKind: 'boolean',
          properties: [],
          requiredProperties: [],
          items: null,
          allowedValues: [],
          additionalPropertiesAllowed: false,
        },
      },
    ],
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

function renderConfiguration(input?: {
  readonly capability?: StudioWorkflowCapability | null;
  readonly onErrorChange?: jest.Mock<void, [string]>;
  readonly parameters?: Record<string, unknown>;
  readonly onChange?: jest.Mock<void, [WorkflowToolCallConfigurationChange]>;
  readonly queryClient?: QueryClient;
}) {
  const onChange = input?.onChange ?? jest.fn();
  const onErrorChange = input?.onErrorChange ?? jest.fn();
  const queryClient =
    input?.queryClient ??
    new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });

  function Harness() {
    const [capability, setCapability] = React.useState(
      input?.capability ?? null,
    );
    const [parameters, setParameters] = React.useState(input?.parameters ?? {});
    return (
      <QueryClientProvider client={queryClient}>
        <WorkflowToolCallConfiguration
          capability={capability}
          disabled={false}
          onChange={(change) => {
            onChange(change);
            setCapability(change.capability);
            setParameters(change.parameters);
          }}
          onErrorChange={onErrorChange}
          parameters={parameters}
          scopeId="scope-alpha"
        />
      </QueryClientProvider>
    );
  }

  return { ...render(<Harness />), onChange, onErrorChange };
}

describe('WorkflowToolCallConfiguration', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('leads with an action picker and writes the exact selected operation', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue(readyCapability);
    const { onChange } = renderConfiguration({
      parameters: { tool: 'nyxid_proxy' },
    });

    const actionPicker = await screen.findByRole('combobox', {
      name: 'Action',
    });
    await waitFor(() => expect(actionPicker).not.toBeDisabled());
    expect(screen.queryByDisplayValue('nyxid_proxy')).not.toBeInTheDocument();
    fireEvent.mouseDown(actionPicker);
    expect(await screen.findByText('Writes data')).toBeInTheDocument();
    expect(
      screen.getByRole('option', {
        name: /PostHog \/ Update dashboard.*Writes data/,
      }),
    ).toBeInTheDocument();
    fireEvent.click(await screen.findByText('PostHog / Update dashboard'));

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith({
        capability: {
          nyxid_operation: {
            user_service_id: 'us-posthog-alpha',
            endpoint_id: 'update-dashboard',
          },
        },
        parameters: {
          tool: 'nyxid_proxy',
          arguments: '{}',
        },
      }),
    );
    expect(await screen.findByText('Ready')).toBeInTheDocument();
    expect(
      screen.getByText('Writes data', { selector: '.ant-tag' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Approval required')).toBeInTheDocument();

    const optionalBoolean = screen.getByRole('combobox', {
      name: 'Include archived',
    });
    expect(optionalBoolean).toHaveValue('');
    fireEvent.mouseDown(optionalBoolean);
    fireEvent.click(await screen.findByText('No'));
    await waitFor(() => {
      const lastChange = onChange.mock.calls.at(-1)?.[0];
      expect(JSON.parse(String(lastChange?.parameters.arguments))).toEqual({
        query: { include_archived: false },
        response_mode: 'text',
      });
    });

    fireEvent.change(screen.getByLabelText('Dashboard id'), {
      target: { value: '42' },
    });
    await waitFor(() => {
      const lastChange = onChange.mock.calls.at(-1)?.[0];
      expect(lastChange?.parameters.tool).toBe('nyxid_proxy');
      expect(JSON.parse(String(lastChange?.parameters.arguments))).toEqual({
        path_params: { dashboard_id: 42 },
        query: { include_archived: false },
        response_mode: 'text',
      });
    });
  });

  it('shows backend setup guidance instead of treating readiness as input validation', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue({
        ...readyCapability,
        status: 'credential_connection_required',
        selectedOperation: null,
        blockers: [
          {
            status: 'credential_connection_required',
            code: 'credential_connection_required',
            safeMessage: 'Reconnect the PostHog service.',
          },
        ],
        remediations: [
          {
            actionKind: 'connect_credential',
            label: 'Reconnect PostHog',
            trustedLocator: 'nyxid:services',
          },
        ],
      });

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
    });

    expect(await screen.findByText('Needs setup')).toBeInTheDocument();
    expect(
      screen.getByText('Reconnect the PostHog service.'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Reconnect PostHog' }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
    );
  });

  it('distinguishes an empty connected-action catalog from loading', async () => {
    jest.spyOn(studioApi, 'listWorkflowCapabilities').mockResolvedValue({
      capabilities: [],
      candidateCount: 1,
      rejectedCount: 1,
      diagnostics: [
        {
          code: 'unsupported_schema',
          safeMessage: 'One connected action uses an unsupported input shape.',
          count: 1,
          source: null,
        },
      ],
    });

    renderConfiguration();

    expect(
      await screen.findByText('No connected actions are available yet.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('One connected action uses an unsupported input shape.'),
    ).toBeInTheDocument();
  });

  it('labels capability discovery as loading before an action can be chosen', () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockReturnValue(
        new Promise<StudioWorkflowCapabilityList>(() => undefined),
      );

    renderConfiguration();

    expect(screen.getByText('Loading connected actions')).toBeInTheDocument();
    expect(screen.queryByText('Choose an action')).not.toBeInTheDocument();
  });

  it('keeps discovery failures recoverable with retry', async () => {
    const listCapabilities = jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockRejectedValueOnce(new Error('network unavailable'))
      .mockResolvedValueOnce(capabilityList);

    renderConfiguration();

    expect(
      await screen.findByText('Connected actions could not be loaded.'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(listCapabilities).toHaveBeenCalledTimes(2));
    expect(await screen.findByLabelText('Action')).toBeInTheDocument();
  });

  it('shows a fresh availability check instead of cached readiness', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    let resolveReadiness: (value: StudioWorkflowCapabilityReadiness) => void =
      () => undefined;
    jest.spyOn(studioApi, 'inspectWorkflowCapabilityReadiness').mockReturnValue(
      new Promise((resolve) => {
        resolveReadiness = resolve;
      }),
    );
    const selector = capabilityList.capabilities[0].selector;
    if (selector.kind !== 'nyxid_operation') {
      throw new Error('Expected a NyxID operation selector.');
    }
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    queryClient.setQueryData(
      [
        'workflow-capability-readiness',
        'scope-alpha',
        capabilitySelectorKey(selector),
      ],
      readyCapability,
    );

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: selector.userServiceId,
          endpoint_id: selector.endpointId,
        },
      },
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
      queryClient,
    });

    expect(await screen.findByText('Checking availability')).toBeVisible();
    expect(screen.queryByText('Ready')).not.toBeInTheDocument();

    resolveReadiness(readyCapability);
    expect(await screen.findByText('Ready')).toBeVisible();
  });

  it('writes the only supported file response mode automatically', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue({
        ...readyCapability,
        selectedOperation: readyCapability.selectedOperation
          ? {
              ...readyCapability.selectedOperation,
              responsePolicy: {
                textAllowed: false,
                fileArtifactAllowed: true,
                mediaTypes: ['application/pdf'],
              },
            }
          : null,
      });
    const { onChange } = renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
    });

    await waitFor(() => {
      const lastChange = onChange.mock.calls.at(-1)?.[0];
      expect(JSON.parse(String(lastChange?.parameters.arguments))).toEqual({
        response_mode: 'file_artifact',
      });
    });
  });

  it('offers a response-format choice when text and files are both supported', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
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

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
    });

    expect(
      await screen.findByRole('combobox', { name: 'Result format' }),
    ).toBeVisible();
  });

  it('associates a generated field error with its input', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue(readyCapability);

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
    });

    const dashboardId = await screen.findByLabelText('Dashboard id');
    fireEvent.change(dashboardId, { target: { value: 'not-a-number' } });

    expect(
      await screen.findByText('Dashboard id must be a whole number.'),
    ).toBeVisible();
    expect(dashboardId).toHaveAttribute('aria-invalid', 'true');
    expect(dashboardId.getAttribute('aria-describedby')).toContain(
      'workflow-tool-field-error-path:dashboard_id',
    );
  });

  it('shows missing required inputs as saveable draft guidance', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
    jest
      .spyOn(studioApi, 'inspectWorkflowCapabilityReadiness')
      .mockResolvedValue(readyCapability);
    const onErrorChange = jest.fn<void, [string]>();

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      onErrorChange,
      parameters: { tool: 'nyxid_proxy', arguments: '{}' },
    });

    const dashboardId = await screen.findByLabelText('Dashboard id');
    expect(screen.getByText('Dashboard id is required.')).toBeVisible();
    expect(
      screen.getByText(
        'Complete the required inputs before this step can run. You can still apply this draft.',
      ),
    ).toBeVisible();
    expect(dashboardId).toHaveAttribute('aria-required', 'true');
    expect(dashboardId).toHaveAttribute('aria-invalid', 'false');
    await waitFor(() => expect(onErrorChange.mock.calls.at(-1)?.[0]).toBe(''));
  });

  it('blocks an invalid saved response mode and clears the error after repair', async () => {
    jest
      .spyOn(studioApi, 'listWorkflowCapabilities')
      .mockResolvedValue(capabilityList);
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
    const onErrorChange = jest.fn<void, [string]>();

    renderConfiguration({
      capability: {
        nyxid_operation: {
          user_service_id: 'us-posthog-alpha',
          endpoint_id: 'update-dashboard',
        },
      },
      onErrorChange,
      parameters: {
        tool: 'nyxid_proxy',
        arguments:
          '{"path_params":{"dashboard_id":42},"response_mode":"binary"}',
      },
    });

    const responseMode = await screen.findByRole('combobox', {
      name: 'Result format',
    });
    expect(
      await screen.findByText(
        'Result format must be one of the available values.',
      ),
    ).toBeVisible();
    expect(responseMode).toHaveAttribute('aria-invalid', 'true');
    await waitFor(() =>
      expect(onErrorChange.mock.calls.at(-1)?.[0]).toBe(
        'Result format must be one of the available values.',
      ),
    );

    fireEvent.mouseDown(responseMode);
    fireEvent.click(await screen.findByText('Text'));

    await waitFor(() => expect(onErrorChange.mock.calls.at(-1)?.[0]).toBe(''));
    expect(responseMode).toHaveAttribute('aria-invalid', 'false');
  });
});
