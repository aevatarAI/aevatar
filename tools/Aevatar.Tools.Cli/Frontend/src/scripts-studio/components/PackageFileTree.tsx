import { FileCode2, FileText, Pencil, Plus, Star, Trash2 } from 'lucide-react';
import type { ScriptPackageEntry } from '../models';

export function PackageFileTree(props: {
  entries: ScriptPackageEntry[];
  selectedFilePath: string;
  entrySourcePath: string;
  onSelectFile: (filePath: string) => void;
  onAddFile: (kind: 'csharp' | 'proto') => void;
  onRenameFile: (filePath: string) => void;
  onRemoveFile: (filePath: string) => void;
  onSetEntry: (filePath: string) => void;
}) {
  return (
    <div className="flex h-full min-h-0 flex-col border-r border-[#EEEAE4] bg-[#FAF8F4]">
      <div className="border-b border-[#EEEAE4] px-4 py-4">
        <div className="panel-eyebrow">Package</div>
        <div className="mt-1 flex items-center justify-between gap-3">
          <div className="text-[14px] font-semibold text-gray-800">Files</div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => props.onAddFile('csharp')}
              title="Add C# file"
              className="panel-icon-button h-8 w-8 rounded-[12px] border border-[#E8E4DD] bg-white text-gray-600"
            >
              <Plus size={14} />
            </button>
            <button
              type="button"
              onClick={() => props.onAddFile('proto')}
              title="Add proto file"
              className="panel-icon-button h-8 w-8 rounded-[12px] border border-[#E8E4DD] bg-white text-gray-600"
            >
              <FileText size={14} />
            </button>
          </div>
        </div>
      </div>

      <div className="min-h-0 flex-1 space-y-2 overflow-y-auto px-3 py-3">
        {props.entries.length === 0 ? (
          <div className="rounded-[18px] border border-dashed border-[#E5DED3] bg-white/70 px-4 py-4 text-[12px] leading-6 text-gray-500">
            Add a C# or proto file to turn this draft into a script package.
          </div>
        ) : props.entries.map(entry => {
          const active = props.selectedFilePath === entry.path;
          const isEntry = entry.kind === 'csharp' && props.entrySourcePath === entry.path;
          return (
            <div
              key={`${entry.kind}:${entry.path}`}
              className={`rounded-[18px] border px-3 py-3 transition-colors ${
                active
                  ? 'border-[color:var(--accent-border)] bg-[#FFF4F1]'
                  : 'border-[#EEEAE4] bg-white hover:bg-[#FFF9F4]'
              }`}
            >
              <button
                type="button"
                onClick={() => props.onSelectFile(entry.path)}
                className="flex w-full items-start gap-3 text-left"
              >
                <div className={`mt-0.5 rounded-[10px] p-2 ${
                  entry.kind === 'csharp'
                    ? 'bg-[#F7EDE4] text-[#9B4D19]'
                    : 'bg-[#EEF3FF] text-[#315A84]'
                }`}>
                  {entry.kind === 'csharp' ? <FileCode2 size={14} /> : <FileText size={14} />}
                </div>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13px] font-medium text-gray-800">{entry.path}</div>
                  <div className="mt-1 text-[11px] uppercase tracking-[0.14em] text-gray-400">
                    {entry.kind === 'csharp' ? 'C# source' : 'Proto schema'}
                  </div>
                </div>
              </button>

              <div className="mt-3 flex items-center justify-between gap-2">
                <div className="text-[11px] text-gray-400">
                  {isEntry ? 'Entry source' : '\u00a0'}
                </div>
                <div className="flex items-center gap-2">
                  {entry.kind === 'csharp' ? (
                    <button
                      type="button"
                      onClick={() => props.onSetEntry(entry.path)}
                      title="Use as entry source"
                      className={`panel-icon-button h-8 w-8 rounded-[12px] border ${
                        isEntry
                          ? 'border-[#E9D6AE] bg-[#FFF7E6] text-[#9B6A1C]'
                          : 'border-[#E8E4DD] bg-white text-gray-500'
                      }`}
                    >
                      <Star size={14} />
                    </button>
                  ) : null}
                  <button
                    type="button"
                    onClick={() => props.onRenameFile(entry.path)}
                    title="Rename file"
                    className="panel-icon-button h-8 w-8 rounded-[12px] border border-[#E8E4DD] bg-white text-gray-500"
                  >
                    <Pencil size={14} />
                  </button>
                  <button
                    type="button"
                    onClick={() => props.onRemoveFile(entry.path)}
                    title="Remove file"
                    className="panel-icon-button h-8 w-8 rounded-[12px] border border-[#F0D7D0] bg-white text-[#B15647]"
                  >
                    <Trash2 size={14} />
                  </button>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
