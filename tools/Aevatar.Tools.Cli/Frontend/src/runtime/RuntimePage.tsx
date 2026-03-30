import { useState } from 'react';
import DraftRunTab from './DraftRunTab';
import ServicesTab from './ServicesTab';
import InvokeTab from './InvokeTab';
import LogsTab from './LogsTab';

type RuntimeTab = 'draft-run' | 'services' | 'invoke' | 'logs';

const TABS: { key: RuntimeTab; label: string }[] = [
  { key: 'draft-run', label: 'Draft Run' },
  { key: 'services', label: 'Services' },
  { key: 'invoke', label: 'Invoke' },
  { key: 'logs', label: 'Logs' },
];

export default function RuntimePage() {
  const [tab, setTab] = useState<RuntimeTab>('draft-run');
  const [scopeId, setScopeId] = useState('default');

  return (
    <>
      <header className="h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center justify-between gap-4">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Runtime</div>
          <div className="text-[18px] font-bold text-gray-800 mt-0.5">Service Runtime</div>
        </div>
        <div className="flex items-center gap-3">
          <label className="text-[12px] font-semibold text-gray-500">Scope</label>
          <input
            className="rounded-lg border border-[#E6E3DE] bg-white px-3 py-1.5 text-[13px] w-48 focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={scopeId}
            onChange={e => setScopeId(e.target.value)}
            placeholder="Scope ID"
          />
        </div>
      </header>

      <div className="flex-1 min-h-0 flex flex-col bg-[#F2F1EE]">
        <nav className="flex gap-0 border-b border-[#E6E3DE] bg-white px-6">
          {TABS.map(t => (
            <button
              key={t.key}
              onClick={() => setTab(t.key)}
              className={`px-4 py-3 text-[13px] font-semibold border-b-2 transition-colors ${
                tab === t.key
                  ? 'border-[#18181B] text-gray-800'
                  : 'border-transparent text-gray-400 hover:text-gray-600'
              }`}
            >
              {t.label}
            </button>
          ))}
        </nav>

        <div className="flex-1 min-h-0 overflow-auto p-6">
          {tab === 'draft-run' && <DraftRunTab scopeId={scopeId} />}
          {tab === 'services' && <ServicesTab scopeId={scopeId} />}
          {tab === 'invoke' && <InvokeTab scopeId={scopeId} />}
          {tab === 'logs' && <LogsTab />}
        </div>
      </div>
    </>
  );
}
