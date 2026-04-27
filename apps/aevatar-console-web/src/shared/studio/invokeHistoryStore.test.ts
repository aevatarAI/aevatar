import {
  buildStudioInvokeHistoryStorageKey,
  loadStudioInvokeHistory,
  STUDIO_INVOKE_HISTORY_LIMIT,
  saveStudioInvokeHistory,
} from './invokeHistoryStore';

function installSessionStorageMock(): void {
  const state = new Map<string, string>();
  const sessionStorageMock: Storage = {
    get length() {
      return state.size;
    },
    clear: () => {
      state.clear();
    },
    getItem: (key: string) =>
      state.has(String(key)) ? (state.get(String(key)) as string) : null,
    key: (index: number) => Array.from(state.keys())[index] ?? null,
    removeItem: (key: string) => {
      state.delete(String(key));
    },
    setItem: (key: string, value: string) => {
      state.set(String(key), String(value));
    },
  };

  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { sessionStorage: sessionStorageMock },
  });
}

describe('studio invoke history store', () => {
  beforeEach(() => {
    installSessionStorageMock();
  });

  afterAll(() => {
    Reflect.deleteProperty(
      globalThis as unknown as Record<string, unknown>,
      'window',
    );
  });

  it('builds storage keys scoped by scopeId and member key', () => {
    expect(
      buildStudioInvokeHistoryStorageKey({
        scopeId: 'scope-1',
        memberKey: 'workflow:workflow-1',
      }),
    ).toBe('aevatar-studio-invoke-history:scope-1::workflow:workflow-1');
  });

  it('returns an empty key when scope or member is missing', () => {
    expect(
      buildStudioInvokeHistoryStorageKey({
        scopeId: '',
        memberKey: 'workflow:workflow-1',
      }),
    ).toBe('');
    expect(
      buildStudioInvokeHistoryStorageKey({
        scopeId: 'scope-1',
        memberKey: '',
      }),
    ).toBe('');
  });

  it('round-trips invoke history entries through sessionStorage', () => {
    const key = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'workflow:workflow-1',
    });
    const entries = [
      { id: 'a', prompt: 'hello' },
      { id: 'b', prompt: 'world' },
    ];

    saveStudioInvokeHistory(key, entries);

    expect(
      loadStudioInvokeHistory<{ id: string; prompt: string }>(key),
    ).toEqual(entries);
  });

  it('caps persisted history at the documented limit so transcripts stay scrollable', () => {
    const key = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'workflow:workflow-1',
    });
    const overflow = Array.from({
      length: STUDIO_INVOKE_HISTORY_LIMIT + 4,
    }).map((_unused, index) => ({ id: `entry-${index}` }));

    saveStudioInvokeHistory(key, overflow);

    expect(loadStudioInvokeHistory(key)).toHaveLength(
      STUDIO_INVOKE_HISTORY_LIMIT,
    );
  });

  it('keeps history isolated between members so switching members does not bleed transcripts', () => {
    const workflowKey = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'workflow:workflow-1',
    });
    const scriptKey = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'script:script-1',
    });

    saveStudioInvokeHistory(workflowKey, [{ id: 'workflow-only' }]);

    expect(loadStudioInvokeHistory(workflowKey)).toEqual([
      { id: 'workflow-only' },
    ]);
    expect(loadStudioInvokeHistory(scriptKey)).toEqual([]);
  });

  it('returns an empty list when stored data is invalid', () => {
    const key = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'workflow:workflow-1',
    });
    (globalThis as { window: Window }).window.sessionStorage.setItem(
      key,
      'not-json',
    );

    expect(loadStudioInvokeHistory(key)).toEqual([]);
  });

  it('clears the stored entry when an empty history is saved', () => {
    const key = buildStudioInvokeHistoryStorageKey({
      scopeId: 'scope-1',
      memberKey: 'workflow:workflow-1',
    });
    saveStudioInvokeHistory(key, [{ id: 'a' }]);
    saveStudioInvokeHistory(key, []);

    expect(
      (globalThis as { window: Window }).window.sessionStorage.getItem(key),
    ).toBeNull();
  });
});
