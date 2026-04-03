import { Settings2, Code2, FolderOpen, MessageSquare, ChevronRight, ChevronDown, FileText } from 'lucide-react';
import { useState, useEffect, useMemo } from 'react';
import type { ManifestEntry } from './types';

type Props = {
  manifest: ManifestEntry[];
  selectedKey: string | null;
  onSelect: (key: string) => void;
  onOpenInStudio?: (type: string, key: string) => void;
  initialFolder?: string | null;
};

type FolderGroup = {
  prefix: string;
  label: string;
  entries: ManifestEntry[];
};

const FILE_ICON_MAP: Record<string, { icon: typeof Settings2; color: string }> = {
  config: { icon: Settings2, color: 'text-blue-500' },
  roles: { icon: Settings2, color: 'text-violet-500' },
  connectors: { icon: Settings2, color: 'text-emerald-500' },
  workflow: { icon: FileText, color: 'text-indigo-500' },
  script: { icon: Code2, color: 'text-green-500' },
  'chat-history': { icon: MessageSquare, color: 'text-sky-500' },
};

function getFileIcon(type: string) {
  return FILE_ICON_MAP[type] ?? { icon: FileText, color: 'text-gray-400' };
}

function getDisplayName(entry: ManifestEntry): string {
  if (entry.name) return entry.name;
  const lastSegment = entry.key.split('/').pop() || entry.key;
  return lastSegment;
}

export default function FileTree({ manifest, selectedKey, onSelect, onOpenInStudio, initialFolder }: Props) {
  const [openFolders, setOpenFolders] = useState<Set<string>>(new Set());

  // Group manifest entries into folders and top-level files
  const { topLevel, folders } = useMemo(() => {
    const topLevel: ManifestEntry[] = [];
    const folderMap = new Map<string, ManifestEntry[]>();

    for (const entry of manifest) {
      const slashIdx = entry.key.indexOf('/');
      if (slashIdx > 0) {
        const prefix = entry.key.slice(0, slashIdx);
        if (!folderMap.has(prefix)) folderMap.set(prefix, []);
        folderMap.get(prefix)!.push(entry);
      } else {
        topLevel.push(entry);
      }
    }

    const folders: FolderGroup[] = [];
    for (const [prefix, entries] of folderMap) {
      folders.push({ prefix, label: `${prefix}/`, entries });
    }
    // Sort folders alphabetically
    folders.sort((a, b) => a.prefix.localeCompare(b.prefix));

    return { topLevel, folders };
  }, [manifest]);

  // Auto-expand folder specified by initialFolder
  useEffect(() => {
    if (initialFolder) {
      setOpenFolders(prev => {
        const next = new Set(prev);
        next.add(initialFolder);
        return next;
      });
    }
  }, [initialFolder]);

  function toggleFolder(prefix: string) {
    setOpenFolders(prev => {
      const next = new Set(prev);
      if (next.has(prefix)) next.delete(prefix);
      else next.add(prefix);
      return next;
    });
  }

  function handleDoubleClick(entry: ManifestEntry) {
    if ((entry.type === 'workflow' || entry.type === 'script') && onOpenInStudio) {
      onOpenInStudio(entry.type, entry.key);
    }
  }

  return (
    <div className="space-y-1 select-none">
      {/* Folder header */}
      <div className="flex items-center gap-2 px-3 py-2 text-[12px] font-semibold text-gray-500">
        <FolderOpen size={14} className="text-amber-500 flex-shrink-0" />
        <span className="truncate">storage/</span>
      </div>

      {/* Folder groups */}
      {folders.map(folder => {
        const isOpen = openFolders.has(folder.prefix);
        return (
          <div key={folder.prefix}>
            <button
              onClick={() => toggleFolder(folder.prefix)}
              className="w-full flex items-center gap-2 pl-7 pr-3 py-2.5 rounded-[14px] text-[13px] text-gray-600 hover:bg-[#FAF8F4] transition-all duration-150"
            >
              {isOpen
                ? <ChevronDown size={12} className="flex-shrink-0 text-gray-400" />
                : <ChevronRight size={12} className="flex-shrink-0 text-gray-400" />
              }
              <FolderOpen size={14} className="flex-shrink-0 text-amber-500" />
              <span className="flex-1 text-left truncate">{folder.label}</span>
              {folder.entries.length > 0 && (
                <span className="text-[11px] text-gray-400 font-mono">{folder.entries.length}</span>
              )}
            </button>

            {isOpen && folder.entries.map(entry => {
              const active = selectedKey === entry.key;
              const { icon: Icon, color } = getFileIcon(entry.type);
              return (
                <button
                  key={entry.key}
                  onClick={() => onSelect(entry.key)}
                  onDoubleClick={() => handleDoubleClick(entry)}
                  className={`w-full flex items-center gap-2.5 pl-[52px] pr-3 py-2 rounded-[14px] text-[12px] transition-all duration-150 ${
                    active
                      ? 'bg-[var(--accent-icon-surface,#EBF0FF)] font-semibold text-gray-800'
                      : 'text-gray-600 hover:bg-[#FAF8F4]'
                  }`}
                  title={entry.key}
                >
                  <Icon size={12} className={`flex-shrink-0 ${color}`} />
                  <span className="flex-1 text-left truncate">{getDisplayName(entry)}</span>
                </button>
              );
            })}

            {isOpen && folder.entries.length === 0 && (
              <div className="pl-[52px] pr-3 py-2 text-[11px] text-gray-400 italic">Empty</div>
            )}
          </div>
        );
      })}

      {/* Top-level files */}
      {topLevel.map(entry => {
        const active = selectedKey === entry.key;
        const { icon: Icon, color } = getFileIcon(entry.type);
        return (
          <button
            key={entry.key}
            onClick={() => onSelect(entry.key)}
            onDoubleClick={() => handleDoubleClick(entry)}
            className={`w-full flex items-center gap-2.5 pl-7 pr-3 py-2.5 rounded-[14px] text-[13px] transition-all duration-150 ${
              active
                ? 'bg-[var(--accent-icon-surface,#EBF0FF)] font-semibold text-gray-800'
                : 'text-gray-600 hover:bg-[#FAF8F4]'
            }`}
          >
            <Icon size={14} className={`flex-shrink-0 ${color}`} />
            <span className="flex-1 text-left truncate">{getDisplayName(entry)}</span>
          </button>
        );
      })}
    </div>
  );
}
