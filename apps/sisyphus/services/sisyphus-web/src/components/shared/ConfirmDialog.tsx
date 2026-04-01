import { Loader2, AlertTriangle } from 'lucide-react'

interface ConfirmDialogProps {
  title: string
  message: string
  confirmLabel?: string
  cancelLabel?: string
  loading?: boolean
  onConfirm: () => void
  onCancel: () => void
}

export default function ConfirmDialog({
  title,
  message,
  confirmLabel = 'Delete',
  cancelLabel = 'Cancel',
  loading = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" style={{ background: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(4px)' }}>
      <div
        className="w-[400px] rounded-lg overflow-hidden animate-scale-in"
        style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)' }}
      >
        <div className="flex items-center gap-3 px-5 py-4" style={{ borderBottom: '1px solid var(--border-default)' }}>
          <AlertTriangle size={18} style={{ color: 'var(--accent-red)' }} />
          <span className="text-sm font-semibold" style={{ color: 'var(--text-primary)' }}>{title}</span>
        </div>
        <div className="px-5 py-4">
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }} dangerouslySetInnerHTML={{ __html: message }} />
        </div>
        <div className="flex items-center justify-end gap-2 px-5 py-3" style={{ borderTop: '1px solid var(--border-default)' }}>
          <button onClick={onCancel} disabled={loading} className="btn-secondary text-xs py-1.5 px-3">
            {cancelLabel}
          </button>
          <button
            onClick={onConfirm}
            disabled={loading}
            className="text-xs py-1.5 px-3 rounded-md font-medium flex items-center gap-1.5 disabled:opacity-50"
            style={{ background: 'rgba(252,165,165,0.15)', color: 'var(--neon-red)', border: '1px solid rgba(252,165,165,0.3)' }}
          >
            {loading && <Loader2 size={13} className="animate-spin" />}
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
