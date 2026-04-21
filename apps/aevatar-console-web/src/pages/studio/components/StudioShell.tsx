import {
  CodeOutlined,
  InfoCircleOutlined,
  NodeIndexOutlined,
  RobotOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { Popover, Typography } from 'antd';
import React from 'react';

export type StudioShellMemberKind =
  | 'workflow'
  | 'script'
  | 'gagent'
  | 'member'
  | 'unknown';

export type StudioShellMemberTone =
  | 'live'
  | 'draft'
  | 'idle'
  | 'planned';

export type StudioShellMemberItem = {
  readonly key: string;
  readonly label: string;
  readonly description: string;
  readonly meta?: string;
  readonly kind?: StudioShellMemberKind;
  readonly tone?: StudioShellMemberTone;
  readonly disabled?: boolean;
};

export type StudioLifecycleStep = {
  readonly key: string;
  readonly label: string;
  readonly description: string;
  readonly status: 'active' | 'available' | 'planned';
  readonly disabled?: boolean;
};

type StudioShellProps = {
  readonly alerts?: React.ReactNode;
  readonly contentOverflow?: 'auto' | 'hidden';
  readonly contextBar?: React.ReactNode;
  readonly currentLifecycleStep?: string;
  readonly lifecycleSteps?: readonly StudioLifecycleStep[];
  readonly members?: readonly StudioShellMemberItem[];
  readonly onSelectLifecycleStep?: (stepKey: string) => void;
  readonly onSelectMember?: (memberKey: string) => void;
  readonly pageTitle: string;
  readonly pageToolbar?: React.ReactNode;
  readonly selectedMemberKey?: string;
  readonly showPageHeader?: boolean;
  readonly children: React.ReactNode;
};

const shellRootStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const railStyle: React.CSSProperties = {
  background: '#fbfcfe',
  borderRight: '1px solid #eef2f6',
  display: 'flex',
  flexDirection: 'column',
  flexShrink: 0,
  minHeight: 0,
  width: 112,
};

const railHeaderStyle: React.CSSProperties = {
  borderBottom: '1px solid #eef2f6',
  display: 'grid',
  gap: 4,
  padding: '10px 8px 8px',
};

const railSectionStyle: React.CSSProperties = {
  borderBottom: '1px solid #eef2f6',
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  padding: '8px 6px 10px',
};

const railSectionHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#6b7280',
  display: 'flex',
  fontSize: 10,
  fontWeight: 700,
  gap: 6,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const memberListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const shellMainStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
};

const shellContentStyle: React.CSSProperties = {
  background: '#ffffff',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
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
  padding: 12,
};

const lifecycleSectionStyle: React.CSSProperties = {
  background: '#ffffff',
  borderBottom: '1px solid #f0f2f5',
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  padding: '8px 12px 10px',
};

const lifecycleHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 6,
  justifyContent: 'space-between',
};

const lifecycleRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 6,
  gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
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

const inlineInfoButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #dbe3f0',
  borderRadius: 999,
  color: '#64748b',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 11,
  height: 22,
  justifyContent: 'center',
  padding: 0,
  width: 22,
};

const inlineInfoPopoverStyle: React.CSSProperties = {
  color: '#4b5563',
  fontSize: 12,
  lineHeight: '18px',
  maxWidth: 240,
};

type InlineInfoButtonProps = {
  readonly ariaLabel: string;
  readonly buttonStyle?: React.CSSProperties;
  readonly content: React.ReactNode;
  readonly placement?: 'bottomLeft' | 'bottomRight' | 'topLeft' | 'topRight';
};

const InlineInfoButton: React.FC<InlineInfoButtonProps> = ({
  ariaLabel,
  buttonStyle,
  content,
  placement = 'bottomLeft',
}) => (
  <Popover
    content={<div style={inlineInfoPopoverStyle}>{content}</div>}
    placement={placement}
    trigger="click"
  >
    <button
      aria-label={ariaLabel}
      onClick={(event) => event.stopPropagation()}
      style={{ ...inlineInfoButtonStyle, ...buttonStyle }}
      type="button"
    >
      <InfoCircleOutlined />
    </button>
  </Popover>
);

const memberKindIconByKey: Record<StudioShellMemberKind, React.ReactNode> = {
  workflow: <NodeIndexOutlined />,
  script: <CodeOutlined />,
  gagent: <RobotOutlined />,
  member: <TeamOutlined />,
  unknown: <TeamOutlined />,
};

function resolveMemberToneStyles(
  tone: StudioShellMemberTone | undefined,
): {
  readonly background: string;
  readonly color: string;
} {
  switch (tone) {
    case 'live':
      return {
        background: 'rgba(22, 163, 74, 0.12)',
        color: '#15803d',
      };
    case 'draft':
      return {
        background: 'rgba(245, 158, 11, 0.16)',
        color: '#b45309',
      };
    case 'planned':
      return {
        background: 'rgba(99, 102, 241, 0.12)',
        color: '#4338ca',
      };
    default:
      return {
        background: 'rgba(148, 163, 184, 0.14)',
        color: '#475569',
      };
  }
}

const StudioShell: React.FC<StudioShellProps> = ({
  alerts,
  contentOverflow = 'auto',
  contextBar,
  currentLifecycleStep,
  lifecycleSteps = [],
  members = [],
  onSelectLifecycleStep,
  onSelectMember,
  pageTitle,
  pageToolbar,
  selectedMemberKey,
  showPageHeader = true,
  children,
}) => {
  return (
    <div style={shellRootStyle}>
      <aside style={railStyle} aria-label="Team members">
        <div style={railHeaderStyle}>
          <span style={visuallyHiddenStyle}>Team members</span>
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <Typography.Title
              level={4}
              style={{
                color: '#111827',
                fontSize: 13,
                fontWeight: 700,
                margin: 0,
                lineHeight: '18px',
              }}
            >
              Team members
            </Typography.Title>
            <InlineInfoButton
              ariaLabel="Open team members help"
              content="Keep one member in focus while Build, Bind, Invoke, and Observe gradually converge into the same workbench."
            />
          </div>
        </div>

        <div style={{ ...railSectionStyle, flex: 1, minHeight: 0 }}>
          <div style={railSectionHeaderStyle}>
            <span>Current focus</span>
          </div>
          {members.length > 0 ? (
            <div style={memberListStyle}>
              {members.map((member) => {
                const isSelected = selectedMemberKey === member.key;
                const toneStyles = resolveMemberToneStyles(member.tone);
                const kind = member.kind ?? 'unknown';
                const memberIcon =
                  memberKindIconByKey[kind] ?? memberKindIconByKey.unknown;

                return (
                  <button
                    key={member.key}
                    aria-current={isSelected ? 'true' : undefined}
                    disabled={member.disabled}
                    onClick={() => onSelectMember?.(member.key)}
                    style={{
                      alignItems: 'center',
                      background: isSelected ? '#eff6ff' : '#ffffff',
                      border: `1px solid ${isSelected ? '#bfdbfe' : '#eef2f6'}`,
                      borderRadius: 8,
                      cursor:
                        member.disabled || !onSelectMember ? 'default' : 'pointer',
                      display: 'flex',
                      gap: 6,
                      opacity: member.disabled ? 0.56 : 1,
                      minHeight: 34,
                      padding: '6px 5px',
                      textAlign: 'left',
                      transition:
                        'background-color 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease',
                      width: '100%',
                    }}
                    type="button"
                  >
                    <span
                      aria-hidden="true"
                      style={{
                        alignItems: 'center',
                        alignSelf: 'start',
                        background: isSelected
                          ? 'rgba(59, 130, 246, 0.12)'
                          : 'rgba(15, 23, 42, 0.05)',
                        borderRadius: 8,
                        color: isSelected ? '#2563eb' : '#475569',
                        display: 'inline-flex',
                        fontSize: 11,
                        flexShrink: 0,
                        height: 20,
                        justifyContent: 'center',
                        width: 20,
                      }}
                    >
                      {memberIcon}
                    </span>
                    <span
                        style={{
                          alignItems: 'center',
                          display: 'flex',
                          gap: 4,
                          minWidth: 0,
                          width: '100%',
                        }}
                    >
                      <span
                        style={{
                          flex: 1,
                          minWidth: 0,
                          overflow: 'hidden',
                        }}
                      >
                        <span
                          style={{
                            color: '#111827',
                            fontSize: 10,
                            fontWeight: 700,
                            minWidth: 0,
                            overflow: 'hidden',
                            textOverflow: 'ellipsis',
                            whiteSpace: 'nowrap',
                          }}
                        >
                          {member.label}
                        </span>
                      </span>
                      <span
                        aria-hidden="true"
                        style={{
                          background: toneStyles.color,
                          borderRadius: 999,
                          display: 'inline-flex',
                          flexShrink: 0,
                          height: 7,
                          width: 7,
                        }}
                      />
                    </span>
                  </button>
                );
              })}
            </div>
          ) : (
            <Typography.Text
              style={{
                color: '#6b7280',
                fontSize: 12,
                lineHeight: '18px',
              }}
            >
              No member focus is available yet. Once Studio resolves the current
              team context, the active member and nearby drafts will appear here.
            </Typography.Text>
          )}
        </div>

      </aside>

      <div style={shellMainStyle}>
        {contextBar}
        {lifecycleSteps.length > 0 ? (
          <div style={lifecycleSectionStyle}>
            <div style={lifecycleHeaderStyle}>
              <Typography.Text
                style={{
                  color: '#6b7280',
                  fontSize: 11,
                  fontWeight: 700,
                  letterSpacing: '0.08em',
                  textTransform: 'uppercase',
                }}
              >
                Member lifecycle
              </Typography.Text>
              <InlineInfoButton
                ariaLabel="Open lifecycle help"
                content="Keep the selected member in one shell while Build, Bind, Invoke, and Observe stay aligned to the same workbench."
              />
            </div>
            <nav aria-label="Member lifecycle" style={lifecycleRowStyle}>
              {lifecycleSteps.map((step, index) => {
                const isActive = currentLifecycleStep === step.key;
                const isPlanned = step.status === 'planned';
                return (
                  <div
                    key={step.key}
                    style={{
                      minWidth: 0,
                      position: 'relative',
                    }}
                  >
                    <button
                      aria-current={isActive ? 'step' : undefined}
                      disabled={step.disabled}
                      onClick={() => onSelectLifecycleStep?.(step.key)}
                      style={{
                        alignItems: 'flex-start',
                        background: isActive ? '#eff6ff' : '#ffffff',
                        border: `1px solid ${isActive ? '#93c5fd' : '#e5e7eb'}`,
                        borderRadius: 10,
                        cursor:
                          step.disabled || !onSelectLifecycleStep
                            ? 'default'
                            : 'pointer',
                        display: 'flex',
                        flexDirection: 'column',
                        gap: 8,
                        minHeight: 58,
                        opacity: step.disabled ? 0.68 : 1,
                        padding: '10px 38px 10px 10px',
                        textAlign: 'left',
                        width: '100%',
                      }}
                      type="button"
                    >
                      <span
                        style={{
                          alignItems: 'center',
                          color: isActive ? '#2563eb' : '#4b5563',
                          display: 'inline-flex',
                          fontSize: 12,
                          fontWeight: 700,
                          gap: 6,
                          minWidth: 0,
                        }}
                      >
                        <span
                          style={{
                            alignItems: 'center',
                            background: isActive ? '#2563eb' : '#e5e7eb',
                            borderRadius: 999,
                            color: '#ffffff',
                            display: 'inline-flex',
                            flexShrink: 0,
                            fontSize: 10,
                            height: 18,
                            justifyContent: 'center',
                            width: 18,
                          }}
                        >
                          {index + 1}
                        </span>
                        <span
                          style={{
                            minWidth: 0,
                            overflow: 'hidden',
                            textOverflow: 'ellipsis',
                            whiteSpace: 'nowrap',
                          }}
                        >
                          {step.label}
                        </span>
                      </span>
                      <span
                        style={{
                          alignItems: 'center',
                          alignSelf: 'start',
                          background: isActive
                            ? 'rgba(37, 99, 235, 0.12)'
                            : isPlanned
                              ? 'rgba(99, 102, 241, 0.12)'
                              : 'rgba(148, 163, 184, 0.14)',
                          borderRadius: 999,
                          color: isActive
                            ? '#2563eb'
                            : isPlanned
                              ? '#4338ca'
                              : '#475569',
                          display: 'inline-flex',
                          fontSize: 9,
                          fontWeight: 700,
                          padding: '2px 7px',
                          textTransform: 'uppercase',
                        }}
                      >
                        {isActive
                          ? 'Current'
                          : isPlanned
                            ? 'Planned'
                            : 'Available'}
                      </span>
                    </button>
                    <InlineInfoButton
                      ariaLabel={`Open lifecycle step ${index + 1} help`}
                      buttonStyle={{
                        position: 'absolute',
                        right: 10,
                        top: 10,
                      }}
                      content={step.description}
                      placement="bottomRight"
                    />
                  </div>
                );
              })}
            </nav>
          </div>
        ) : null}
        {alerts ? <div style={shellAlertsStyle}>{alerts}</div> : null}
        <div data-testid="studio-shell-content" style={shellContentStyle}>
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
