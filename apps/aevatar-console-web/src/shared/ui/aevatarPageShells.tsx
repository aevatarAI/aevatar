import { PageContainer, ProCard } from "@ant-design/pro-components";
import {
  Drawer,
  Empty,
  Grid,
  Space,
  Tag,
  Typography,
  theme,
} from "antd";
import React from "react";
import {
  AEVATAR_GLOBAL_UI_SPEC,
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";

type AevatarPageShellProps = {
  children: React.ReactNode;
  content?: React.ReactNode;
  extra?: React.ReactNode;
  onBack?: () => void;
  title: React.ReactNode;
};

type AevatarWorkbenchLayoutProps = {
  rail: React.ReactNode;
  railWidth?: number;
  stage: React.ReactNode;
  stageAside?: React.ReactNode;
  stageAsideWidth?: number;
};

type AevatarPanelProps = {
  children: React.ReactNode;
  description?: React.ReactNode;
  extra?: React.ReactNode;
  ghost?: boolean;
  minHeight?: number | string;
  padding?: number | string;
  title?: React.ReactNode;
};

type AevatarContextDrawerProps = {
  children: React.ReactNode;
  extra?: React.ReactNode;
  onClose: () => void;
  open: boolean;
  subtitle?: React.ReactNode;
  title: React.ReactNode;
  width?: number;
};

type AevatarStatusTagProps = {
  domain: AevatarStatusDomain;
  label?: string;
  status: string;
};

const pageContentStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
  minHeight: 0,
};

const panelInnerStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 12,
  minHeight: 0,
};

const sectionHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
  width: "100%",
};

const stageCellStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  minHeight: 0,
};

export const AevatarPageShell: React.FC<AevatarPageShellProps> = ({
  children,
  content,
  extra,
  onBack,
  title,
}) => (
  <PageContainer
    content={content}
    extra={extra ? [extra] : undefined}
    onBack={onBack}
    title={title}
  >
    <div style={pageContentStyle}>{children}</div>
  </PageContainer>
);

export const AevatarWorkbenchLayout: React.FC<AevatarWorkbenchLayoutProps> = ({
  rail,
  railWidth = 320,
  stage,
  stageAside,
  stageAsideWidth = 320,
}) => {
  const screens = Grid.useBreakpoint();
  const showRailColumn = screens.lg;
  const showStageAsideColumn = Boolean(stageAside && screens.xxl);
  const gridTemplateColumns = showStageAsideColumn
    ? `${railWidth}px minmax(0, 1fr) ${stageAsideWidth}px`
    : showRailColumn
      ? `${railWidth}px minmax(0, 1fr)`
      : "minmax(0, 1fr)";

  return (
    <div
      style={{
        display: "grid",
        flex: 1,
        gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
        gridTemplateColumns,
        minHeight: 0,
      }}
    >
      {showRailColumn ? <div style={stageCellStyle}>{rail}</div> : null}
      <div style={stageCellStyle}>
        {!showRailColumn ? rail : null}
        <div style={stageCellStyle}>{stage}</div>
        {!showStageAsideColumn && stageAside ? (
          <div style={{ ...stageCellStyle, marginTop: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap }}>
            {stageAside}
          </div>
        ) : null}
      </div>
      {showStageAsideColumn && stageAside ? (
        <div style={stageCellStyle}>{stageAside}</div>
      ) : null}
    </div>
  );
};

export const AevatarPanel: React.FC<AevatarPanelProps> = ({
  children,
  description,
  extra,
  ghost = false,
  minHeight,
  padding = 16,
  title,
}) => {
  const { token } = theme.useToken();

  return (
    <ProCard
      bodyStyle={{ minHeight: 0, padding: ghost ? 0 : undefined }}
      ghost={ghost}
      style={
        ghost
          ? undefined
          : buildAevatarPanelStyle(token as AevatarThemeSurfaceToken, {
              minHeight,
              padding,
            })
      }
    >
      <div style={panelInnerStyle}>
        {title || description || extra ? (
          <div style={sectionHeaderStyle}>
            <Space direction="vertical" size={4} style={{ minWidth: 0 }}>
              {title ? <Typography.Text strong>{title}</Typography.Text> : null}
              {description ? (
                <Typography.Paragraph
                  style={{ color: token.colorTextSecondary, margin: 0 }}
                >
                  {description}
                </Typography.Paragraph>
              ) : null}
            </Space>
            {extra ? <div>{extra}</div> : null}
          </div>
        ) : null}
        {children}
      </div>
    </ProCard>
  );
};

export const AevatarContextDrawer: React.FC<AevatarContextDrawerProps> = ({
  children,
  extra,
  onClose,
  open,
  subtitle,
  title,
  width = AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth,
}) => {
  const { token } = theme.useToken();

  return (
    <Drawer
      destroyOnHidden
      onClose={onClose}
      open={open}
      size={width >= AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth ? "large" : "default"}
      styles={{ body: aevatarDrawerBodyStyle }}
      title={
        <Space direction="vertical" size={2}>
          <Typography.Text strong>{title}</Typography.Text>
          {subtitle ? (
            <Typography.Text style={{ color: token.colorTextSecondary }}>
              {subtitle}
            </Typography.Text>
          ) : null}
        </Space>
      }
      extra={extra}
    >
      <div style={aevatarDrawerScrollStyle}>{children}</div>
    </Drawer>
  );
};

export const AevatarStatusTag: React.FC<AevatarStatusTagProps> = ({
  domain,
  label,
  status,
}) => {
  const { token } = theme.useToken();

  return (
    <Tag
      bordered
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        domain,
        status,
      )}
    >
      {label ?? formatAevatarStatusLabel(status)}
    </Tag>
  );
};

export const AevatarInspectorEmpty: React.FC<{
  description: React.ReactNode;
  title?: React.ReactNode;
}> = ({ description, title = "Select an item" }) => {
  const { token } = theme.useToken();

  return (
    <Empty
      description={
        <Space direction="vertical" size={4}>
          <Typography.Text strong>{title}</Typography.Text>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {description}
          </Typography.Text>
        </Space>
      }
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );
};
