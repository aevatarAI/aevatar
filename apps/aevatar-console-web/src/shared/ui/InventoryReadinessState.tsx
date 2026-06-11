import {
  Alert,
  Button,
  Empty,
  Skeleton,
  Space,
  Typography,
  theme,
} from "antd";
import React from "react";

export type InventoryReadinessKind = "empty" | "error" | "loading";

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

  if (kind === "loading") {
    return (
      <div
        aria-busy="true"
        style={{
          display: "flex",
          flexDirection: "column",
          gap: 14,
          padding: 24,
        }}
      >
        <Space direction="vertical" size={4}>
          <Typography.Text strong style={{ color: token.colorTextHeading }}>
            {title}
          </Typography.Text>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {description}
          </Typography.Text>
        </Space>
        <Skeleton active paragraph={{ rows: 4 }} title={false} />
      </div>
    );
  }

  if (kind === "error") {
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
