import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import {
  QueryClient,
  QueryClientProvider,
} from '@tanstack/react-query';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
import type {
  StudioWorkflowCapability,
  StudioWorkflowCapabilityList,
  StudioWorkflowCapabilityReadiness,
} from '@/shared/studio/models';
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
  readonly parameters?: Record<string, unknown>;
  readonly onChange?: jest.Mock<void, [WorkflowToolCallConfigurationChange]>;
}) {
  const onChange = input?.onChange ?? jest.fn();
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  function Harness() {
    const [capability, setCapability] = React.useState(
      input?.capability ?? null,
    );
    const [parameters, setParameters] = React.useState(
      input?.parameters ?? {},
    );
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
          onErrorChange={jest.fn()}
          parameters={parameters}
          scopeId="scope-alpha"
        />
      </QueryClientProvider>
    );
  }

  return { ...render(<Harness />), onChange };
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
    expect(screen.getByText('Writes data')).toBeInTheDocument();
    expect(screen.getByText('Approval required')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Dashboard id'), {
      target: { value: '42' },
    });
    await waitFor(() => {
      const lastChange = onChange.mock.calls.at(-1)?.[0];
      expect(lastChange?.parameters.tool).toBe('nyxid_proxy');
      expect(JSON.parse(String(lastChange?.parameters.arguments))).toEqual({
        path_params: { dashboard_id: 42 },
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
            trustedLocator: '/settings/connections/posthog',
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
    ).toHaveAttribute('href', '/settings/connections/posthog');
  });

  it('distinguishes an empty connected-action catalog from loading', async () => {
    jest.spyOn(studioApi, 'listWorkflowCapabilities').mockResolvedValue({
      capabilities: [],
      candidateCount: 0,
      rejectedCount: 0,
      diagnostics: [],
    });

    renderConfiguration();

    expect(
      await screen.findByText('No connected actions are available yet.'),
    ).toBeInTheDocument();
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
});
