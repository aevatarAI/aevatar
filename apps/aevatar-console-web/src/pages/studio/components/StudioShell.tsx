import {
  CheckOutlined,
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
  readonly canDelete?: boolean;
  readonly canRename?: boolean;
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
  readonly inventoryActions?: React.ReactNode;
  readonly lifecycleSteps?: readonly StudioLifecycleStep[];
  readonly members?: readonly StudioShellMemberItem[];
  readonly onSelectLifecycleStep?: (stepKey: string) => void;
  readonly onSelectMember?: (memberKey: string) => void;
  readonly pageTitle: string;
  readonly pageToolbar?: React.ReactNode;
  readonly railFooter?: React.ReactNode;
  readonly selectedMemberKey?: string;
  readonly showPageHeader?: boolean;
  readonly children: React.ReactNode;
};

const shellRootStyle: React.CSSProperties = {
  background: '#f7f8fb',
  display: 'flex',
  flex: 1,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const railStyle: React.CSSProperties = {
  background:
    'linear-gradient(180deg, rgba(255, 253, 249, 0.98) 0%, rgba(249, 245, 237, 0.98) 100%)',
  borderRight: '1px solid #ebe2d4',
  display: 'flex',
  flexDirection: 'column',
  flexShrink: 0,
  minHeight: 0,
  width: 276,
};

const railHeaderStyle: React.CSSProperties = {
  borderBottom: '1px solid #ece3d5',
  display: 'grid',
  gap: 10,
  padding: '14px 12px 12px',
};

const railSectionStyle: React.CSSProperties = {
  borderBottom: '1px solid #ece3d5',
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  padding: '10px 12px 12px',
};

const railSectionHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#7b6e5a',
  display: 'flex',
  fontSize: 10,
  fontWeight: 700,
  gap: 6,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const railSectionHeaderRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 8,
  justifyContent: 'space-between',
};

const railSectionHeaderStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
};

const memberListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const shellMainStyle: React.CSSProperties = {
  background: '#fcfbf8',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
};

const shellContentStyle: React.CSSProperties = {
  background: 'transparent',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
};

const shellAlertsStyle: React.CSSProperties = {
  borderBottom: '1px solid rgba(229, 220, 203, 0.9)',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  padding: '0 16px 12px',
};

const shellHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255, 255, 255, 0.94)',
  borderBottom: '1px solid rgba(229, 220, 203, 0.88)',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
  margin: '0 16px',
  padding: '14px 18px',
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
  padding: '14px 16px 16px',
};

const lifecycleSectionStyle: React.CSSProperties = {
  background: 'transparent',
  borderBottom: '1px solid rgba(229, 220, 203, 0.82)',
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  padding: '0 16px 10px',
};

const lifecycleHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 6,
  justifyContent: 'space-between',
};

const lifecycleRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 6,
  minWidth: 0,
  overflowX: 'auto',
  paddingBottom: 2,
  scrollbarWidth: 'thin',
};

const lifecycleConnectorStyle: React.CSSProperties = {
  background: '#dbcdb4',
  borderRadius: 999,
  display: 'block',
  flex: '0 0 20px',
  height: 1,
};

const railSearchInputStyle: React.CSSProperties = {
  background: 'rgba(255, 252, 246, 0.96)',
  border: '1px solid #e5dccb',
  borderRadius: 10,
  color: '#2f2a23',
  fontSize: 11.5,
  minWidth: 0,
  outline: 'none',
  padding: '8px 10px',
  width: '100%',
};

const railFilterRowStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
};

const railFilterButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255, 250, 244, 0.92)',
  border: '1px solid #e6decd',
  borderRadius: 999,
  color: '#5f574b',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 10.5,
  fontWeight: 700,
  gap: 5,
  minHeight: 26,
  padding: '0 9px',
};

const railPillStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(245, 239, 228, 0.96)',
  border: '1px solid #e4dac8',
  borderRadius: 999,
  color: '#6c6558',
  display: 'inline-flex',
  fontSize: 10,
  fontWeight: 700,
  lineHeight: '16px',
  minHeight: 22,
  padding: '0 8px',
};

const railFooterStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
};

const inlineInfoButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #dbe3f0',
  borderRadius: 999,
  color: '#64748b',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 10,
  height: 20,
  justifyContent: 'center',
  padding: 0,
  width: 20,
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

function formatMemberKindLabel(kind: StudioShellMemberKind | undefined): string {
  switch (kind) {
    case 'workflow':
      return 'Workflow';
    case 'script':
      return 'Script';
    case 'gagent':
      return 'GAgent';
    case 'member':
      return 'Member';
    default:
      return 'Focus';
  }
}

function buildMemberSearchText(member: StudioShellMemberItem): string {
  return [
    member.label,
    member.description,
    member.meta,
    formatMemberKindLabel(member.kind),
  ]
    .join(' ')
    .toLowerCase();
}

function handleCardKeyboardSelect(
  event: React.KeyboardEvent<HTMLElement>,
  disabled: boolean,
  onSelect?: () => void,
): void {
  if (disabled || !onSelect) {
    return;
  }

  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault();
    onSelect();
  }
}

const StudioShell: React.FC<StudioShellProps> = ({
  alerts,
  contentOverflow = 'auto',
  contextBar,
  currentLifecycleStep,
  inventoryActions,
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
  const [memberSearch, setMemberSearch] = React.useState('');
  const [memberFilter, setMemberFilter] = React.useState<
    'all' | StudioShellMemberKind
  >('all');

  const memberFilterOptions = React.useMemo(() => {
    const counts = members.reduce<Record<string, number>>((current, member) => {
      const kind = member.kind ?? 'unknown';
      current[kind] = (current[kind] ?? 0) + 1;
      return current;
    }, {});

    return [
      {
        key: 'all' as const,
        label: 'All',
        count: members.length,
      },
      {
        key: 'workflow' as const,
        label: 'Workflow',
        count: counts.workflow ?? 0,
      },
      {
        key: 'script' as const,
        label: 'Script',
        count: counts.script ?? 0,
      },
      {
        key: 'gagent' as const,
        label: 'GAgent',
        count: counts.gagent ?? 0,
      },
      {
        key: 'member' as const,
        label: 'Member',
        count: counts.member ?? 0,
      },
    ].filter((item) => item.key === 'all' || item.count > 0);
  }, [members]);

  const filteredMembers = React.useMemo(() => {
    const normalizedSearch = memberSearch.trim().toLowerCase();

    return members.filter((member) => {
      if (memberFilter !== 'all' && (member.kind ?? 'unknown') !== memberFilter) {
        return false;
      }

      if (!normalizedSearch) {
        return true;
      }

      return buildMemberSearchText(member).includes(normalizedSearch);
    });
  }, [memberFilter, memberSearch, members]);

  return (
    <div style={shellRootStyle}>
      <aside style={railStyle} aria-label="Team members">
        <div style={railHeaderStyle}>
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
                color: '#16120d',
                fontSize: 14,
                fontWeight: 700,
                margin: 0,
                lineHeight: '20px',
              }}
            >
              Team members
            </Typography.Title>
            <span style={railPillStyle}>{members.length}</span>
            <InlineInfoButton
              ariaLabel="Open team members help"
              content="Keep one member in focus while Build, Bind, Invoke, and Observe gradually converge into the same workbench."
            />
          </div>
          <div style={{ display: 'grid', gap: 8 }}>
            <input
              aria-label="Search team members"
              onChange={(event) => setMemberSearch(event.target.value)}
              placeholder="Search members or revisions"
              style={railSearchInputStyle}
              type="search"
              value={memberSearch}
            />
            <div style={railFilterRowStyle}>
              {memberFilterOptions.map((option) => {
                const active = memberFilter === option.key;
                return (
                  <button
                    key={option.key}
                    aria-pressed={active}
                    onClick={() => setMemberFilter(option.key)}
                    style={{
                      ...railFilterButtonStyle,
                      background: active ? '#131820' : railFilterButtonStyle.background,
                      borderColor: active ? '#131820' : '#e6decd',
                      color: active ? '#fbfaf6' : '#5f574b',
                    }}
                    type="button"
                  >
                    <span>{option.label}</span>
                    <span
                      style={{
                        opacity: active ? 0.86 : 0.7,
                      }}
                    >
                      {option.count}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        </div>

        <div style={{ ...railSectionStyle, flex: 1, minHeight: 0 }}>
          <div style={railSectionHeaderStackStyle}>
            <div style={railSectionHeaderRowStyle}>
              <div style={railSectionHeaderStyle}>
                <span>Member inventory</span>
              </div>
            </div>
            {inventoryActions}
          </div>
          {filteredMembers.length > 0 ? (
            <div style={memberListStyle}>
              {filteredMembers.map((member) => {
                const isSelected = selectedMemberKey === member.key;
                const toneStyles = resolveMemberToneStyles(member.tone);
                const kind = member.kind ?? 'unknown';
                const memberIcon =
                  memberKindIconByKey[kind] ?? memberKindIconByKey.unknown;

                return (
                  <div
                    key={member.key}
                    aria-current={isSelected ? 'true' : undefined}
                    aria-disabled={member.disabled ? 'true' : undefined}
                    onClick={() => {
                      if (!member.disabled) {
                        onSelectMember?.(member.key);
                      }
                    }}
                    onKeyDown={(event) =>
                      handleCardKeyboardSelect(
                        event,
                        Boolean(member.disabled),
                        onSelectMember ? () => onSelectMember(member.key) : undefined,
                      )
                    }
                    role="button"
                    style={{
                      background: isSelected
                        ? 'linear-gradient(180deg, rgba(25, 34, 48, 0.98) 0%, rgba(34, 43, 58, 0.98) 100%)'
                        : 'rgba(255, 252, 246, 0.98)',
                      border: `1px solid ${isSelected ? '#141a22' : '#ebe3d4'}`,
                      borderRadius: 16,
                      boxShadow: isSelected
                        ? '0 10px 22px rgba(15, 23, 42, 0.14)'
                        : '0 4px 14px rgba(110, 94, 71, 0.05)',
                      cursor:
                        member.disabled || !onSelectMember ? 'default' : 'pointer',
                      alignItems: 'center',
                      display: 'flex',
                      gap: 10,
                      opacity: member.disabled ? 0.56 : 1,
                      boxSizing: 'border-box',
                      minHeight: 0,
                      overflow: 'hidden',
                      padding: '10px 12px',
                      textAlign: 'left',
                      transition:
                        'background-color 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease',
                      width: '100%',
                    }}
                    title={[member.description, member.meta].filter(Boolean).join(' · ')}
                    tabIndex={
                      member.disabled || !onSelectMember ? -1 : 0
                    }
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        flex: 1,
                        minWidth: 0,
                      }}
                    >
                      <div
                        aria-hidden="true"
                        style={{
                          alignItems: 'center',
                          background: isSelected
                            ? 'rgba(255, 255, 255, 0.14)'
                            : 'rgba(32, 24, 12, 0.05)',
                          borderRadius: 8,
                          color: isSelected ? '#f8dcc2' : '#6b5c48',
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 11,
                          height: 24,
                          justifyContent: 'center',
                          width: 24,
                        }}
                      >
                        {memberIcon}
                      </div>
                      <div
                        style={{
                          alignItems: 'center',
                          display: 'flex',
                          flex: 1,
                          gap: 8,
                          minWidth: 0,
                        }}
                      >
                        <span
                          style={{
                            background: isSelected
                              ? 'rgba(255, 255, 255, 0.14)'
                              : 'rgba(243, 236, 224, 0.92)',
                            border: `1px solid ${isSelected ? 'rgba(255,255,255,0.12)' : '#e6decd'}`,
                            borderRadius: 999,
                            color: isSelected ? '#f4efe6' : '#746655',
                            display: 'inline-flex',
                            flexShrink: 0,
                            fontSize: 9.5,
                            fontWeight: 700,
                            lineHeight: '14px',
                            minHeight: 20,
                            padding: '0 7px',
                          }}
                        >
                          {formatMemberKindLabel(kind)}
                        </span>
                        <span
                          style={{
                            color: isSelected ? '#fbfaf6' : '#111827',
                            fontSize: 13,
                            fontWeight: 700,
                            lineHeight: '20px',
                            minWidth: 0,
                            overflow: 'hidden',
                            textOverflow: 'ellipsis',
                            whiteSpace: 'nowrap',
                          }}
                        >
                          {member.label}
                        </span>
                      </div>
                      <div
                        aria-hidden="true"
                        style={{
                          alignItems: 'center',
                          alignSelf: 'center',
                          background: toneStyles.background,
                          borderRadius: 999,
                          color: toneStyles.color,
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 9.5,
                          fontWeight: 700,
                          justifyContent: 'center',
                          lineHeight: '14px',
                          minHeight: 22,
                          minWidth: 24,
                          padding: '0 7px',
                        }}
                      >
                        <span
                          style={{
                            background: toneStyles.color,
                            borderRadius: 999,
                            display: 'inline-flex',
                            height: 7,
                            marginRight: 6,
                            width: 7,
                          }}
                        />
                        {member.tone ?? 'idle'}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          ) : (
            <Typography.Text
              style={{
                color: '#73685a',
                fontSize: 12,
                lineHeight: '18px',
              }}
            >
              {members.length > 0
                ? 'No members match the current search or filter. Try clearing the rail controls.'
                : 'No team members yet. Create a member to start building in Studio.'}
            </Typography.Text>
          )}
        </div>

      </aside>

      <div style={shellMainStyle}>
        {contextBar}
        {lifecycleSteps.length > 0 ? (
          <div data-testid="studio-lifecycle-section" style={lifecycleSectionStyle}>
            <div style={lifecycleHeaderStyle}>
              <Typography.Text
                style={{
                  color: '#6b7280',
                  fontSize: 10,
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
            <nav
              aria-label="Member lifecycle"
              data-testid="studio-lifecycle-stepper"
              style={lifecycleRowStyle}
            >
              {lifecycleSteps.map((step, index) => {
                const isActive = currentLifecycleStep === step.key;
                const isPlanned = step.status === 'planned';
                const indicatorBackground = isActive
                  ? '#ffffff'
                  : step.disabled || isPlanned
                    ? '#f3f4f6'
                    : '#eef4ff';
                const indicatorColor = isActive
                  ? '#111827'
                  : step.disabled || isPlanned
                    ? '#9ca3af'
                    : '#2f54eb';
                return (
                  <React.Fragment key={step.key}>
                    {index > 0 ? (
                      <span aria-hidden="true" style={lifecycleConnectorStyle} />
                    ) : null}
                    <button
                      aria-current={isActive ? 'step' : undefined}
                      disabled={step.disabled}
                      onClick={() => onSelectLifecycleStep?.(step.key)}
                      title={step.description}
                      style={{
                        alignItems: 'center',
                        background: isActive ? '#111827' : '#ffffff',
                        border: `1px solid ${isActive ? '#111827' : '#e5dccb'}`,
                        borderRadius: 999,
                        cursor:
                          step.disabled || !onSelectLifecycleStep
                            ? 'default'
                            : 'pointer',
                        display: 'flex',
                        flex: '0 0 auto',
                        gap: 8,
                        minHeight: 0,
                        opacity: step.disabled ? 0.68 : 1,
                        padding: '6px 14px',
                        textAlign: 'left',
                      }}
                      type="button"
                    >
                      <span
                        style={{
                          alignItems: 'center',
                          background: indicatorBackground,
                          border: `1px solid ${isActive ? '#ffffff' : indicatorBackground}`,
                          borderRadius: 999,
                          color: indicatorColor,
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 9.5,
                          fontWeight: 700,
                          height: 22,
                          justifyContent: 'center',
                          width: 22,
                        }}
                      >
                        {step.disabled || isPlanned ? index + 1 : <CheckOutlined />}
                      </span>
                      <span
                        style={{
                          color: isActive ? '#ffffff' : '#111827',
                          fontSize: 10.5,
                          fontWeight: isActive ? 700 : 600,
                          lineHeight: '16px',
                          minWidth: 0,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                      >
                        {step.label}
                      </span>
                    </button>
                  </React.Fragment>
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
