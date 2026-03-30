import { Settings2, UserCircle, ArrowRightLeft, Bot, FolderOpen } from 'lucide-react';
import type { ConfigFile } from './types';

type Props = {
  scopeId: string;
  selectedFile: ConfigFile;
  onSelect: (file: ConfigFile) => void;
  isDirty: (file: ConfigFile) => boolean;
};

const FILES: { file: ConfigFile; label: string; icon: typeof Settings2 }[] = [
  { file: 'config.json', label: 'config.json', icon: Settings2 },
  { file: 'roles.json', label: 'roles.json', icon: UserCircle },
  { file: 'connectors.json', label: 'connectors.json', icon: ArrowRightLeft },
  { file: 'actors.json', label: 'actors.json', icon: Bot },
];

const FILE_COLORS: Record<ConfigFile, string> = {
  'config.json': 'text-blue-500',
  'roles.json': 'text-violet-500',
  'connectors.json': 'text-emerald-500',
  'actors.json': 'text-orange-500',
};

export default function FileTree({ scopeId, selectedFile, onSelect, isDirty }: Props) {
  const shortScope = scopeId.length > 24 ? scopeId.slice(0, 10) + '...' + scopeId.slice(-10) : scopeId;

  return (
    <div className="space-y-1 select-none">
      {/* Folder header */}
      <div className="flex items-center gap-2 px-3 py-2 text-[12px] font-semibold text-gray-500">
        <FolderOpen size={14} className="text-amber-500 flex-shrink-0" />
        <span className="truncate" title={scopeId}>{shortScope || 'scope'}/</span>
      </div>

      {/* Files */}
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
    </div>
  );
}
