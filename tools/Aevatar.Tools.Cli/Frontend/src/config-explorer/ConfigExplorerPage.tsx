import { Save, Loader2 } from 'lucide-react';
import { useConfigStore } from './useConfigStore';
import FileTree from './FileTree';
import EditorPanel from './EditorPanel';

type Props = {
  scopeId: string;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function ConfigExplorerPage({ scopeId, flash }: Props) {
  const store = useConfigStore(scopeId);

  async function handleSaveAll() {
    try {
      await store.saveAll();
      flash('All changes saved', 'success');
    } catch (e: any) {
      flash(e?.message || 'Failed to save', 'error');
    }
  }

  if (!scopeId) {
    return (
      <>
        <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
          <div>
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Configuration</div>
            <div className="text-[18px] font-bold text-gray-800 mt-0.5">Explorer</div>
          </div>
        </header>
        <div className="flex-1 flex items-center justify-center text-[14px] text-gray-400 bg-[#F2F1EE]">
          Sign in to view configuration
        </div>
      </>
    );
  }

  return (
    <>
      {/* Header */}
      <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center justify-between gap-4">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Configuration</div>
          <div className="text-[18px] font-bold text-gray-800 mt-0.5">Explorer</div>
        </div>
        <div className="flex items-center gap-3">
          {store.anyDirty && (
            <span className="text-[11px] text-amber-600 font-medium">Unsaved changes</span>
          )}
          <button
            onClick={handleSaveAll}
            disabled={!store.anyDirty}
            className="inline-flex items-center gap-1.5 rounded-lg bg-[#18181B] px-4 py-2 text-[13px] font-semibold text-white hover:bg-[#333] disabled:opacity-30 transition-colors"
          >
            <Save size={14} />
            Save All
          </button>
        </div>
      </header>

      {/* Body */}
      <section className="flex-1 min-h-0 grid grid-cols-[280px_minmax(0,1fr)] bg-[#F2F1EE]">
        {/* Left: File Tree */}
        <aside className="border-r border-[#E6E3DE] bg-white/94 min-h-0 overflow-y-auto p-4">
          {store.loading ? (
            <div className="py-8 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
              <Loader2 size={24} className="animate-spin text-gray-400" />
              <span>Loading...</span>
            </div>
          ) : (
            <FileTree
              scopeId={scopeId}
              selectedFile={store.selectedFile}
              onSelect={store.setSelectedFile}
              isDirty={store.isDirty}
              chatConversations={store.chatConversations}
            />
          )}
        </aside>

        {/* Right: Editor */}
        <div className="min-h-0 overflow-y-auto p-6">
          {store.loading ? (
            <div className="py-8 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
              <Loader2 size={28} className="animate-spin text-gray-400" />
              <span>Loading...</span>
            </div>
          ) : (
            <EditorPanel store={store} flash={flash} />
          )}
        </div>
      </section>
    </>
  );
}
