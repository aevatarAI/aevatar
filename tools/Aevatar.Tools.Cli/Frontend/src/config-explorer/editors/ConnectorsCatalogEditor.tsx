import { useCallback, useEffect, useState } from 'react';
import { Loader2, Plus, Trash2, ChevronDown } from 'lucide-react';
import * as api from '../../api';
import { type ConnectorState, toConnectorState, toConnectorPayload, createEmptyConnector } from '../../studio';

type Props = { flash: (msg: string, type: 'success' | 'error') => void };

export default function ConnectorsCatalogEditor({ flash }: Props) {
  const [tab, setTab] = useState<'catalog' | 'raw'>('catalog');
  const [connectors, setConnectors] = useState<ConnectorState[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [expandedKey, setExpandedKey] = useState<string | null>(null);
  const [rawJson, setRawJson] = useState('');
  const [catalogMeta, setCatalogMeta] = useState<any>(null);

  const loadCatalog = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api.connectors.getCatalog();
      setCatalogMeta(data);
      const items = Array.isArray(data?.connectors) ? data.connectors : [];
      setConnectors(items.map((c: any) => toConnectorState(c)));
      setRawJson(JSON.stringify(data, null, 2));
    } catch {
      setConnectors([]);
      setRawJson('{}');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { loadCatalog(); }, [loadCatalog]);

  async function saveCatalog() {
    setSaving(true);
    try {
      const payload = { ...catalogMeta, connectors: connectors.map(toConnectorPayload) };
      await api.connectors.saveCatalog(payload);
      flash('Connectors saved', 'success');
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
      await api.connectors.saveCatalog(parsed);
      flash('Connectors saved (raw)', 'success');
      await loadCatalog();
    } catch (e: any) {
      flash(e?.message || 'Invalid JSON or save failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  function addConnector() {
    const next = createEmptyConnector('http');
    setConnectors(prev => [next, ...prev]);
    setExpandedKey(next.key);
  }

  function removeConnector(key: string) {
    setConnectors(prev => prev.filter(c => c.key !== key));
  }

  function update(key: string, patch: Partial<ConnectorState>) {
    setConnectors(prev => prev.map(c => c.key === key ? { ...c, ...patch } : c));
  }

  if (loading) {
    return (
      <div className="py-12 flex flex-col items-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin" />
        <span>Loading connectors...</span>
      </div>
    );
  }

  return (
    <div className="space-y-4 max-w-[780px]">
      {/* Tabs + actions */}
      <div className="flex items-center justify-between">
        <div className="workspace-segmented-control">
          <button onClick={() => setTab('catalog')} className={`workspace-segmented-button ${tab === 'catalog' ? 'active' : ''}`}>Catalog</button>
          <button onClick={() => setTab('raw')} className={`workspace-segmented-button ${tab === 'raw' ? 'active' : ''}`}>Raw</button>
        </div>
        <div className="flex items-center gap-2">
          {tab === 'catalog' && (
            <button onClick={addConnector} className="ghost-action !min-h-[34px]">
              <Plus size={12} /> Add
            </button>
          )}
          <button onClick={tab === 'catalog' ? saveCatalog : saveRaw} disabled={saving} className="solid-action !min-h-[34px] disabled:opacity-50">
            {saving ? <Loader2 size={12} className="animate-spin" /> : null}
            Save
          </button>
        </div>
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
        <div className="space-y-2">
          {connectors.length === 0 && (
            <div className="text-center py-8 text-[13px] text-gray-400">No connectors yet. Click Add to create one.</div>
          )}
          {connectors.map(connector => {
            const expanded = expandedKey === connector.key;
            return (
              <div key={connector.key} className="workspace-card overflow-hidden">
                <button
                  onClick={() => setExpandedKey(expanded ? null : connector.key)}
                  className="w-full px-4 py-3 flex items-center justify-between gap-3 text-left hover:bg-[#FAFAF9]"
                >
                  <div className="min-w-0">
                    <div className="text-[13px] font-semibold text-gray-800 truncate">{connector.name || 'New connector'}</div>
                    <div className="text-[11px] text-gray-400 truncate">{connector.type}</div>
                  </div>
                  <ChevronDown size={14} className={`text-gray-400 transition-transform ${expanded ? 'rotate-180' : ''}`} />
                </button>

                {expanded && (
                  <div className="px-4 pb-4 space-y-3 border-t border-[#F2F1EE]">
                    <Field label="Name" value={connector.name} onChange={v => update(connector.key, { name: v })} />
                    <div>
                      <label className="block text-[11px] font-semibold text-gray-500 mb-1">Type</label>
                      <select
                        value={connector.type}
                        onChange={e => update(connector.key, { type: e.target.value as ConnectorState['type'] })}
                        className="panel-input"
                      >
                        <option value="http">HTTP</option>
                        <option value="cli">CLI</option>
                        <option value="mcp">MCP</option>
                      </select>
                    </div>

                    {connector.type === 'http' && (
                      <>
                        <Field label="Base URL" value={connector.http.baseUrl} onChange={v => update(connector.key, { http: { ...connector.http, baseUrl: v } })} />
                        <Field label="Allowed Methods (comma-separated)" value={connector.http.allowedMethods.join(', ')} onChange={v => update(connector.key, { http: { ...connector.http, allowedMethods: v.split(',').map(s => s.trim().toUpperCase()).filter(Boolean) } })} />
                        <Field label="Allowed Paths (comma-separated)" value={connector.http.allowedPaths.join(', ')} onChange={v => update(connector.key, { http: { ...connector.http, allowedPaths: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
                      </>
                    )}

                    {connector.type === 'cli' && (
                      <>
                        <Field label="Command" value={connector.cli.command} onChange={v => update(connector.key, { cli: { ...connector.cli, command: v } })} />
                        <Field label="Working Directory" value={connector.cli.workingDirectory} onChange={v => update(connector.key, { cli: { ...connector.cli, workingDirectory: v } })} />
                      </>
                    )}

                    {connector.type === 'mcp' && (
                      <>
                        <Field label="Server Name" value={connector.mcp.serverName} onChange={v => update(connector.key, { mcp: { ...connector.mcp, serverName: v } })} />
                        <Field label="Command" value={connector.mcp.command} onChange={v => update(connector.key, { mcp: { ...connector.mcp, command: v } })} />
                        <Field label="Default Tool" value={connector.mcp.defaultTool} onChange={v => update(connector.key, { mcp: { ...connector.mcp, defaultTool: v } })} />
                      </>
                    )}

                    <div className="flex justify-end">
                      <button onClick={() => removeConnector(connector.key)} className="inline-flex items-center gap-1 text-[11px] text-red-500 hover:text-red-700">
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
        className="panel-input"
      />
    </div>
  );
}
