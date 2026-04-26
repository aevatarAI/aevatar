import {
  buildStudioBindContract,
  buildStudioBindInvokePath,
  buildStudioBindInvokeUrl,
} from './bindContract';

describe('bindContract', () => {
  it('builds the streaming invoke path for chat endpoints', () => {
    expect(
      buildStudioBindInvokePath('scope-1', 'chat', 'default', {
        endpointId: 'chat',
        kind: 'chat',
      }),
    ).toBe('/api/scopes/scope-1/services/default/invoke/chat:stream');
  });

  it('builds a bind contract from the selected service and endpoint', () => {
    const contract = buildStudioBindContract({
      authSession: {
        enabled: true,
        authenticated: true,
        name: 'Abigail Deng',
        email: 'abigail@example.com',
        scopeId: 'scope-1',
        scopeSource: 'nyxid',
      },
      endpoint: {
        endpointId: 'chat',
        displayName: 'Chat',
        kind: 'chat',
        requestTypeUrl: '',
        responseTypeUrl: '',
        description: 'Chat with the member.',
      },
      origin: 'https://console.example.test',
      revision: {
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
      scopeId: 'scope-1',
      service: {
        serviceKey: 'scope-1:default:workspace-demo',
        tenantId: 'scope-1',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'workspace-demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'dep-2',
        primaryActorId: 'actor-default',
        deploymentStatus: 'Active',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    });

    expect(contract).toMatchObject({
      authLabel: 'Authenticated',
      deploymentStatus: 'Active',
      endpointDisplayName: 'Chat',
      invokePath: '/api/scopes/scope-1/services/default/invoke/chat:stream',
      invokeUrl:
        'https://console.example.test/api/scopes/scope-1/services/default/invoke/chat:stream',
      revisionId: 'rev-2',
      scopeLabel: 'scope-1',
      scopeSource: 'nyxid',
      serviceDisplayName: 'workspace-demo',
      serviceId: 'default',
      serviceKey: 'scope-1:default:workspace-demo',
    });
    expect(contract?.streaming).toEqual({
      aguiFrames: true,
      sse: true,
      webSocket: false,
    });
    expect(
      buildStudioBindInvokeUrl(
        'scope-1',
        'chat',
        'default',
        { endpointId: 'chat', kind: 'chat' },
        'https://console.example.test',
      ),
    ).toBe(
      'https://console.example.test/api/scopes/scope-1/services/default/invoke/chat:stream',
    );
  });
});
