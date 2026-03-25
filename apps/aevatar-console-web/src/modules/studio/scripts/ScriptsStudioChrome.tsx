import {
  CloseOutlined,
  DownOutlined,
} from '@ant-design/icons';
import React from 'react';

export function ScriptsStudioEmptyState(props: {
  title: string;
  copy: string;
}): React.JSX.Element {
  return (
    <div className="console-scripts-empty">
      <div className="console-scripts-empty-title">{props.title}</div>
      <div className="console-scripts-empty-copy">{props.copy}</div>
    </div>
  );
}

export function ScriptsStudioResultCard(props: {
  active: boolean;
  title: string;
  summary: string;
  meta: string;
  status?: string;
  onClick: () => void;
}): React.JSX.Element {
  return (
    <button
      type="button"
      onClick={props.onClick}
      className={`console-scripts-run-card ${props.active ? 'active' : ''}`}
      aria-label={props.title}
    >
      <div className="console-scripts-run-card-head">
        <div className="console-scripts-run-card-copy">
          <div className="console-scripts-run-card-title">{props.title}</div>
          <div className="console-scripts-run-card-meta">{props.meta}</div>
        </div>
        {props.status ? (
          <span className="console-scripts-run-card-status">{props.status}</span>
        ) : null}
      </div>
      <div className="console-scripts-run-card-summary">{props.summary}</div>
    </button>
  );
}

export function ScriptsStudioSection(props: {
  eyebrow?: string;
  title: string;
  children: React.ReactNode;
  actions?: React.ReactNode;
  defaultOpen?: boolean;
  bodyClassName?: string;
}): React.JSX.Element {
  const [open, setOpen] = React.useState(props.defaultOpen ?? true);

  return (
    <section className="console-scripts-section">
      <div className="console-scripts-section-head">
        <div className="console-scripts-section-title-wrap">
          {props.eyebrow ? (
            <div className="console-scripts-eyebrow">{props.eyebrow}</div>
          ) : null}
          <div className="console-scripts-section-title">{props.title}</div>
        </div>
        <div className="console-scripts-section-actions">
          {props.actions}
          <button
            type="button"
            onClick={() => setOpen((value) => !value)}
            className="console-scripts-icon-button"
            aria-expanded={open}
            aria-label={props.title}
            title={open ? `Collapse ${props.title}` : `Expand ${props.title}`}
          >
            <DownOutlined
              className={`console-scripts-chevron ${open ? '' : 'collapsed'}`}
            />
          </button>
        </div>
      </div>
      {open ? (
        <div
          className={
            props.bodyClassName || 'console-scripts-section-body'
          }
        >
          {props.children}
        </div>
      ) : null}
    </section>
  );
}

export function ScriptsStudioModal(props: {
  open: boolean;
  eyebrow: string;
  title: string;
  onClose: () => void;
  children: React.ReactNode;
  actions?: React.ReactNode;
  width?: number | string;
}): React.JSX.Element | null {
  if (!props.open) {
    return null;
  }

  return (
    <div className="console-scripts-modal-overlay" onClick={props.onClose}>
      <div
        className="console-scripts-modal-shell"
        style={props.width ? { width: props.width } : undefined}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="console-scripts-modal-head">
          <div>
            <div className="console-scripts-eyebrow">{props.eyebrow}</div>
            <div className="console-scripts-modal-title">{props.title}</div>
          </div>
          <button
            type="button"
            onClick={props.onClose}
            title="Close dialog"
            aria-label="Close dialog"
            className="console-scripts-icon-button"
          >
            <CloseOutlined />
          </button>
        </div>
        <div className="console-scripts-modal-body">{props.children}</div>
        {props.actions ? (
          <div className="console-scripts-modal-actions">{props.actions}</div>
        ) : null}
      </div>
    </div>
  );
}
