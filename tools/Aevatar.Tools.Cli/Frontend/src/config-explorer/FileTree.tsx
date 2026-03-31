import { Settings2, UserCircle, ArrowRightLeft, Bot, FolderOpen, MessageSquare, ChevronRight, ChevronDown, FileText } from 'lucide-react';
import { useState, useEffect } from 'react';
import type { ConfigFile, WorkflowEntry } from './types';
import type { ConversationMeta } from '../runtime/chatTypes';

type Props = {
  scopeId: string;
  selectedFile: ConfigFile;
  onSelect: (file: ConfigFile) => void;
  isDirty: (file: ConfigFile) => boolean;
  chatConversations: ConversationMeta[];
  workflows: WorkflowEntry[];
  onOpenWorkflowInStudio?: (workflowId: string) => void;
  initialFolder?: string | null;
};

const FILES: { file: ConfigFile; label: string; icon: typeof Settings2 }[] = [
  { file: 'config.json', label: 'config.json', icon: Settings2 },
  { file: 'roles.json', label: 'roles.json', icon: UserCircle },
  { file: 'connectors.json', label: 'connectors.json', icon: ArrowRightLeft },
  { file: 'actors.json', label: 'actors.json', icon: Bot },
];

const FILE_COLORS: Record<string, string> = {
  'config.json': 'text-blue-500',
  'roles.json': 'text-violet-500',
  'connectors.json': 'text-emerald-500',
  'actors.json': 'text-orange-500',
};

export default function FileTree({ scopeId, selectedFile, onSelect, isDirty, chatConversations, workflows, onOpenWorkflowInStudio, initialFolder }: Props) {
  const shortScope = scopeId.length > 24 ? scopeId.slice(0, 10) + '...' + scopeId.slice(-10) : scopeId;
  const [chatFolderOpen, setChatFolderOpen] = useState(false);
  const [workflowsFolderOpen, setWorkflowsFolderOpen] = useState(false);

  useEffect(() => {
    if (initialFolder === 'workflows') {
      setWorkflowsFolderOpen(true);
    }
  }, [initialFolder]);

  return (
    <div className="space-y-1 select-none">
      {/* Folder header */}
      <div className="flex items-center gap-2 px-3 py-2 text-[12px] font-semibold text-gray-500">
        <FolderOpen size={14} className="text-amber-500 flex-shrink-0" />
        <span className="truncate" title={scopeId}>{shortScope || 'scope'}/</span>
      </div>

      {/* workflows/ folder */}
      <button
        onClick={() => setWorkflowsFolderOpen(!workflowsFolderOpen)}
        className="w-full flex items-center gap-2 pl-7 pr-3 py-2.5 rounded-[14px] text-[13px] text-gray-600 hover:bg-[#FAF8F4] transition-all duration-150"
      >
        {workflowsFolderOpen
          ? <ChevronDown size={12} className="flex-shrink-0 text-gray-400" />
          : <ChevronRight size={12} className="flex-shrink-0 text-gray-400" />
        }
        <FolderOpen size={14} className="flex-shrink-0 text-amber-500" />
        <span className="flex-1 text-left truncate">workflows/</span>
        {workflows.length > 0 && (
          <span className="text-[11px] text-gray-400 font-mono">{workflows.length}</span>
        )}
      </button>

      {workflowsFolderOpen && workflows.map(wf => {
        const fileKey: ConfigFile = `workflow:${wf.workflowId}`;
        const active = selectedFile === fileKey;
        return (
          <button
            key={wf.workflowId}
            onClick={() => onSelect(fileKey)}
            onDoubleClick={() => onOpenWorkflowInStudio?.(wf.workflowId)}
            className={`w-full flex items-center gap-2.5 pl-[52px] pr-3 py-2 rounded-[14px] text-[12px] transition-all duration-150 ${
              active
                ? 'bg-[var(--accent-icon-surface,#EBF0FF)] font-semibold text-gray-800'
                : 'text-gray-600 hover:bg-[#FAF8F4]'
            }`}
            title={`${wf.name} (${wf.stepCount} steps)`}
          >
            <FileText size={12} className="flex-shrink-0 text-indigo-500" />
            <span className="flex-1 text-left truncate">{wf.name || wf.workflowId}.yaml</span>
          </button>
        );
      })}

      {workflowsFolderOpen && workflows.length === 0 && (
        <div className="pl-[52px] pr-3 py-2 text-[11px] text-gray-400 italic">No workflows</div>
      )}

      {/* Static config files */}
      {FILES.map(({ file, label, icon: Icon }) => {
        const active = selectedFile === file;
        const dirty = isDirty(file);
        return (
          <button
            key={file}
            onClick={() => onSelect(file)}
            className={`w-full flex items-center gap-2.5 pl-7 pr-3 py-2.5 rounded-[14px] text-[13px] transition-all duration-150 ${
              active
                ? 'bg-[var(--accent-icon-surface,#EBF0FF)] font-semibold text-gray-800'
                : 'text-gray-600 hover:bg-[#FAF8F4]'
            }`}
          >
            <Icon size={14} className={`flex-shrink-0 ${FILE_COLORS[file]}`} />
            <span className="flex-1 text-left truncate">{label}</span>
            {dirty && (
              <span className="w-1.5 h-1.5 rounded-full bg-amber-500 flex-shrink-0" />
            )}
          </button>
        );
      })}

      {/* chat-histories folder */}
      <button
        onClick={() => setChatFolderOpen(!chatFolderOpen)}
        className="w-full flex items-center gap-2 pl-7 pr-3 py-2.5 rounded-[14px] text-[13px] text-gray-600 hover:bg-[#FAF8F4] transition-all duration-150"
      >
        {chatFolderOpen
          ? <ChevronDown size={12} className="flex-shrink-0 text-gray-400" />
          : <ChevronRight size={12} className="flex-shrink-0 text-gray-400" />
        }
        <FolderOpen size={14} className="flex-shrink-0 text-amber-500" />
        <span className="flex-1 text-left truncate">chat-histories/</span>
        {chatConversations.length > 0 && (
          <span className="text-[11px] text-gray-400 font-mono">{chatConversations.length}</span>
        )}
      </button>

      {chatFolderOpen && chatConversations.map(conv => {
        const fileKey: ConfigFile = `chat-history:${conv.id}`;
        const active = selectedFile === fileKey;
        const label = conv.title || conv.id;
        return (
          <button
            key={conv.id}
            onClick={() => onSelect(fileKey)}
            className={`w-full flex items-center gap-2.5 pl-[52px] pr-3 py-2 rounded-[14px] text-[12px] transition-all duration-150 ${
              active
                ? 'bg-[var(--accent-icon-surface,#EBF0FF)] font-semibold text-gray-800'
                : 'text-gray-600 hover:bg-[#FAF8F4]'
            }`}
            title={`${label} (${conv.messageCount} messages)`}
          >
            <MessageSquare size={12} className="flex-shrink-0 text-sky-500" />
            <span className="flex-1 text-left truncate">{label}</span>
          </button>
        );
      })}

      {chatFolderOpen && chatConversations.length === 0 && (
        <div className="pl-[52px] pr-3 py-2 text-[11px] text-gray-400 italic">No conversations</div>
      )}
    </div>
  );
}
