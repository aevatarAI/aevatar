import { PlusOutlined } from '@ant-design/icons';
import { Button, Typography } from 'antd';
import React from 'react';

type WorkflowStudioEmptyStateProps = {
  readonly description?: string;
  readonly disabled?: boolean;
  readonly onAddFirstStep?: () => void;
  readonly title?: string;
};

const WorkflowStudioEmptyState: React.FC<WorkflowStudioEmptyStateProps> = ({
  description = 'Start this workflow by adding the first step.',
  disabled = false,
  onAddFirstStep,
  title = 'Add first step',
}) => (
  <div
    data-testid="workflow-studio-empty-state"
    style={{
      alignItems: 'center',
      display: 'flex',
      flexDirection: 'column',
      gap: 10,
      left: '50%',
      pointerEvents: 'auto',
      position: 'absolute',
      top: '50%',
      transform: 'translate(-50%, -50%)',
      zIndex: 5,
    }}
  >
    <Button
      aria-label={title}
      disabled={disabled}
      icon={<PlusOutlined />}
      onClick={onAddFirstStep}
      style={{
        border: '1px dashed #9ca3af',
        borderRadius: 8,
        height: 112,
        width: 112,
      }}
    />
    <Typography.Text strong style={{ color: '#1f2937', fontSize: 18 }}>
      {title}
    </Typography.Text>
    <Typography.Text
      style={{ color: '#6b7280', fontSize: 13, textAlign: 'center' }}
    >
      {description}
    </Typography.Text>
  </div>
);

export default WorkflowStudioEmptyState;
