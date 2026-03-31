import { Loader2, ExternalLink } from 'lucide-react';
import type { ConfigStore } from './useConfigStore';
import ConfigEditor from './editors/ConfigEditor';
import RolesEditor from './editors/RolesEditor';
import ConnectorsEditor from './editors/ConnectorsEditor';
import ActorsEditor from './editors/ActorsEditor';
import ChatHistoryViewer from './editors/ChatHistoryViewer';

type Props = {
  store: ConfigStore;
  flash: (msg: string, type: 'success' | 'error') => void;
  onOpenWorkflowInStudio?: (workflowId: string) => void;
};

function WorkflowViewer({ store, onOpenWorkflowInStudio }: { store: ConfigStore; onOpenWorkflowInStudio?: (workflowId: string) => void }) {
  const workflowId = store.selectedFile.replace('workflow:', '');
  const wf = store.workflows.find(w => w.workflowId === workflowId);

  if (store.workflowLoading) {
    return (
      <div className="py-8 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin text-gray-400" />
        <span>Loading workflow...</span>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[15px] font-semibold text-gray-800">{wf?.name || workflowId}</div>
          {wf?.description && <div className="text-[12px] text-gray-500 mt-1">{wf.description}</div>}
          {wf && <div className="text-[11px] text-gray-400 mt-1">{wf.stepCount} steps · {wf.directoryLabel}</div>}
        </div>
        {onOpenWorkflowInStudio && (
          <button
            onClick={() => onOpenWorkflowInStudio(workflowId)}
            className="inline-flex items-center gap-1.5 rounded-lg bg-[#18181B] px-3 py-1.5 text-[12px] font-semibold text-white hover:bg-[#333] transition-colors"
          >
            <ExternalLink size={12} />
            Open in Studio
          </button>
        )}
      </div>
      {store.selectedWorkflowYaml != null ? (
        <pre className="rounded-[14px] border border-[#E6E3DE] bg-[#FAFAF9] p-4 text-[12px] text-gray-700 font-mono whitespace-pre-wrap overflow-x-auto max-h-[70vh] overflow-y-auto">
          {store.selectedWorkflowYaml}
        </pre>
      ) : (
        <div className="text-[13px] text-gray-400">Could not load workflow YAML.</div>
      )}
    </div>
  );
}

export default function EditorPanel({ store, flash, onOpenWorkflowInStudio }: Props) {
  if (store.selectedFile.startsWith('workflow:')) {
    return <WorkflowViewer store={store} onOpenWorkflowInStudio={onOpenWorkflowInStudio} />;
  }

  if (store.selectedFile.startsWith('chat-history:')) {
    const convId = store.selectedFile.replace('chat-history:', '');
    return <ChatHistoryViewer store={store} conversationId={convId} flash={flash} />;
  }

  switch (store.selectedFile) {
    case 'config.json':
      return <ConfigEditor store={store} flash={flash} />;
    case 'roles.json':
      return <RolesEditor store={store} flash={flash} />;
    case 'connectors.json':
      return <ConnectorsEditor store={store} flash={flash} />;
    case 'actors.json':
      return <ActorsEditor store={store} flash={flash} />;
    default:
      return null;
  }
}
