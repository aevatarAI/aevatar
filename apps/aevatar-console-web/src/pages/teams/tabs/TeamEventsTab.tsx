import { Button, Space, Typography, theme } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { AEVATAR_INTERACTIVE_CHIP_CLASS } from "@/shared/ui/interactionStandards";
import {
  CompactFactValue,
  DetailPill,
  FactLine,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";

type RunSwitchOption = {
  readonly buttonStyle: React.CSSProperties;
  readonly label: string;
  readonly runId: string;
};

type EventStreamRow = {
  readonly detail: string;
  readonly detailNote: string;
  readonly flowLabel: string;
  readonly key: string;
  readonly stageLabel: string;
  readonly stageStyle: React.CSSProperties;
  readonly timeLabel: string;
};

type MemberMappingRow = {
  readonly implementation: string;
  readonly key: string;
  readonly member: string;
  readonly responsibility: string;
  readonly serviceLabel: string;
  readonly serviceNote: string;
  readonly statusLabel: string;
  readonly statusNote?: string;
  readonly statusStyle: React.CSSProperties;
};

type TeamEventsTabProps = {
  readonly activeRunLabel: string;
  readonly activeRunMetaLabel: string;
  readonly currentRunStatusLabel?: string;
  readonly currentRunStatusStyle?: React.CSSProperties;
  readonly eventRows: readonly EventStreamRow[];
  readonly isRunsError: boolean;
  readonly isRunsLoading: boolean;
  readonly memberMappingRows: readonly MemberMappingRow[];
  readonly onOpenAudit: () => void;
  readonly onOpenMissionControl: () => void;
  readonly onSelectRun: (runId: string) => void;
  readonly openAuditButtonStyle: React.CSSProperties;
  readonly playbackSummary: string;
  readonly provenanceLabel: string;
  readonly provenanceStyle: React.CSSProperties;
  readonly runSwitchOptions: readonly RunSwitchOption[];
  readonly showOpenAudit: boolean;
  readonly showOpenMissionControl: boolean;
};

const TeamEventsTab: React.FC<TeamEventsTabProps> = ({
  activeRunLabel,
  activeRunMetaLabel,
  currentRunStatusLabel,
  currentRunStatusStyle,
  eventRows,
  isRunsError,
  isRunsLoading,
  memberMappingRows,
  onOpenAudit,
  onOpenMissionControl,
  onSelectRun,
  openAuditButtonStyle,
  playbackSummary,
  provenanceLabel,
  provenanceStyle,
  runSwitchOptions,
  showOpenAudit,
  showOpenMissionControl,
}) => {
  const { token } = theme.useToken();

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="当前任务事件流"
        extra={
          <Space size={8} wrap>
            <DetailPill compact style={provenanceStyle} text={provenanceLabel} />
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {activeRunMetaLabel}
            </Typography.Text>
          </Space>
        }
      >
        {isRunsLoading ? (
          <AevatarInspectorEmpty description="正在加载最近运行。" />
        ) : isRunsError ? (
          <AevatarInspectorEmpty
            title="运行信号暂不可用"
            description="当前无法读取最近运行。"
          />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div
              style={{
                alignItems: "flex-start",
                background: token.colorBgContainerDisabled,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                display: "flex",
                flexWrap: "wrap",
                gap: 12,
                justifyContent: "space-between",
                padding: 16,
              }}
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 8, minWidth: 0 }}>
                <Space wrap>
                  <CompactFactValue value={activeRunLabel} />
                  {currentRunStatusLabel && currentRunStatusStyle ? (
                    <DetailPill
                      compact
                      style={currentRunStatusStyle}
                      text={currentRunStatusLabel}
                    />
                  ) : null}
                </Space>
                <Typography.Text type="secondary">{playbackSummary}</Typography.Text>
                <div style={{ minWidth: 0 }}>
                  <CompactFactValue
                    color="var(--ant-color-text-secondary)"
                    strong={false}
                    value={activeRunMetaLabel}
                  />
                </div>
                {runSwitchOptions.length > 1 ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      切换 Run
                    </Typography.Text>
                    <div
                      style={{
                        alignItems: "center",
                        background: token.colorBgContainer,
                        border: `1px solid ${token.colorBorderSecondary}`,
                        borderRadius: 999,
                        display: "inline-flex",
                        flexWrap: "wrap",
                        gap: 6,
                        padding: 6,
                      }}
                    >
                      {runSwitchOptions.map((option) => (
                        <button
                          aria-label={`切换到 ${option.runId}`}
                          className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                          key={option.runId}
                          onClick={() => onSelectRun(option.runId)}
                          style={option.buttonStyle}
                          type="button"
                        >
                          {option.label}
                        </button>
                      ))}
                    </div>
                  </div>
                ) : null}
              </div>
              {showOpenAudit || showOpenMissionControl ? (
                <Space wrap>
                  {showOpenMissionControl ? (
                    <Button
                      onClick={onOpenMissionControl}
                      style={openAuditButtonStyle}
                      type="primary"
                    >
                      打开 Mission Control
                    </Button>
                  ) : null}
                  {showOpenAudit ? (
                    <Button onClick={onOpenAudit} style={openAuditButtonStyle}>
                      打开完整审计
                    </Button>
                  ) : null}
                </Space>
              ) : null}
            </div>
            {eventRows.length > 0 ? (
              <div
                style={{
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 18,
                  overflow: "hidden",
                }}
              >
                <div style={{ overflowX: "auto" }}>
                  <div style={{ minWidth: 920 }}>
                    <div
                      style={{
                        background: token.colorBgContainerDisabled,
                        borderBottom: `1px solid ${token.colorBorderSecondary}`,
                        color: token.colorTextSecondary,
                        display: "grid",
                        fontSize: 12,
                        fontWeight: 600,
                        gap: 16,
                        gridTemplateColumns:
                          "96px 112px minmax(200px, 1.25fr) minmax(280px, 2fr)",
                        padding: "12px 16px",
                      }}
                    >
                      <span>时间</span>
                      <span>事件</span>
                      <span>流向</span>
                      <span>说明</span>
                    </div>
                    {eventRows.map((row, index) => (
                      <div
                        key={row.key}
                        style={{
                          borderTop:
                            index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                          display: "grid",
                          gap: 16,
                          gridTemplateColumns:
                            "96px 112px minmax(200px, 1.25fr) minmax(280px, 2fr)",
                          padding: "14px 16px",
                        }}
                      >
                        <Typography.Text
                          strong
                          style={{ fontFamily: factValueFontFamily, whiteSpace: "nowrap" }}
                        >
                          {row.timeLabel}
                        </Typography.Text>
                        <DetailPill compact style={row.stageStyle} text={row.stageLabel} />
                        <FactLine rows={2} text={row.flowLabel} />
                        <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                          <Typography.Text>{row.detail}</Typography.Text>
                          {row.detailNote ? (
                            <FactLine rows={2} secondary text={row.detailNote} />
                          ) : null}
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            ) : (
              <Typography.Text type="secondary">
                当前还没有更多可见的事件事实。
              </Typography.Text>
            )}
          </div>
        )}
      </AevatarPanel>
      <AevatarPanel
        title="本次 Run 成员映射"
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            仅展示当前 run 命中的成员、职责与关联服务
          </Typography.Text>
        }
      >
        {memberMappingRows.length > 0 ? (
          <div
            style={{
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 18,
              overflow: "hidden",
            }}
          >
            <div style={{ overflowX: "auto" }}>
              <div style={{ minWidth: 920 }}>
                <div
                  style={{
                    background: token.colorBgContainerDisabled,
                    borderBottom: `1px solid ${token.colorBorderSecondary}`,
                    color: token.colorTextSecondary,
                    display: "grid",
                    fontSize: 12,
                    fontWeight: 600,
                    gap: 16,
                    gridTemplateColumns:
                      "minmax(120px, 1fr) minmax(220px, 1.8fr) minmax(120px, 0.9fr) minmax(200px, 1.2fr) minmax(132px, 0.95fr)",
                    padding: "12px 16px",
                  }}
                >
                  <span>成员</span>
                  <span>职责</span>
                  <span>实现</span>
                  <span>关联服务</span>
                  <span>状态</span>
                </div>
                {memberMappingRows.map((row, index) => (
                  <div
                    key={row.key}
                    style={{
                      alignItems: "center",
                      borderTop:
                        index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(120px, 1fr) minmax(220px, 1.8fr) minmax(120px, 0.9fr) minmax(200px, 1.2fr) minmax(132px, 0.95fr)",
                      padding: "14px 16px",
                    }}
                  >
                    <Typography.Text strong>{row.member}</Typography.Text>
                    <FactLine rows={2} text={row.responsibility} />
                    <Typography.Text style={{ fontFamily: factValueFontFamily }}>
                      {row.implementation}
                    </Typography.Text>
                    <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                      <Typography.Text strong>{row.serviceLabel}</Typography.Text>
                      <FactLine rows={1} secondary text={row.serviceNote} />
                    </div>
                    <div
                      style={{
                        alignItems: "flex-start",
                        display: "flex",
                        flexDirection: "column",
                        gap: 4,
                        minWidth: 0,
                      }}
                    >
                      <DetailPill compact style={row.statusStyle} text={row.statusLabel} />
                      {row.statusNote ? (
                        <Typography.Text style={{ fontSize: 12 }} type="secondary">
                          {row.statusNote}
                        </Typography.Text>
                      ) : null}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title="当前 run 还没有命中可见成员"
            description="等这支团队产生运行步骤或事件后，这里才会显示本次 run 的参与成员。"
          />
        )}
      </AevatarPanel>
    </div>
  );
};

export default TeamEventsTab;
