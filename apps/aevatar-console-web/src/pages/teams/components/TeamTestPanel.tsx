import {
  CheckCircleOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
  WarningOutlined,
} from "@ant-design/icons";
import { Alert, Button, Input, Space, Typography, theme } from "antd";
import React from "react";
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

function formatStatusLabel(status: TeamTestStatus): string {
  switch (status) {
    case "setting-entry":
      return "正在设置入口";
    case "running":
      return "测试中";
    case "success":
      return "已完成";
    case "error":
      return "失败";
    case "stopped":
      return "已停止";
    default:
      return "待测试";
  }
}

function formatLifecycleLabelForTeamTest(label: string): string {
  switch (label.trim().toLowerCase()) {
    case "created":
      return "已创建";
    case "build ready":
    case "build_ready":
      return "可绑定";
    case "bind ready":
    case "bind_ready":
      return "可调用";
    case "unknown":
      return "状态未知";
    default:
      return label || "状态未知";
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
          title="正在检查入口成员"
          description="成员清单同步完成后即可选择入口成员。"
        />
      );
    }

    if (rosterError) {
      return (
        <AevatarInspectorEmpty
          compact
          title="成员清单暂不可见"
          description="当前无法读取 Team 成员，暂时不能选择入口成员。"
        />
      );
    }

    if (rosterRows.length === 0) {
      return (
        <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
          <AevatarInspectorEmpty
            compact
            title="这支 Team 还没有成员"
            description="先创建一个成员，完成 Build / Bind 后再测试 Team。"
          />
          {createMemberHref ? (
            <Button
              href={createMemberHref}
              onClick={handleNavigate(createMemberHref)}
              type="primary"
            >
              创建第一个成员
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
            message="还没有可作为入口的成员"
            description="成员需要完成 Build / Bind，并进入可调用状态后才能测试 Team。"
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
                text={formatLifecycleLabelForTeamTest(row.lifecycleLabel)}
              />
              {row.canInvokeAsEntry ? (
                <DetailPill
                  compact
                  style={{
                    background: token.colorSuccessBg,
                    border: `1px solid ${token.colorSuccessBorder}`,
                    color: token.colorSuccess,
                  }}
                  text="可测试"
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
                  title={!hasPrompt ? "请先输入测试问题。" : undefined}
                  type="primary"
                >
                  设为入口并测试
                </Button>
              ) : (
                <Button
                  href={row.buildStudioHref}
                  disabled={isEntryActionBusy}
                  onClick={handleNavigate(row.buildStudioHref)}
                  size="small"
                >
                  先 Build / Bind
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
            <Typography.Text strong>未选择入口成员</Typography.Text>
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
            <Typography.Text strong>入口成员不在当前 Team 成员清单中</Typography.Text>
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
              {entryMember?.name || "入口成员"}
            </Typography.Text>
            {entryMember ? (
              <DetailPill
                compact
                style={entryMember.lifecycleStyle}
                text={formatLifecycleLabelForTeamTest(entryMember.lifecycleLabel)}
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
            服务
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
              在 Studio 编辑
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
              清除入口成员
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
              测试团队
            </Typography.Text>
            <DetailPill
              compact
              style={resolveTestStatusPill(token, status)}
              text={formatStatusLabel(status)}
            />
          </Space>
          <Typography.Text style={{ fontSize: 13 }} type="secondary">
            通过入口成员发起一次真实 Team 调用。
          </Typography.Text>
          {showCurrentMemberContext ? (
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              当前页面选中的是 {currentMemberLabel || normalizedCurrentMemberId}
              ，Team 测试仍通过入口成员发起。
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
              上次测试 · {lastResult.finishedAtLabel}
            </Typography.Text>
            <Space size={6} wrap>
              <DetailPill
                compact
                style={resolveTestStatusPill(token, lastResult.status)}
                text={formatStatusLabel(lastResult.status)}
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
          aria-label="测试问题"
          autoSize={{ minRows: 3, maxRows: 8 }}
          disabled={disabled || isRunning || isSettingEntry}
          onChange={(event) => onPromptChange(event.target.value)}
          placeholder="输入这支 Team 要处理的问题..."
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
              停止
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
              开始测试
            </Button>
          )}
          {error?.actionLabel === "Retry" && !isRunning ? (
            <Button block disabled={!canTest} onClick={onTest}>
              重试
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
          <span>测试记录</span>
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
              ? "等待 Team 返回..."
              : "测试结果会显示在这里。")}
        </Typography.Paragraph>
      </div>
    </section>
  );
};

export default TeamTestPanel;
