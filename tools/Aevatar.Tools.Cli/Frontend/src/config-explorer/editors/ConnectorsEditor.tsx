import { useState } from 'react';
import { Plus, Trash2, ArrowRightLeft, Pencil, Globe, Terminal, Server } from 'lucide-react';
import type { ConfigStore } from '../useConfigStore';
import type { ConnectorState } from '../../studio';
import { formatMapText, parseMapText } from '../../studio';
import EditorDrawer from '../EditorDrawer';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

const TYPE_META: Record<string, { label: string; icon: typeof Globe; color: string }> = {
  http: { label: 'HTTP', icon: Globe, color: 'text-blue-500' },
  cli: { label: 'CLI', icon: Terminal, color: 'text-amber-600' },
  mcp: { label: 'MCP', icon: Server, color: 'text-purple-500' },
};

export default function ConnectorsEditor({ store, flash }: Props) {
  const [editingKey, setEditingKey] = useState<string | null>(null);
  const [addMenuOpen, setAddMenuOpen] = useState(false);

  const editingConnector = editingKey ? store.connectors.find(c => c.key === editingKey) : null;

  function handleDone() {
    setEditingKey(null);
  }

  async function handleSave() {
    try {
      await store.saveConnectors();
      flash('Connectors saved', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to save connectors', 'error');
    }
  }

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">connectors.json</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">Connector Catalog</div>
        </div>
        <div className="flex items-center gap-2">
          <div className="relative">
            <button
              onClick={() => setAddMenuOpen(!addMenuOpen)}
              className="inline-flex items-center gap-1.5 rounded-lg border border-[#E6E3DE] px-3 py-2 text-[13px] font-medium text-gray-600 hover:bg-gray-50 transition-colors"
            >
              <Plus size={14} />
              Add
            </button>
            {addMenuOpen && (
              <div className="absolute right-0 mt-1 w-36 rounded-xl border border-gray-200 bg-white shadow-lg z-30 py-1">
                {(['http', 'cli', 'mcp'] as const).map(type => {
                  const meta = TYPE_META[type];
                  return (
                    <button
                      key={type}
                      onClick={() => { store.addConnector(type); setAddMenuOpen(false); }}
                      className="w-full flex items-center gap-2 px-3 py-2 text-[13px] text-gray-700 hover:bg-gray-50"
                    >
                      <meta.icon size={14} className={meta.color} />
                      {meta.label}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
          <button
            onClick={handleSave}
            disabled={!store.connectorsDirty || store.connectorsSaving}
            className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
          >
            {store.connectorsSaving ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>

      {/* Connector cards */}
      {store.connectors.length === 0 ? (
        <div className="rounded-2xl border border-dashed border-[#E6E3DE] bg-white p-8 text-center">
          <ArrowRightLeft size={32} className="mx-auto text-gray-300 mb-2" />
          <div className="text-[13px] text-gray-400">No connectors defined</div>
        </div>
      ) : (
        <div className="space-y-2">
          {store.connectors.map(conn => {
            const meta = TYPE_META[conn.type] || TYPE_META.http;
            const TypeIcon = meta.icon;
            return (
              <div
                key={conn.key}
                className="rounded-2xl border border-[#EEEAE4] bg-white px-5 py-4 flex items-center gap-4 group hover:border-[#D8D4CE] transition-colors cursor-pointer"
                onClick={() => setEditingKey(conn.key)}
              >
                <TypeIcon size={18} className={`${meta.color} flex-shrink-0`} />
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="text-[14px] font-semibold text-gray-800">{conn.name}</span>
                    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase ${
                      conn.type === 'http' ? 'bg-blue-50 text-blue-600' :
                      conn.type === 'cli' ? 'bg-amber-50 text-amber-600' :
                      'bg-purple-50 text-purple-600'
                    }`}>
                      {conn.type}
                    </span>
                    {!conn.enabled && (
                      <span className="rounded-full px-2 py-0.5 text-[10px] font-medium bg-gray-100 text-gray-400">disabled</span>
                    )}
                  </div>
                  <div className="text-[11px] text-gray-400 mt-0.5 truncate font-mono">
                    {conn.type === 'http' && conn.http.baseUrl}
                    {conn.type === 'cli' && conn.cli.command}
                    {conn.type === 'mcp' && (conn.mcp.serverName || conn.mcp.command)}
                  </div>
                </div>
                <div className="flex items-center gap-1">
                  <button
                    onClick={e => { e.stopPropagation(); setEditingKey(conn.key); }}
                    className="opacity-0 group-hover:opacity-100 rounded-lg p-1.5 text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-all"
                  >
                    <Pencil size={13} />
                  </button>
                  <button
                    onClick={e => { e.stopPropagation(); store.removeConnector(conn.key); }}
                    className="opacity-0 group-hover:opacity-100 rounded-lg p-1.5 text-gray-400 hover:text-red-500 hover:bg-red-50 transition-all"
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Edit drawer */}
      <EditorDrawer
        open={!!editingConnector}
        title={editingConnector ? `Edit Connector: ${editingConnector.name}` : ''}
        onClose={handleDone}
        onSave={handleDone}
      >
        {editingConnector && (
          <ConnectorForm
            connector={editingConnector}
            onChange={patch => store.updateConnector(editingConnector.key, patch)}
          />
        )}
      </EditorDrawer>
    </div>
  );
}

function ConnectorForm(props: {
  connector: ConnectorState;
  onChange: (patch: Partial<ConnectorState>) => void;
}) {
  const { connector: c, onChange } = props;

  return (
    <div className="space-y-5">
      {/* Common fields */}
      <div className="grid grid-cols-2 gap-3">
        <FieldInput label="Name" value={c.name} onChange={v => onChange({ name: v })} mono />
        <div>
          <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Type</label>
          <select
            className="w-full mt-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400"
            value={c.type}
            onChange={e => onChange({ type: e.target.value as ConnectorState['type'] })}
          >
            <option value="http">HTTP</option>
            <option value="cli">CLI</option>
            <option value="mcp">MCP</option>
          </select>
        </div>
      </div>

      <div className="grid grid-cols-3 gap-3">
        <div className="flex items-center gap-2">
          <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Enabled</label>
          <button
            type="button"
            onClick={() => onChange({ enabled: !c.enabled })}
            className={`relative w-9 h-5 rounded-full transition-colors ${c.enabled ? 'bg-green-500' : 'bg-gray-300'}`}
          >
            <span className={`absolute top-0.5 left-0.5 w-4 h-4 rounded-full bg-white shadow transition-transform ${c.enabled ? 'translate-x-4' : ''}`} />
          </button>
        </div>
        <FieldInput label="Timeout (ms)" value={c.timeoutMs} onChange={v => onChange({ timeoutMs: v })} mono />
        <FieldInput label="Retry" value={c.retry} onChange={v => onChange({ retry: v })} mono />
      </div>

      {/* Divider */}
      <div className="border-t border-[#E6E3DE]" />

      {/* Type-specific fields */}
      {c.type === 'http' && <HttpFields connector={c} onChange={onChange} />}
      {c.type === 'cli' && <CliFields connector={c} onChange={onChange} />}
      {c.type === 'mcp' && <McpFields connector={c} onChange={onChange} />}
    </div>
  );
}

function HttpFields(props: { connector: ConnectorState; onChange: (patch: Partial<ConnectorState>) => void }) {
  const { connector: c, onChange } = props;
  return (
    <div className="space-y-3">
      <div className="text-[10px] font-semibold uppercase tracking-wider text-blue-500">HTTP Configuration</div>
      <FieldInput label="Base URL" value={c.http.baseUrl} onChange={v => onChange({ http: { ...c.http, baseUrl: v } })} mono placeholder="https://api.example.com" />
      <FieldInput label="Allowed Methods" value={c.http.allowedMethods.join(', ')} onChange={v => onChange({ http: { ...c.http, allowedMethods: v.split(',').map(s => s.trim()).filter(Boolean) } })} placeholder="GET, POST" />
      <FieldInput label="Allowed Paths" value={c.http.allowedPaths.join(', ')} onChange={v => onChange({ http: { ...c.http, allowedPaths: v.split(',').map(s => s.trim()).filter(Boolean) } })} placeholder="/" />
      <FieldInput label="Allowed Input Keys" value={c.http.allowedInputKeys.join(', ')} onChange={v => onChange({ http: { ...c.http, allowedInputKeys: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldTextarea label="Default Headers" value={formatMapText(c.http.defaultHeaders)} onChange={v => onChange({ http: { ...c.http, defaultHeaders: parseMapText(v) } })} placeholder="key: value (one per line)" />
    </div>
  );
}

function CliFields(props: { connector: ConnectorState; onChange: (patch: Partial<ConnectorState>) => void }) {
  const { connector: c, onChange } = props;
  return (
    <div className="space-y-3">
      <div className="text-[10px] font-semibold uppercase tracking-wider text-amber-600">CLI Configuration</div>
      <FieldInput label="Command" value={c.cli.command} onChange={v => onChange({ cli: { ...c.cli, command: v } })} mono />
      <FieldInput label="Fixed Arguments" value={c.cli.fixedArguments.join(', ')} onChange={v => onChange({ cli: { ...c.cli, fixedArguments: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldInput label="Allowed Operations" value={c.cli.allowedOperations.join(', ')} onChange={v => onChange({ cli: { ...c.cli, allowedOperations: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldInput label="Allowed Input Keys" value={c.cli.allowedInputKeys.join(', ')} onChange={v => onChange({ cli: { ...c.cli, allowedInputKeys: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldInput label="Working Directory" value={c.cli.workingDirectory} onChange={v => onChange({ cli: { ...c.cli, workingDirectory: v } })} mono />
      <FieldTextarea label="Environment" value={formatMapText(c.cli.environment)} onChange={v => onChange({ cli: { ...c.cli, environment: parseMapText(v) } })} placeholder="KEY: value (one per line)" />
    </div>
  );
}

function McpFields(props: { connector: ConnectorState; onChange: (patch: Partial<ConnectorState>) => void }) {
  const { connector: c, onChange } = props;
  return (
    <div className="space-y-3">
      <div className="text-[10px] font-semibold uppercase tracking-wider text-purple-500">MCP Configuration</div>
      <FieldInput label="Server Name" value={c.mcp.serverName} onChange={v => onChange({ mcp: { ...c.mcp, serverName: v } })} mono />
      <FieldInput label="Command" value={c.mcp.command} onChange={v => onChange({ mcp: { ...c.mcp, command: v } })} mono />
      <FieldInput label="Arguments" value={c.mcp.arguments.join(', ')} onChange={v => onChange({ mcp: { ...c.mcp, arguments: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldInput label="Default Tool" value={c.mcp.defaultTool} onChange={v => onChange({ mcp: { ...c.mcp, defaultTool: v } })} mono />
      <FieldInput label="Allowed Tools" value={c.mcp.allowedTools.join(', ')} onChange={v => onChange({ mcp: { ...c.mcp, allowedTools: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldInput label="Allowed Input Keys" value={c.mcp.allowedInputKeys.join(', ')} onChange={v => onChange({ mcp: { ...c.mcp, allowedInputKeys: v.split(',').map(s => s.trim()).filter(Boolean) } })} />
      <FieldTextarea label="Environment" value={formatMapText(c.mcp.environment)} onChange={v => onChange({ mcp: { ...c.mcp, environment: parseMapText(v) } })} placeholder="KEY: value (one per line)" />
    </div>
  );
}

function FieldInput(props: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  mono?: boolean;
}) {
  return (
    <div>
      <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">{props.label}</label>
      <input
        className={`w-full mt-1 rounded-lg border border-[#E6E3DE] bg-white px-3 py-2 text-[13px] focus:outline-none focus:ring-2 focus:ring-blue-400 ${props.mono ? 'font-mono' : ''}`}
        value={props.value}
        onChange={e => props.onChange(e.target.value)}
        placeholder={props.placeholder}
      />
    </div>
  );
}

function FieldTextarea(props: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
}) {
  return (
    <div>
      <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">{props.label}</label>
      <textarea
        className="w-full mt-1 rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] px-3 py-2 text-[13px] font-mono leading-relaxed focus:outline-none focus:ring-2 focus:ring-blue-400 min-h-[70px] resize-y"
        value={props.value}
        onChange={e => props.onChange(e.target.value)}
        placeholder={props.placeholder}
      />
    </div>
  );
}
