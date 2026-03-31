import { useState } from 'react';
import { Plus, Trash2, UserCircle, Pencil } from 'lucide-react';
import type { ConfigStore } from '../useConfigStore';
import type { RoleState } from '../../studio';
import EditorDrawer from '../EditorDrawer';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function RolesEditor({ store, flash }: Props) {
  const [editingKey, setEditingKey] = useState<string | null>(null);

  const editingRole = editingKey ? store.roles.find(r => r.key === editingKey) : null;

  function openEditor(role: RoleState) {
    setEditingKey(role.key);
  }

  function handleDone() {
    setEditingKey(null);
  }

  async function handleSave() {
    try {
      await store.saveRoles();
      flash('Roles saved', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to save roles', 'error');
    }
  }

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">roles.json</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">Role Catalog</div>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => { store.addRole(); }}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[#E6E3DE] px-3 py-2 text-[13px] font-medium text-gray-600 hover:bg-gray-50 transition-colors"
          >
            <Plus size={14} />
            Add Role
          </button>
          <button
            onClick={handleSave}
            disabled={!store.rolesDirty || store.rolesSaving}
            className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
          >
            {store.rolesSaving ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>

      {/* Role cards */}
      {store.roles.length === 0 ? (
        <div className="rounded-2xl border border-dashed border-[#E6E3DE] bg-white p-8 text-center">
          <UserCircle size={32} className="mx-auto text-gray-300 mb-2" />
          <div className="text-[13px] text-gray-400">No roles defined</div>
        </div>
      ) : (
        <div className="space-y-2">
          {store.roles.map(role => (
            <div
              key={role.key}
              className="rounded-2xl border border-[#EEEAE4] bg-white px-5 py-4 flex items-center gap-4 group hover:border-[#D8D4CE] transition-colors cursor-pointer"
              onClick={() => openEditor(role)}
            >
              <UserCircle size={18} className="text-violet-500 flex-shrink-0" />
              <div className="flex-1 min-w-0">
                <div className="text-[14px] font-semibold text-gray-800">{role.name || role.id}</div>
                <div className="text-[11px] text-gray-400 mt-0.5 flex items-center gap-2">
                  <span className="font-mono">{role.id}</span>
                  {role.model && (
                    <>
                      <span className="text-gray-300">/</span>
                      <span>{role.model}</span>
                    </>
                  )}
                  {role.connectorsText && (
                    <>
                      <span className="text-gray-300">/</span>
                      <span>{role.connectorsText.split(/[\n,]/).filter(Boolean).length} connector(s)</span>
                    </>
                  )}
                </div>
              </div>
              <div className="flex items-center gap-1">
                <button
                  onClick={e => { e.stopPropagation(); openEditor(role); }}
                  className="opacity-0 group-hover:opacity-100 rounded-lg p-1.5 text-gray-400 hover:text-gray-600 hover:bg-gray-100 transition-all"
                >
                  <Pencil size={13} />
                </button>
                <button
                  onClick={e => { e.stopPropagation(); store.removeRole(role.key); }}
                  className="opacity-0 group-hover:opacity-100 rounded-lg p-1.5 text-gray-400 hover:text-red-500 hover:bg-red-50 transition-all"
                >
                  <Trash2 size={13} />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Edit drawer */}
      <EditorDrawer
        open={!!editingRole}
        title={editingRole ? `Edit Role: ${editingRole.name || editingRole.id}` : ''}
        onClose={handleDone}
        onSave={handleDone}
      >
        {editingRole && (
          <RoleForm
            role={editingRole}
            onChange={patch => store.updateRole(editingRole.key, patch)}
            connectorNames={store.connectors.map(c => c.name)}
          />
        )}
      </EditorDrawer>
    </div>
  );
}

function RoleForm(props: {
  role: RoleState;
  onChange: (patch: Partial<RoleState>) => void;
  connectorNames: string[];
}) {
  const { role, onChange, connectorNames } = props;

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3">
        <FieldInput label="Role ID" value={role.id} onChange={v => onChange({ id: v })} mono />
        <FieldInput label="Name" value={role.name} onChange={v => onChange({ name: v })} />
      </div>

      <div className="grid grid-cols-2 gap-3">
        <FieldInput label="Provider" value={role.provider} onChange={v => onChange({ provider: v })} placeholder="Default" />
        <FieldInput label="Model" value={role.model} onChange={v => onChange({ model: v })} placeholder="Default" />
      </div>

      <div>
        <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">System Prompt</label>
        <textarea
          className="w-full mt-1 rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] px-3 py-2 text-[13px] leading-relaxed focus:outline-none focus:ring-2 focus:ring-blue-400 min-h-[120px] resize-y"
          value={role.systemPrompt}
          onChange={e => onChange({ systemPrompt: e.target.value })}
          placeholder="Instructions for this role..."
        />
      </div>

      <div>
        <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Connectors</label>
        {connectorNames.length > 0 ? (
          <div className="mt-2 flex flex-wrap gap-2">
            {connectorNames.map(name => {
              const active = role.connectorsText.split(/[\n,]/).map(s => s.trim()).includes(name);
              return (
                <button
                  key={name}
                  type="button"
                  onClick={() => {
                    const current = role.connectorsText.split(/[\n,]/).map(s => s.trim()).filter(Boolean);
                    const next = active ? current.filter(c => c !== name) : [...current, name];
                    onChange({ connectorsText: next.join('\n') });
                  }}
                  className={`rounded-lg px-3 py-1.5 text-[12px] font-medium border transition-colors ${
                    active
                      ? 'bg-emerald-50 border-emerald-200 text-emerald-700'
                      : 'bg-white border-[#E6E3DE] text-gray-500 hover:bg-gray-50'
                  }`}
                >
                  {name}
                </button>
              );
            })}
          </div>
        ) : (
          <textarea
            className="w-full mt-1 rounded-lg border border-[#E6E3DE] bg-[#FAFAF9] px-3 py-2 text-[13px] font-mono focus:outline-none focus:ring-2 focus:ring-blue-400 min-h-[60px] resize-y"
            value={role.connectorsText}
            onChange={e => onChange({ connectorsText: e.target.value })}
            placeholder="One connector name per line..."
          />
        )}
      </div>
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
