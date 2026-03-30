import { useEffect, useRef, useState } from 'react';
import { normalizeBackendSseFrame, type RuntimeEvent } from './sseUtils';
import * as api from '../api';

type GAgentType = { typeName: string; fullName: string; assemblyName: string };
type ActorGroup = { gAgentType: string; actorIds: string[] };

export default function GAgentTab(props: { scopeId: string }) {
  const [types, setTypes] = useState<GAgentType[]>([]);
  const [typesLoading, setTypesLoading] = useState(false);
  const [selectedType, setSelectedType] = useState('');

  const [actorGroups, setActorGroups] = useState<ActorGroup[]>([]);
  const [actorMode, setActorMode] = useState<'new' | 'existing'>('new');
  const [selectedActorId, setSelectedActorId] = useState('');
  const [manualActorId, setManualActorId] = useState('');

  const [prompt, setPrompt] = useState('');
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [streamText, setStreamText] = useState('');
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const [lastActorId, setLastActorId] = useState('');
  const abortRef = useRef<AbortController | null>(null);

  // Load types and actors on mount
  useEffect(() => {
    loadTypes();
    if (props.scopeId) loadActors();
  }, [props.scopeId]);

  async function loadTypes() {
    setTypesLoading(true);
    try {
      const data = await api.gagent.listTypes();
      setTypes(data ?? []);
      if (data?.length && !selectedType) {
        setSelectedType(data[0].fullName);
      }
    } catch (e: any) {
      setError(e?.message || 'Failed to load GAgent types');
    } finally {
      setTypesLoading(false);
    }
  }

  async function loadActors() {
    try {
      const data = await api.gagent.listActors(props.scopeId);
      setActorGroups(data ?? []);
    } catch {
      // Actor store may not be configured
    }
  }

  const currentActorIds = actorGroups.find(
    g => g.gAgentType === selectedType,
  )?.actorIds ?? [];

  const effectiveActorId =
    actorMode === 'existing'
      ? selectedActorId || manualActorId
      : undefined;

  async function handleRun() {
    if (!prompt.trim() || !props.scopeId.trim() || !selectedType) return;
    setRunning(true);
    setEvents([]);
    setStreamText('');
    setError('');
    setLastActorId('');

    const controller = new AbortController();
    abortRef.current = controller;

    try {
      await api.gagent.streamDraftRun(
        props.scopeId,
        selectedType,
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
            // Auto-persist actor ID
            api.gagent.addActor(props.scopeId, selectedType, actorId).then(() => loadActors()).catch(() => {});
          }
          if (evt.type === 'RUN_ERROR') {
            setError((evt.message as string) || 'Run error');
          }
        },
        controller.signal,
      );
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        setError(e?.message || String(e));
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

  return (
    <div className="space-y-4">
      {/* GAgent Type Selector */}
      <div className="space-y-2">
        <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">
          GAgent Type
        </label>
        <div className="flex gap-2 items-center">
          <select
            className="flex-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={selectedType}
            onChange={e => {
              setSelectedType(e.target.value);
              setSelectedActorId('');
            }}
            disabled={typesLoading}
          >
            {types.length === 0 && <option value="">No types available</option>}
            {types.map(t => (
              <option key={t.fullName} value={t.fullName}>
                {t.typeName} ({t.assemblyName})
              </option>
            ))}
          </select>
          <button
            onClick={loadTypes}
            disabled={typesLoading}
            className="rounded-lg border border-[#E6E3DE] px-3 py-2 text-[13px] text-gray-600 hover:bg-gray-50 disabled:opacity-40"
          >
            {typesLoading ? 'Loading...' : 'Refresh'}
          </button>
        </div>
      </div>

      {/* Actor Mode */}
      <div className="space-y-2">
        <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">
          Actor
        </label>
        <div className="flex gap-4 items-center">
          <label className="flex items-center gap-1.5 text-[13px] text-gray-700 cursor-pointer">
            <input
              type="radio"
              name="actorMode"
              checked={actorMode === 'new'}
              onChange={() => setActorMode('new')}
              className="accent-[#18181B]"
            />
            New Actor
          </label>
          <label className="flex items-center gap-1.5 text-[13px] text-gray-700 cursor-pointer">
            <input
              type="radio"
              name="actorMode"
              checked={actorMode === 'existing'}
              onChange={() => setActorMode('existing')}
              className="accent-[#18181B]"
            />
            Reuse Existing
          </label>
        </div>

        {actorMode === 'existing' && (
          <div className="flex gap-2">
            {currentActorIds.length > 0 ? (
              <select
                className="flex-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                value={selectedActorId}
                onChange={e => setSelectedActorId(e.target.value)}
              >
                <option value="">Select an actor ID...</option>
                {currentActorIds.map(id => (
                  <option key={id} value={id}>{id}</option>
                ))}
              </select>
            ) : (
              <input
                className="flex-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                value={manualActorId}
                onChange={e => setManualActorId(e.target.value)}
                placeholder="Enter actor ID manually"
              />
            )}
          </div>
        )}
      </div>

      {/* Prompt */}
      <div className="space-y-2">
        <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Prompt</label>
        <textarea
          rows={3}
          className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
          value={prompt}
          onChange={e => setPrompt(e.target.value)}
          placeholder="Enter your prompt..."
        />
      </div>

      {/* Actions */}
      <div className="flex gap-2">
        <button
          onClick={handleRun}
          disabled={running || !prompt.trim() || !props.scopeId.trim() || !selectedType}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
        >
          {running ? 'Running...' : 'Run'}
        </button>
        {running && (
          <button onClick={handleStop} className="rounded-lg border border-red-300 px-4 py-2 text-[13px] text-red-600 hover:bg-red-50">
            Stop
          </button>
        )}
      </div>

      {/* Error */}
      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{error}</div>}

      {/* Run Info */}
      {runId && (
        <div className="text-[12px] text-gray-500">
          Run ID: <span className="font-mono">{runId}</span>
          {lastActorId && <span className="ml-3">Actor: <span className="font-mono">{lastActorId}</span></span>}
          {finished && <span className="ml-2 text-green-600 font-semibold">Finished</span>}
        </div>
      )}

      {/* Response */}
      {streamText && (
        <div className="space-y-1">
          <div className="text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Response</div>
          <pre className="whitespace-pre-wrap rounded-lg border border-[#E6E3DE] bg-white p-4 text-[13px] leading-6 max-h-[400px] overflow-auto">
            {streamText}
          </pre>
        </div>
      )}

      {/* Actor History */}
      {actorGroups.length > 0 && (
        <details className="rounded-lg border border-[#E6E3DE] bg-white">
          <summary className="px-4 py-3 text-[12px] font-semibold text-gray-500 uppercase tracking-wider cursor-pointer hover:bg-gray-50">
            Saved Actors ({actorGroups.reduce((n, g) => n + g.actorIds.length, 0)})
          </summary>
          <div className="px-4 pb-3 space-y-2">
            {actorGroups.map(g => (
              <div key={g.gAgentType} className="space-y-1">
                <div className="text-[12px] font-semibold text-gray-600">{g.gAgentType}</div>
                <div className="flex flex-wrap gap-1">
                  {g.actorIds.map(id => (
                    <span key={id} className="inline-flex items-center gap-1 rounded bg-[#F0EDE8] px-2 py-0.5 text-[11px] font-mono text-gray-700">
                      {id}
                      <button
                        onClick={() => {
                          api.gagent.removeActor(props.scopeId, g.gAgentType, id).then(() => loadActors()).catch(() => {});
                        }}
                        className="text-gray-400 hover:text-red-500 ml-0.5"
                        title="Remove"
                      >
                        x
                      </button>
                    </span>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}
