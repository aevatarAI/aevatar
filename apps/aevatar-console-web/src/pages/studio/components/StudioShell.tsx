import {
  ApiOutlined,
  CodeOutlined,
  NodeIndexOutlined,
  PlayCircleOutlined,
  SettingOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { Tooltip, Typography } from 'antd';
import React from 'react';

export type StudioWorkspacePage =
  | 'workflows'
  | 'studio'
  | 'execution'
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
  readonly contextBar?: React.ReactNode;
  readonly currentPage: StudioWorkspacePage;
  readonly navItems: readonly StudioShellNavItem[];
  readonly onSelectPage: (page: StudioWorkspacePage) => void;
  readonly pageTitle: string;
  readonly pageToolbar?: React.ReactNode;
  readonly showPageHeader?: boolean;
  readonly children: React.ReactNode;
};

const navButtonBaseStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  width: 40,
  height: 36,
  padding: 0,
  border: 'none',
  background: 'transparent',
  borderRadius: 6,
  cursor: 'pointer',
  justifyContent: 'center',
  transition: 'background-color 0.18s ease, color 0.18s ease',
};

const navItemContentStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  justifyContent: 'center',
};

const navItemIconStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'inline-flex',
  fontSize: 18,
  height: 20,
  justifyContent: 'center',
  width: 20,
};

const shellRootStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #d9d9d9',
  borderRadius: 8,
  display: 'flex',
  flex: 1,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
};

const shellSidebarStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#001529',
  display: 'flex',
  flexDirection: 'column',
  flexShrink: 0,
  gap: 2,
  paddingTop: 8,
  width: 56,
};

const shellSidebarNavStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
  width: '100%',
};

const shellSidebarSpacerStyle: React.CSSProperties = {
  flex: 1,
};

const shellMainStyle: React.CSSProperties = {
  background: '#ffffff',
  display: 'flex',
  flexDirection: 'column',
  flex: 1,
  minHeight: 0,
};

const shellContentStyle: React.CSSProperties = {
  background: '#fafafa',
  flex: 1,
  minHeight: 0,
  overflow: 'hidden',
};

const shellAlertsStyle: React.CSSProperties = {
  borderBottom: '1px solid #f0f0f0',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  padding: '12px 16px',
};

const shellHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  borderBottom: '1px solid #f0f0f0',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
  padding: '12px 16px',
};

const shellHeaderTitleStyle: React.CSSProperties = {
  color: '#1d2129',
  fontSize: 13,
  fontWeight: 500,
  margin: 0,
};

const shellPageBodyStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  overflowX: 'hidden',
  padding: 16,
};

const visuallyHiddenStyle: React.CSSProperties = {
  border: 0,
  clip: 'rect(0 0 0 0)',
  height: 1,
  margin: -1,
  overflow: 'hidden',
  padding: 0,
  position: 'absolute',
  whiteSpace: 'nowrap',
  width: 1,
};

const navItemIconByKey: Record<StudioWorkspacePage, React.ReactNode> = {
  workflows: <NodeIndexOutlined />,
  studio: <NodeIndexOutlined />,
  execution: <PlayCircleOutlined />,
  scripts: <CodeOutlined />,
  roles: <TeamOutlined />,
  connectors: <ApiOutlined />,
  settings: <SettingOutlined />,
};

const StudioShell: React.FC<StudioShellProps> = ({
  alerts,
  contentOverflow = 'auto',
  contextBar,
  currentPage,
  navItems,
  onSelectPage,
  pageTitle,
  pageToolbar,
  showPageHeader = true,
  children,
}) => {
  const [hoveredKey, setHoveredKey] =
    React.useState<StudioWorkspacePage | null>(null);

  return (
    <div style={shellRootStyle}>
      <aside style={shellSidebarStyle} aria-label="Workbench">
        <span style={visuallyHiddenStyle}>Workbench</span>
        <nav style={shellSidebarNavStyle} aria-label="Workbench navigation">
          {navItems
            .filter((item) => item.key !== 'settings')
            .map((item) => {
            const active = currentPage === item.key;
            const hovered = hoveredKey === item.key;

            return (
              <React.Fragment key={item.key}>
                {item.key === 'execution' ? <div style={shellSidebarSpacerStyle} /> : null}
                <Tooltip
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
                        ? '#1890ff'
                        : hovered
                          ? 'rgba(255, 255, 255, 0.12)'
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
                    <span style={navItemContentStyle}>
                      <span
                        aria-hidden="true"
                        style={{
                          ...navItemIconStyle,
                          color: active ? '#ffffff' : '#ffffffa6',
                        }}
                      >
                        {navItemIconByKey[item.key]}
                      </span>
                    </span>
                  </button>
                </Tooltip>
              </React.Fragment>
            );
          })}
        </nav>
        <Tooltip title="编辑器设置" placement="right" mouseEnterDelay={0.2}>
          <button
            type="button"
            aria-label="编辑器设置"
            aria-current={currentPage === 'settings' ? 'page' : undefined}
            style={{
              ...navButtonBaseStyle,
              background:
                currentPage === 'settings'
                  ? '#1890ff'
                  : hoveredKey === 'settings'
                    ? 'rgba(255, 255, 255, 0.12)'
                    : 'transparent',
              marginBottom: 8,
            }}
            onClick={() => onSelectPage('settings')}
            onMouseEnter={() => setHoveredKey('settings')}
            onMouseLeave={() =>
              setHoveredKey((current) =>
                current === 'settings' ? null : current,
              )
            }
          >
            <span style={navItemContentStyle}>
              <span
                aria-hidden="true"
                style={{
                  ...navItemIconStyle,
                  color: currentPage === 'settings' ? '#ffffff' : '#ffffffa6',
                }}
              >
                <SettingOutlined />
              </span>
            </span>
          </button>
        </Tooltip>
      </aside>
      <div style={shellMainStyle}>
        {contextBar}
        {alerts ? <div style={shellAlertsStyle}>{alerts}</div> : null}
        <div
          style={{
            ...shellContentStyle,
          }}
        >
          {showPageHeader ? (
            <div style={shellHeaderStyle}>
              <Typography.Title level={4} style={shellHeaderTitleStyle}>
                {pageTitle}
              </Typography.Title>
              {pageToolbar}
            </div>
          ) : null}
          <div
            style={{
              ...shellPageBodyStyle,
              overflowY: contentOverflow,
            }}
          >
            {children}
          </div>
        </div>
      </div>
    </div>
  );
};

export default StudioShell;
