import { useState } from 'react';
import * as api from '../api';

type TimelineEntry = {
  timestamp: string;
  eventType: string;
  summary: string;
};

function readStr(obj: any, ...keys: string[]): string {
  for (const k of keys) {
    if (typeof obj?.[k] === 'string') return obj[k];
  }
  return '';
}

function parseEntry(raw: any): TimelineEntry {
  const summary =
    readStr(raw, 'summary', 'Summary', 'description', 'Description') ||
    truncate(JSON.stringify(raw?.payload ?? raw?.Payload ?? raw?.data ?? raw?.Data ?? ''), 80);
  return {
    timestamp: readStr(raw, 'timestamp', 'Timestamp', 'occurredAt', 'OccurredAt'),
    eventType: readStr(raw, 'eventType', 'EventType', 'type', 'Type'),
    summary,
  };
}

function truncate(value: string, max: number): string {
  return value.length <= max ? value : value.slice(0, max - 1) + '\u2026';
}

export default function LogsTab() {
  const [actorId, setActorId] = useState('');
  const [take, setTake] = useState(50);
  const [entries, setEntries] = useState<TimelineEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  async function handleLoad() {
    if (!actorId.trim()) return;
    setLoading(true);
    setError('');
    try {
      const data = await api.runtime.getActorTimeline(actorId.trim(), take);
      const items = Array.isArray(data) ? data : (data?.items ?? data?.Items ?? []);
      setEntries(items.map(parseEntry));
    } catch (e: any) {
      setError(e?.message || String(e));
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex gap-4 items-end">
        <div className="flex-1 space-y-2">
          <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Actor ID</label>
          <input
            className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={actorId}
            onChange={e => setActorId(e.target.value)}
            placeholder="Enter actor ID..."
          />
        </div>
        <div className="w-24 space-y-2">
          <label className="block text-[12px] font-semibold text-gray-500 uppercase tracking-wider">Take</label>
          <input
            type="number"
            className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={take}
            onChange={e => setTake(Number(e.target.value) || 50)}
          />
        </div>
        <button
          onClick={handleLoad}
          disabled={loading || !actorId.trim()}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-40"
        >
          {loading ? 'Loading...' : 'Load Timeline'}
        </button>
      </div>

      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-[13px] text-red-700">{error}</div>}

      {entries.length > 0 && (
        <div className="overflow-auto rounded-lg border border-[#E6E3DE]">
          <table className="w-full text-[13px]">
            <thead className="bg-[#F7F5F2]">
              <tr>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600 w-[220px]">Timestamp</th>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600 w-[180px]">Event Type</th>
                <th className="px-4 py-2.5 text-left font-semibold text-gray-600">Summary</th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-[#E6E3DE]">
              {entries.map((entry, i) => (
                <tr key={i} className="hover:bg-[#F7F5F2]">
                  <td className="px-4 py-2.5 font-mono text-[12px] text-gray-500">{entry.timestamp}</td>
                  <td className="px-4 py-2.5">
                    <span className="rounded-full bg-[#F0EDE8] px-2 py-0.5 text-[11px]">{entry.eventType}</span>
                  </td>
                  <td className="px-4 py-2.5 text-gray-600">{entry.summary}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {entries.length === 0 && !loading && !error && actorId.trim() && (
        <div className="text-[13px] text-gray-400">No timeline entries.</div>
      )}
    </div>
  );
}
