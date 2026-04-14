import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { loadDraftRunPayload } from '@/shared/runs/draftRunSession';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeInvokePage, { buildServiceOptions } from './invoke';

jest.mock('@ant-design/pro-components', () => {
  const mockReact = require('react');

  const ProCard = ({ children, extra, title }: any) =>
    mockReact.createElement(
      'section',
      null,
      title ? mockReact.createElement('div', null, title) : null,
      extra ? mockReact.createElement('div', null, extra) : null,
      children,
    );

  const PageContainer = ({
    children,
    content,
    extra,
    pageHeaderRender,
    title,
  }: any) =>
    mockReact.createElement(
      'section',
      null,
      pageHeaderRender === false
        ? null
        : title
          ? mockReact.createElement('h1', null, title)
          : null,
      content ? mockReact.createElement('div', null, content) : null,
      Array.isArray(extra) ? mockReact.createElement('div', null, extra) : null,
      children,
    );

  return {
    PageContainer,
    ProCard,
    ProConfigProvider: ({ children }: any) =>
      mockReact.createElement(mockReact.Fragment, null, children),
  };
});

jest.mock('@/shared/ui/aevatarPageShells', () => {
  const mockReact = require('react');

  return {
    AevatarContextDrawer: ({ children, open }: any) =>
      open ? mockReact.createElement('section', null, children) : null,
    AevatarHelpTooltip: () =>
      mockReact.createElement('button', {
        'aria-label': 'Show help',
        type: 'button',
      }),
    AevatarInspectorEmpty: ({ description, title }: any) =>
      mockReact.createElement(
        'section',
        null,
        title ? mockReact.createElement('div', null, title) : null,
        description ? mockReact.createElement('div', null, description) : null,
      ),
    AevatarPanel: ({ children, description, extra, title }: any) =>
      mockReact.createElement(
        'section',
        null,
        title ? mockReact.createElement('div', null, title) : null,
        description ? mockReact.createElement('div', null, description) : null,
        extra ? mockReact.createElement('div', null, extra) : null,
        children,
      ),
    AevatarPageShell: ({ children }: any) =>
      mockReact.createElement('section', null, children),
    AevatarStatusTag: ({ status }: any) =>
      mockReact.createElement('span', null, status),
  };
});

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    bindScopeGAgent: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'nyxid-chat',
      displayName: 'NyxID Chat',
      revisionId: 'rev-nyxid',
      targetKind: 'gagent',
      implementationKind: 'gagent',
      deploymentId: 'deploy-nyxid',
      deploymentStatus: 'Active',
      updatedAt: '2026-03-26T08:00:00Z',
      gAgent: {
        actorTypeName: 'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent',
        preferredActorId: 'NyxIdChat:scope-a',
      },
    })),
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

jest.mock('./components/ScopeServiceRuntimeWorkbench', () => {
  const mockReact = require('react');

  const ScopeServiceRuntimeWorkbench = (props: any) => {
    const [activeTab, setActiveTab] = mockReact.useState('overview');
    const [auditOpen, setAuditOpen] = mockReact.useState(false);

    return mockReact.createElement(
      'section',
      null,
      mockReact.createElement('div', null, 'Published Services'),
      mockReact.createElement(
        'div',
        {
          role: 'tablist',
        },
        ['Bindings (1)', 'Revisions (1)', 'Runs (1)'].map((label) =>
          mockReact.createElement(
            'button',
            {
              'aria-selected':
                activeTab === label.toLowerCase().split(' ')[0] ? 'true' : 'false',
              key: label,
              onClick: () => {
                const nextTab = label.toLowerCase().split(' ')[0];
                setActiveTab(nextTab);
              },
              role: 'tab',
              type: 'button',
            },
            label,
          ),
        ),
      ),
      mockReact.createElement(
        'div',
        null,
        props.selectedServiceId
          ? `Selected service: ${props.selectedServiceId}`
          : 'Selected service: none',
      ),
      activeTab === 'bindings'
        ? mockReact.createElement('div', null, 'Knowledge base')
        : null,
      activeTab === 'revisions'
        ? mockReact.createElement('div', null, 'rev-2')
        : null,
      activeTab === 'runs'
        ? mockReact.createElement(
            mockReact.Fragment,
            null,
            mockReact.createElement('div', null, 'run-42'),
            mockReact.createElement(
              'button',
              {
                onClick: () => setAuditOpen(true),
                type: 'button',
              },
              'Load audit',
            ),
          )
        : null,
      auditOpen
        ? mockReact.createElement(
            'section',
            null,
            mockReact.createElement('div', null, 'Run Audit'),
            mockReact.createElement('div', null, 'Resolved successfully'),
            mockReact.createElement('div', null, 'Timeline Highlights'),
          )
        : null,
    );
  };

  return {
    __esModule: true,
    default: ScopeServiceRuntimeWorkbench,
  };
});

import { servicesApi } from '@/shared/api/servicesApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { studioApi } from '@/shared/studio/api';

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

  it('prepends the built-in NyxID Chat service and maps published services', () => {
    const result = buildServiceOptions(
      [
        {
          serviceKey: 'scope-a:default:default:hello-chat',
          tenantId: 'scope-a',
          appId: 'default',
          namespace: 'default',
          serviceId: 'hello-chat',
          displayName: 'hello-chat',
          defaultServingRevisionId: 'rev-2',
          activeServingRevisionId: 'rev-2',
          deploymentId: 'deploy-2',
          primaryActorId: 'actor://scope-a/hello-chat/2',
          deploymentStatus: 'Active',
          endpoints: [
            {
              endpointId: 'chat',
              displayName: 'chat',
              kind: 'chat',
              requestTypeUrl: 'type.googleapis.com/aevatar.ChatRequestEvent',
              responseTypeUrl: 'type.googleapis.com/aevatar.ChatResponseEvent',
              description: 'Chat endpoint.',
            },
            {
              endpointId: 'health',
              displayName: 'health',
              kind: 'command',
              requestTypeUrl: 'type.googleapis.com/google.protobuf.Empty',
              responseTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
              description: 'Health endpoint.',
            },
          ],
          policyIds: [],
          updatedAt: '2026-04-09T09:30:00Z',
        },
      ],
      'hello-chat',
    );

    expect(result).toEqual([
      expect.objectContaining({
        serviceId: 'nyxid-chat',
        kind: 'nyxid-chat',
      }),
      expect.objectContaining({
        serviceId: 'hello-chat',
        kind: 'service',
        primaryActorId: 'actor://scope-a/hello-chat/2',
      }),
    ]);
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
    expect(await screen.findByText('Legacy Invoke Lab')).toBeTruthy();
    expect(
      await screen.findByText(
        'Team home is now the primary surface. Use this legacy lab when you need direct endpoint probes, raw payload testing, or older deep links.',
      ),
    ).toBeTruthy();
    expect(
      screen.queryByText(
        'Invoke Lab keeps parameters on the left, execution on the main stage, and deeper context in the drawer or lab console.',
      ),
    ).toBeNull();
    expect(
      screen.queryByText(
        'Load the current scope, then choose the published service and endpoint you want to probe.',
      ),
    ).toBeNull();
    expect(await screen.findByText('Playground')).toBeTruthy();
    expect(await screen.findByText('Inspector')).toBeTruthy();
    expect(
      screen.getAllByRole('button', { name: 'Browse services' }).length,
    ).toBeGreaterThan(0);
    const invokeButton = await screen.findByRole('button', {
      name: 'Invoke endpoint',
    });
    await waitFor(() => {
      expect(invokeButton).not.toBeDisabled();
    });
    fireEvent.change(screen.getByPlaceholderText('Prompt or payload text'), {
      target: { value: 'run the scope command' },
    });
    fireEvent.click(invokeButton);

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
    const promptInput = await screen.findByPlaceholderText(
      'Describe the task, ask a question, or paste the next operator instruction.',
    );
    fireEvent.change(promptInput, {
      target: { value: 'hello service' },
    });
    fireEvent.click(await screen.findByLabelText('Send'));

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

    expect(screen.getByRole('button', { name: 'Chat' })).toBeTruthy();
    expect(
      screen.queryByRole('button', { name: 'Output' }),
    ).toBeNull();
    expect(await screen.findByText('hello from scope service')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Observed Events' }));
    expect(await screen.findByText('Observed Events (3)')).toBeTruthy();
    expect(await screen.findByText('Latest raw payloads')).toBeTruthy();
    expect(screen.getByText('run-1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue in Runs' }));

    expect(window.location.pathname).toBe('/runtime/runs');
    const draftKey = new URLSearchParams(window.location.search).get(
      'draftKey',
    );
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

  it('defaults to NyxID Chat when no published service is listed for the scope', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([]);
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({ ok: true });
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        type: 'RUN_STARTED',
        runId: 'run-nyxid',
        threadId: 'thread-nyxid',
        timestamp: Date.now(),
      };
      yield {
        type: 'TEXT_MESSAGE_CONTENT',
        delta: 'hello from NyxID Chat',
        messageId: 'msg-nyxid',
        timestamp: Date.now(),
      };
    });

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    expect((await screen.findAllByText('NyxID Chat')).length).toBeGreaterThan(0);
    expect(screen.getByText('/ Chat')).toBeTruthy();
    expect(screen.queryByText('Prompt / Payload')).toBeNull();
    expect(
      screen.queryByText('No published project service is selected yet.'),
    ).toBeNull();

    const promptInput = await screen.findByPlaceholderText(
      'Describe the task, ask a question, or paste the next operator instruction.',
    );
    fireEvent.change(promptInput, {
      target: { value: 'hello nyxid' },
    });
    fireEvent.click(await screen.findByLabelText('Send'));

    await waitFor(() => {
      expect(studioApi.bindScopeGAgent).toHaveBeenCalledWith({
        actorTypeName: 'Aevatar.GAgents.NyxidChat.NyxIdChatGAgent',
        displayName: 'NyxID Chat',
        endpoints: [
          {
            description:
              'Chat with NyxID about services, credentials, and configuration.',
            displayName: 'Chat',
            endpointId: 'chat',
            kind: 'chat',
          },
        ],
        scopeId: 'scope-a',
        serviceId: 'nyxid-chat',
      });
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-a',
        {
          prompt: 'hello nyxid',
        },
        expect.any(Object),
        {
          serviceId: 'nyxid-chat',
        },
      );
    });

    expect(await screen.findByText('hello from NyxID Chat')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Observed Events' }));
    expect(await screen.findByText('Observed Events (2)')).toBeTruthy();
    expect(await screen.findByText('Latest raw payloads')).toBeTruthy();
  });

  it('keeps the invoke lab workspace constrained so the chat composer stays visible', async () => {
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

    const viewport = await screen.findByTestId('invoke-lab-workspace-viewport');
    const grid = await screen.findByTestId('invoke-lab-workspace-grid');

    expect(viewport).toHaveStyle({
      flex: '1 1 auto',
      minHeight: '0',
      overflow: 'hidden',
    });
    expect(grid).toHaveStyle({
      alignItems: 'stretch',
      height: '100%',
      minHeight: '0',
    });
    expect(
      await screen.findByPlaceholderText(
        'Describe the task, ask a question, or paste the next operator instruction.',
      ),
    ).toBeTruthy();
  });

  it('renders semantic chat output for reasoning, steps, and tool activity', async () => {
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
        runId: 'run-semantic',
        threadId: 'thread-semantic',
        timestamp: Date.now(),
      };
      yield {
        type: 'CUSTOM',
        name: 'aevatar.llm.reasoning',
        value: {
          delta: 'Inspecting bindings and preparing the answer.',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'CUSTOM',
        name: 'aevatar.step.request',
        value: {
          stepId: 'Inspecting the request',
          stepType: 'analysis',
          input: 'hello service',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'TOOL_CALL_START',
        toolName: 'knowledge.search',
        toolCallId: 'tool-1',
        timestamp: Date.now(),
      };
      yield {
        type: 'TOOL_CALL_END',
        toolName: 'knowledge.search',
        toolCallId: 'tool-1',
        result: 'Found the binding details.',
        timestamp: Date.now(),
      };
      yield {
        type: 'CUSTOM',
        name: 'aevatar.step.completed',
        value: {
          stepId: 'Inspecting the request',
          success: true,
          output: 'Binding looks healthy.',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'TEXT_MESSAGE_CONTENT',
        delta: 'Binding looks healthy.',
        messageId: 'msg-semantic',
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
    const promptInput = await screen.findByPlaceholderText(
      'Describe the task, ask a question, or paste the next operator instruction.',
    );
    fireEvent.change(promptInput, {
      target: { value: 'hello service' },
    });
    fireEvent.click(await screen.findByLabelText('Send'));
    expect(
      await screen.findByText(/Binding looks healthy\./),
    ).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'Thinking' })[0]);
    expect(
      await screen.findByText('Inspecting bindings and preparing the answer.'),
    ).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: /2 actions/i })[0]);
    expect(await screen.findByText('Inspecting the request')).toBeTruthy();
    expect(await screen.findByText('knowledge.search')).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'knowledge.search' })[0]);
    expect(await screen.findByText('Found the binding details.')).toBeTruthy();
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

    await screen.findByText('Prompt / Payload');

    await waitFor(() => {
      expect(
        screen.getByRole('button', { name: 'Invoke endpoint' }),
      ).not.toBeDisabled();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Reset' }));

    await waitFor(() => {
      expect(
        screen.getByText('No published team service is selected yet.'),
      ).toBeTruthy();
    });

    expect(
      screen.getByRole('button', { name: 'Invoke endpoint' }),
    ).toBeDisabled();
    expect(
      new URLSearchParams(window.location.search).get('serviceId'),
    ).toBeNull();
    expect(
      new URLSearchParams(window.location.search).get('endpointId'),
    ).toBeNull();
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

    expect(await screen.findByText('Legacy Invoke Lab')).toBeTruthy();
    fireEvent.click(
      screen.getAllByRole('button', { name: 'Browse services' })[0],
    );

    expect(await screen.findByText('Published Services')).toBeTruthy();
    expect(
      await screen.findByRole('tab', { name: /Bindings \(1\)/i }),
    ).toBeTruthy();
    expect(
      await screen.findByRole('tab', { name: /Revisions \(1\)/i }),
    ).toBeTruthy();
    expect(
      await screen.findByRole('tab', { name: /Runs \(1\)/i }),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: /Bindings \(1\)/i }));
    expect(await screen.findByText('Knowledge base')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: /Runs \(1\)/i }));
    expect(await screen.findByText('run-42')).toBeTruthy();
    const auditButton = screen
      .getAllByRole('button')
      .find((button) =>
        /Load audit|Inspecting/i.test(button.textContent || ''),
      );
    expect(auditButton).toBeTruthy();
    fireEvent.click(auditButton as HTMLElement);

    expect(await screen.findByText('Run Audit')).toBeTruthy();
    expect(await screen.findByText('Resolved successfully')).toBeTruthy();
    expect(await screen.findByText('Timeline Highlights')).toBeTruthy();
  });
});
