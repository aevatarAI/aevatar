import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { loadDraftRunPayload } from '@/shared/runs/draftRunSession';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeInvokePage from './invoke';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: 'scope-a',
      scopeSource: 'nyxid',
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Workspace Demo',
      serviceKey: 'scope-a:default:default:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'deploy-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor://scope-a/default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    })),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
  },
}));

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getServiceBindings: jest.fn(),
    createServiceBinding: jest.fn(),
    updateServiceBinding: jest.fn(),
    retireServiceBinding: jest.fn(),
    getServiceRevisions: jest.fn(),
    getServiceRevision: jest.fn(),
    retireServiceRevision: jest.fn(),
    listServiceRuns: jest.fn(),
    getServiceRunAudit: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

import { servicesApi } from '@/shared/api/servicesApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';

describe('ScopeInvokePage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/invoke?scopeId=scope-a');
    jest.clearAllMocks();
    (scopeRuntimeApi.getServiceBindings as jest.Mock).mockResolvedValue({
      serviceKey: 'scope-a:default:default:default',
      updatedAt: '2026-03-31T08:20:00Z',
      bindings: [
        {
          bindingId: 'binding-knowledge',
          displayName: 'Knowledge base',
          bindingKind: 'secret',
          policyIds: ['policy-alpha'],
          retired: false,
          serviceRef: null,
          connectorRef: null,
          secretRef: {
            secretName: 'knowledge-api-key',
          },
        },
      ],
    });
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockResolvedValue({
      scopeId: 'scope-a',
      serviceId: 'default',
      serviceKey: 'scope-a:default:default:default',
      displayName: 'Workspace Demo',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'deploy-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor://scope-a/default',
      catalogStateVersion: 2,
      catalogLastEventId: 'evt-2',
      updatedAt: '2026-03-31T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'ready',
          artifactHash: 'artifact-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'active',
          deploymentId: 'deploy-2',
          primaryActorId: 'actor://scope-a/default',
          createdAt: '2026-03-31T07:00:00Z',
          preparedAt: '2026-03-31T07:10:00Z',
          publishedAt: '2026-03-31T07:20:00Z',
          retiredAt: null,
          workflowName: 'support_flow',
          workflowDefinitionActorId: 'definition://support',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
          staticPreferredActorId: '',
        },
      ],
    });
    (scopeRuntimeApi.getServiceRevision as jest.Mock).mockResolvedValue({
      revisionId: 'rev-2',
      implementationKind: 'workflow',
      status: 'ready',
      artifactHash: 'artifact-2',
      failureReason: '',
      isDefaultServing: true,
      isActiveServing: true,
      isServingTarget: true,
      allocationWeight: 100,
      servingState: 'active',
      deploymentId: 'deploy-2',
      primaryActorId: 'actor://scope-a/default',
      createdAt: '2026-03-31T07:00:00Z',
      preparedAt: '2026-03-31T07:10:00Z',
      publishedAt: '2026-03-31T07:20:00Z',
      retiredAt: null,
      workflowName: 'support_flow',
      workflowDefinitionActorId: 'definition://support',
      inlineWorkflowCount: 0,
      scriptId: '',
      scriptRevision: '',
      scriptDefinitionActorId: '',
      scriptSourceHash: '',
      staticActorTypeName: '',
      staticPreferredActorId: '',
    });
    (scopeRuntimeApi.listServiceRuns as jest.Mock).mockResolvedValue({
      scopeId: 'scope-a',
      serviceId: 'default',
      serviceKey: 'scope-a:default:default:default',
      displayName: 'Workspace Demo',
      runs: [
        {
          scopeId: 'scope-a',
          serviceId: 'default',
          runId: 'run-42',
          actorId: 'actor://scope-a/default',
          definitionActorId: 'definition://support',
          revisionId: 'rev-2',
          deploymentId: 'deploy-2',
          workflowName: 'support_flow',
          completionStatus: 'completed',
          stateVersion: 9,
          lastEventId: 'evt-9',
          lastUpdatedAt: '2026-03-31T08:30:00Z',
          boundAt: '2026-03-31T08:25:00Z',
          bindingUpdatedAt: '2026-03-31T08:26:00Z',
          lastSuccess: true,
          totalSteps: 4,
          completedSteps: 4,
          roleReplyCount: 2,
          lastOutput: 'Resolved successfully',
          lastError: '',
        },
      ],
    });
    (scopeRuntimeApi.getServiceRunAudit as jest.Mock).mockResolvedValue({
      summary: {
        scopeId: 'scope-a',
        serviceId: 'default',
        runId: 'run-42',
        actorId: 'actor://scope-a/default',
        definitionActorId: 'definition://support',
        revisionId: 'rev-2',
        deploymentId: 'deploy-2',
        workflowName: 'support_flow',
        completionStatus: 'completed',
        stateVersion: 9,
        lastEventId: 'evt-9',
        lastUpdatedAt: '2026-03-31T08:30:00Z',
        boundAt: '2026-03-31T08:25:00Z',
        bindingUpdatedAt: '2026-03-31T08:26:00Z',
        lastSuccess: true,
        totalSteps: 4,
        completedSteps: 4,
        roleReplyCount: 2,
        lastOutput: 'Resolved successfully',
        lastError: '',
      },
      audit: {
        reportVersion: '1.0',
        projectionScope: 'actor_shared',
        topologySource: 'runtime_snapshot',
        completionStatus: 'completed',
        workflowName: 'support_flow',
        rootActorId: 'actor://scope-a/default',
        commandId: 'cmd-42',
        stateVersion: 9,
        lastEventId: 'evt-9',
        createdAt: '2026-03-31T08:25:00Z',
        updatedAt: '2026-03-31T08:30:00Z',
        startedAt: '2026-03-31T08:25:05Z',
        endedAt: '2026-03-31T08:30:00Z',
        durationMs: 295000,
        success: true,
        input: 'hello service',
        finalOutput: 'Resolved successfully',
        finalError: '',
        topology: [],
        steps: [],
        roleReplies: [],
        timeline: [
          {
            timestamp: '2026-03-31T08:25:05Z',
            stage: 'step_requested',
            message: 'Asked support agent',
            agentId: 'agent-1',
            stepId: 'answer',
            stepType: 'llm_call',
            eventType: 'requested',
            data: {},
          },
        ],
        summary: {
          totalSteps: 4,
          requestedSteps: 4,
          completedSteps: 4,
          roleReplyCount: 2,
          stepTypeCounts: {
            llm_call: 4,
          },
        },
      },
    });
  });

  it('invokes a non-chat endpoint for the selected scope service', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'run',
            kind: 'command',
            requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
            responseTypeUrl: 'type.googleapis.com/google.protobuf.Empty',
            description: 'Submit a command run.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValue({
      accepted: true,
      commandId: 'cmd-1',
    });

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    await waitFor(() => {
      expect(servicesApi.listServices).toHaveBeenCalledWith({
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
      });
    });
    expect(await screen.findByText('Lab Console')).toBeTruthy();
    expect(await screen.findByText('Invoke Lab')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Open operator brief' })).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Invoke endpoint' })).toBeTruthy();
    fireEvent.change(
      screen.getByPlaceholderText('Prompt or payload text'),
      {
        target: { value: 'run the scope command' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Invoke endpoint' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-a',
        {
          endpointId: 'run',
          prompt: 'run the scope command',
          payloadTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
          payloadBase64: undefined,
        },
        {
          serviceId: 'default',
        },
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Output' }));

    expect(await screen.findByText('Invocation Receipt')).toBeTruthy();
    expect(await screen.findByText(/"accepted": true/)).toBeTruthy();
  });

  it('streams a chat endpoint for the selected scope service', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'chat',
            kind: 'chat',
            requestTypeUrl: 'type.googleapis.com/aevatar.ChatRequestEvent',
            responseTypeUrl: 'type.googleapis.com/aevatar.ChatResponseEvent',
            description: 'Chat with the published scope service.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({ ok: true });
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        type: 'RUN_STARTED',
        runId: 'run-1',
        threadId: 'thread-1',
        timestamp: Date.now(),
      };
      yield {
        type: 'CUSTOM',
        name: 'aevatar.run.context',
        value: {
          actorId: 'actor://scope-a/default',
          commandId: 'cmd-1',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'TEXT_MESSAGE_CONTENT',
        delta: 'hello from scope service',
        messageId: 'msg-1',
        timestamp: Date.now(),
      };
    });

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    await waitFor(() => {
      expect(servicesApi.listServices).toHaveBeenCalledWith({
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
      });
    });
    expect(await screen.findByText('Lab Console')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Stream chat' })).toBeTruthy();
    fireEvent.change(
      screen.getByPlaceholderText('Prompt or payload text'),
      {
        target: { value: 'hello service' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Stream chat' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-a',
        {
          prompt: 'hello service',
        },
        expect.any(Object),
        {
          serviceId: 'default',
        },
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Output' }));

    expect(await screen.findByText('Assistant Output')).toBeTruthy();
    expect(screen.getByText('hello from scope service')).toBeTruthy();
    expect(screen.getByText('run-1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue in Runs' }));

    expect(window.location.pathname).toBe('/runtime/runs');
    const draftKey = new URLSearchParams(window.location.search).get('draftKey');
    expect(draftKey).toBeTruthy();
    expect(loadDraftRunPayload(draftKey)).toEqual(
      expect.objectContaining({
        kind: 'observed_run_session',
        scopeId: 'scope-a',
        serviceOverrideId: 'default',
        endpointId: 'chat',
        actorId: 'actor://scope-a/default',
        commandId: 'cmd-1',
        runId: 'run-1',
        events: [
          expect.objectContaining({
            type: 'RUN_STARTED',
            runId: 'run-1',
          }),
          expect.objectContaining({
            type: 'CUSTOM',
          }),
          expect.objectContaining({
            type: 'TEXT_MESSAGE_CONTENT',
            delta: 'hello from scope service',
          }),
        ],
      }),
    );
  });

  it('keeps the invoke lab empty after reset instead of auto-refilling a service', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'run',
            kind: 'command',
            requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
            responseTypeUrl: 'type.googleapis.com/google.protobuf.Empty',
            description: 'Submit a command run.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    expect(await screen.findByRole('button', { name: 'Invoke endpoint' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));

    await waitFor(() => {
      expect(
        screen.getByText('No published project service is selected yet.'),
      ).toBeTruthy();
    });

    expect(screen.getByRole('button', { name: 'Invoke endpoint' })).toBeDisabled();
    expect(new URLSearchParams(window.location.search).get('serviceId')).toBeNull();
    expect(new URLSearchParams(window.location.search).get('endpointId')).toBeNull();
  });

  it('opens the service runtime workbench with bindings, revisions, and runs', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'chat',
            kind: 'chat',
            requestTypeUrl: 'type.googleapis.com/aevatar.ChatRequestEvent',
            responseTypeUrl: 'type.googleapis.com/aevatar.ChatResponseEvent',
            description: 'Chat with the published scope service.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    expect(await screen.findByText('Invoke Lab')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Browse services' }));

    expect(await screen.findByText('Published Services')).toBeTruthy();
    expect(await screen.findByRole('tab', { name: /Bindings \(1\)/i })).toBeTruthy();
    expect(await screen.findByRole('tab', { name: /Revisions \(1\)/i })).toBeTruthy();
    expect(await screen.findByRole('tab', { name: /Runs \(1\)/i })).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: /Bindings \(1\)/i }));
    expect(await screen.findByText('Knowledge base')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: /Runs \(1\)/i }));
    expect(await screen.findByText('run-42')).toBeTruthy();
    const auditButton = screen
      .getAllByRole('button')
      .find((button) => /Load audit|Inspecting/i.test(button.textContent || ''));
    expect(auditButton).toBeTruthy();
    fireEvent.click(auditButton as HTMLElement);

    expect(await screen.findByText('Run Audit')).toBeTruthy();
    expect(await screen.findByText('Resolved successfully')).toBeTruthy();
    expect(await screen.findByText('Timeline Highlights')).toBeTruthy();
  });
});
