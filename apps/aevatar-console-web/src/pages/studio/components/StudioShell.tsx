import {
  ApiOutlined,
  BuildOutlined,
  CodeOutlined,
  DoubleRightOutlined,
  NodeIndexOutlined,
  SettingOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { Col, Row, Tooltip, Typography } from 'antd';
import React from 'react';
import { cardStackStyle, stretchColumnStyle } from '@/shared/ui/proComponents';

export type StudioWorkspacePage =
  | 'workflows'
  | 'studio'
  | 'scripts'
  | 'roles'
  | 'connectors'
  | 'settings';

export type StudioShellNavItem = {
  readonly key: StudioWorkspacePage;
  readonly label: string;
  readonly description: string;
  readonly count?: React.ReactNode;
};

type StudioShellProps = {
  readonly alerts?: React.ReactNode;
  readonly contentOverflow?: 'auto' | 'hidden';
  readonly currentPage: StudioWorkspacePage;
  readonly navItems: readonly StudioShellNavItem[];
  readonly onSelectPage: (page: StudioWorkspacePage) => void;
  readonly pageTitle: string;
  readonly pageToolbar?: React.ReactNode;
  readonly showPageHeader?: boolean;
  readonly children: React.ReactNode;
};

const workbenchShellStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-layout, transparent)',
  borderRadius: 16,
  display: 'flex',
  flexDirection: 'column',
  height: '100%',
  justifyContent: 'space-between',
  paddingBlock: 8,
  transition: 'width 0.2s ease',
  width: '100%',
};

const workbenchListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  paddingInline: 6,
};

const navButtonBaseStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  width: '100%',
  height: 56,
  padding: 0,
  textAlign: 'left',
  border: 'none',
  background: 'transparent',
  borderRadius: 14,
  cursor: 'pointer',
  overflow: 'hidden',
  position: 'relative',
  transition: 'background-color 0.18s ease, color 0.18s ease, width 0.2s ease',
};

const navIndicatorStyle: React.CSSProperties = {
  width: 3,
  borderRadius: '0 999px 999px 0',
  bottom: 10,
  flexShrink: 0,
  left: 0,
  position: 'absolute',
  top: 10,
};

const navItemContentStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: 1,
  gap: 10,
  height: '100%',
  justifyContent: 'flex-start',
  minWidth: 0,
  paddingInline: 14,
};

const navItemIconStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'inline-flex',
  flexShrink: 0,
  fontSize: 20,
  height: 20,
  justifyContent: 'center',
  width: 20,
};

const navItemLabelStyle: React.CSSProperties = {
  color: '#1f2937',
  fontSize: 14,
  fontWeight: 500,
  lineHeight: '22px',
  minWidth: 0,
};

const workbenchFooterStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  paddingInline: 6,
};

const shellRootStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
};

const shellRowStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
};

const shellColumnStyle: React.CSSProperties = {
  ...stretchColumnStyle,
  flexDirection: 'column',
  minHeight: 0,
};

const shellContentStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const navItemIconByKey: Record<StudioWorkspacePage, React.ReactNode> = {
  workflows: <NodeIndexOutlined />,
  studio: <BuildOutlined />,
  scripts: <CodeOutlined />,
  roles: <TeamOutlined />,
  connectors: <ApiOutlined />,
  settings: <SettingOutlined />,
};

const StudioShell: React.FC<StudioShellProps> = ({
  alerts,
  contentOverflow = 'auto',
  currentPage,
  navItems,
  onSelectPage,
  pageTitle,
  pageToolbar,
  showPageHeader = true,
  children,
}) => {
  const [isExpanded, setIsExpanded] = React.useState(false);
  const [hoveredKey, setHoveredKey] =
    React.useState<StudioWorkspacePage | null>(null);
  const [isToggleHovered, setIsToggleHovered] = React.useState(false);
  const sidebarWidth = isExpanded ? 160 : 64;

  return (
    <div style={shellRootStyle}>
      {alerts}
      <Row gutter={[16, 16]} align="stretch" wrap={false} style={shellRowStyle}>
        <Col
          flex={`${sidebarWidth}px`}
          style={{
            ...shellColumnStyle,
            maxWidth: sidebarWidth,
            minWidth: sidebarWidth,
            transition: 'max-width 0.2s ease, min-width 0.2s ease',
            width: sidebarWidth,
          }}
        >
          <aside
            style={{
              ...workbenchShellStyle,
              width: sidebarWidth,
            }}
            aria-label="Workbench"
          >
            <nav style={workbenchListStyle} aria-label="Workbench navigation">
              {navItems.map((item) => {
                const active = currentPage === item.key;
                const hovered = hoveredKey === item.key;

                return (
                  <Tooltip
                    key={item.key}
                    title={item.label}
                    placement="right"
                    mouseEnterDelay={0.2}
                  >
                    <button
                      type="button"
                      aria-label={item.label}
                      aria-current={active ? 'page' : undefined}
                      style={{
                        ...navButtonBaseStyle,
                        background: active
                          ? 'rgba(22, 119, 255, 0.08)'
                          : hovered
                            ? '#fafafa'
                            : 'transparent',
                      }}
                      onClick={() => onSelectPage(item.key)}
                      onMouseEnter={() => setHoveredKey(item.key)}
                      onMouseLeave={() =>
                        setHoveredKey((current) =>
                          current === item.key ? null : current,
                        )
                      }
                    >
                      <span
                        aria-hidden="true"
                        style={{
                          ...navIndicatorStyle,
                          background: active ? '#1677ff' : 'transparent',
                        }}
                      />
                      <span style={navItemContentStyle}>
                        <span
                          aria-hidden="true"
                          style={{
                            ...navItemIconStyle,
                            color: active ? '#1677ff' : '#667085',
                          }}
                        >
                          {navItemIconByKey[item.key]}
                        </span>
                        {isExpanded ? (
                          <Typography.Text
                            style={{
                              ...navItemLabelStyle,
                              color: active ? '#1677ff' : '#1f2937',
                            }}
                            ellipsis={{ tooltip: false }}
                          >
                            {item.label}
                          </Typography.Text>
                        ) : null}
                      </span>
                    </button>
                  </Tooltip>
                );
              })}
            </nav>
            <div style={workbenchFooterStyle}>
              <Tooltip
                title={isExpanded ? 'Collapse workbench' : 'Expand workbench'}
                placement="right"
              >
                <button
                  type="button"
                  aria-label={
                    isExpanded ? 'Collapse workbench' : 'Expand workbench'
                  }
                  aria-pressed={isExpanded}
                  style={{
                    ...navButtonBaseStyle,
                    background: isToggleHovered ? '#fafafa' : 'transparent',
                  }}
                  onClick={() => setIsExpanded((current) => !current)}
                  onMouseEnter={() => setIsToggleHovered(true)}
                  onMouseLeave={() => setIsToggleHovered(false)}
                >
                  <span style={navItemContentStyle}>
                    <span
                      aria-hidden="true"
                      style={{
                        ...navItemIconStyle,
                        color: '#667085',
                        transform: isExpanded ? 'rotate(180deg)' : 'none',
                        transition: 'transform 0.2s ease',
                      }}
                    >
                      <DoubleRightOutlined />
                    </span>
                    {isExpanded ? (
                      <Typography.Text style={navItemLabelStyle}>
                        Collapse
                      </Typography.Text>
                    ) : null}
                  </span>
                </button>
              </Tooltip>
            </div>
          </aside>
        </Col>
        <Col flex="auto" style={{ ...shellColumnStyle, minWidth: 0 }}>
          <div
            style={{
              ...shellContentStyle,
              overflowY: contentOverflow,
            }}
          >
            {showPageHeader ? (
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'center',
                  gap: 16,
                }}
              >
                <Typography.Title level={4} style={{ margin: 0 }}>
                  {pageTitle}
                </Typography.Title>
                {pageToolbar}
              </div>
            ) : null}
            {children}
          </div>
        </Col>
      </Row>
    </div>
  );
};

export default StudioShell;
