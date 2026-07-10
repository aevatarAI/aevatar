import {
  CheckCircleOutlined,
  PlayCircleOutlined,
  StopOutlined,
  WarningOutlined,
} from "@ant-design/icons";
import { Alert, Button, Input, Space, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  factValueFontFamily,
} from "./TeamDetailPrimitives";
import type { TeamTestErrorDescription } from "./teamTestErrors";

export type TeamTestStatus =
  | "idle"
  | "setting-entry"
  | "running"
  | "success"
  | "error"
  | "stopped";

export type TeamTestRosterRow = {
  readonly buildStudioHref: string;
  readonly canInvokeAsEntry: boolean;
  readonly editStudioHref: string;
  readonly implementationKind: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
  readonly workflowSupported: boolean;
};

export type TeamTestLastResult = {
  readonly finishedAtLabel: string;
  readonly runId?: string;
  readonly status: "success" | "error" | "stopped";
  readonly summary: string;
};

type TeamTestPanelProps = {
  readonly createMemberHref: string;
  readonly currentMemberId?: string | null;
  readonly currentMemberLabel?: string;
  readonly disabled?: boolean;
  readonly entryActionBusyMemberId?: string;
  readonly entryMemberId?: string | null;
  readonly error?: TeamTestErrorDescription | null;
  readonly lastResult?: TeamTestLastResult | null;
  readonly onClearEntry?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onPromptChange: (value: string) => void;
  readonly onSetEntry: (
    memberId: string,
    options?: { readonly test?: boolean },
  ) => void;
  readonly onStop: () => void;
  readonly onTest: () => void;
  readonly prompt: string;
  readonly resultText: string;
  readonly rosterError?: boolean;
  readonly rosterLoading?: boolean;
  readonly rosterRows: readonly TeamTestRosterRow[];
  readonly rosterSyncing?: boolean;
  readonly status: TeamTestStatus;
};

function resolveTestStatusPill(
  token: ReturnType<typeof theme.useToken>["token"],
  status: TeamTestStatus,
): React.CSSProperties {
  switch (status) {
    case "running":
    case "setting-entry":
      return {
        background: token.colorInfoBg,
        border: `1px solid ${token.colorInfoBorder}`,
        color: token.colorInfo,
      };
    case "success":
      return {
        background: token.colorSuccessBg,
        border: `1px solid ${token.colorSuccessBorder}`,
        color: token.colorSuccess,
      };
    case "error":
      return {
        background: token.colorErrorBg,
        border: `1px solid ${token.colorErrorBorder}`,
        color: token.colorError,
      };
    case "stopped":
      return {
        background: token.colorWarningBg,
        border: `1px solid ${token.colorWarningBorder}`,
        color: token.colorWarning,
      };
    default:
      return {
        background: token.colorFillQuaternary,
        border: `1px solid ${token.colorBorderSecondary}`,
        color: token.colorTextSecondary,
      };
  }
}

function formatStatusLabel(
  status: TeamTestStatus,
  intl: ReturnType<typeof useIntl>,
): string {
  switch (status) {
    case "setting-entry":
      return intl.formatMessage({ id: "teams.detail.test.status.settingEntry" });
    case "running":
      return intl.formatMessage({ id: "teams.detail.test.status.running" });
    case "success":
      return intl.formatMessage({ id: "teams.detail.test.status.success" });
    case "error":
      return intl.formatMessage({ id: "teams.detail.test.status.error" });
    case "stopped":
      return intl.formatMessage({ id: "teams.detail.test.status.stopped" });
    default:
      return intl.formatMessage({ id: "teams.detail.test.status.idle" });
  }
}

function formatLifecycleLabelForTeamTest(
  label: string,
  intl: ReturnType<typeof useIntl>,
): string {
  switch (label.trim().toLowerCase()) {
    case "created":
      return intl.formatMessage({ id: "teams.detail.status.created" });
    case "build ready":
    case "build_ready":
      return intl.formatMessage({ id: "teams.detail.status.buildReady" });
    case "bind ready":
    case "bind_ready":
      return intl.formatMessage({ id: "teams.detail.status.bindReady" });
    case "unknown":
      return intl.formatMessage({ id: "teams.detail.status.unknown" });
    default:
      return label || intl.formatMessage({ id: "teams.detail.status.unknown" });
  }
}

const TeamTestPanel: React.FC<TeamTestPanelProps> = ({
  createMemberHref,
  currentMemberId,
  currentMemberLabel,
  disabled = false,
  entryActionBusyMemberId = "",
  entryMemberId,
  error,
  lastResult,
  onClearEntry,
  onNavigate,
  onPromptChange,
  onSetEntry,
  onStop,
  onTest,
  prompt,
  resultText,
  rosterError = false,
  rosterLoading = false,
  rosterRows,
  rosterSyncing = false,
  status,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const normalizedEntryMemberId = entryMemberId?.trim() ?? "";
  const normalizedCurrentMemberId = currentMemberId?.trim() ?? "";
  const showCurrentMemberContext =
    normalizedCurrentMemberId.length > 0 &&
    normalizedCurrentMemberId !== normalizedEntryMemberId;
  const entryMember =
    rosterRows.find((row) => row.memberId === normalizedEntryMemberId) ?? null;
  const readyRows = rosterRows.filter((row) => row.canInvokeAsEntry);
  const isRunning = status === "running";
  const isSettingEntry = status === "setting-entry";
  const isEntryActionBusy = entryActionBusyMemberId.trim().length > 0;
  const hasPrompt = prompt.trim().length > 0;
  const canTest = Boolean(
    normalizedEntryMemberId &&
      entryMember?.canInvokeAsEntry &&
      hasPrompt &&
      !disabled &&
      !isRunning &&
      !isSettingEntry,
  );

  const handleNavigate = React.useCallback(
    (href: string) => (event: React.MouseEvent<HTMLElement>) => {
      if (!href || !onNavigate) {
        return;
      }

      event.preventDefault();
      onNavigate(href);
    },
    [onNavigate],
  );

  const renderEntrySelection = () => {
    if (rosterSyncing || rosterLoading) {
      return (
        <AevatarInspectorEmpty
          compact
          title={intl.formatMessage({ id: "teams.detail.test.entry.checking.title" })}
          description={intl.formatMessage({
            id: "teams.detail.test.entry.checking.description",
          })}
        />
      );
    }

    if (rosterError) {
      return (
        <AevatarInspectorEmpty
          compact
          title={intl.formatMessage({
            id: "teams.detail.test.entry.rosterUnavailable.title",
          })}
          description={intl.formatMessage({
            id: "teams.detail.test.entry.rosterUnavailable.description",
          })}
        />
      );
    }

    if (rosterRows.length === 0) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({ id: "teams.detail.test.entry.empty.title" })}
            description={intl.formatMessage({
              id: "teams.detail.test.entry.empty.description",
            })}
          />
          {createMemberHref ? (
            <Button
              href={createMemberHref}
              onClick={handleNavigate(createMemberHref)}
              type="primary"
            >
              {intl.formatMessage({
                id: "teams.members.actions.createFirstWorkflow",
              })}
            </Button>
          ) : null}
        </div>
      );
    }

    return (
      <div style={{ display: "grid", gap: 10 }}>
        {readyRows.length === 0 ? (
          <Alert
            showIcon
            type="warning"
            message={intl.formatMessage({ id: "teams.detail.test.entry.noReady.title" })}
            description={intl.formatMessage({
              id: "teams.detail.test.entry.noReady.description",
            })}
          />
        ) : null}
        {rosterRows.map((row) => {
          const canSelectMissingEntryBeforePrompt = !normalizedEntryMemberId;
          const promptRequired = !hasPrompt && !canSelectMissingEntryBeforePrompt;
          const actionStartsTest = hasPrompt;

          return (
            <div
              key={row.memberId}
              style={{
                alignItems: "center",
                background: token.colorBgContainer,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                display: "flex",
                flexWrap: "wrap",
                gap: 12,
                justifyContent: "space-between",
                padding: 14,
              }}
            >
              <div
                style={{
                  display: "flex",
                  flex: "1 1 220px",
                  flexDirection: "column",
                  gap: 4,
                  minWidth: 0,
                }}
              >
                <Typography.Text strong>{row.name}</Typography.Text>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {row.implementationKind}
                </Typography.Text>
              </div>
              <Space size={6} style={{ flex: "1 1 150px" }} wrap>
                <DetailPill
                  compact
                  style={row.lifecycleStyle}
                  text={formatLifecycleLabelForTeamTest(row.lifecycleLabel, intl)}
                />
                {row.canInvokeAsEntry ? (
                  <DetailPill
                    compact
                    style={{
                      background: token.colorSuccessBg,
                      border: `1px solid ${token.colorSuccessBorder}`,
                      color: token.colorSuccess,
                    }}
                    text={intl.formatMessage({ id: "teams.detail.test.entry.testable" })}
                  />
                ) : null}
              </Space>
              <Space size={8} style={{ flex: "0 1 auto" }} wrap>
                {row.canInvokeAsEntry ? (
                  <Button
                    disabled={
                      promptRequired ||
                      disabled ||
                      isRunning ||
                      isSettingEntry ||
                      (isEntryActionBusy && entryActionBusyMemberId !== row.memberId)
                    }
                    loading={entryActionBusyMemberId === row.memberId}
                    onClick={() => onSetEntry(row.memberId, { test: actionStartsTest })}
                    size="small"
                    title={
                      promptRequired
                        ? intl.formatMessage({
                            id: "teams.detail.test.entry.promptRequiredTitle",
                          })
                        : undefined
                    }
                    type="primary"
                  >
                    {intl.formatMessage({
                      id: actionStartsTest
                        ? "teams.detail.test.entry.setAndTest"
                        : "teams.members.actions.setEntry",
                    })}
                  </Button>
                ) : (
                  <Button
                    href={row.workflowSupported ? row.buildStudioHref : undefined}
                    disabled={isEntryActionBusy || !row.workflowSupported}
                    onClick={
                      row.workflowSupported
                        ? handleNavigate(row.buildStudioHref)
                        : undefined
                    }
                    size="small"
                    title={
                      row.workflowSupported
                        ? undefined
                        : intl.formatMessage({
                            id: "teams.detail.test.entry.noReady.description",
                          })
                    }
                  >
                    {intl.formatMessage({ id: "teams.detail.test.entry.buildFirst" })}
                  </Button>
                )}
              </Space>
            </div>
          );
        })}
      </div>
    );
  };

  const renderEntryStrip = () => {
    if (!normalizedEntryMemberId) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Space align="center" size={8} wrap>
            <WarningOutlined style={{ color: token.colorWarning }} />
            <Typography.Text strong>
              {intl.formatMessage({ id: "teams.detail.test.entry.noneSelected" })}
            </Typography.Text>
          </Space>
          {renderEntrySelection()}
        </div>
      );
    }

    if (!entryMember && !rosterLoading && !rosterSyncing) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Space align="center" size={8} wrap>
            <WarningOutlined style={{ color: token.colorWarning }} />
            <Typography.Text strong>
              {intl.formatMessage({ id: "teams.detail.test.entry.notInRoster" })}
            </Typography.Text>
          </Space>
          {renderEntrySelection()}
        </div>
      );
    }

    return (
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: 14,
          justifyContent: "space-between",
        }}
      >
        <div
          style={{
            display: "flex",
            flex: "1 1 240px",
            flexDirection: "column",
            gap: 4,
            minWidth: 0,
          }}
        >
          <Space align="center" size={8} wrap>
            <CheckCircleOutlined
              style={{
                color: entryMember?.canInvokeAsEntry
                  ? token.colorSuccess
                  : token.colorWarning,
              }}
            />
            <Typography.Text strong>
              {entryMember?.name ||
                intl.formatMessage({ id: "teams.detail.test.entry.fallback" })}
            </Typography.Text>
            {entryMember ? (
              <DetailPill
                compact
                style={entryMember.lifecycleStyle}
                text={formatLifecycleLabelForTeamTest(entryMember.lifecycleLabel, intl)}
              />
            ) : null}
          </Space>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {entryMember?.canInvokeAsEntry
              ? intl.formatMessage({ id: "teams.detail.test.entry.configuredReady" })
              : intl.formatMessage({ id: "teams.detail.test.entry.configuredNeedsBinding" })}
          </Typography.Text>
        </div>
        <Space size={8} style={{ flex: "0 1 auto" }} wrap>
          {entryMember?.workflowSupported ? (
            <Button
              href={entryMember.editStudioHref}
              onClick={handleNavigate(entryMember.editStudioHref)}
              size="small"
            >
              {intl.formatMessage({ id: "teams.members.actions.workflowStudio" })}
            </Button>
          ) : null}
          {onClearEntry ? (
            <Button
              disabled={
                isRunning ||
                isSettingEntry ||
                (isEntryActionBusy && entryActionBusyMemberId !== normalizedEntryMemberId)
              }
              loading={entryActionBusyMemberId === normalizedEntryMemberId}
              onClick={onClearEntry}
              size="small"
            >
              {intl.formatMessage({ id: "teams.members.actions.clearEntry" })}
            </Button>
          ) : null}
        </Space>
      </div>
    );
  };

  return (
    <section
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 24,
        boxShadow: token.boxShadowSecondary,
        display: "flex",
        flexDirection: "column",
        gap: 18,
        padding: 24,
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          flexWrap: "wrap",
          gap: 12,
          justifyContent: "space-between",
        }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <Space align="center" size={8} wrap>
            <PlayCircleOutlined style={{ color: token.colorPrimary }} />
            <Typography.Text strong style={{ fontSize: 16 }}>
              {intl.formatMessage({ id: "teams.detail.actions.test" })}
            </Typography.Text>
            <DetailPill
              compact
              style={resolveTestStatusPill(token, status)}
              text={formatStatusLabel(status, intl)}
            />
          </Space>
          <Typography.Text style={{ fontSize: 13 }} type="secondary">
            {intl.formatMessage({ id: "teams.detail.test.subtitle" })}
          </Typography.Text>
          {showCurrentMemberContext ? (
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {intl.formatMessage(
                { id: "teams.detail.test.currentMemberContext" },
                { member: currentMemberLabel || normalizedCurrentMemberId },
              )}
            </Typography.Text>
          ) : null}
        </div>
        {lastResult ? (
          <div
            style={{
              alignItems: "flex-end",
              display: "flex",
              flexDirection: "column",
              gap: 4,
              minWidth: 0,
            }}
          >
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {intl.formatMessage(
                { id: "teams.detail.test.lastResult" },
                { time: lastResult.finishedAtLabel },
              )}
            </Typography.Text>
            <Space size={6} wrap>
              <DetailPill
                compact
                style={resolveTestStatusPill(token, lastResult.status)}
                text={formatStatusLabel(lastResult.status, intl)}
              />
            </Space>
          </div>
        ) : null}
      </div>

      <div
        style={{
          background: token.colorFillAlter,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: 18,
          padding: 16,
        }}
      >
        {renderEntryStrip()}
      </div>

      {error ? (
        <Alert
          showIcon
          type={
            error.kind === "backend_unsupported" ||
            error.kind === "entry_not_ready" ||
            error.kind === "entry_missing" ||
            error.kind === "entry_syncing"
              ? "warning"
              : error.kind === "aborted"
                ? "info"
                : "error"
          }
          message={error.title}
          description={error.description}
        />
      ) : null}

      <div
        style={{
          alignItems: "stretch",
          display: "flex",
          flexWrap: "wrap",
          gap: 14,
        }}
      >
        <Input.TextArea
          aria-label={intl.formatMessage({ id: "teams.detail.test.prompt.aria" })}
          autoSize={{ minRows: 3, maxRows: 8 }}
          disabled={disabled || isRunning || isSettingEntry}
          onChange={(event) => onPromptChange(event.target.value)}
          placeholder={intl.formatMessage({
            id: "teams.detail.test.prompt.placeholder",
          })}
          style={{ flex: "1 1 280px" }}
          value={prompt}
        />
        <Space direction="vertical" size={8} style={{ flex: "0 1 160px", minWidth: 132 }}>
          {isRunning ? (
            <Button
              block
              danger
              icon={<StopOutlined />}
              onClick={onStop}
              type="primary"
            >
              {intl.formatMessage({ id: "teams.detail.test.actions.stop" })}
            </Button>
          ) : (
            <Button
              block
              disabled={!canTest}
              icon={<PlayCircleOutlined />}
              loading={isSettingEntry}
              onClick={onTest}
              type="primary"
            >
              {intl.formatMessage({ id: "teams.detail.test.actions.start" })}
            </Button>
          )}
          {error?.action === "retry" && !isRunning ? (
            <Button block disabled={!canTest} onClick={onTest}>
              {error.actionLabel ||
                intl.formatMessage({ id: "teams.detail.test.actions.retry" })}
            </Button>
          ) : null}
        </Space>
      </div>

      <div
        aria-live="polite"
        style={{
          background: token.colorBgElevated,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: 18,
          minHeight: 148,
          overflow: "hidden",
        }}
      >
        <div
          style={{
            alignItems: "center",
            borderBottom: `1px solid ${token.colorBorderSecondary}`,
            color: token.colorTextSecondary,
            display: "flex",
            fontSize: 12,
            justifyContent: "space-between",
            padding: "10px 14px",
          }}
        >
          <span>{intl.formatMessage({ id: "teams.detail.test.history.title" })}</span>
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {formatStatusLabel(status, intl)}
          </Typography.Text>
        </div>
        <Typography.Paragraph
          style={{
            fontFamily: factValueFontFamily,
            margin: 0,
            minHeight: 104,
            overflowWrap: "anywhere",
            padding: 14,
            whiteSpace: "pre-wrap",
          }}
          type={resultText ? undefined : "secondary"}
        >
          {resultText ||
            (isRunning
              ? intl.formatMessage({ id: "teams.detail.test.history.waiting" })
              : intl.formatMessage({ id: "teams.detail.test.history.empty" }))}
        </Typography.Paragraph>
      </div>
    </section>
  );
};

export default TeamTestPanel;
