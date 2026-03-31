import { useEffect, useRef, useState } from 'react';
import { normalizeBackendSseFrame, type RuntimeEvent } from './sseUtils';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';

// ── Types ──

type GAgentType = { typeName: string; fullName: string; assemblyName: string };
type ActorGroup = { gAgentType: string; actorIds: string[] };
type ExtraEndpoint = {
  endpointId: string;
  displayName: string;
  kind: 'command' | 'chat';
  requestTypeUrl: string;
  description: string;
};

// ── GAgent Page ──

export default function GAgentPage() {
  const scopeId = nyxid.loadSession()?.user.sub || '';

  const [types, setTypes] = useState<GAgentType[]>([]);
  const [typesLoading, setTypesLoading] = useState(false);
  const [typesError, setTypesError] = useState('');
  const [selectedType, setSelectedType] = useState<GAgentType | null>(null);

  const [actorGroups, setActorGroups] = useState<ActorGroup[]>([]);

  // Bind state
  const [binding, setBinding] = useState(false);
  const [bindResult, setBindResult] = useState('');
  const [extraEndpoints, setExtraEndpoints] = useState<ExtraEndpoint[]>([]);
  const [newEp, setNewEp] = useState<ExtraEndpoint>({ endpointId: '', displayName: '', kind: 'command', requestTypeUrl: '', description: '' });
  const [showAddEp, setShowAddEp] = useState(false);

  // Draft-run state
  const [actorMode, setActorMode] = useState<'new' | 'existing'>('new');
  const [selectedActorId, setSelectedActorId] = useState('');
  const [manualActorId, setManualActorId] = useState('');
  const [prompt, setPrompt] = useState('');
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [streamText, setStreamText] = useState('');
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState('');
  const [lastActorId, setLastActorId] = useState('');
  const abortRef = useRef<AbortController | null>(null);

  // ── Data loading ──

  useEffect(() => {
    loadTypes();
    if (scopeId) loadActors();
  }, [scopeId]);

  async function loadTypes() {
    setTypesLoading(true);
    setTypesError('');
    try {
      const data = await api.gagent.listTypes();
      setTypes(data ?? []);
    } catch (e: any) {
      setTypesError(e?.message || 'Failed to load GAgent types');
    } finally {
      setTypesLoading(false);
    }
  }

  async function loadActors() {
    try {
      const data = await api.gagent.listActors(scopeId);
      setActorGroups(data ?? []);
    } catch {
      // Storage may not be configured
    }
  }

  const currentActorIds = selectedType
    ? (actorGroups.find(g => g.gAgentType === selectedType.fullName)?.actorIds ?? [])
    : [];

  const effectiveActorId =
    actorMode === 'existing'
      ? selectedActorId || manualActorId
      : undefined;

  // ── Bind as service ──

  async function handleBind() {
    if (!selectedType || !scopeId) return;
    setBinding(true);
    setBindResult('');
    try {
      const res = await fetch(`/api/scopes/${encodeURIComponent(scopeId)}/binding`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${nyxid.getAccessToken() || ''}`,
        },
        body: JSON.stringify({
          implementationKind: 'gagent',
          gAgent: {
            actorTypeName: `${selectedType.fullName}, ${selectedType.assemblyName}`,
            preferredActorId: effectiveActorId || null,
            // Backend always injects a default chat endpoint; extra endpoints are appended.
            endpoints: extraEndpoints.map(ep => ({
              endpointId: ep.endpointId,
              displayName: ep.displayName || ep.endpointId,
              kind: ep.kind,
              requestTypeUrl: ep.requestTypeUrl || null,
              responseTypeUrl: null,
              description: ep.description || null,
            })),
          },
        }),
      });
      if (!res.ok) {
        const body = await res.json().catch(() => ({}));
        throw new Error(body?.message || `Bind failed (${res.status})`);
      }
      setBindResult(`Bound "${selectedType.typeName}" as scope service.`);
    } catch (e: any) {
      setBindResult(`Error: ${e?.message || String(e)}`);
    } finally {
      setBinding(false);
    }
  }

  function handleAddEndpoint() {
    if (!newEp.endpointId.trim()) return;
    setExtraEndpoints(prev => [...prev, { ...newEp, endpointId: newEp.endpointId.trim() }]);
    setNewEp({ endpointId: '', displayName: '', kind: 'command', requestTypeUrl: '', description: '' });
    setShowAddEp(false);
  }

  function handleRemoveEndpoint(id: string) {
    setExtraEndpoints(prev => prev.filter(ep => ep.endpointId !== id));
  }

  // ── Draft-run ──

  async function handleDraftRun() {
    if (!prompt.trim() || !scopeId || !selectedType) return;
    setRunning(true);
    setEvents([]);
    setStreamText('');
    setRunError('');
    setLastActorId('');

    const controller = new AbortController();
    abortRef.current = controller;

    try {
      await api.gagent.streamDraftRun(
        scopeId,
        selectedType.fullName,
        prompt.trim(),
        effectiveActorId?.trim() || undefined,
        (frame: any) => {
          const evt = normalizeBackendSseFrame(frame);
          if (!evt) return;
          setEvents(prev => [...prev, evt]);

          if (evt.type === 'TEXT_MESSAGE_CONTENT') {
            setStreamText(prev => prev + (evt.delta as string || ''));
          }
          if (evt.type === 'RUN_STARTED' && evt.threadId) {
            const actorId = evt.threadId as string;
            setLastActorId(actorId);
            api.gagent.addActor(scopeId, selectedType.fullName, actorId).then(() => loadActors()).catch(() => {});
          }
          if (evt.type === 'RUN_ERROR') {
            setRunError((evt.message as string) || 'Run error');
          }
        },
        controller.signal,
      );
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        setRunError(e?.message || String(e));
      }
    } finally {
      setRunning(false);
      abortRef.current = null;
    }
  }

  function handleStop() {
    abortRef.current?.abort();
  }

  const runId = events.find(e => e.type === 'RUN_STARTED')?.runId as string | undefined;
  const finished = events.some(e => e.type === 'RUN_FINISHED');

  // ── Render ──

  return (
    <>
      {/* Header */}
      <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center justify-between gap-4">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Service Binding</div>
          <div className="text-[18px] font-bold text-gray-800 mt-0.5">GAgents</div>
        </div>
        <div className="flex items-center gap-3">
          <label className="text-[12px] font-semibold text-gray-500">Scope</label>
          <span className="rounded-lg border border-[#E6E3DE] bg-gray-50 px-3 py-1.5 text-[13px] w-48 truncate text-gray-600">
            {scopeId || <span className="text-amber-600">Not logged in</span>}
          </span>
        </div>
      </header>

      {/* Body — split: left type list, right detail/actions */}
      <div className="flex-1 min-h-0 flex bg-[#F2F1EE]">

        {/* ─── Left: Type Catalog ─── */}
        <aside className="w-[320px] flex-shrink-0 border-r border-[#E6E3DE] bg-white flex flex-col">
          <div className="px-4 py-3 border-b border-[#E6E3DE] flex items-center justify-between">
            <span className="text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Available Types</span>
            <button
              onClick={loadTypes}
              disabled={typesLoading}
              className="text-[11px] text-blue-600 hover:underline disabled:opacity-40"
            >
              {typesLoading ? 'Loading...' : 'Refresh'}
            </button>
          </div>

          {typesError && (
            <div className="mx-3 mt-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-[12px] text-red-700">{typesError}</div>
          )}

          <div className="flex-1 min-h-0 overflow-auto">
            {types.length === 0 && !typesLoading && !typesError && (
              <div className="px-4 py-6 text-center text-[13px] text-gray-400">
                No GAgent types found.<br />
                Make sure the backend has AIGAgentBase-derived classes loaded.
              </div>
            )}
            {types.map(t => (
              <button
                key={t.fullName}
                onClick={() => {
                  setSelectedType(t);
                  setSelectedActorId('');
                  setManualActorId('');
                  setBindResult('');
                  setStreamText('');
                  setEvents([]);
                  setRunError('');
                  setLastActorId('');
                  setExtraEndpoints([]);
                  setShowAddEp(false);
                }}
                className={`w-full text-left px-4 py-3 border-b border-[#F0EDE8] transition-colors ${
                  selectedType?.fullName === t.fullName
                    ? 'bg-blue-50 border-l-2 border-l-blue-500'
                    : 'hover:bg-[#F7F5F2]'
                }`}
              >
                <div className="text-[13px] font-semibold text-gray-800">{t.typeName}</div>
                <div className="text-[11px] text-gray-400 mt-0.5 truncate">{t.fullName}</div>
                <div className="text-[10px] text-gray-300 mt-0.5">{t.assemblyName}</div>
              </button>
            ))}
          </div>
        </aside>

        {/* ─── Right: Detail & Actions ─── */}
        <div className="flex-1 min-h-0 overflow-auto p-6 space-y-5">

          {!selectedType ? (
            <div className="flex items-center justify-center h-full text-[14px] text-gray-400">
              Select a GAgent type from the left panel
            </div>
          ) : (
            <>
              {/* Selected type info */}
              <div className="rounded-xl border border-[#E6E3DE] bg-white p-5">
                <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400 mb-1">Selected Type</div>
                <div className="text-[16px] font-bold text-gray-800">{selectedType.typeName}</div>
                <div className="text-[12px] text-gray-500 mt-0.5 font-mono">{selectedType.fullName}</div>
              </div>

              {/* Actor selection */}
              <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-3">
                <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400">Actor</div>
                <div className="flex gap-4 items-center">
                  <label className="flex items-center gap-1.5 text-[13px] text-gray-700 cursor-pointer">
                    <input
                      type="radio" name="actorMode" checked={actorMode === 'new'}
                      onChange={() => setActorMode('new')} className="accent-[#18181B]"
                    />
                    Create New
                  </label>
                  <label className="flex items-center gap-1.5 text-[13px] text-gray-700 cursor-pointer">
                    <input
                      type="radio" name="actorMode" checked={actorMode === 'existing'}
                      onChange={() => setActorMode('existing')} className="accent-[#18181B]"
                    />
                    Reuse Existing
                  </label>
                </div>

                {actorMode === 'existing' && (
                  <div className="space-y-2">
                    {currentActorIds.length > 0 ? (
                      <select
                        className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                        value={selectedActorId}
                        onChange={e => setSelectedActorId(e.target.value)}
                      >
                        <option value="">Select a saved actor...</option>
                        {currentActorIds.map(id => (
                          <option key={id} value={id}>{id}</option>
                        ))}
                      </select>
                    ) : null}
                    <input
                      className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                      value={manualActorId}
                      onChange={e => setManualActorId(e.target.value)}
                      placeholder="Or enter actor ID manually..."
                    />
                  </div>
                )}
              </div>

              {/* Actions: Bind / Draft-Run */}
              <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-4">
                {/* Bind as Service */}
                <div>
                  <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400 mb-2">Bind as Scope Service</div>

                  {/* Endpoints — chat is always auto-added by the backend */}
                  <div className="mb-3 space-y-1">
                    {/* Fixed chat endpoint indicator */}
                    <div className="flex items-center gap-2 rounded-lg bg-green-50 border border-green-200 px-3 py-1.5">
                      <span className="inline-block w-1.5 h-1.5 rounded-full bg-green-400" />
                      <span className="text-[12px] font-mono text-green-700">chat</span>
                      <span className="text-[11px] text-green-500 ml-1">auto · ChatRequestEvent</span>
                    </div>

                    {/* Extra endpoints */}
                    {extraEndpoints.map(ep => (
                      <div key={ep.endpointId} className="flex items-center gap-2 rounded-lg bg-[#F7F5F2] border border-[#E6E3DE] px-3 py-1.5">
                        <span className="inline-block w-1.5 h-1.5 rounded-full bg-blue-400" />
                        <span className="text-[12px] font-mono text-gray-700">{ep.endpointId}</span>
                        <span className="text-[11px] text-gray-400">{ep.kind}</span>
                        {ep.requestTypeUrl && (
                          <span className="text-[11px] text-gray-400 truncate max-w-[160px]">{ep.requestTypeUrl}</span>
                        )}
                        <button
                          onClick={() => handleRemoveEndpoint(ep.endpointId)}
                          className="ml-auto text-gray-300 hover:text-red-500 text-[13px] leading-none"
                          title="Remove"
                        >&times;</button>
                      </div>
                    ))}

                    {/* Add endpoint form */}
                    {showAddEp ? (
                      <div className="rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] p-3 space-y-2">
                        <div className="grid grid-cols-2 gap-2">
                          <input
                            className="rounded border border-[#E6E3DE] bg-white px-2 py-1 text-[12px] font-mono focus:outline-none focus:ring-1 focus:ring-blue-400"
                            placeholder="endpoint-id *"
                            value={newEp.endpointId}
                            onChange={e => setNewEp(v => ({ ...v, endpointId: e.target.value }))}
                          />
                          <select
                            className="rounded border border-[#E6E3DE] bg-white px-2 py-1 text-[12px] focus:outline-none focus:ring-1 focus:ring-blue-400"
                            value={newEp.kind}
                            onChange={e => setNewEp(v => ({ ...v, kind: e.target.value as 'chat' | 'command' }))}
                          >
                            <option value="command">command</option>
                            <option value="chat">chat</option>
                          </select>
                        </div>
                        <input
                          className="w-full rounded border border-[#E6E3DE] bg-white px-2 py-1 text-[12px] font-mono focus:outline-none focus:ring-1 focus:ring-blue-400"
                          placeholder="requestTypeUrl (optional)"
                          value={newEp.requestTypeUrl}
                          onChange={e => setNewEp(v => ({ ...v, requestTypeUrl: e.target.value }))}
                        />
                        <input
                          className="w-full rounded border border-[#E6E3DE] bg-white px-2 py-1 text-[12px] focus:outline-none focus:ring-1 focus:ring-blue-400"
                          placeholder="description (optional)"
                          value={newEp.description}
                          onChange={e => setNewEp(v => ({ ...v, description: e.target.value }))}
                        />
                        <div className="flex gap-2">
                          <button
                            onClick={handleAddEndpoint}
                            disabled={!newEp.endpointId.trim()}
                            className="rounded bg-blue-600 px-3 py-1 text-[12px] font-semibold text-white hover:bg-blue-700 disabled:opacity-40"
                          >Add</button>
                          <button
                            onClick={() => setShowAddEp(false)}
                            className="rounded border border-[#E6E3DE] px-3 py-1 text-[12px] text-gray-500 hover:bg-gray-50"
                          >Cancel</button>
                        </div>
                      </div>
                    ) : (
                      <button
                        onClick={() => setShowAddEp(true)}
                        className="text-[11px] text-blue-500 hover:underline mt-0.5"
                      >+ Add extra endpoint</button>
                    )}
                  </div>

                  <div className="flex items-center gap-3">
                    <button
                      onClick={handleBind}
                      disabled={binding || !scopeId}
                      className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
                    >
                      {binding ? 'Binding...' : 'Bind as Service'}
                    </button>
                    {bindResult && (
                      <span className={`text-[12px] ${bindResult.startsWith('Error') ? 'text-red-600' : 'text-green-600'}`}>
                        {bindResult}
                      </span>
                    )}
                  </div>
                  <p className="text-[11px] text-gray-400 mt-1">
                    Binds this GAgent as the default scope service. The chat endpoint is always included. After binding, use the Scope chat tab to interact.
                  </p>
                </div>

                {/* Draft Run */}
                <div className="border-t border-[#E6E3DE] pt-4">
                  <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400 mb-2">Draft Run</div>
                  <textarea
                    rows={3}
                    className="w-full rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400 mb-2"
                    value={prompt}
                    onChange={e => setPrompt(e.target.value)}
                    placeholder="Enter your prompt to test this GAgent..."
                  />
                  <div className="flex gap-2">
                    <button
                      onClick={handleDraftRun}
                      disabled={running || !prompt.trim() || !scopeId}
                      className="rounded-lg bg-blue-600 px-4 py-2 text-[13px] font-semibold text-white hover:bg-blue-700 disabled:opacity-40"
                    >
                      {running ? 'Running...' : 'Draft Run'}
                    </button>
                    {running && (
                      <button onClick={handleStop} className="rounded-lg border border-red-300 px-4 py-2 text-[13px] text-red-600 hover:bg-red-50">
                        Stop
                      </button>
                    )}
                  </div>
                  <p className="text-[11px] text-gray-400 mt-1">
                    Creates an ad-hoc actor and sends the prompt directly. Does not affect scope binding.
                  </p>
                </div>
              </div>

              {/* Run output */}
              {(runError || runId || streamText) && (
                <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-3">
                  <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400">Run Output</div>

                  {runError && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{runError}</div>
                  )}

                  {runId && (
                    <div className="text-[12px] text-gray-500">
                      Run: <span className="font-mono">{runId}</span>
                      {lastActorId && <span className="ml-3">Actor: <span className="font-mono">{lastActorId}</span></span>}
                      {finished && <span className="ml-2 text-green-600 font-semibold">Finished</span>}
                    </div>
                  )}

                  {streamText && (
                    <pre className="whitespace-pre-wrap rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] p-4 text-[13px] leading-6 max-h-[400px] overflow-auto">
                      {streamText}
                    </pre>
                  )}
                </div>
              )}

              {/* Saved Actors */}
              {currentActorIds.length > 0 && (
                <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-3">
                  <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400">
                    Saved Actors for {selectedType.typeName} ({currentActorIds.length})
                  </div>
                  <div className="flex flex-wrap gap-2">
                    {currentActorIds.map(id => (
                      <span key={id} className="inline-flex items-center gap-1.5 rounded-lg bg-[#F0EDE8] px-3 py-1.5 text-[12px] font-mono text-gray-700">
                        {id}
                        <button
                          onClick={() => {
                            api.gagent.removeActor(scopeId, selectedType.fullName, id).then(() => loadActors()).catch(() => {});
                          }}
                          className="text-gray-400 hover:text-red-500"
                          title="Remove"
                        >
                          &times;
                        </button>
                      </span>
                    ))}
                  </div>
                </div>
              )}

              {/* All Actor Groups */}
              {actorGroups.length > 1 && (
                <details className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden">
                  <summary className="px-5 py-3 text-[10px] font-semibold text-gray-400 uppercase tracking-wider cursor-pointer hover:bg-gray-50">
                    All Saved Actors ({actorGroups.reduce((n, g) => n + g.actorIds.length, 0)} total)
                  </summary>
                  <div className="px-5 pb-4 space-y-3">
                    {actorGroups.map(g => (
                      <div key={g.gAgentType}>
                        <div className="text-[12px] font-semibold text-gray-600 mb-1">{g.gAgentType}</div>
                        <div className="flex flex-wrap gap-1">
                          {g.actorIds.map(id => (
                            <span key={id} className="inline-block rounded bg-[#F0EDE8] px-2 py-0.5 text-[11px] font-mono text-gray-600">
                              {id}
                            </span>
                          ))}
                        </div>
                      </div>
                    ))}
                  </div>
                </details>
              )}
            </>
          )}
        </div>
      </div>
    </>
  );
}
