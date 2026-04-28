import { afterEach, describe, expect, it, vi } from 'vitest';
import { buildScopeServiceQuery, gagent, nyxidChat } from './api';

function jsonResponse(body: unknown) {
  return {
    ok: true,
    status: 200,
    headers: new Headers({ 'content-type': 'application/json' }),
    json: async () => body,
  } as Response;
}

describe('scope service query helpers', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('maps scope id onto the pinned scope-service identity defaults', () => {
    const query = buildScopeServiceQuery(' scope-a ', { take: '20' });
    const params = new URLSearchParams(query);

    expect(params.get('tenantId')).toBe('scope-a');
    expect(params.get('appId')).toBe('default');
    expect(params.get('namespace')).toBe('default');
    expect(params.get('take')).toBe('20');
  });

  it('keeps extra parameters when no scope fallback is available', () => {
    const query = buildScopeServiceQuery('', { take: '20' });
    const params = new URLSearchParams(query);

    expect(params.get('tenantId')).toBeNull();
    expect(params.get('appId')).toBeNull();
    expect(params.get('namespace')).toBeNull();
    expect(params.get('take')).toBe('20');
  });

  it('decodes gagent actor groups from registry snapshot response shape', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      scopeId: 'scope-a',
      stateVersion: 42,
      groups: [
        {
          gAgentType: 'Tests.OrdersGAgent',
          actorIds: ['orders-1', 'orders-2'],
        },
      ],
    }));

    await expect(gagent.listActors('scope-a')).resolves.toEqual([
      {
        gAgentType: 'Tests.OrdersGAgent',
        actorIds: ['orders-1', 'orders-2'],
      },
    ]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-a/gagent-actors',
      expect.objectContaining({ credentials: 'include' }),
    );
  });

  it('rejects legacy gagent actor group arrays', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse([
      {
        gAgentType: 'Tests.OrdersGAgent',
        actorIds: ['orders-1'],
      },
    ]));

    await expect(gagent.listActors('scope-a')).rejects.toThrow(/groups/i);
  });

  it('decodes nyxid conversations from registry snapshot response shape', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({
      scopeId: 'scope-a',
      stateVersion: 42,
      conversations: [
        {
          actorId: 'nyxid-chat-1',
        },
      ],
    }));

    await expect(nyxidChat.listConversations('scope-a')).resolves.toEqual([
      {
        actorId: 'nyxid-chat-1',
      },
    ]);
  });

  it('rejects legacy nyxid conversation arrays', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse([
      {
        actorId: 'nyxid-chat-1',
      },
    ]));

    await expect(nyxidChat.listConversations('scope-a')).rejects.toThrow(/conversations/i);
  });
});
