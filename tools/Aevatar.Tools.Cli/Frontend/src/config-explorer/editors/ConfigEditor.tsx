import { useEffect, useMemo, useRef, useState } from 'react';
import { Check, ChevronDown } from 'lucide-react';
import type { ConfigStore } from '../useConfigStore';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function ConfigEditor({ store, flash }: Props) {
  const [filterText, setFilterText] = useState('');
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && e.target instanceof Node && !containerRef.current.contains(e.target))
        setDropdownOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const readyProviders = useMemo(
    () => store.providers.filter(p => p.status === 'ready'),
    [store.providers],
  );

  const groupedModels = useMemo(() => {
    const prefixToProvider: Record<string, string> = {};
    for (const p of readyProviders) {
      const slug = p.provider_slug;
      const name = p.provider_name;
      if (slug === 'openai') { for (const pfx of ['gpt-', 'o1-', 'o1', 'o3-', 'o3', 'o4-', 'chatgpt-']) prefixToProvider[pfx] = name; }
      else if (slug === 'anthropic') { prefixToProvider['claude-'] = name; }
      else if (slug === 'google-ai') { prefixToProvider['gemini-'] = name; }
      else if (slug === 'mistral') { for (const pfx of ['mistral-', 'codestral-', 'magistral-']) prefixToProvider[pfx] = name; }
      else if (slug === 'cohere') { for (const pfx of ['command-']) prefixToProvider[pfx] = name; }
      else if (slug === 'deepseek') { prefixToProvider['deepseek-'] = name; }
      else { prefixToProvider[slug + '-'] = name; }
    }
    const q = filterText.trim().toLowerCase();
    const groups = new Map<string, string[]>();
    for (const model of store.supportedModels) {
      if (q && !model.toLowerCase().includes(q)) continue;
      let provider = 'Other';
      for (const [pfx, name] of Object.entries(prefixToProvider)) {
        if (model.startsWith(pfx) || model === pfx.replace(/-$/, '')) { provider = name; break; }
      }
      if (!groups.has(provider)) groups.set(provider, []);
      groups.get(provider)!.push(model);
    }
    return groups;
  }, [store.supportedModels, readyProviders, filterText]);

  const hasModels = store.supportedModels.length > 0;

  async function handleSave() {
    try {
      await store.saveConfig();
      flash('Config saved', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to save config', 'error');
    }
  }

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">config.json</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">LLM Configuration</div>
        </div>
        <button
          onClick={handleSave}
          disabled={!store.configDirty || store.configSaving}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
        >
          {store.configSaving ? 'Saving...' : 'Save'}
        </button>
      </div>

      {/* Connected providers */}
      {readyProviders.length > 0 && (
        <div className="rounded-2xl border border-[#EEEAE4] bg-white p-5 space-y-3">
          <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400">Connected Providers</div>
          <div className="flex flex-wrap gap-2">
            {store.providers.map(p => (
              <span
                key={p.provider_slug}
                className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-[12px] font-medium ${
                  p.status === 'ready'
                    ? 'bg-green-50 text-green-700'
                    : p.status === 'expired'
                    ? 'bg-amber-50 text-amber-700'
                    : 'bg-gray-100 text-gray-400'
                }`}
              >
                <span className={`inline-block w-1.5 h-1.5 rounded-full ${
                  p.status === 'ready' ? 'bg-green-500' : p.status === 'expired' ? 'bg-amber-500' : 'bg-gray-300'
                }`} />
                {p.provider_name}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Model selector */}
      <div className="rounded-2xl border border-[#EEEAE4] bg-white p-5 space-y-4">
        <div className="text-[10px] font-semibold uppercase tracking-wider text-gray-400">Default Model</div>

        <div ref={containerRef} className="relative">
          {hasModels ? (
            <div className="relative">
              <input
                ref={inputRef}
                className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2.5 pr-8 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
                value={dropdownOpen ? filterText : store.defaultModel}
                placeholder={store.modelsLoading ? 'Loading models...' : 'Select a model...'}
                onChange={e => { setFilterText(e.target.value); if (!dropdownOpen) setDropdownOpen(true); }}
                onFocus={() => { setFilterText(''); setDropdownOpen(true); }}
              />
              <button
                type="button"
                className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600"
                onClick={() => { setDropdownOpen(!dropdownOpen); if (!dropdownOpen) { setFilterText(''); inputRef.current?.focus(); } }}
                tabIndex={-1}
              >
                <ChevronDown size={14} />
              </button>

              {dropdownOpen && (
                <div className="absolute z-50 mt-1 w-full max-h-[320px] overflow-auto rounded-xl border border-gray-200 bg-white shadow-lg">
                  {store.modelsLoading ? (
                    <div className="px-3 py-3 text-[12px] text-gray-400">Loading...</div>
                  ) : groupedModels.size === 0 ? (
                    <div className="px-3 py-3 text-[12px] text-gray-400">No matching models</div>
                  ) : (
                    Array.from(groupedModels.entries()).map(([provider, models]) => (
                      <div key={provider}>
                        <div className="px-3 pt-3 pb-1 text-[10px] font-semibold uppercase tracking-wider text-gray-400">{provider}</div>
                        {models.map(model => (
                          <button
                            key={model}
                            type="button"
                            className={`w-full flex items-center justify-between text-left px-3 py-2 text-[13px] hover:bg-gray-50 ${
                              model === store.defaultModel ? 'bg-blue-50 text-blue-700 font-medium' : 'text-gray-700'
                            }`}
                            onClick={() => {
                              store.setDefaultModel(model);
                              setDropdownOpen(false);
                              setFilterText('');
                            }}
                          >
                            {model}
                            {model === store.defaultModel && <Check size={14} className="text-blue-500" />}
                          </button>
                        ))}
                      </div>
                    ))
                  )}
                </div>
              )}
            </div>
          ) : (
            <input
              className="w-full rounded-lg border border-[#E6E3DE] bg-white px-3 py-2.5 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
              value={store.defaultModel}
              placeholder={store.modelsLoading ? 'Loading...' : 'Enter model name...'}
              onChange={e => store.setDefaultModel(e.target.value)}
            />
          )}
        </div>

        {store.defaultModel && (
          <div className="flex items-center gap-2 text-[12px] text-gray-500">
            <span className="font-mono bg-gray-50 px-2 py-0.5 rounded">{store.defaultModel}</span>
          </div>
        )}
      </div>
    </div>
  );
}
