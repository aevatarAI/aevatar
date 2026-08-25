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
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  AEVATAR_PRESSABLE_CARD_CLASS,
} from '@/shared/ui/interactionStandards';
import { t } from "@/shared/i18n/messages";

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
  readonly contentScrollMode?: 'contained' | 'page';
  readonly contextBar?: React.ReactNode;
  readonly currentLifecycleStep?: string;
  readonly inventoryActions?: React.ReactNode;
  readonly lifecycleSteps?: readonly StudioLifecycleStep[];
  readonly members?: readonly StudioShellMemberItem[];
  readonly onSelectLifecycleStep?: (stepKey: string) => void;
  readonly onSelectMember?: (memberKey: string) => void;
  readonly pageTitle: string;
  readonly pageToolbar?: React.ReactNode;
  readonly selectedMemberKey?: string;
  readonly showLifecycle?: boolean;
  readonly showMemberRail?: boolean;
  readonly showPageHeader?: boolean;
  readonly children: React.ReactNode;
};

function formatMemberTone(tone: StudioShellMemberTone | undefined): string {
  switch (tone) {
    case 'live':
      return t("pages.studio.studioshell.live", "Live");
    case 'draft':
      return t("pages.studio.studioshell.draft", "Draft");
    case 'planned':
      return t("pages.studio.studioshell.planned", "Planned");
    case 'idle':
    default:
      return t("pages.studio.studioshell.idle", "Idle");
  }
}

const shellRootStyle: React.CSSProperties = {
  background: '#f4f4f0',
  display: 'flex',
  flex: 1,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const railStyle: React.CSSProperties = {
  background: '#fbfaf7',
  borderRight: '1px solid #e5e0d4',
  display: 'flex',
  flexDirection: 'column',
  flexShrink: 0,
  minHeight: 0,
  width: 264,
};

const railHeaderStyle: React.CSSProperties = {
  borderBottom: '1px solid #e8e3d8',
  display: 'grid',
  gap: 10,
  padding: '14px 14px 12px',
};

const railSectionStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  padding: '12px 10px 12px',
};

const railSectionHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#8a8172',
  display: 'flex',
  fontSize: 10.5,
  fontWeight: 700,
  gap: 6,
  letterSpacing: '0.04em',
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
  gap: 2,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const shellMainStyle: React.CSSProperties = {
  background: 'transparent',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
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
  borderBottom: '1px solid #e8e3d8',
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
  padding: '0 20px 12px',
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
  padding: '16px 20px 20px',
};

const lifecycleSectionStyle: React.CSSProperties = {
  background: 'transparent',
  borderBottom: '1px solid #e8e3d8',
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  padding: '0 20px 12px',
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
  background: '#d9d2c2',
  borderRadius: 999,
  display: 'block',
  flex: '0 0 16px',
  height: 1,
};

const railSearchInputStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e0d4',
  borderRadius: 8,
  color: '#2f2a23',
  fontSize: 12,
  minWidth: 0,
  outline: 'none',
  padding: '7px 10px',
  width: '100%',
};

const railFilterRowStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
};

const railFilterButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'transparent',
  border: '1px solid transparent',
  borderRadius: 999,
  color: '#6f675a',
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
  background: '#f0ede4',
  borderRadius: 999,
  color: '#6c6558',
  display: 'inline-flex',
  fontSize: 10,
  fontWeight: 700,
  lineHeight: '16px',
  minHeight: 20,
  padding: '0 8px',
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
      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
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
        background: 'rgba(217, 119, 6, 0.14)',
        color: '#b45309',
      };
    case 'planned':
      return {
        background: 'rgba(79, 70, 229, 0.12)',
        color: '#4f46e5',
      };
    default:
      return {
        background: 'rgba(100, 116, 139, 0.12)',
        color: '#64748b',
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
      return t("pages.studio.studioshell.member", "Member");
    default:
      return t("pages.studio.studioshell.focus", "Focus");
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
  contentScrollMode = 'contained',
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
  showLifecycle = true,
  showMemberRail = true,
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
        label: t("pages.studio.studioshell.all", "All"),
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
        label: t("pages.studio.studioshell.member", "Member"),
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
  const usesPageScroll = contentScrollMode === 'page';
  const mainStyle = usesPageScroll
    ? ({
        ...shellMainStyle,
        overflowX: 'hidden',
        overflowY: 'auto',
      } satisfies React.CSSProperties)
    : shellMainStyle;
  const contentStyle = usesPageScroll
    ? ({
        ...shellContentStyle,
        flex: '0 0 auto',
        overflow: 'visible',
      } satisfies React.CSSProperties)
    : shellContentStyle;
  const pageBodyStyle = usesPageScroll
    ? ({
        ...shellPageBodyStyle,
        flex: '0 0 auto',
        overflow: 'visible',
      } satisfies React.CSSProperties)
    : ({
        ...shellPageBodyStyle,
        overflowY: contentOverflow,
      } satisfies React.CSSProperties);

  return (
    <div style={shellRootStyle}>
      {showMemberRail ? (
      <aside style={railStyle} aria-label={t("pages.studio.studioshell.team.members.3", "Team members")}>
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
                color: '#17130c',
                fontSize: 13,
                fontWeight: 700,
                letterSpacing: '0.01em',
                margin: 0,
                lineHeight: '20px',
              }}
            >
              {t("pages.studio.studioshell.team.members.4", "Team members")}</Typography.Title>
            <span style={railPillStyle}>{members.length}</span>
            <InlineInfoButton
              ariaLabel={t("pages.studio.studioshell.open.team.members.help", "Open team members help")}
              content={t(
                "pages.studio.studioshell.keep.one.member.in",
                "Keep one member in focus while its draft, published service, and run evidence stay visible in the same workbench.",
              )}
            />
          </div>
          <div style={{ display: 'grid', gap: 8 }}>
            <input
              aria-label={t("pages.studio.studioshell.search.team.members.2", "Search team members")}
              onChange={(event) => setMemberSearch(event.target.value)}
              placeholder={t("pages.studio.studioshell.search.members.or.revisions.2", "Search members or revisions")}
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
                    className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                    onClick={() => setMemberFilter(option.key)}
                    style={{
                      ...railFilterButtonStyle,
                      background: active ? '#17130c' : railFilterButtonStyle.background,
                      color: active ? '#fbfaf6' : railFilterButtonStyle.color,
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
                <span>{t("pages.studio.studioshell.member.inventory.2", "Member inventory")}</span>
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
                  // biome-ignore lint/a11y/useSemanticElements: The member card keeps the existing composite card interaction contract.
                  <div
                    key={member.key}
                    aria-current={isSelected ? 'true' : undefined}
                    aria-disabled={member.disabled ? 'true' : undefined}
                    className={AEVATAR_PRESSABLE_CARD_CLASS}
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
                      background: isSelected ? '#ffffff' : 'transparent',
                      border: `1px solid ${isSelected ? '#e5e0d4' : 'transparent'}`,
                      borderRadius: 10,
                      boxShadow: isSelected
                        ? '0 1px 3px rgba(28, 24, 16, 0.08)'
                        : 'none',
                      cursor:
                        member.disabled || !onSelectMember ? 'default' : 'pointer',
                      alignItems: 'center',
                      display: 'flex',
                      gap: 8,
                      opacity: member.disabled ? 0.56 : 1,
                      boxSizing: 'border-box',
                      minHeight: 0,
                      overflow: 'hidden',
                      padding: '7px 10px',
                      textAlign: 'left',
                      transition:
                        'background-color 0.16s ease, border-color 0.16s ease, box-shadow 0.16s ease',
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
                          background: isSelected ? '#f0ede4' : 'transparent',
                          borderRadius: 8,
                          color: isSelected ? '#5a5142' : '#8a8172',
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 12,
                          height: 26,
                          justifyContent: 'center',
                          width: 26,
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
                            color: '#9a9184',
                            display: 'inline-flex',
                            flexShrink: 0,
                            fontSize: 9.5,
                            fontWeight: 700,
                            letterSpacing: '0.02em',
                            lineHeight: '14px',
                            textTransform: 'uppercase',
                          }}
                        >
                          {formatMemberKindLabel(kind)}
                        </span>
                        <span
                          style={{
                            color: isSelected ? '#17130c' : '#3f382d',
                            fontSize: 13,
                            fontWeight: isSelected ? 700 : 600,
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
                          color: toneStyles.color,
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 9.5,
                          fontWeight: 700,
                          justifyContent: 'center',
                          lineHeight: '14px',
                          minHeight: 20,
                          padding: '0 2px',
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
                        {formatMemberTone(member.tone)}
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
                ? t("pages.studio.studioshell.no.members.match.the.current.search", "No members match the current search or filter. Try clearing the rail controls.")
                : t("pages.studio.studioshell.no.team.members.yet.create.member", "No team members yet. Create a member to start building in Studio.")}
            </Typography.Text>
          )}
        </div>

      </aside>
      ) : null}

      <div data-testid="studio-shell-main" style={mainStyle}>
        {contextBar}
        {showLifecycle && lifecycleSteps.length > 0 ? (
          <div data-testid="studio-lifecycle-section" style={lifecycleSectionStyle}>
            <div style={lifecycleHeaderStyle}>
              <Typography.Text
                style={{
                  color: '#6b7280',
                  fontSize: 10,
                  fontWeight: 700,
                  letterSpacing: 0,
                  textTransform: 'uppercase',
                }}
              >
                {t("pages.studio.studioshell.member.lifecycle.3", "Member lifecycle")}</Typography.Text>
              <InlineInfoButton
                ariaLabel={t("pages.studio.studioshell.open.lifecycle.help", "Open lifecycle help")}
                content={t(
                  "pages.studio.studioshell.keep.the.selected.member",
                  "Keep the selected member in one shell while authoring context, callable service state, and run evidence stay aligned.",
                )}
              />
            </div>
            <nav
              aria-label={t("pages.studio.studioshell.member.lifecycle.4", "Member lifecycle")}
              data-testid="studio-lifecycle-stepper"
              style={lifecycleRowStyle}
            >
              {lifecycleSteps.map((step, index) => {
                const isActive = currentLifecycleStep === step.key;
                const isPlanned = step.status === 'planned';
                const indicatorBackground = isActive
                  ? '#ffffff'
                  : step.disabled || isPlanned
                    ? '#f1eee7'
                    : '#ece8de';
                const indicatorColor = isActive
                  ? '#17130c'
                  : step.disabled || isPlanned
                    ? '#a29a8b'
                    : '#6f675a';
                return (
                  <React.Fragment key={step.key}>
                    {index > 0 ? (
                      <span aria-hidden="true" style={lifecycleConnectorStyle} />
                    ) : null}
                    <button
                      aria-current={isActive ? 'step' : undefined}
                      className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                      disabled={step.disabled}
                      onClick={() => onSelectLifecycleStep?.(step.key)}
                      title={step.description}
                      style={{
                        alignItems: 'center',
                        background: isActive ? '#17130c' : '#ffffff',
                        border: `1px solid ${isActive ? '#17130c' : '#e5e0d4'}`,
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
                        padding: '5px 12px',
                        textAlign: 'left',
                      }}
                      type="button"
                    >
                      <span
                        style={{
                          alignItems: 'center',
                          background: indicatorBackground,
                          borderRadius: 999,
                          color: indicatorColor,
                          display: 'inline-flex',
                          flexShrink: 0,
                          fontSize: 9.5,
                          fontWeight: 700,
                          height: 18,
                          justifyContent: 'center',
                          width: 18,
                        }}
                      >
                        {step.disabled || isPlanned ? index + 1 : <CheckOutlined />}
                      </span>
                      <span
                        style={{
                          color: isActive ? '#ffffff' : '#3f382d',
                          fontSize: 11,
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
        <div data-testid="studio-shell-content" style={contentStyle}>
          {showPageHeader ? (
            <div style={shellHeaderStyle}>
              <Typography.Title level={4} style={shellHeaderTitleStyle}>
                {pageTitle}
              </Typography.Title>
              {pageToolbar}
            </div>
          ) : null}
          <div style={pageBodyStyle}>
            {children}
          </div>
        </div>
      </div>
    </div>
  );
};

export default StudioShell;
