import { useCallback, useEffect, useRef, useState } from 'react';
import { Loader2, HelpCircle } from 'lucide-react';
import * as api from '../../api';

type Props = { flash: (msg: string, type: 'success' | 'error') => void };

/** Field definitions derived from config.template.json. */
const FIELDS: FieldDef[] = [
  {
    key: 'defaultModel',
    label: 'Default Chat Model',
    type: 'text',
    placeholder: 'e.g. deepseek-chat, claude-sonnet-4-20250514',
    description:
      'Override the LLM model used for chat conversations. Leave empty to use the provider default. Does not affect Studio AI workflow or script generation.',
  },
  {
    key: 'preferredLlmRoute',
    label: 'Preferred Chat Route',
    type: 'text',
    placeholder: 'e.g. chrono-llm, /api/v1/proxy/s/my-service',
    description:
      'Route chat conversations to a specific NyxID proxy service. Empty = gateway default. Does not affect Studio AI workflow or script generation.',
  },
  {
    key: 'maxToolRounds',
    label: 'Max Tool Rounds',
    type: 'number',
    placeholder: '0',
    description: 'Maximum tool-call iterations per chat turn. 0 = unlimited (recommended).',
  },
  {
    key: 'runtimeMode',
    label: 'Runtime Mode',
    type: 'select',
    options: ['local', 'remote'],
    description: 'Which backend runtime to use for workflow execution.',
  },
  {
    key: 'localRuntimeBaseUrl',
    label: 'Local Runtime URL',
    type: 'text',
    placeholder: 'http://127.0.0.1:5080',
    description: 'Base URL for the local runtime backend.',
  },
  {
    key: 'remoteRuntimeBaseUrl',
    label: 'Remote Runtime URL',
    type: 'text',
    placeholder: 'https://aevatar-console-backend-api.aevatar.ai',
    description: 'Base URL for the remote runtime backend.',
  },
];

type FieldDef = {
  key: string;
  label: string;
  type: 'text' | 'number' | 'select';
  placeholder?: string;
  description?: string;
  options?: string[];
};

function Tooltip({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function close(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  return (
    <div ref={ref} className="relative inline-flex">
      <button
        type="button"
        onClick={() => setOpen(v => !v)}
        className="text-gray-300 hover:text-gray-500 transition-colors"
      >
        <HelpCircle size={14} />
      </button>
      {open && (
        <div className="absolute left-1/2 -translate-x-1/2 bottom-full mb-1.5 z-50 w-[240px] rounded-lg bg-gray-800 text-white text-[11px] leading-relaxed px-3 py-2 shadow-lg">
          {text}
          <div className="absolute left-1/2 -translate-x-1/2 top-full w-0 h-0 border-x-[5px] border-x-transparent border-t-[5px] border-t-gray-800" />
        </div>
      )}
    </div>
  );
}

export default function UserConfigEditor({ flash }: Props) {
  const [tab, setTab] = useState<'fields' | 'raw'>('fields');
  const [config, setConfig] = useState<Record<string, any>>({});
  const [rawJson, setRawJson] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api.userConfig.get();
      setConfig(data ?? {});
      setRawJson(JSON.stringify(data ?? {}, null, 2));
    } catch {
      setConfig({});
      setRawJson('{}');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  function updateField(key: string, value: any) {
    setConfig(prev => ({ ...prev, [key]: value }));
  }

  async function saveFields() {
    setSaving(true);
    try {
      const payload: Record<string, any> = {};
      for (const field of FIELDS) {
        const v = config[field.key];
        if (field.type === 'number') {
          const n = parseInt(v, 10);
          if (!isNaN(n) && n > 0) payload[field.key] = n;
          // omit 0 or empty → server treats as unset
        } else if (typeof v === 'string' && v.trim() !== '') {
          payload[field.key] = v.trim();
        }
      }
      await api.userConfig.save(payload);
      flash('Config saved', 'success');
      await load();
    } catch (e: any) {
      flash(e?.message || 'Save failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  async function saveRaw() {
    setSaving(true);
    try {
      const parsed = JSON.parse(rawJson);
      await api.userConfig.save(parsed);
      flash('Config saved (raw)', 'success');
      await load();
    } catch (e: any) {
      flash(e?.message || 'Invalid JSON or save failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  if (loading) {
    return (
      <div className="py-12 flex flex-col items-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin" />
        <span>Loading config...</span>
      </div>
    );
  }

  return (
    <div className="space-y-4 max-w-[780px]">
      {/* Tabs + Save */}
      <div className="flex items-center justify-between">
        <div className="workspace-segmented-control">
          <button
            onClick={() => setTab('fields')}
            className={`workspace-segmented-button ${tab === 'fields' ? 'active' : ''}`}
          >
            Fields
          </button>
          <button
            onClick={() => setTab('raw')}
            className={`workspace-segmented-button ${tab === 'raw' ? 'active' : ''}`}
          >
            Raw
          </button>
        </div>
        <button
          onClick={tab === 'fields' ? saveFields : saveRaw}
          disabled={saving}
          className="solid-action !min-h-[34px] disabled:opacity-50"
        >
          {saving ? <Loader2 size={12} className="animate-spin" /> : null}
          Save
        </button>
      </div>

      {tab === 'raw' ? (
        <div className="workspace-editor-frame">
          <textarea
            value={rawJson}
            onChange={e => setRawJson(e.target.value)}
            className="panel-textarea !min-h-[400px] !max-h-[70vh] !border-0 !bg-transparent !shadow-none p-4"
            spellCheck={false}
          />
        </div>
      ) : (
        <div className="workspace-card divide-y divide-[#EEEAE4]">
          {FIELDS.map(field => (
            <div key={field.key} className="px-5 py-4">
              <div className="flex items-center gap-4">
                <div className="flex-shrink-0 w-[180px] flex items-center gap-1.5">
                  <label className="text-[13px] font-semibold text-gray-800">{field.label}</label>
                  {field.description && <Tooltip text={field.description} />}
                </div>
                <div className="flex-1 min-w-0">
                  {field.type === 'select' ? (
                    <select
                      value={config[field.key] ?? ''}
                      onChange={e => updateField(field.key, e.target.value)}
                      className="panel-input"
                    >
                      {field.options?.map(opt => (
                        <option key={opt} value={opt}>{opt}</option>
                      ))}
                    </select>
                  ) : (
                    <input
                      type={field.type === 'number' ? 'number' : 'text'}
                      value={config[field.key] ?? ''}
                      onChange={e => updateField(field.key, field.type === 'number' ? e.target.value : e.target.value)}
                      placeholder={field.placeholder}
                      className="panel-input"
                    />
                  )}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
