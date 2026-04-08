import { AlertCircle, Loader2 } from 'lucide-react';
import { useEffect } from 'react';
import { useConfigStore } from './useConfigStore';
import FileTree from './FileTree';
import EditorPanel from './EditorPanel';

type Props = {
  scopeId: string;
  flash: (msg: string, type: 'success' | 'error') => void;
  initialFolder?: string | null;
  onInitialFolderConsumed?: () => void;
  onOpenWorkflowInStudio?: (workflowId: string) => void;
  onOpenScriptInStudio?: (scriptId: string) => void;
};

export default function ConfigExplorerPage({ scopeId, flash, initialFolder, onInitialFolderConsumed, onOpenWorkflowInStudio, onOpenScriptInStudio }: Props) {
  const store = useConfigStore(scopeId);

  useEffect(() => {
    if (initialFolder && onInitialFolderConsumed) {
      onInitialFolderConsumed();
    }
  }, [initialFolder, onInitialFolderConsumed]);

  function handleOpenInStudio(type: string, key: string) {
    if (type === 'workflow' && onOpenWorkflowInStudio) {
      const id = key.split('/').pop()?.replace(/\.yaml$/i, '') || key;
      onOpenWorkflowInStudio(id);
    } else if (type === 'script' && onOpenScriptInStudio) {
      const id = key.split('/').pop()?.replace(/\.cs$/i, '') || key;
      onOpenScriptInStudio(id);
    }
  }

  if (!scopeId) {
    return (
      <>
        <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center">
          <div>
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Storage</div>
            <div className="text-[18px] font-bold text-gray-800 mt-0.5">Explorer</div>
          </div>
        </header>
        <div className="flex-1 flex items-center justify-center text-[14px] text-gray-400 bg-[#F2F1EE]">
          Sign in to view storage
        </div>
      </>
    );
  }

  return (
    <>
      <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 backdrop-blur-sm px-6 flex items-center justify-between gap-4">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">Storage</div>
          <div className="text-[18px] font-bold text-gray-800 mt-0.5">Explorer</div>
        </div>
      </header>

      {store.errorMessage ? (
        <div className="mx-6 mt-4 rounded-[24px] border border-[#F0D7A5] bg-[#FFF7E8] px-5 py-4 text-[#8A4B12] shadow-[0_14px_32px_rgba(180,125,44,0.12)]">
          <div className="flex items-start gap-3">
            <AlertCircle size={18} className="mt-0.5 flex-shrink-0" />
            <div>
              <div className="text-[11px] font-semibold uppercase tracking-[0.14em] text-[#A55A17]">Chrono Storage</div>
              <div className="mt-1 text-[14px] font-semibold">Explorer is unavailable</div>
              <div className="mt-1 text-[13px] leading-6">{store.errorMessage}</div>
            </div>
          </div>
        </div>
      ) : null}

      <section className="explorer-page flex-1 min-h-0 grid grid-cols-[280px_minmax(0,1fr)] bg-[#F2F1EE]">
        <aside className="border-r border-[#E6E3DE] bg-white/94 min-h-0 overflow-y-auto p-4">
          {store.loading ? (
            <div className="py-8 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
              <Loader2 size={24} className="animate-spin text-gray-400" />
              <span>Loading...</span>
            </div>
          ) : store.errorMessage && store.manifest.length === 0 ? (
            <div className="rounded-2xl border border-[#F0D7A5] bg-[#FFF8EB] px-4 py-4 text-[13px] leading-6 text-[#8A4B12]">
              {store.errorMessage}
            </div>
          ) : (
            <FileTree
              manifest={store.manifest}
              selectedKey={store.selectedKey}
              onSelect={store.setSelectedKey}
              onOpenInStudio={handleOpenInStudio}
              initialFolder={initialFolder}
            />
          )}
        </aside>

        <div className="min-h-0 overflow-y-auto p-6">
          {store.loading ? (
            <div className="py-8 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
              <Loader2 size={28} className="animate-spin text-gray-400" />
              <span>Loading...</span>
            </div>
          ) : (
            <EditorPanel
              selectedKey={store.selectedKey}
              content={store.selectedContent}
              mediaInfo={store.mediaInfo}
              loading={store.contentLoading}
              manifest={store.manifest}
              onOpenInStudio={handleOpenInStudio}
              onSave={store.saveFile}
              onDelete={store.deleteFile}
              flash={flash}
            />
          )}
        </div>
      </section>
    </>
  );
}
