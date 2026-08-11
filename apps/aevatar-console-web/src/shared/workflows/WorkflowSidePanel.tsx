import { CloseOutlined } from '@ant-design/icons';
import { Button, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';

type WorkflowSidePanelProps = {
  readonly ariaLabel: string;
  readonly bodyStyle?: React.CSSProperties;
  readonly children: React.ReactNode;
  readonly closeAriaLabel?: string;
  readonly closeDisabled?: boolean;
  readonly height?: React.CSSProperties['height'];
  readonly onClose: () => void;
  readonly subtitle?: React.ReactNode;
  readonly title: React.ReactNode;
  readonly width: number;
};

export const workflowSidePanelBodyStyle: React.CSSProperties = {
  display: 'grid',
  flex: 1,
  gap: 16,
  minHeight: 0,
  overflow: 'auto',
  padding: '18px 20px 20px',
};

const WorkflowSidePanel: React.FC<WorkflowSidePanelProps> = ({
  ariaLabel,
  bodyStyle,
  children,
  closeAriaLabel,
  closeDisabled = false,
  height,
  onClose,
  subtitle,
  title,
  width,
}) => (
  <aside
    aria-label={ariaLabel}
    style={{
      background: '#ffffff',
      borderLeft: '1px solid #e5e7eb',
      boxShadow: '-12px 0 28px rgba(15, 23, 42, 0.08)',
      display: 'flex',
      flex: `0 0 ${width}px`,
      flexDirection: 'column',
      height,
      minHeight: 0,
      overflow: 'hidden',
      position: 'relative',
      width,
      zIndex: 2,
    }}
  >
    <header
      style={{
        alignItems: 'flex-start',
        borderBottom: '1px solid #eef2f7',
        display: 'flex',
        gap: 12,
        justifyContent: 'space-between',
        padding: '16px 20px 14px',
      }}
    >
      <div style={{ display: 'grid', gap: 4, minWidth: 0 }}>
        <Typography.Text strong style={{ color: '#111827', fontSize: 16 }}>
          {title}
        </Typography.Text>
        {subtitle ? (
          <Typography.Text style={{ color: '#6b7280' }}>
            {subtitle}
          </Typography.Text>
        ) : null}
      </div>
      <Button
        aria-label={
          closeAriaLabel || t('teamMemberWorkflowStudio.common.close', 'Close')
        }
        disabled={closeDisabled}
        icon={<CloseOutlined />}
        onClick={onClose}
        size="small"
        style={{ height: 28, width: 28 }}
        type="text"
      />
    </header>
    <div
      style={{
        ...workflowSidePanelBodyStyle,
        ...bodyStyle,
      }}
    >
      {children}
    </div>
  </aside>
);

export default WorkflowSidePanel;
