import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import StudioMemberInvokePanel from './StudioMemberInvokePanel';

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(),
    invokeEndpoint: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    bindScopeGAgent: jest.fn(),
  },
}));

describe('StudioMemberInvokePanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValue({
      requestId: 'run-1',
      commandId: 'cmd-1',
      targetActorId: 'actor-1',
      accepted: true,
    });
  });

  it('renders the contract-first invoke layout', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeId: 'scope-1',
        selectedMemberLabel: 'workspace-demo',
        scopeBinding: {
          available: true,
          scopeId: 'scope-1',
          serviceId: 'default',
          displayName: 'workspace-demo',
          serviceKey: 'scope-1:default:workspace-demo',
          defaultServingRevisionId: 'rev-2',
          activeServingRevisionId: 'rev-2',
          deploymentId: 'dep-2',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-default',
          updatedAt: '2026-03-26T08:00:00Z',
          revisions: [
            {
              revisionId: 'rev-2',
              implementationKind: 'workflow',
              status: 'Published',
              artifactHash: 'hash-2',
              failureReason: '',
              isDefaultServing: true,
              isActiveServing: true,
              isServingTarget: true,
              allocationWeight: 100,
              servingState: 'Active',
              deploymentId: 'dep-2',
              primaryActorId: 'actor-default',
              createdAt: '2026-03-26T07:00:00Z',
              preparedAt: '2026-03-26T07:01:00Z',
              publishedAt: '2026-03-26T07:02:00Z',
              retiredAt: null,
              workflowName: 'workspace-demo',
              workflowDefinitionActorId: 'scope-workflow:scope-1:default',
              inlineWorkflowCount: 1,
              scriptId: '',
              scriptRevision: '',
              scriptDefinitionActorId: '',
              scriptSourceHash: '',
              staticActorTypeName: '',
            },
          ],
        },
        services: [
          {
            serviceId: 'default',
            displayName: 'workspace-demo',
            namespace: 'default',
            kind: 'service',
            primaryActorId: 'actor-default',
            deploymentStatus: 'Active',
            endpoints: [
              {
                endpointId: 'submit',
                displayName: 'Submit',
                description: 'Send a structured request into the member.',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
          },
        ],
      }),
    );

    expect(await screen.findByTestId('studio-member-invoke-panel')).toBeTruthy();
    expect(screen.getByText('Invocation Contract')).toBeTruthy();
    expect(screen.getByText('Current member')).toBeTruthy();
    expect(screen.getByText('Playground')).toBeTruthy();
    expect(screen.getByText('Live Trace')).toBeTruthy();
    expect(screen.getByText('Request History (0)')).toBeTruthy();
    expect(screen.queryByText('Published service')).toBeNull();
  });

  it('records successful invoke requests into history and restores them on click', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeId: 'scope-1',
        services: [
          {
            serviceId: 'default',
            displayName: 'workspace-demo',
            namespace: 'default',
            kind: 'service',
            primaryActorId: 'actor-default',
            deploymentStatus: 'Active',
            endpoints: [
              {
                endpointId: 'submit',
                displayName: 'Submit',
                description: 'Send a structured request into the member.',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Invoke request input'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });
    fireEvent.change(screen.getByPlaceholderText('type.googleapis.com/example.Command'), {
      target: {
        value: 'type.googleapis.com/example.Submit',
      },
    });
    fireEvent.change(screen.getByPlaceholderText('Paste a pre-encoded protobuf payload when needed.'), {
      target: {
        value: 'ZXhhbXBsZS1wYXlsb2Fk',
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Invoke' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
          prompt: 'Route this escalation to billing review.',
          payloadTypeUrl: 'type.googleapis.com/example.Submit',
          payloadBase64: 'ZXhhbXBsZS1wYXlsb2Fk',
        }),
        {
          serviceId: 'default',
        },
      );
    });

    expect(await screen.findByText('Request History (1)')).toBeTruthy();
    expect(screen.getAllByText('workspace-demo / Submit').length).toBeGreaterThan(0);
    expect(screen.getAllByText('actor-1').length).toBeGreaterThan(0);

    fireEvent.change(screen.getByLabelText('Invoke request input'), {
      target: {
        value: 'Overwrite prompt',
      },
    });

    fireEvent.click(
      screen.getAllByRole('button', { name: /workspace-demo\s*\/\s*Submit/i })[0],
    );

    await waitFor(() => {
      expect(screen.getByLabelText('Invoke request input')).toHaveValue(
        'Route this escalation to billing review.',
      );
    });
  });

  it('renders a clear empty state when no selected member is available for invoke', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeId: 'scope-1',
        services: [],
        emptyState: {
          message: 'Select a member to invoke.',
          description:
            'Choose a member from Team members or continue from Bind so Invoke stays pinned to one member.',
          type: 'info',
        },
      }),
    );

    expect(await screen.findByText('Select a member to invoke.')).toBeTruthy();
    expect(
      screen.getByText(
        'Choose a member from Team members or continue from Bind so Invoke stays pinned to one member.',
      ),
    ).toBeTruthy();
    expect(screen.queryByText('Invocation Contract')).toBeNull();
  });
});
