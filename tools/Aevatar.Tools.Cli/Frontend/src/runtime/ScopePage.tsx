import { useRef, useState } from 'react';
import { normalizeBackendSseFrame, type RuntimeEvent, type RuntimeEventType } from './sseUtils';
import * as api from '../api';
import * as nyxid from '../auth/nyxid';

type InvokeState = {
  status: 'idle' | 'running' | 'success' | 'error';
  events: RuntimeEvent[];
  text: string;
  runId: string;
  actorId: string;
  error: string;
};

function createIdleState(): InvokeState {
  return { status: 'idle', events: [], text: '', runId: '', actorId: '', error: '' };
}

function readStr(obj: any, ...keys: string[]): string {
  for (const k of keys) if (typeof obj?.[k] === 'string') return obj[k];
  return '';
}

// ── Event type config ──────────────────────────────────────────────────────────
type EventConfig = { label: string; bg: string; text: string };
const EVENT_CONFIG: Partial<Record<RuntimeEventType, EventConfig>> = {
  RUN_STARTED:           { label: 'Run Started',     bg: 'bg-blue-100',   text: 'text-blue-700'   },
  RUN_FINISHED:          { label: 'Run Finished',     bg: 'bg-green-100',  text: 'text-green-700'  },
  RUN_ERROR:             { label: 'Error',            bg: 'bg-red-100',    text: 'text-red-700'    },
  STEP_STARTED:          { label: 'Step Started',     bg: 'bg-purple-100', text: 'text-purple-700' },
  STEP_FINISHED:         { label: 'Step Finished',    bg: 'bg-purple-50',  text: 'text-purple-600' },
  TEXT_MESSAGE_START:    { label: 'Message Start',    bg: 'bg-gray-100',   text: 'text-gray-500'   },
  TEXT_MESSAGE_CONTENT:  { label: 'Text',             bg: 'bg-amber-50',   text: 'text-amber-700'  },
  TEXT_MESSAGE_END:      { label: 'Message End',      bg: 'bg-gray-100',   text: 'text-gray-500'   },
  HUMAN_INPUT_REQUEST:   { label: 'Input Request',    bg: 'bg-orange-100', text: 'text-orange-700' },
  CUSTOM:                { label: 'Custom',           bg: 'bg-slate-100',  text: 'text-slate-600'  },
  STATE_SNAPSHOT:        { label: 'Snapshot',         bg: 'bg-teal-50',    text: 'text-teal-600'   },
};

function getEventConfig(type: string): EventConfig {
  return (EVENT_CONFIG as any)[type] ?? { label: type, bg: 'bg-gray-100', text: 'text-gray-600' };
}

// ── Single event row ───────────────────────────────────────────────────────────
function EventRow({ evt, index }: { evt: RuntimeEvent; index: number }) {
  const [expanded, setExpanded] = useState(false);
  const cfg = getEventConfig(evt.type);

  // Skip pure text-delta events — they're already shown in the main text preview
  // but do show them here as well, just abbreviated inline
  const renderDetail = () => {
    switch (evt.type) {
      case 'RUN_STARTED':
      case 'RUN_FINISHED':
        return (
          <span className="font-mono text-[12px] text-gray-600">
            {evt.runId ? `run: ${evt.runId}` : ''}
            {evt.threadId ? `  thread: ${evt.threadId}` : ''}
          </span>
        );
      case 'RUN_ERROR':
        return <span className="text-red-700 text-[12px]">{String(evt.message || '')}</span>;
      case 'STEP_STARTED':
      case 'STEP_FINISHED':
        return <span className="font-mono text-[12px] text-gray-700">{String(evt.stepName || '')}</span>;
      case 'TEXT_MESSAGE_START':
        return <span className="text-gray-400 text-[12px]">messageId: {String(evt.messageId || '')}  role: {String(evt.role || '')}</span>;
      case 'TEXT_MESSAGE_END':
        return <span className="text-gray-400 text-[12px]">messageId: {String(evt.messageId || '')}</span>;
      case 'TEXT_MESSAGE_CONTENT': {
        const delta = String(evt.delta || '');
        return (
          <span className="font-mono text-[12px] text-gray-700 whitespace-pre-wrap break-all">
            {delta}
          </span>
        );
      }
      case 'HUMAN_INPUT_REQUEST':
        return (
          <div className="text-[12px] text-orange-800">
            {evt.prompt ? <div className="whitespace-pre-wrap">{String(evt.prompt)}</div> : null}
            {evt.stepId ? <div className="text-gray-400">step: {String(evt.stepId)}</div> : null}
          </div>
        );
      case 'CUSTOM': {
        const name = String(evt.name || '');
        const value = evt.value;
        const actorId = (value as any)?.actorId || (value as any)?.actor_id;
        const json = JSON.stringify(value, null, 2);
        return (
          <div className="text-[12px]">
            {name && <span className="font-semibold text-slate-700 mr-2">{name}</span>}
            {actorId && <span className="font-mono text-blue-600 mr-2">actor: {actorId}</span>}
            {value !== undefined && value !== null && (
              <button
                onClick={() => setExpanded(v => !v)}
                className="text-gray-400 hover:text-gray-600 underline text-[11px]"
              >
                {expanded ? 'hide' : 'show'} payload
              </button>
            )}
            {expanded && (
              <pre className="mt-1 bg-[#F7F5F2] rounded p-2 text-[11px] font-mono whitespace-pre-wrap break-all border border-[#E6E3DE] max-h-[400px] overflow-auto">
                {json}
              </pre>
            )}
          </div>
        );
      }
      case 'STATE_SNAPSHOT': {
        const json = JSON.stringify(evt.snapshot, null, 2);
        return (
          <div className="text-[12px]">
            <button
              onClick={() => setExpanded(v => !v)}
              className="text-gray-400 hover:text-gray-600 underline text-[11px]"
            >
              {expanded ? 'hide' : 'show'} snapshot
            </button>
            {expanded && (
              <pre className="mt-1 bg-[#F7F5F2] rounded p-2 text-[11px] font-mono whitespace-pre-wrap break-all border border-[#E6E3DE] max-h-[400px] overflow-auto">
                {json}
              </pre>
            )}
          </div>
        );
      }
      default: {
        const { type: _type, timestamp: _ts, ...rest } = evt;
        const json = JSON.stringify(rest, null, 2);
        return (
          <div className="text-[12px]">
            <button
              onClick={() => setExpanded(v => !v)}
              className="text-gray-400 hover:text-gray-600 underline text-[11px]"
            >
              {expanded ? 'hide' : 'show'} detail
            </button>
            {expanded && (
              <pre className="mt-1 bg-[#F7F5F2] rounded p-2 text-[11px] font-mono whitespace-pre-wrap break-all border border-[#E6E3DE] max-h-[400px] overflow-auto">
                {json}
              </pre>
            )}
          </div>
        );
      }
    }
  };

  return (
    <div className={`flex gap-3 px-4 py-2.5 border-b border-[#F0EDE8] last:border-0 ${index % 2 === 0 ? 'bg-white' : 'bg-[#FAFAF8]'}`}>
      {/* index */}
      <div className="flex-shrink-0 w-6 text-right text-[11px] text-gray-300 pt-0.5 select-none">{index + 1}</div>
      {/* badge */}
      <div className="flex-shrink-0 pt-0.5">
        <span className={`inline-block rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${cfg.bg} ${cfg.text}`}>
          {cfg.label}
        </span>
      </div>
      {/* detail */}
      <div className="flex-1 min-w-0">{renderDetail()}</div>
    </div>
  );
}

// ── Timeline item row (actor timeline) ────────────────────────────────────────
function TimelineRow({ item, index }: { item: any; index: number }) {
  const [expanded, setExpanded] = useState(false);
  const eventType = readStr(item, 'eventType', 'EventType', 'type', 'Type');
  const ts = readStr(item, 'timestamp', 'Timestamp', 'occurredAt');
  const summary = readStr(item, 'summary', 'Summary', 'description');
  const payload = item?.payload ?? item?.data ?? item?.Payload ?? item?.Data;

  const cfg = getEventConfig(eventType);

  return (
    <div className={`px-4 py-2.5 border-b border-[#F0EDE8] last:border-0 ${index % 2 === 0 ? 'bg-white' : 'bg-[#FAFAF8]'}`}>
      <div className="flex gap-3 items-start">
        <div className="flex-shrink-0 w-6 text-right text-[11px] text-gray-300 pt-0.5 select-none">{index + 1}</div>
        <div className="flex-shrink-0 pt-0.5">
          <span className={`inline-block rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${cfg.bg} ${cfg.text}`}>
            {eventType || 'event'}
          </span>
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3 flex-wrap">
            {ts && <span className="font-mono text-[11px] text-gray-400">{ts}</span>}
            {summary && <span className="text-[12px] text-gray-700">{summary}</span>}
            {!summary && payload !== undefined && (
              <button
                onClick={() => setExpanded(v => !v)}
                className="text-gray-400 hover:text-gray-600 underline text-[11px]"
              >
                {expanded ? 'hide' : 'show'} payload
              </button>
            )}
          </div>
          {summary && payload !== undefined && (
            <button
              onClick={() => setExpanded(v => !v)}
              className="mt-0.5 text-gray-400 hover:text-gray-600 underline text-[11px]"
            >
              {expanded ? 'hide' : 'show'} payload
            </button>
          )}
          {expanded && (
            <pre className="mt-2 bg-[#F7F5F2] rounded p-2 text-[11px] font-mono whitespace-pre-wrap break-all border border-[#E6E3DE] max-h-[400px] overflow-auto">
              {JSON.stringify(payload, null, 2)}
            </pre>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Main page ──────────────────────────────────────────────────────────────────
export default function ScopePage() {
  const scopeId = nyxid.loadSession()?.user.sub || '';

  // Services
  const [services, setServices] = useState<any[]>([]);
  const [servicesLoading, setServicesLoading] = useState(false);

  // Binding
  const [binding, setBinding] = useState<any>(null);

  // Invoke
  const [selectedServiceId, setSelectedServiceId] = useState('default');
  const [prompt, setPrompt] = useState('');
  const [invoke, setInvoke] = useState<InvokeState>(createIdleState());
  const abortRef = useRef<AbortController | null>(null);

  // Logs
  const [logsActorId, setLogsActorId] = useState('');
  const [timeline, setTimeline] = useState<any[]>([]);
  const [timelineLoading, setTimelineLoading] = useState(false);

  async function loadServices() {
    if (!scopeId) return;
    setServicesLoading(true);
    try {
      const [svcList, bindResult] = await Promise.all([
        api.scope.listServices(scopeId),
        api.scope.getBinding(scopeId).catch(() => null),
      ]);
      setServices(Array.isArray(svcList) ? svcList : []);
      setBinding(bindResult);
    } catch { setServices([]); } finally { setServicesLoading(false); }
  }

  async function handleInvoke() {
    if (!scopeId || !prompt.trim()) return;
    const controller = new AbortController();
    abortRef.current = controller;
    setInvoke({ ...createIdleState(), status: 'running' });
    setTimeline([]);

    const onFrame = (frame: any) => {
      const evt = normalizeBackendSseFrame(frame);
      if (!evt) return;
      setInvoke(prev => {
        const next = { ...prev, events: [...prev.events, evt] };
        if (evt.type === 'TEXT_MESSAGE_CONTENT') next.text = prev.text + (evt.delta as string || '');
        if (evt.type === 'RUN_STARTED') { next.runId = evt.runId as string || ''; }
        if (evt.type === 'RUN_ERROR') { next.error = evt.message as string || 'Error'; next.status = 'error'; }
        if (evt.type === 'CUSTOM') {
          const actId = (evt as any)?.value?.actorId || (evt as any)?.value?.actor_id;
          if (actId) next.actorId = actId;
        }
        return next;
      });
    };

    try {
      await api.scope.streamInvoke(scopeId, selectedServiceId, prompt.trim(), onFrame, controller.signal);
      setInvoke(prev => ({ ...prev, status: 'success' }));
    } catch (e: any) {
      if (e?.name !== 'AbortError') setInvoke(prev => ({ ...prev, status: 'error', error: e?.message || String(e) }));
    } finally { abortRef.current = null; }
  }

  function handleStop() { abortRef.current?.abort(); }

  async function loadTimeline(actorId?: string) {
    const id = actorId || logsActorId.trim() || invoke.actorId;
    if (!id) return;
    if (actorId) setLogsActorId(actorId);
    setTimelineLoading(true);
    try {
      const data = await api.scope.getActorTimeline(id, 50);
      const items = Array.isArray(data) ? data : (data?.items ?? data?.Items ?? []);
      setTimeline(items);
    } catch { setTimeline([]); } finally { setTimelineLoading(false); }
  }

  if (!scopeId) {
    return (
      <>
        <header className="h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
          <div>
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Scope</div>
            <div className="text-[18px] font-bold text-gray-800">Not Logged In</div>
          </div>
        </header>
        <div className="flex-1 flex items-center justify-center text-gray-400 text-[14px]">
          Sign in with NyxID to access your scope.
        </div>
      </>
    );
  }

  return (
    <>
      <header className="h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center justify-between gap-4">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Scope</div>
          <div className="text-[18px] font-bold text-gray-800 font-mono">{scopeId}</div>
        </div>
        <button
          onClick={loadServices}
          disabled={servicesLoading}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
        >
          {servicesLoading ? 'Loading...' : 'Load Services'}
        </button>
      </header>

      <div className="flex-1 min-h-0 overflow-auto p-6 space-y-6 bg-[#F2F1EE]">

        {/* ── Binding Status ── */}
        {binding && (
          <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-2">
            <div className="text-[12px] font-semibold uppercase tracking-wider text-gray-400">Default Scope Binding</div>
            <div className="grid grid-cols-3 gap-4 text-[13px]">
              <div><span className="text-gray-400">Service:</span> <strong>{readStr(binding, 'serviceId', 'ServiceId') || 'default'}</strong></div>
              <div><span className="text-gray-400">Name:</span> {readStr(binding, 'displayName', 'DisplayName') || '-'}</div>
              <div><span className="text-gray-400">Available:</span> {binding?.available ? 'Yes' : 'No'}</div>
            </div>
          </div>
        )}

        {/* ── Services Table ── */}
        {services.length > 0 && (
          <div className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden">
            <div className="px-5 py-3 border-b border-[#E6E3DE] text-[12px] font-semibold uppercase tracking-wider text-gray-400">
              Services ({services.length})
            </div>
            <table className="w-full text-[13px]">
              <thead className="bg-[#F7F5F2]">
                <tr>
                  <th className="px-5 py-2.5 text-left font-semibold text-gray-600">Service ID</th>
                  <th className="px-5 py-2.5 text-left font-semibold text-gray-600">Name</th>
                  <th className="px-5 py-2.5 text-left font-semibold text-gray-600">Status</th>
                  <th className="px-5 py-2.5 text-left font-semibold text-gray-600">Endpoints</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[#E6E3DE]">
                {services.map((s: any) => {
                  const sid = readStr(s, 'serviceId', 'ServiceId');
                  return (
                    <tr key={sid}
                      onClick={() => setSelectedServiceId(sid)}
                      className={`cursor-pointer hover:bg-[#F7F5F2] ${selectedServiceId === sid ? 'bg-blue-50' : ''}`}>
                      <td className="px-5 py-2.5 font-mono">{sid}</td>
                      <td className="px-5 py-2.5">{readStr(s, 'displayName', 'DisplayName')}</td>
                      <td className="px-5 py-2.5">
                        <span className="rounded-full bg-[#F0EDE8] px-2 py-0.5 text-[11px]">
                          {readStr(s, 'deploymentStatus', 'DeploymentStatus')}
                        </span>
                      </td>
                      <td className="px-5 py-2.5">
                        {(s.endpoints || s.Endpoints || []).map((ep: any) => readStr(ep, 'kind', 'Kind') || readStr(ep, 'endpointId', 'EndpointId')).join(', ')}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {/* ── Invoke Service ── */}
        <div className="rounded-xl border border-[#E6E3DE] bg-white p-5 space-y-4">
          <div className="text-[12px] font-semibold uppercase tracking-wider text-gray-400">
            Invoke Service: <span className="font-mono text-gray-700">{selectedServiceId}</span>
          </div>

          <textarea
            rows={4}
            className="w-full rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] px-4 py-3 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={prompt}
            onChange={e => setPrompt(e.target.value)}
            placeholder="Enter your prompt..."
          />

          <div className="flex gap-2 flex-wrap">
            <button
              onClick={handleInvoke}
              disabled={invoke.status === 'running' || !prompt.trim()}
              className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
            >
              {invoke.status === 'running' ? 'Streaming...' : 'Stream Chat'}
            </button>
            {invoke.status === 'running' && (
              <button onClick={handleStop} className="rounded-lg border border-red-300 px-4 py-2 text-[13px] text-red-600 hover:bg-red-50">
                Abort
              </button>
            )}
            {invoke.actorId && (
              <button
                onClick={() => loadTimeline(invoke.actorId)}
                className="rounded-lg border border-[#E6E3DE] px-4 py-2 text-[13px] hover:bg-[#F7F5F2]"
              >
                Load Actor Timeline
              </button>
            )}
          </div>

          {/* Run metadata */}
          {invoke.runId && (
            <div className="text-[12px] text-gray-500 font-mono bg-[#F7F5F2] rounded-lg px-3 py-2 flex flex-wrap gap-4">
              <span>run: <strong>{invoke.runId}</strong></span>
              {invoke.actorId && <span>actor: <strong>{invoke.actorId}</strong></span>}
              {invoke.status === 'success' && <span className="text-green-600 font-semibold non-mono">✓ Done</span>}
              {invoke.status === 'error' && <span className="text-red-600 font-semibold non-mono">✗ Error</span>}
            </div>
          )}

          {invoke.error && (
            <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{invoke.error}</div>
          )}

          {/* Streamed text */}
          {invoke.text && (
            <div>
              <div className="text-[11px] font-semibold uppercase tracking-wider text-gray-400 mb-1.5">Response</div>
              <pre className="whitespace-pre-wrap rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] p-4 text-[13px] leading-6">
                {invoke.text}
              </pre>
            </div>
          )}
        </div>

        {/* ── Live SSE Event Feed ── */}
        {invoke.events.length > 0 && (
          <div className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden">
            <div className="px-5 py-3 border-b border-[#E6E3DE] flex items-center justify-between">
              <div className="text-[12px] font-semibold uppercase tracking-wider text-gray-400">
                Live Event Feed
              </div>
              <div className="text-[12px] text-gray-400">{invoke.events.length} events</div>
            </div>
            <div className="divide-y divide-[#F0EDE8]">
              {invoke.events.map((evt, i) => (
                <EventRow key={i} evt={evt} index={i} />
              ))}
            </div>
          </div>
        )}

        {/* ── Actor Timeline ── */}
        <div className="rounded-xl border border-[#E6E3DE] bg-white overflow-hidden">
          <div className="px-5 py-3 border-b border-[#E6E3DE] flex items-center justify-between gap-4">
            <div className="text-[12px] font-semibold uppercase tracking-wider text-gray-400">Actor Timeline</div>
            <div className="flex gap-2 flex-1 max-w-xl">
              <input
                className="flex-1 rounded-lg border border-[#E6E3DE] bg-[#FAFAF8] px-3 py-1.5 text-[12px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400"
                value={logsActorId}
                onChange={e => setLogsActorId(e.target.value)}
                placeholder="Actor ID"
              />
              <button
                onClick={() => loadTimeline()}
                disabled={timelineLoading || (!logsActorId.trim() && !invoke.actorId)}
                className="rounded-lg bg-[#18181B] px-3 py-1.5 text-[12px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
              >
                {timelineLoading ? 'Loading…' : 'Load'}
              </button>
            </div>
          </div>

          {timeline.length === 0 ? (
            <div className="px-5 py-8 text-center text-[13px] text-gray-400">
              {timelineLoading ? 'Loading timeline…' : 'No timeline data. Enter an Actor ID and click Load, or run an invocation first.'}
            </div>
          ) : (
            <div>
              <div className="px-5 py-2 bg-[#F7F5F2] border-b border-[#E6E3DE] text-[11px] text-gray-400">
                {timeline.length} entries
              </div>
              <div>
                {timeline.map((item: any, i: number) => (
                  <TimelineRow key={i} item={item} index={i} />
                ))}
              </div>
            </div>
          )}
        </div>

      </div>
    </>
  );
}
