import { useState } from 'react';
import { Loader2, ExternalLink, Pencil, Save, X, Trash2 } from 'lucide-react';
import type { ManifestEntry } from './types';
import type { MediaInfo } from './useConfigStore';
import { detectMediaKind } from './contentFormatting';
import RolesCatalogEditor from './editors/RolesCatalogEditor';
import ConnectorsCatalogEditor from './editors/ConnectorsCatalogEditor';
import UserConfigEditor from './editors/UserConfigEditor';
import ExplorerContentView from './ExplorerContentView';

type Props = {
  selectedKey: string | null;
  content: string | null;
  mediaInfo?: MediaInfo | null;
  loading: boolean;
  manifest: ManifestEntry[];
  onOpenInStudio?: (type: string, key: string) => void;
  onSave?: (key: string, content: string) => Promise<void>;
  onDelete?: (key: string) => Promise<void>;
  flash: (msg: string, type: 'success' | 'error') => void;
};

export default function EditorPanel({ selectedKey, content, mediaInfo, loading, manifest, onOpenInStudio, onSave, onDelete, flash }: Props) {
  const [editing, setEditing] = useState(false);
  const [editContent, setEditContent] = useState('');
  const [saving, setSaving] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (!selectedKey) {
    return (
      <div className="py-16 flex items-center justify-center text-[14px] text-gray-400">
        Select a file to view
      </div>
    );
  }

  if (loading) {
    return (
      <div className="py-12 flex flex-col items-center justify-center gap-2 text-[13px] text-gray-400">
        <Loader2 size={24} className="animate-spin text-gray-400" />
        <span>Loading file...</span>
      </div>
    );
  }

  const entry = manifest.find(f => f.key === selectedKey);
  const fileType = entry?.type ?? 'file';
  const isMediaFile = !!mediaInfo || (selectedKey ? detectMediaKind(selectedKey) !== null && detectMediaKind(selectedKey) !== 'markdown' : false);
  const isReadOnly = fileType === 'workflow' || fileType === 'script' || isMediaFile;

  // Rich editors for specific file types
  if (fileType === 'roles') return <RolesCatalogEditor flash={flash} />;
  if (fileType === 'connectors') return <ConnectorsCatalogEditor flash={flash} />;
  if (fileType === 'config') return <UserConfigEditor flash={flash} />;

  const fileName = selectedKey.split('/').pop() || selectedKey;

  function startEditing() {
    setEditContent(content ?? '');
    setEditing(true);
  }

  function cancelEditing() {
    setEditing(false);
    setEditContent('');
    setConfirmDelete(false);
  }

  async function handleSave() {
    if (!onSave) return;
    setSaving(true);
    try {
      await onSave(selectedKey!, editContent);
      setEditing(false);
      flash('Saved', 'success');
    } catch (e: any) {
      flash(e?.message || 'Save failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete() {
    if (!onDelete) return;
    setSaving(true);
    try {
      await onDelete(selectedKey!);
      setEditing(false);
      setConfirmDelete(false);
      flash('Deleted', 'success');
    } catch (e: any) {
      flash(e?.message || 'Delete failed', 'error');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="max-w-[780px] space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em] text-gray-400">{fileType}</div>
          <div className="text-[16px] font-bold text-gray-800 mt-0.5">{fileName}</div>
          {entry?.updatedAt && (
            <div className="text-[11px] text-gray-400 mt-1">{new Date(entry.updatedAt).toLocaleString()}</div>
          )}
        </div>
        <div className="flex items-center gap-2">
          {(fileType === 'workflow' || fileType === 'script') && onOpenInStudio && (
            <button
              onClick={() => onOpenInStudio(fileType, selectedKey)}
              className="ghost-action !min-h-[34px]"
            >
              <ExternalLink size={12} /> Open in Studio
            </button>
          )}
          {isMediaFile && onDelete && (
            confirmDelete ? (
              <button
                onClick={handleDelete}
                disabled={saving}
                className="inline-flex items-center gap-1.5 rounded-lg bg-red-600 px-3 py-1.5 text-[12px] font-semibold text-white hover:bg-red-700 disabled:opacity-50"
              >
                {saving ? <Loader2 size={12} className="animate-spin" /> : null} Confirm Delete
              </button>
            ) : (
              <button
                onClick={() => setConfirmDelete(true)}
                className="inline-flex items-center gap-1.5 rounded-lg border border-red-200 bg-white px-3 py-1.5 text-[12px] font-semibold text-red-600 hover:bg-red-50"
              >
                <Trash2 size={12} /> Delete
              </button>
            )
          )}
          {!isReadOnly && !editing ? (
            <button
              onClick={startEditing}
              className="solid-action !min-h-[34px]"
            >
              <Pencil size={12} /> Edit
            </button>
          ) : !isReadOnly && editing ? (
            <>
              <button
                onClick={handleSave}
                disabled={saving}
                className="solid-action !min-h-[34px] disabled:opacity-50"
              >
                {saving ? <Loader2 size={12} className="animate-spin" /> : <Save size={12} />} Save
              </button>
              <button
                onClick={cancelEditing}
                className="ghost-action !min-h-[34px]"
              >
                <X size={12} /> Cancel
              </button>
              {onDelete ? (
                confirmDelete ? (
                  <button
                    onClick={handleDelete}
                    disabled={saving}
                    className="inline-flex items-center gap-1.5 rounded-lg bg-red-600 px-3 py-1.5 text-[12px] font-semibold text-white hover:bg-red-700 disabled:opacity-50"
                  >
                    Confirm Delete
                  </button>
                ) : (
                  <button
                    onClick={() => setConfirmDelete(true)}
                    className="inline-flex items-center gap-1.5 rounded-lg border border-red-200 bg-white px-3 py-1.5 text-[12px] font-semibold text-red-600 hover:bg-red-50"
                  >
                    <Trash2 size={12} /> Delete
                  </button>
                )
              ) : null}
            </>
          ) : null}
        </div>
      </div>

      {/* Content */}
      <div className="workspace-editor-frame">
        {editing ? (
          <textarea
            value={editContent}
            onChange={e => setEditContent(e.target.value)}
            className="panel-textarea !min-h-[400px] !max-h-[70vh] !border-0 !bg-transparent !shadow-none p-4"
            spellCheck={false}
          />
        ) : (
          <ExplorerContentView fileType={fileType} content={content} mediaInfo={mediaInfo} fileKey={selectedKey} />
        )}
      </div>
    </div>
  );
}
