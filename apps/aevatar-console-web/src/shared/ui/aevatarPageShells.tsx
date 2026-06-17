import { ArrowLeftOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { PageContainer, ProCard } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Empty,
  Grid,
  Space,
  Tag,
  Tooltip,
  Typography,
  theme,
} from 'antd';
import React from 'react';
import {
  AEVATAR_GLOBAL_UI_SPEC,
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from '@/shared/ui/aevatarWorkbench';
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from '@/shared/ui/interactionStandards';
import { t } from "@/shared/i18n/messages";

export type AevatarLayoutMode = 'viewport' | 'document';

const AevatarLayoutModeContext =
  React.createContext<AevatarLayoutMode>('viewport');

type AevatarPageShellProps = {
  breadcrumbRender?: false;
  children: React.ReactNode;
  content?: React.ReactNode;
  extra?: React.ReactNode;
  layoutMode?: AevatarLayoutMode;
  onBack?: () => void;
  pageHeaderRender?: false;
  title: React.ReactNode;
  titleHelp?: React.ReactNode;
};

type AevatarWorkbenchLayoutProps = {
  layoutMode?: AevatarLayoutMode;
  rail: React.ReactNode;
  railWidth?: number;
  stage: React.ReactNode;
  stageAside?: React.ReactNode;
  stageAsideWidth?: number;
};

type AevatarTwoPaneLayoutProps = {
  layoutMode?: AevatarLayoutMode;
  rail: React.ReactNode;
  railWidth?: number;
  stage: React.ReactNode;
};

type AevatarPanelProps = {
  children: React.ReactNode;
  description?: React.ReactNode;
  extra?: React.ReactNode;
  ghost?: boolean;
  layoutMode?: AevatarLayoutMode;
  minHeight?: number | string;
  padding?: number | string;
  style?: React.CSSProperties;
  title?: React.ReactNode;
  titleHelp?: React.ReactNode;
};

type AevatarContextDrawerProps = {
  children: React.ReactNode;
  extra?: React.ReactNode;
  mobilePlacement?: 'bottom' | 'right';
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

type AevatarBackButtonProps = {
  ariaLabel?: string;
  className?: string;
  onBack: () => void;
  style?: React.CSSProperties;
  title?: React.ReactNode;
};

const pageContentViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
  height: '100%',
  minHeight: 0,
};

const pageContentDocumentStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
  height: 'auto',
  minHeight: 'fit-content',
  width: '100%',
};

const pageContainerViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
};

const pageContainerDocumentStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  height: 'auto',
  minHeight: 'fit-content',
  width: '100%',
};

const pageContainerChildrenViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
};

const pageContainerChildrenDocumentStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  height: 'auto',
  minHeight: 'fit-content',
  width: '100%',
};

const compactPageContainerChildrenDocumentStyle: React.CSSProperties = {
  ...pageContainerChildrenDocumentStyle,
  paddingInline: 0,
};

const panelInnerViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
};

const panelInnerDocumentStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minHeight: 'fit-content',
  width: '100%',
};

const sectionHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
  rowGap: 8,
  flexWrap: 'wrap',
  width: '100%',
};

const stageCellViewportStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
};

const stageCellDocumentStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  minHeight: 'fit-content',
};

const helpTriggerButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'transparent',
  border: 'none',
  cursor: 'help',
  display: 'inline-flex',
  justifyContent: 'center',
  lineHeight: 1,
  padding: 0,
};

const helpTooltipContentStyle: React.CSSProperties = {
  maxWidth: 320,
  whiteSpace: 'normal',
};

const titleRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'inline-flex',
  flexWrap: 'wrap',
  gap: 6,
  maxWidth: '100%',
};

const backButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'inline-flex',
  flex: '0 0 auto',
  height: 32,
  justifyContent: 'center',
  width: 32,
};

export const AevatarBackButton: React.FC<AevatarBackButtonProps> = ({
  ariaLabel,
  className,
  onBack,
  style,
  title,
}) => {
  const label = ariaLabel ?? t("shared.ui.aevatarpageshells.back", "Back");

  return (
    <Tooltip title={title ?? label}>
      <Button
        aria-label={label}
        className={className}
        data-aevatar-back-button="true"
        icon={<ArrowLeftOutlined />}
        onClick={onBack}
        size="small"
        style={{ ...backButtonStyle, ...style }}
        type="text"
      />
    </Tooltip>
  );
};

export const AevatarHelpTooltip: React.FC<{
  content: React.ReactNode;
}> = ({ content }) => {
  const { token } = theme.useToken();

  return (
    <Tooltip
      placement="topLeft"
      styles={{ container: helpTooltipContentStyle }}
      title={<div>{content}</div>}
    >
      <button
        aria-label={t("shared.ui.aevatarpageshells.show.help", "Show help")}
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
        style={{ ...helpTriggerButtonStyle, color: token.colorTextDescription }}
        type="button"
      >
        <InfoCircleOutlined />
      </button>
    </Tooltip>
  );
};

export const AevatarTitleWithHelp: React.FC<{
  help: React.ReactNode;
  title: React.ReactNode;
}> = ({ help, title }) => (
  <div style={titleRowStyle}>
    <span>{title}</span>
    <AevatarHelpTooltip content={help} />
  </div>
);

export const AevatarPageShell: React.FC<AevatarPageShellProps> = ({
  breadcrumbRender,
  children,
  content,
  extra,
  layoutMode = 'viewport',
  onBack,
  pageHeaderRender,
  title,
  titleHelp,
}) => {
  const screens = Grid.useBreakpoint();
  const useCompactDocumentPadding = layoutMode === 'document' && !screens.md;

  return (
    <AevatarLayoutModeContext.Provider value={layoutMode}>
      <PageContainer
        breadcrumbRender={breadcrumbRender}
        className={
          layoutMode === 'document'
            ? 'aevatar-page-shell aevatar-page-shell-document'
            : 'aevatar-page-shell aevatar-page-shell-viewport'
        }
        childrenContentStyle={
          layoutMode === 'document'
            ? useCompactDocumentPadding
              ? compactPageContainerChildrenDocumentStyle
              : pageContainerChildrenDocumentStyle
            : pageContainerChildrenViewportStyle
        }
        content={content}
        extra={extra}
        onBack={onBack}
        pageHeaderRender={pageHeaderRender}
        style={
          layoutMode === 'document'
            ? pageContainerDocumentStyle
            : pageContainerViewportStyle
        }
        title={
          titleHelp ? (
            <AevatarTitleWithHelp help={titleHelp} title={title} />
          ) : (
            title
          )
        }
      >
        <div
          style={
            layoutMode === 'document'
              ? pageContentDocumentStyle
              : pageContentViewportStyle
          }
        >
          {children}
        </div>
      </PageContainer>
    </AevatarLayoutModeContext.Provider>
  );
};

// Default console layout: keep one navigator rail and one primary stage.
export const AevatarTwoPaneLayout: React.FC<AevatarTwoPaneLayoutProps> = ({
  layoutMode,
  rail,
  railWidth,
  stage,
}) => {
  const inheritedLayoutMode = React.useContext(AevatarLayoutModeContext);

  return (
    <AevatarWorkbenchLayout
      layoutMode={layoutMode ?? inheritedLayoutMode}
      rail={rail}
      railWidth={railWidth}
      stage={stage}
    />
  );
};

export const AevatarWorkbenchLayout: React.FC<AevatarWorkbenchLayoutProps> = ({
  layoutMode,
  rail,
  railWidth = 320,
  stage,
  stageAside,
  stageAsideWidth = 320,
}) => {
  const inheritedLayoutMode = React.useContext(AevatarLayoutModeContext);
  const resolvedLayoutMode = layoutMode ?? inheritedLayoutMode;
  const screens = Grid.useBreakpoint();
  const showRailColumn = screens.lg;
  const showStageAsideColumn = Boolean(stageAside && screens.xxl);
  const gridTemplateColumns = showStageAsideColumn
    ? `${railWidth}px minmax(0, 1fr) ${stageAsideWidth}px`
    : showRailColumn
      ? `${railWidth}px minmax(0, 1fr)`
      : 'minmax(0, 1fr)';
  const stageCellStyle =
    resolvedLayoutMode === 'document'
      ? stageCellDocumentStyle
      : stageCellViewportStyle;

  return (
    <div
      style={{
        display: 'grid',
        gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
        gridTemplateColumns,
        minHeight: resolvedLayoutMode === 'document' ? 'fit-content' : 0,
        width: '100%',
        ...(resolvedLayoutMode === 'viewport' ? { flex: 1 } : {}),
        ...(resolvedLayoutMode === 'document'
          ? { alignItems: 'start' as const }
          : {}),
      }}
    >
      {showRailColumn ? <div style={stageCellStyle}>{rail}</div> : null}
      <div style={stageCellStyle}>
        {!showRailColumn ? rail : null}
        <div style={stageCellStyle}>{stage}</div>
        {!showStageAsideColumn && stageAside ? (
          <div
            style={{
              ...stageCellStyle,
              marginTop: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
            }}
          >
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
  layoutMode,
  minHeight,
  padding = 16,
  style,
  title,
  titleHelp,
}) => {
  const inheritedLayoutMode = React.useContext(AevatarLayoutModeContext);
  const resolvedLayoutMode = layoutMode ?? inheritedLayoutMode;
  const { token } = theme.useToken();
  const resolvedPanelMinHeight =
    minHeight ?? (resolvedLayoutMode === 'document' ? 'fit-content' : 0);
  const panelBodyStyle =
    resolvedLayoutMode === 'document'
      ? {
          height: 'auto',
          minHeight: 'fit-content',
          overflow: 'visible',
          padding: ghost ? 0 : undefined,
        }
      : {
          minHeight: 0,
          padding: ghost ? 0 : undefined,
        };
  const panelInnerStyle =
    resolvedLayoutMode === 'document'
      ? panelInnerDocumentStyle
      : panelInnerViewportStyle;

  return (
    <ProCard
      bodyStyle={panelBodyStyle}
      ghost={ghost}
      style={
        ghost
          ? undefined
          : {
              ...buildAevatarPanelStyle(token as AevatarThemeSurfaceToken, {
                minHeight: resolvedPanelMinHeight,
                overflow:
                  resolvedLayoutMode === 'document' ? 'visible' : 'hidden',
                padding,
              }),
              ...style,
            }
      }
    >
      <div style={panelInnerStyle}>
        {title || description || extra ? (
          <div style={sectionHeaderStyle}>
            <Space
              orientation="vertical"
              size={4}
              style={{ flex: 1, minWidth: 0 }}
            >
              {title || titleHelp ? (
                <div style={titleRowStyle}>
                  {title ? (
                    <Typography.Text strong>{title}</Typography.Text>
                  ) : null}
                  {titleHelp ? (
                    <AevatarHelpTooltip content={titleHelp} />
                  ) : null}
                </div>
              ) : null}
              {description ? (
                <Typography.Paragraph
                  style={{
                    color: token.colorTextSecondary,
                    margin: 0,
                    overflowWrap: 'anywhere',
                    wordBreak: 'break-word',
                  }}
                >
                  {description}
                </Typography.Paragraph>
              ) : null}
            </Space>
            {extra ? <div style={{ flexShrink: 0 }}>{extra}</div> : null}
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
  mobilePlacement = 'right',
  onClose,
  open,
  subtitle,
  title,
  width = AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth,
}) => {
  const screens = Grid.useBreakpoint();
  const { token } = theme.useToken();
  const placement = screens.md ? 'right' : mobilePlacement;
  const isBottomPlacement = placement === 'bottom';

  return (
    <Drawer
      destroyOnHidden
      onClose={onClose}
      open={open}
      placement={placement}
      rootClassName={`aevatar-context-drawer-${placement}`}
      size={
        !isBottomPlacement &&
        width >= AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth
          ? 'large'
          : 'default'
      }
      styles={{
        body: aevatarDrawerBodyStyle,
        ...(isBottomPlacement
          ? { wrapper: { height: '76vh', maxHeight: '88vh', width: '100%' } }
          : null),
      }}
      title={
        <Space orientation="vertical" size={2}>
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
  compact?: boolean;
  description: React.ReactNode;
  title?: React.ReactNode;
}> = ({ compact = false, description, title = 'Select an item' }) => {
  const { token } = theme.useToken();
  const emptyStyles = compact
    ? {
        image: { height: 32, marginBottom: 4 },
        root: { marginBlock: 4 },
      }
    : undefined;

  return (
    <Empty
      description={
        <Space orientation="vertical" size={compact ? 2 : 4}>
          <Typography.Text strong>{title}</Typography.Text>
          <Typography.Text
            style={{
              color: token.colorTextSecondary,
              fontSize: compact ? 13 : undefined,
            }}
          >
            {description}
          </Typography.Text>
        </Space>
      }
      image={Empty.PRESENTED_IMAGE_SIMPLE}
      styles={emptyStyles}
    />
  );
};
