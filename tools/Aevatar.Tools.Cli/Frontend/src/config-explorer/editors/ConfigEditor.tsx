import type { ConfigStore } from '../useConfigStore';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function ConfigEditor({ store, flash }: Props) {
  async function handleSave() {
    try {
      JSON.parse(store.configJson); // validate
      await store.saveConfig();
      flash('Config saved', 'success');
    } catch (e: any) {
      if (e instanceof SyntaxError) {
        flash('Invalid JSON: ' + e.message, 'error');
      } else {
        flash(e?.message || 'Failed to save config', 'error');
      }
    }
  }

  return (
    <div className="max-w-[680px] space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">config.json</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">Configuration</div>
        </div>
        <button
          onClick={handleSave}
          disabled={!store.configDirty || store.configSaving}
          className="rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
        >
          {store.configSaving ? 'Saving...' : 'Save'}
        </button>
      </div>

      {/* JSON editor */}
      <div className="rounded-2xl border border-[#EEEAE4] bg-white p-1">
        <textarea
          className="w-full min-h-[400px] p-4 text-[13px] font-mono leading-relaxed bg-transparent resize-y focus:outline-none"
          value={store.configJson}
          onChange={e => store.setConfigJson(e.target.value)}
          spellCheck={false}
        />
      </div>
    </div>
  );
}
