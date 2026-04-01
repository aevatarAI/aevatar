import { useCallback, useEffect, useState } from 'react';
import { Loader2, Plus, Trash2, ChevronDown } from 'lucide-react';
import * as api from '../../api';
import { type RoleState, toRoleState, toRolePayload } from '../../studio';

type Props = { flash: (msg: string, type: 'success' | 'error') => void };

export default function RolesCatalogEditor({ flash }: Props) {
  const [tab, setTab] = useState<'catalog' | 'raw'>('catalog');
  const [roles, setRoles] = useState<RoleState[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [expandedKey, setExpandedKey] = useState<string | null>(null);
  const [rawJson, setRawJson] = useState('');
  const [catalogMeta, setCatalogMeta] = useState<any>(null);

  const loadCatalog = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api.roles.getCatalog();
      setCatalogMeta(data);
      const items = Array.isArray(data?.roles) ? data.roles : [];
      setRoles(items.map((r: any, i: number) => toRoleState(r, i)));
      setRawJson(JSON.stringify(data, null, 2));
    } catch {
      setRoles([]);
      setRawJson('{}');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { loadCatalog(); }, [loadCatalog]);

  async function saveCatalog() {
    setSaving(true);
    try {
      const payload = { ...catalogMeta, roles: roles.map(toRolePayload) };
      await api.roles.saveCatalog(payload);
      flash('Roles saved', 'success');
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
      await api.roles.saveCatalog(parsed);
      flash('Roles saved (raw)', 'success');
      await loadCatalog();
    } catch (e: any) {
      flash(e?.message || 'Invalid JSON or save failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  function addRole() {
    const next: RoleState = {
      key: `role_${crypto.randomUUID()}`,
      id: '', name: '', systemPrompt: '', provider: '', model: '',
      connectorsText: '', ornnSkillsMode: 'all', ornnSelectedSkills: [],
    };
    setRoles(prev => [next, ...prev]);
    setExpandedKey(next.key);
  }

  function removeRole(key: string) {
    setRoles(prev => prev.filter(r => r.key !== key));
  }

  function updateRole(key: string, patch: Partial<RoleState>) {
    setRoles(prev => prev.map(r => r.key === key ? { ...r, ...patch } : r));
  }

  if (loading) {
    return (
      <div className="py-12 flex flex-col items-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin" />
        <span>Loading roles...</span>
      </div>
    );
  }

  return (
    <div className="space-y-4 max-w-[780px]">
      {/* Tabs + actions */}
      <div className="flex items-center justify-between">
        <div className="flex gap-1 rounded-lg bg-[#F2F1EE] p-0.5">
          <button onClick={() => setTab('catalog')} className={`px-3 py-1 rounded-md text-[12px] font-semibold transition-colors ${tab === 'catalog' ? 'bg-white text-gray-800 shadow-sm' : 'text-gray-500 hover:text-gray-700'}`}>Catalog</button>
          <button onClick={() => setTab('raw')} className={`px-3 py-1 rounded-md text-[12px] font-semibold transition-colors ${tab === 'raw' ? 'bg-white text-gray-800 shadow-sm' : 'text-gray-500 hover:text-gray-700'}`}>Raw</button>
        </div>
        <div className="flex items-center gap-2">
          {tab === 'catalog' && (
            <button onClick={addRole} className="inline-flex items-center gap-1 rounded-lg border border-[#EEEAE4] bg-white px-3 py-1.5 text-[12px] font-semibold text-gray-700 hover:bg-[#FAF8F4]">
              <Plus size={12} /> Add
            </button>
          )}
          <button onClick={tab === 'catalog' ? saveCatalog : saveRaw} disabled={saving} className="inline-flex items-center gap-1 rounded-lg bg-[#18181B] px-3 py-1.5 text-[12px] font-semibold text-white hover:bg-[#333] disabled:opacity-50">
            {saving ? <Loader2 size={12} className="animate-spin" /> : null}
            Save
          </button>
        </div>
      </div>

      {tab === 'raw' ? (
        <div className="rounded-2xl border border-[#EEEAE4] bg-white p-1">
          <textarea
            value={rawJson}
            onChange={e => setRawJson(e.target.value)}
            className="w-full min-h-[400px] max-h-[70vh] p-4 text-[13px] font-mono leading-relaxed text-gray-700 resize-y outline-none"
            spellCheck={false}
          />
        </div>
      ) : (
        <div className="space-y-2">
          {roles.length === 0 && (
            <div className="text-center py-8 text-[13px] text-gray-400">No roles yet. Click Add to create one.</div>
          )}
          {roles.map(role => {
            const expanded = expandedKey === role.key;
            return (
              <div key={role.key} className="rounded-2xl border border-[#EEEAE4] bg-white overflow-hidden">
                <button
                  onClick={() => setExpandedKey(expanded ? null : role.key)}
                  className="w-full px-4 py-3 flex items-center justify-between gap-3 text-left hover:bg-[#FAFAF9]"
                >
                  <div className="min-w-0">
                    <div className="text-[13px] font-semibold text-gray-800 truncate">{role.name || role.id || 'New role'}</div>
                    <div className="text-[11px] text-gray-400 truncate">
                      {role.id || 'id'}{role.provider ? ` · ${role.provider}` : ''}{role.model ? ` · ${role.model}` : ''}
                    </div>
                  </div>
                  <ChevronDown size={14} className={`text-gray-400 transition-transform ${expanded ? 'rotate-180' : ''}`} />
                </button>

                {expanded && (
                  <div className="px-4 pb-4 space-y-3 border-t border-[#F2F1EE]">
                    <Field label="Role ID" value={role.id} onChange={v => updateRole(role.key, { id: v })} />
                    <Field label="Name" value={role.name} onChange={v => updateRole(role.key, { name: v })} />
                    <Field label="Provider" value={role.provider} onChange={v => updateRole(role.key, { provider: v })} />
                    <Field label="Model" value={role.model} onChange={v => updateRole(role.key, { model: v })} />
                    <div>
                      <label className="block text-[11px] font-semibold text-gray-500 mb-1">System Prompt</label>
                      <textarea
                        value={role.systemPrompt}
                        onChange={e => updateRole(role.key, { systemPrompt: e.target.value })}
                        rows={4}
                        className="w-full rounded-lg border border-[#EEEAE4] px-3 py-2 text-[13px] text-gray-700 resize-y outline-none focus:border-gray-400"
                      />
                    </div>
                    <div>
                      <label className="block text-[11px] font-semibold text-gray-500 mb-1">Connectors (one per line)</label>
                      <textarea
                        value={role.connectorsText}
                        onChange={e => updateRole(role.key, { connectorsText: e.target.value })}
                        rows={2}
                        className="w-full rounded-lg border border-[#EEEAE4] px-3 py-2 text-[13px] text-gray-700 resize-y outline-none focus:border-gray-400"
                      />
                    </div>
                    <div className="flex justify-end">
                      <button onClick={() => removeRole(role.key)} className="inline-flex items-center gap-1 text-[11px] text-red-500 hover:text-red-700">
                        <Trash2 size={12} /> Remove
                      </button>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function Field({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div>
      <label className="block text-[11px] font-semibold text-gray-500 mb-1">{label}</label>
      <input
        value={value}
        onChange={e => onChange(e.target.value)}
        className="w-full rounded-lg border border-[#EEEAE4] px-3 py-1.5 text-[13px] text-gray-700 outline-none focus:border-gray-400"
      />
    </div>
  );
}
