import type { ReactNode } from 'react';
import { X } from 'lucide-react';

export function EmptyState(props: { title: string; copy: string; }) {
  return (
    <div className="flex h-full min-h-[180px] items-center justify-center rounded-[24px] border border-[#EEEAE4] bg-[#FAF8F4] p-6 text-center">
      <div className="max-w-[360px]">
        <div className="text-[14px] font-semibold text-gray-800">{props.title}</div>
        <div className="mt-2 text-[12px] leading-6 text-gray-500">{props.copy}</div>
      </div>
    </div>
  );
}

export function StudioResultCard(props: {
  active: boolean;
  title: string;
  summary: string;
  meta: string;
  status?: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={props.onClick}
      className={`execution-run-card ${props.active ? 'active' : ''}`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-[13px] font-semibold text-gray-800">{props.title}</div>
          <div className="mt-1 text-[11px] text-gray-400">{props.meta}</div>
        </div>
        {props.status ? (
          <span className="rounded-full border border-[#E5DED3] bg-[#F7F2E8] px-2.5 py-1 text-[10px] uppercase tracking-[0.14em] text-[#8E6A3D]">
            {props.status}
          </span>
        ) : null}
      </div>
      <div className="mt-3 text-[12px] leading-6 text-gray-600">{props.summary}</div>
    </button>
  );
}

export function ScriptsStudioModal(props: {
  open: boolean;
  eyebrow: string;
  title: string;
  onClose: () => void;
  children: ReactNode;
  actions?: ReactNode;
  width?: string;
}) {
  if (!props.open) {
    return null;
  }

  return (
    <div className="modal-overlay" onClick={props.onClose}>
      <div className="modal-shell" style={props.width ? { width: props.width } : undefined} onClick={event => event.stopPropagation()}>
        <div className="modal-header">
          <div>
            <div className="panel-eyebrow">{props.eyebrow}</div>
            <div className="panel-title !mt-0">{props.title}</div>
          </div>
          <button type="button" onClick={props.onClose} title="Close dialog." className="panel-icon-button">
            <X size={16} />
          </button>
        </div>
        <div className="modal-body">{props.children}</div>
        <div className="modal-footer">{props.actions}</div>
      </div>
    </div>
  );
}
