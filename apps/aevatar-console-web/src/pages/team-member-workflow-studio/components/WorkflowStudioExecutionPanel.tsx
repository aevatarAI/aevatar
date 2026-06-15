import { Alert, Segmented, Tag, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import {
  buildExecutionTrace,
  formatDurationBetween,
  type ExecutionLogItem,
} from "@/shared/studio/execution";
import type { StudioExecutionDetail } from "@/shared/studio/models";

type WorkflowStudioExecutionPanelProps = {
  readonly detail: StudioExecutionDetail | null;
  readonly error?: string;
  readonly height?: number;
};

type DetailMode = "logs" | "evidence";

type NodeRunCard = {
  readonly statusLog: ExecutionLogItem;
  readonly inputText: string;
  readonly interactionText: string;
  readonly pendingText: string;
  readonly outputText: string;
  readonly stepId: string;
};

type MutableNodeRunCard = {
  statusLog: ExecutionLogItem;
  inputText: string;
  interactionText: string;
  pendingText: string;
  outputText: string;
  stepId: string;
};

const categoryLabels: Record<NonNullable<ExecutionLogItem["category"]>, string> = {
  custom: "Custom",
  lifecycle: "Run",
  output: "Output",
  raw: "Raw",
  snapshot: "Snapshot",
  step: "Step",
  usage: "Usage",
};

const categoryColors: Record<NonNullable<ExecutionLogItem["category"]>, string> = {
  custom: "default",
  lifecycle: "blue",
  output: "green",
  raw: "volcano",
  snapshot: "geekblue",
  step: "cyan",
  usage: "gold",
};

const toneColors: Record<ExecutionLogItem["tone"], string> = {
  completed: "green",
  failed: "red",
  pending: "orange",
  run: "blue",
  started: "processing",
};

function formatConsoleDateTime(value: string | null | undefined): string {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) {
    return value;
  }

  return date.toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function trimConsoleText(value: string, maxLength: number): string {
  const text = value.trim();
  return text.length > maxLength ? `${text.slice(0, maxLength - 3)}...` : text;
}

function isEvidenceLog(log: ExecutionLogItem): boolean {
  return ["custom", "raw", "snapshot", "usage"].includes(log.category || "");
}

function isTerminalStepLog(log: ExecutionLogItem | undefined): boolean {
  return log?.tone === "completed" || log?.tone === "failed";
}

function buildNodeRunCards(logs: readonly ExecutionLogItem[]): NodeRunCard[] {
  const activeCardIndexByStepId = new Map<string, number>();
  const cards: MutableNodeRunCard[] = [];

  logs.forEach((log) => {
    if (log.category !== "step" || !log.stepId) {
      return;
    }

    const activeIndex = activeCardIndexByStepId.get(log.stepId);
    const activeCard =
      typeof activeIndex === "number" ? cards[activeIndex] : undefined;

    if (log.tone === "started") {
      cards.push({
        statusLog: log,
        inputText: log.clipboardText.trim(),
        interactionText: "",
        pendingText: "",
        outputText: "",
        stepId: log.stepId,
      });
      activeCardIndexByStepId.set(log.stepId, cards.length - 1);
      return;
    }

    if (log.tone === "pending" || log.tone === "run") {
      if (activeCard && !isTerminalStepLog(activeCard.statusLog)) {
        activeCard.statusLog = log;
        if (log.tone === "pending") {
          activeCard.pendingText = log.clipboardText.trim();
        } else {
          activeCard.interactionText = log.clipboardText.trim();
        }
      } else {
        cards.push({
          statusLog: log,
          inputText: "",
          interactionText: log.tone === "run" ? log.clipboardText.trim() : "",
          pendingText: log.tone === "pending" ? log.clipboardText.trim() : "",
          outputText: "",
          stepId: log.stepId,
        });
        activeCardIndexByStepId.set(log.stepId, cards.length - 1);
      }
      return;
    }

    if (log.tone === "completed" || log.tone === "failed") {
      if (activeCard && !isTerminalStepLog(activeCard.statusLog)) {
        activeCard.statusLog = log;
        activeCard.outputText = log.clipboardText.trim();
        activeCardIndexByStepId.delete(log.stepId);
        return;
      }

      cards.push({
        statusLog: log,
        inputText: "",
        interactionText: "",
        pendingText: "",
        outputText: log.clipboardText.trim(),
        stepId: log.stepId,
      });
    }
  });

  return cards;
}

function buildOutputText(
  detail: StudioExecutionDetail,
  logs: readonly ExecutionLogItem[],
): string {
  const explicitOutput = String(detail.output || "").trim();
  if (explicitOutput) {
    return explicitOutput;
  }

  const finishedLog = [...logs]
    .reverse()
    .find((log) => log.category === "output" && log.clipboardText.trim());
  return finishedLog?.clipboardText.trim() || "";
}

function renderMetric(label: string, value: React.ReactNode): React.ReactNode {
  return (
    <span
      style={{
        alignItems: "baseline",
        display: "inline-flex",
        gap: 5,
        whiteSpace: "nowrap",
      }}
    >
      <Typography.Text style={{ color: "#64748b", fontSize: 11 }}>
        {label}
      </Typography.Text>
      <Typography.Text strong style={{ color: "#111827", fontSize: 12 }}>
        {value}
      </Typography.Text>
    </span>
  );
}

function renderPayloadBlock(text: string): React.ReactNode {
  const payload = text.trim();
  if (!payload) {
    return null;
  }

  return (
    <pre
      style={{
        background: "#f8fafc",
        border: "1px solid #e5e7eb",
        borderRadius: 4,
        color: "#334155",
        fontSize: 11,
        lineHeight: "16px",
        margin: "6px 0 0",
        maxHeight: 118,
        overflow: "auto",
        padding: 8,
        whiteSpace: "pre-wrap",
        wordBreak: "break-word",
      }}
    >
      {payload}
    </pre>
  );
}

function renderNodeTextBlock(
  label: string,
  value: string,
  emptyText: string,
): React.ReactNode {
  const text = value.trim();

  return (
    <div
      style={{
        display: "grid",
        gap: 4,
        minWidth: 0,
      }}
    >
      <Typography.Text
        style={{
          color: "#64748b",
          fontSize: 11,
          fontWeight: 600,
          textTransform: "uppercase",
        }}
      >
        {label}
      </Typography.Text>
      <pre
        style={{
          background: text ? "#f8fafc" : "#ffffff",
          border: "1px solid #e5e7eb",
          borderRadius: 6,
          color: text ? "#334155" : "#94a3b8",
          fontFamily: "inherit",
          fontSize: 12,
          lineHeight: "18px",
          margin: 0,
          maxHeight: 120,
          minHeight: 34,
          overflow: "auto",
          padding: "8px 10px",
          whiteSpace: "pre-wrap",
          wordBreak: "break-word",
        }}
      >
        {text || emptyText}
      </pre>
    </div>
  );
}

function renderNodeRunCard(card: NodeRunCard, index: number): React.ReactNode {
  const log = card.statusLog;

  return (
    <div
      aria-label={t(
        "teamMemberWorkflowStudio.executionPanel.nodeRunCardAria",
        "{stepId} node run",
        { stepId: card.stepId },
      )}
      key={[index, card.stepId, log.timestamp].join(":")}
      style={{
        border: "1px solid #e5e7eb",
        borderRadius: 8,
        display: "grid",
        gap: 10,
        padding: 10,
      }}
    >
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: 6,
          minWidth: 0,
        }}
      >
        <Tag color="cyan" style={{ marginInlineEnd: 0 }}>
          {t("teamMemberWorkflowStudio.executionPanel.node", "Node")}
        </Tag>
        <Tag color={toneColors[log.tone]} style={{ marginInlineEnd: 0 }}>
          {log.tone}
        </Tag>
        <Typography.Text strong style={{ fontSize: 12 }}>
          {card.stepId}
        </Typography.Text>
        {log.meta ? (
          <Typography.Text style={{ color: "#64748b", fontSize: 11 }}>
            {log.meta}
          </Typography.Text>
        ) : null}
        <Typography.Text style={{ color: "#94a3b8", fontSize: 11 }}>
          {formatConsoleDateTime(log.timestamp)}
        </Typography.Text>
      </div>
      {renderNodeTextBlock(
        t("teamMemberWorkflowStudio.executionPanel.nodeInput", "Input"),
        card.inputText,
        t(
          "teamMemberWorkflowStudio.executionPanel.emptyNodeInput",
          "No user input provided.",
        ),
      )}
      {card.pendingText
        ? renderNodeTextBlock(
            t(
              "teamMemberWorkflowStudio.executionPanel.nodePrompt",
              "Prompt",
            ),
            card.pendingText,
            "",
          )
        : null}
      {card.interactionText
        ? renderNodeTextBlock(
            t(
              "teamMemberWorkflowStudio.executionPanel.nodeInteraction",
              "Interaction",
            ),
            card.interactionText,
            "",
          )
        : null}
      {renderNodeTextBlock(
        t("teamMemberWorkflowStudio.executionPanel.nodeOutput", "Output"),
        card.outputText,
        t(
          "teamMemberWorkflowStudio.executionPanel.emptyNodeOutput",
          "No output captured.",
        ),
      )}
    </div>
  );
}

function renderLogRow(
  log: ExecutionLogItem,
  index: number,
  options: { readonly showPayload?: boolean } = {},
): React.ReactNode {
  const category = log.category || "custom";
  const payloadText =
    options.showPayload && log.payloadText
      ? log.payloadText
      : "";

  return (
    <div
      key={[
        index,
        log.timestamp,
        log.title,
        log.meta,
        log.eventType || "",
      ].join(":")}
      style={{
        borderBottom: "1px solid #edf2f7",
        display: "grid",
        gap: 4,
        padding: "8px 0",
      }}
    >
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: 6,
          minWidth: 0,
        }}
      >
        <Tag color={categoryColors[category]} style={{ marginInlineEnd: 0 }}>
          {categoryLabels[category]}
        </Tag>
        <Tag color={toneColors[log.tone]} style={{ marginInlineEnd: 0 }}>
          {log.tone}
        </Tag>
        <Typography.Text strong style={{ fontSize: 12 }}>
          {log.title}
        </Typography.Text>
        {log.meta ? (
          <Typography.Text style={{ color: "#64748b", fontSize: 11 }}>
            {log.meta}
          </Typography.Text>
        ) : null}
        <Typography.Text style={{ color: "#94a3b8", fontSize: 11 }}>
          {formatConsoleDateTime(log.timestamp)}
        </Typography.Text>
      </div>
      {log.previewText ? (
        <Typography.Text
          style={{
            color: "#334155",
            fontSize: 12,
            overflowWrap: "anywhere",
          }}
        >
          {trimConsoleText(log.previewText, 260)}
        </Typography.Text>
      ) : null}
      {renderPayloadBlock(payloadText)}
    </div>
  );
}

const WorkflowStudioExecutionPanel: React.FC<WorkflowStudioExecutionPanelProps> = ({
  detail,
  error,
  height = 210,
}) => {
  const [detailMode, setDetailMode] = React.useState<DetailMode>("logs");
  const trace = React.useMemo(() => buildExecutionTrace(detail), [detail]);
  const logs = trace?.logs ?? [];
  const rawFrames = detail?.frames ?? [];
  const hasExecutionContent = Boolean(error || detail);
  const nodeRunCards = React.useMemo(() => buildNodeRunCards(logs), [logs]);
  const evidenceLogs = logs.filter(isEvidenceLog);
  const stepLogCount = new Set(
    logs
      .filter((log) => log.category === "step" && log.stepId)
      .map((log) => log.stepId),
  ).size;
  const outputText = detail ? buildOutputText(detail, logs) : "";
  const duration = detail
    ? formatDurationBetween(detail.startedAtUtc, detail.completedAtUtc)
    : "";
  const visibleLogs = detailMode === "evidence" ? evidenceLogs : [];

  if (!hasExecutionContent) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.executionPanel.consoleAria",
        "Draft run console",
      )}
      style={{
        background: "#ffffff",
        borderTop: "1px solid #dbe3ee",
        display: "grid",
        flex: `0 0 ${height}px`,
        gridTemplateRows: "min-content minmax(0, 1fr)",
        minHeight: 0,
      }}
    >
      <section
        data-testid="member-run-result-panel"
        style={{
          display: "contents",
        }}
      >
        {error ? (
          <div style={{ padding: "10px 14px" }}>
            <Alert message={error} showIcon type="error" />
          </div>
        ) : detail ? (
          <>
            <div
              style={{
                alignItems: "center",
                borderBottom: "1px solid #edf2f7",
                display: "flex",
                flexWrap: "wrap",
                gap: 12,
                justifyContent: "space-between",
                minHeight: 42,
                padding: "7px 14px",
              }}
            >
              <div
                style={{
                  alignItems: "center",
                  display: "flex",
                  flexWrap: "wrap",
                  gap: 10,
                  minWidth: 0,
                }}
              >
                <Typography.Text strong style={{ fontSize: 12 }}>
                  {detail.workflowName}
                </Typography.Text>
                <Tag
                  color={detail.status === "failed" ? "red" : "blue"}
                  style={{ marginInlineEnd: 0 }}
                >
                  {detail.status}
                </Tag>
                {duration
                  ? renderMetric(
                      t(
                        "teamMemberWorkflowStudio.executionPanel.duration",
                        "Duration",
                      ),
                      duration,
                    )
                  : null}
              </div>
              <div
                style={{
                  alignItems: "center",
                  display: "flex",
                  flexWrap: "wrap",
                  gap: 12,
                }}
              >
                {renderMetric(
                  t("teamMemberWorkflowStudio.executionPanel.events", "Events"),
                  rawFrames.length,
                )}
                {renderMetric(
                  t("teamMemberWorkflowStudio.executionPanel.steps", "Steps"),
                  stepLogCount,
                )}
                {renderMetric(
                  t("teamMemberWorkflowStudio.executionPanel.logs", "Logs"),
                  nodeRunCards.length,
                )}
              </div>
            </div>

            <div
              style={{
                display: "grid",
                gap: 0,
                gridTemplateColumns: "minmax(420px, 1.35fr) minmax(340px, 0.85fr)",
                minHeight: 0,
                minWidth: 0,
              }}
            >
              <section
                aria-label={t(
                  "teamMemberWorkflowStudio.executionPanel.output",
                  "Output",
                )}
                style={{
                  borderRight: "1px solid #edf2f7",
                  display: "grid",
                  gridTemplateRows: "min-content minmax(0, 1fr)",
                  minHeight: 0,
                  minWidth: 0,
                  padding: "10px 14px 12px",
                }}
              >
                <div
                  style={{
                    alignItems: "center",
                    display: "flex",
                    justifyContent: "space-between",
                    marginBottom: 8,
                    minWidth: 0,
                  }}
                >
                  <Typography.Text strong style={{ fontSize: 13 }}>
                    {t("teamMemberWorkflowStudio.executionPanel.output", "Output")}
                  </Typography.Text>
                  <Typography.Text style={{ color: "#64748b", fontSize: 11 }}>
                    {t(
                      "teamMemberWorkflowStudio.executionPanel.resultFirst",
                      "Result",
                    )}
                  </Typography.Text>
                </div>
                {detail.error ? (
                  <Alert message={detail.error} showIcon type="error" />
                ) : outputText ? (
                  <pre
                    style={{
                      background: "#0f172a",
                      border: "1px solid #1e293b",
                      borderRadius: 6,
                      color: "#e2e8f0",
                      fontSize: 13,
                      lineHeight: "20px",
                      margin: 0,
                      minHeight: 0,
                      overflow: "auto",
                      padding: 14,
                      whiteSpace: "pre-wrap",
                      wordBreak: "break-word",
                    }}
                  >
                    {outputText}
                  </pre>
                ) : (
                  <div
                    style={{
                      alignItems: "center",
                      background: "#f8fafc",
                      border: "1px solid #e5e7eb",
                      borderRadius: 6,
                      color: "#64748b",
                      display: "flex",
                      fontSize: 12,
                      justifyContent: "center",
                      minHeight: 0,
                      padding: 14,
                    }}
                  >
                    {t(
                      "teamMemberWorkflowStudio.executionPanel.emptyOutput",
                      "Output will appear after the draft run emits a result.",
                    )}
                  </div>
                )}
              </section>

              <section
                aria-label={t(
                  "teamMemberWorkflowStudio.executionPanel.timeline",
                  "Timeline",
                )}
                style={{
                  display: "grid",
                  gridTemplateRows: "min-content minmax(0, 1fr)",
                  minHeight: 0,
                  minWidth: 0,
                  padding: "10px 14px 12px",
                }}
              >
                <div
                  style={{
                    alignItems: "center",
                    display: "flex",
                    gap: 10,
                    justifyContent: "space-between",
                    marginBottom: 8,
                    minWidth: 0,
                  }}
                >
                  <Typography.Text strong style={{ fontSize: 13 }}>
                    {t(
                      "teamMemberWorkflowStudio.executionPanel.runLog",
                      "Run log",
                    )}
                  </Typography.Text>
                  <Segmented
                    size="small"
                    value={detailMode}
                    onChange={(value) => setDetailMode(value as DetailMode)}
                    options={[
                      {
                        label: t(
                          "teamMemberWorkflowStudio.executionPanel.logs",
                          "Logs",
                        ),
                        value: "logs",
                      },
                      {
                        label: t(
                          "teamMemberWorkflowStudio.executionPanel.evidence",
                          "Evidence frames",
                        ),
                        value: "evidence",
                      },
                    ]}
                  />
                </div>
                <div
                  style={{
                    minHeight: 0,
                    overflow: "auto",
                    paddingRight: 4,
                  }}
                >
                  {detailMode === "logs" && nodeRunCards.length ? (
                    <div
                      style={{
                        display: "grid",
                        gap: 10,
                      }}
                    >
                      {nodeRunCards.map((card, index) =>
                        renderNodeRunCard(card, index),
                      )}
                    </div>
                  ) : visibleLogs.length ? (
                    visibleLogs.map((log, index) =>
                      renderLogRow(log, index, {
                        showPayload: detailMode === "evidence",
                      }),
                    )
                  ) : (
                    <Typography.Text style={{ color: "#64748b", fontSize: 12 }}>
                      {detailMode === "evidence"
                        ? t(
                            "teamMemberWorkflowStudio.executionPanel.emptyEvidence",
                            "Usage, snapshots, and raw observed events will appear here when the backend emits them.",
                          )
                        : rawFrames.length
                          ? t(
                            "teamMemberWorkflowStudio.executionPanel.rawFrames",
                            "{count} run event(s) received, but no step output is available yet.",
                            { count: rawFrames.length },
                          )
                        : t(
                            "teamMemberWorkflowStudio.executionPanel.emptyLogs",
                            "Step outputs will appear here after the workflow draft runs.",
                          )}
                    </Typography.Text>
                  )}
                </div>
              </section>
            </div>
          </>
        ) : null}
      </section>
    </aside>
  );
};

export default WorkflowStudioExecutionPanel;
