import { Alert, Button, Empty, Space, Typography, theme } from 'antd';
import React from 'react';
import AevatarContentSkeleton from './AevatarContentSkeleton';

export type InventoryReadinessKind = 'empty' | 'error' | 'loading';

type InventoryReadinessAction = {
  label: string;
  onClick: () => void;
};

export type InventoryReadinessStateProps = {
  action?: InventoryReadinessAction;
  description: React.ReactNode;
  kind: InventoryReadinessKind;
  title: React.ReactNode;
};

export const InventoryReadinessState: React.FC<
  InventoryReadinessStateProps
> = ({ action, description, kind, title }) => {
  const { token } = theme.useToken();

  if (kind === 'loading') {
    return (
      <AevatarContentSkeleton
        ariaLabel={
          typeof title === 'string' || typeof title === 'number'
            ? String(title)
            : 'Loading inventory'
        }
        columnWidths={[96, 136, '1.4fr', '1fr', '1fr', 96, 152, 128]}
        rows={4}
        tableMinWidth={1100}
        variant="table"
      />
    );
  }

  if (kind === 'error') {
    return (
      <div style={{ padding: 18 }}>
        <Alert
          action={
            action ? (
              <Button size="small" onClick={action.onClick}>
                {action.label}
              </Button>
            ) : undefined
          }
          description={description}
          message={title}
          showIcon
          type="error"
        />
      </div>
    );
  }

  return (
    <Empty
      description={
        <Space direction="vertical" size={8}>
          <Typography.Text strong>{title}</Typography.Text>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {description}
          </Typography.Text>
          {action ? (
            <Button size="small" type="primary" onClick={action.onClick}>
              {action.label}
            </Button>
          ) : null}
        </Space>
      }
      image={Empty.PRESENTED_IMAGE_SIMPLE}
      style={{ padding: 24 }}
    />
  );
};

export default InventoryReadinessState;
