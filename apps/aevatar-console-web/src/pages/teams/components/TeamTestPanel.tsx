import {
  CheckCircleOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
  WarningOutlined,
} from "@ant-design/icons";
import { Alert, Button, Input, Space, Typography, theme } from "antd";
import React from "react";
import { useTranslation } from "@/shared/i18n/localization";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import {
  CompactFactValue,
  DetailPill,
  FactLine,
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
};

export type TeamTestLastResult = {
  readonly finishedAtLabel: string;
  readonly runId?: string;
  readonly status: "success" | "error" | "stopped";
  readonly summary: string;
};

type TeamTestPanelProps = {
  readonly createMemberHref: string;
  readonly disabled?: boolean;
  readonly entryActionBusyMemberId?: string;
  readonly entryMemberId?: string | null;
  readonly error?: TeamTestErrorDescription | null;
  readonly lastResult?: TeamTestLastResult | null;
  readonly onClearEntry?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onPromptChange: (value: string) => void;
  readonly onSetEntryAndTest: (memberId: string) => void;
  readonly onStop: () => void;
  readonly onTest: () => void;
  readonly prompt: string;
  readonly resultText: string;
  readonly rosterError?: boolean;
  readonly rosterLoading?: boolean;
  readonly rosterRows: readonly TeamTestRosterRow[];
  readonly rosterSyncing?: boolean;
  readonly status: TeamTestStatus;
  readonly teamId: string;
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
  t: (key: string) => string,
): string {
  switch (status) {
    case "setting-entry":
      return t("team.test.status.settingEntry");
    case "running":
      return t("team.test.status.running");
    case "success":
      return t("team.test.status.success");
    case "error":
      return t("team.test.status.error");
    case "stopped":
      return t("team.test.status.stopped");
    default:
      return t("team.test.status.idle");
  }
}

function formatLifecycleLabelForTeamTest(
  label: string,
  t: (key: string) => string,
): string {
  switch (label.trim().toLowerCase()) {
    case "created":
      return t("team.test.lifecycle.created");
    case "build ready":
    case "build_ready":
      return t("team.test.lifecycle.buildReady");
    case "bind ready":
    case "bind_ready":
      return t("team.test.lifecycle.bindReady");
    case "unknown":
      return t("common.status.unknown");
    default:
      return label || t("common.status.unknown");
  }
}

const TeamTestPanel: React.FC<TeamTestPanelProps> = ({
  createMemberHref,
  disabled = false,
  entryActionBusyMemberId = "",
  entryMemberId,
  error,
  lastResult,
  onClearEntry,
  onNavigate,
  onPromptChange,
  onSetEntryAndTest,
  onStop,
  onTest,
  prompt,
  resultText,
  rosterError = false,
  rosterLoading = false,
  rosterRows,
  rosterSyncing = false,
  status,
  teamId,
}) => {
  const { token } = theme.useToken();
  const { t } = useTranslation();
  const normalizedEntryMemberId = entryMemberId?.trim() ?? "";
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
          title={t("team.test.entry.checking.title")}
          description={t("team.test.entry.checking.description")}
        />
      );
    }

    if (rosterError) {
      return (
        <AevatarInspectorEmpty
          compact
          title={t("team.members.error.title")}
          description={t("team.test.entry.error.description")}
        />
      );
    }

    if (rosterRows.length === 0) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <AevatarInspectorEmpty
            compact
            title={t("team.members.empty.title")}
            description={t("team.test.empty.description")}
          />
          {createMemberHref ? (
            <Button
              href={createMemberHref}
              onClick={handleNavigate(createMemberHref)}
              type="primary"
            >
              {t("team.members.empty.createFirst")}
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
            message={t("team.test.noReady.message")}
            description={t("team.test.noReady.description")}
          />
        ) : null}
        {rosterRows.map((row) => (
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
              <FactLine monospace rows={1} secondary text={row.memberId} />
            </div>
            <Space size={6} style={{ flex: "1 1 150px" }} wrap>
              <DetailPill
                compact
                style={row.lifecycleStyle}
                text={formatLifecycleLabelForTeamTest(row.lifecycleLabel, t)}
              />
              {row.canInvokeAsEntry ? (
                <DetailPill
                  compact
                  style={{
                    background: token.colorSuccessBg,
                    border: `1px solid ${token.colorSuccessBorder}`,
                    color: token.colorSuccess,
                  }}
                  text={t("team.test.readyBadge")}
                />
              ) : null}
            </Space>
            <Space size={8} style={{ flex: "0 1 auto" }} wrap>
              {row.canInvokeAsEntry ? (
                <Button
                  disabled={
                    !hasPrompt ||
                    disabled ||
                    isRunning ||
                    isSettingEntry ||
                    (isEntryActionBusy && entryActionBusyMemberId !== row.memberId)
                  }
                  loading={entryActionBusyMemberId === row.memberId}
                  onClick={() => onSetEntryAndTest(row.memberId)}
                  size="small"
                  title={!hasPrompt ? t("team.test.promptRequired") : undefined}
                  type="primary"
                >
                  {t("team.test.setEntryAndTest")}
                </Button>
              ) : (
                <Button
                  href={row.buildStudioHref}
                  disabled={isEntryActionBusy}
                  onClick={handleNavigate(row.buildStudioHref)}
                  size="small"
                >
                  {t("team.test.buildBindFirst")}
                </Button>
              )}
            </Space>
          </div>
        ))}
      </div>
    );
  };

  const renderEntryStrip = () => {
    if (!normalizedEntryMemberId) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Space align="center" size={8} wrap>
            <WarningOutlined style={{ color: token.colorWarning }} />
            <Typography.Text strong>{t("team.test.noEntry")}</Typography.Text>
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
              {t("team.test.entryMissingFromRoster")}
            </Typography.Text>
            <CompactFactValue value={normalizedEntryMemberId} />
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
              {entryMember?.name || t("team.test.entryFallback")}
            </Typography.Text>
            {entryMember ? (
              <DetailPill
                compact
                style={entryMember.lifecycleStyle}
                text={formatLifecycleLabelForTeamTest(entryMember.lifecycleLabel, t)}
              />
            ) : null}
          </Space>
          <CompactFactValue
            color={token.colorTextSecondary}
            head={8}
            strong={false}
            tail={6}
            value={normalizedEntryMemberId}
          />
        </div>
        <div
          style={{
            display: "flex",
            flex: "1 1 180px",
            flexDirection: "column",
            gap: 4,
            minWidth: 0,
          }}
        >
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {t("team.members.columns.service")}
          </Typography.Text>
          <CompactFactValue value={entryMember?.serviceId || "--"} />
        </div>
        <Space size={8} style={{ flex: "0 1 auto" }} wrap>
          {entryMember?.editStudioHref ? (
            <Button
              href={entryMember.editStudioHref}
              onClick={handleNavigate(entryMember.editStudioHref)}
              size="small"
            >
              {t("team.test.editInStudio")}
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
              {t("team.members.clearEntry")}
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
              {t("team.test.title")}
            </Typography.Text>
            <DetailPill
              compact
              style={resolveTestStatusPill(token, status)}
              text={formatStatusLabel(status, t)}
            />
          </Space>
          <Typography.Text style={{ fontSize: 13 }} type="secondary">
            {t("team.test.subtitle")}
          </Typography.Text>
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
              {t("team.test.last", { time: lastResult.finishedAtLabel })}
            </Typography.Text>
            <Space size={6} wrap>
              <DetailPill
                compact
                style={resolveTestStatusPill(token, lastResult.status)}
                text={formatStatusLabel(lastResult.status, t)}
              />
              {lastResult.runId ? (
                <CompactFactValue head={6} tail={6} value={lastResult.runId} />
              ) : null}
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
          aria-label={t("team.test.promptAria")}
          autoSize={{ minRows: 3, maxRows: 8 }}
          disabled={disabled || isRunning || isSettingEntry}
          onChange={(event) => onPromptChange(event.target.value)}
          placeholder={t("team.test.promptPlaceholder")}
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
              {t("common.stop")}
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
              {t("team.test.start")}
            </Button>
          )}
          {error?.actionLabel && !isRunning ? (
            <Button block disabled={!canTest} onClick={onTest}>
              {error.actionLabel}
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
          <span>{t("team.test.resultTitle")}</span>
          <Space size={6}>
            <LinkOutlined />
            <CompactFactValue
              color={token.colorTextSecondary}
              head={8}
              strong={false}
              tail={6}
              value={teamId}
            />
          </Space>
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
              ? t("team.test.waiting")
              : t("team.test.resultEmpty"))}
        </Typography.Paragraph>
      </div>
    </section>
  );
};

export default TeamTestPanel;
