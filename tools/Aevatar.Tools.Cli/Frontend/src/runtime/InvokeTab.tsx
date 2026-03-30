import { useRef, useState } from 'react';
import { normalizeBackendSseFrame, type RuntimeEvent } from './sseUtils';
import * as api from '../api';

export default function InvokeTab(props: { scopeId: string }) {
  const [serviceId, setServiceId] = useState('');
  const [sessionId, setSessionId] = useState('');
  const [prompt, setPrompt] = useState('');
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [streamText, setStreamText] = useState('');
  const [running, setRunning] = useState(false);
  const [error, setError] = useState('');
  const abortRef = useRef<AbortController | null>(null);

  async function handleInvoke() {
    if (!prompt.trim() || !props.scopeId.trim()) return;
    setRunning(true);
    setEvents([]);
    setStreamText('');
    setError('');

    const controller = new AbortController();
    abortRef.current = controller;

    try {
      await api.runtime.streamInvoke(
        props.scopeId,
        serviceId.trim() || 'default',
        prompt.trim(),
        (frame: any) => {
          const evt = normalizeBackendSseFrame(frame);
          if (!evt) return;
          setEvents(prev => [...prev, evt]);
          if (evt.type === 'TEXT_MESSAGE_CONTENT') {
            setStreamText(prev => prev + (evt.delta as string || ''));
          }
          if (evt.type === 'RUN_ERROR') {
            setError((evt.message as string) || 'Run error');
          }
        },
        controller.signal
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
      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-2">
          <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Service ID (optional)</label>
          <input
            className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={serviceId}
            onChange={e => setServiceId(e.target.value)}
            placeholder="Leave empty for scope-level chat"
          />
        </div>
        <div className="space-y-2">
          <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Session ID (optional)</label>
          <input
            className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={sessionId}
            onChange={e => setSessionId(e.target.value)}
            placeholder="For conversation continuity"
          />
        </div>
      </div>

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

      <div className="flex gap-2">
        <button
          onClick={handleInvoke}
          disabled={running || !prompt.trim() || !props.scopeId.trim()}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
        >
          {running ? 'Invoking...' : 'Invoke Chat'}
        </button>
        {running && (
          <button onClick={handleStop} className="rounded-lg border border-red-300 px-4 py-2 text-[13px] text-red-600 hover:bg-red-50">
            Stop
          </button>
        )}
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{error}</div>}

      {runId && (
        <div className="text-[12px] text-gray-500">
          Run ID: <span className="font-mono">{runId}</span>
          {finished && <span className="ml-2 text-green-600 font-semibold">Finished</span>}
        </div>
      )}

      {streamText && (
        <div className="space-y-1">
          <div className="text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Response</div>
          <pre className="whitespace-pre-wrap rounded-lg border border-[#E6E3DE] bg-white p-4 text-[13px] leading-6 max-h-[400px] overflow-auto">
            {streamText}
          </pre>
        </div>
      )}
    </div>
  );
}
