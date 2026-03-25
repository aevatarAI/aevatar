import { Tooltip } from 'antd';
import React from 'react';

type ScriptsHeaderIconActionProps = {
  readonly ariaLabel: string;
  readonly children: React.ReactNode;
  readonly disabled?: boolean;
  readonly onClick: () => void;
  readonly tone?: 'default' | 'emphasis';
  readonly tooltip: React.ReactNode;
};

const ScriptsHeaderIconAction: React.FC<ScriptsHeaderIconActionProps> = ({
  ariaLabel,
  children,
  disabled = false,
  onClick,
  tone = 'default',
  tooltip,
}) => (
  <Tooltip title={tooltip} placement="bottom">
    <span className="console-scripts-tooltip-anchor">
      <button
        type="button"
        onClick={onClick}
        className={`console-scripts-icon-button console-scripts-header-action ${
          tone === 'emphasis' ? 'console-scripts-header-action-emphasis' : ''
        }`.trim()}
        aria-label={ariaLabel}
        disabled={disabled}
      >
        {children}
      </button>
    </span>
  </Tooltip>
);

export default ScriptsHeaderIconAction;
