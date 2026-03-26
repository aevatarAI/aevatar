import type { RunStatus } from "@aevatar-react-sdk/agui";
import type {
  ProColumns,
  ProDescriptionsItemProps,
} from "@ant-design/pro-components";
import { Button, Space, Tag, Typography } from "antd";
import React from "react";
import type { RecentRunEntry } from "@/shared/runs/recentRuns";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type { RunTransport } from "./runEventPresentation";

export type RunFormValues = {
  prompt: string;
  workflow?: string;
  actorId?: string;
  transport: RunTransport;
};

export type ResumeFormValues = {
  approved: boolean;
  userInput?: string;
};

export type SignalFormValues = {
  payload?: string;
};

export type RunPreset = {
  key: string;
  title: string;
  workflow: string;
  prompt: string;
  description: string;
  tags: string[];
};

export type RunStatusValue = RunStatus | "unknown";
export type RunFocusStatus =
  | "idle"
  | "running"
  | "human_input"
  | "human_approval"
  | "wait_signal"
  | "finished"
  | "error";

export type RunFocusRecord = {
  status: RunFocusStatus;
  label: string;
  alertType: "info" | "success" | "warning" | "error";
  title: string;
  description: string;
};

export type RecentRunRow = RecentRunEntry & {
  key: string;
  statusValue: RunStatusValue;
};

export type RecentRunTableRow = RecentRunRow & {
  onRestore?: () => void;
  onOpenActor?: () => void;
};

export type RunSummaryRecord = {
  status: RunStatus;
  transport: RunTransport;
  workflowName: string;
  actorId: string;
  commandId: string;
  runId: string;
  focusStatus: RunFocusStatus;
  focusLabel: string;
  lastEventAt: string;
  messageCount: number;
  eventCount: number;
  activeSteps: string[];
};

export type SelectedWorkflowRecord = {
  workflowName: string;
  groupLabel: string;
  sourceLabel: string;
  llmStatus: "processing" | "success";
  description: string;
};

export type WaitingSignalRecord = {
  signalName: string;
  stepId: string;
  runId: string;
  prompt: string;
};

export type HumanInputRecord = {
  stepId: string;
  runId: string;
  suspensionType: string;
  prompt: string;
  timeoutSeconds: number;
};

export type ConsoleViewKey = "timeline" | "messages" | "events";

export const composerRailMinWidth = 320;
export const composerRailDefaultWidth = 360;
export const composerRailMaxWidth = 560;
export const composerRailKeyboardStep = 24;
export const monitorWorkbenchMinWidth = 520;
export const composerRailCompactWidth = 320;
export const composerRailComfortWidth = 336;

const composerRailCompactBreakpoint = 1120;
const composerRailComfortBreakpoint = 1360;

export const builtInPresets: RunPreset[] = [
  {
    key: "direct",
    title: "Direct chat",
    workflow: "direct",
    prompt:
      "Summarize what this workflow can do and produce a concise execution result.",
    description:
      "Baseline direct workflow for quick validation of the chat stream.",
    tags: ["baseline", "llm"],
  },
  {
    key: "human-input",
    title: "Human input triage",
    workflow: "human_input_manual_triage",
    prompt:
      "A production incident needs manual classification before the workflow can continue.",
    description: "Use this to verify human input prompts and resume flow.",
    tags: ["human_input", "resume"],
  },
  {
    key: "human-approval",
    title: "Human approval gate",
    workflow: "human_approval_release_gate",
    prompt:
      "Prepare a release summary that requires explicit human approval before rollout.",
    description: "Use this to verify approval flow and moderation checkpoints.",
    tags: ["human_approval", "approval"],
  },
  {
    key: "wait-signal",
    title: "Wait signal",
    workflow: "wait_signal_manual_success",
    prompt: "Wait for an external readiness signal before completing the run.",
    description:
      "Use this to verify waiting_signal and manual signal delivery.",
    tags: ["wait_signal", "signal"],
  },
];

export const runStatusValueEnum = {
  idle: { text: "Idle", status: "Default" },
  running: { text: "Running", status: "Processing" },
  finished: { text: "Finished", status: "Success" },
  error: { text: "Error", status: "Error" },
  unknown: { text: "Unknown", status: "Default" },
} as const;

const transportValueEnum = {
  sse: { text: "SSE", status: "Processing" },
  ws: { text: "WebSocket", status: "Success" },
} as const;

const runFocusValueEnum = {
  idle: { text: "Idle", status: "Default" },
  running: { text: "Running", status: "Processing" },
  human_input: { text: "Human input", status: "Warning" },
  human_approval: { text: "Approval", status: "Warning" },
  wait_signal: { text: "Wait signal", status: "Warning" },
  finished: { text: "Finished", status: "Success" },
  error: { text: "Error", status: "Error" },
} as const;

export const runSummaryColumns: ProDescriptionsItemProps<RunSummaryRecord>[] = [
  {
    title: "Transport",
    dataIndex: "transport",
    valueType: "status" as any,
    valueEnum: transportValueEnum,
  },
  {
    title: "Workflow",
    dataIndex: "workflowName",
    render: (_, record) => record.workflowName || "n/a",
  },
  {
    title: "Actor",
    dataIndex: "actorId",
    render: (_, record) =>
      record.actorId ? (
        <Typography.Text copyable>{record.actorId}</Typography.Text>
      ) : (
        "n/a"
      ),
  },
  {
    title: "Command",
    dataIndex: "commandId",
    render: (_, record) =>
      record.commandId ? (
        <Typography.Text copyable>{record.commandId}</Typography.Text>
      ) : (
        "n/a"
      ),
  },
  {
    title: "RunId",
    dataIndex: "runId",
    render: (_, record) =>
      record.runId ? (
        <Typography.Text copyable>{record.runId}</Typography.Text>
      ) : (
        "n/a"
      ),
  },
  {
    title: "Current focus",
    dataIndex: "focusStatus",
    valueType: "status" as any,
    valueEnum: runFocusValueEnum,
    render: (_, record) => <Tag color="processing">{record.focusLabel}</Tag>,
  },
  {
    title: "Last event",
    dataIndex: "lastEventAt",
    valueType: "dateTime",
    render: (_, record) => record.lastEventAt || "n/a",
  },
  {
    title: "Active steps",
    dataIndex: "activeSteps",
    render: (_, record) =>
      record.activeSteps.length > 0 ? (
        <Space wrap size={[4, 4]}>
          {record.activeSteps.map((step) => (
            <Tag key={step} color="processing">
              {step}
            </Tag>
          ))}
        </Space>
      ) : (
        <Tag>None</Tag>
      ),
  },
];

export const humanInputColumns: ProDescriptionsItemProps<HumanInputRecord>[] = [
  {
    title: "Step",
    dataIndex: "stepId",
    render: (_, record) => record.stepId || "n/a",
  },
  {
    title: "Run",
    dataIndex: "runId",
    render: (_, record) => record.runId || "n/a",
  },
  {
    title: "Suspension",
    dataIndex: "suspensionType",
    render: (_, record) => record.suspensionType || "n/a",
  },
  {
    title: "Timeout",
    dataIndex: "timeoutSeconds",
    valueType: "digit",
  },
  {
    title: "Prompt",
    dataIndex: "prompt",
    render: (_, record) => record.prompt || "n/a",
  },
];

export const workflowDescriptionColumns: ProDescriptionsItemProps<SelectedWorkflowRecord>[] =
  [
    {
      title: "Workflow",
      dataIndex: "workflowName",
      render: (_, record) => (
        <Tag color="processing">{record.workflowName}</Tag>
      ),
    },
    {
      title: "Group",
      dataIndex: "groupLabel",
    },
    {
      title: "Source",
      dataIndex: "sourceLabel",
    },
    {
      title: "LLM",
      dataIndex: "llmStatus",
      valueType: "status" as any,
      valueEnum: {
        processing: { text: "Required", status: "Processing" },
        success: { text: "Optional", status: "Success" },
      },
    },
    {
      title: "Description",
      dataIndex: "description",
    },
  ];

export const waitingSignalColumns: ProDescriptionsItemProps<WaitingSignalRecord>[] =
  [
    {
      title: "Signal name",
      dataIndex: "signalName",
    },
    {
      title: "Step",
      dataIndex: "stepId",
      render: (_, record) => record.stepId || "n/a",
    },
    {
      title: "Run",
      dataIndex: "runId",
      render: (_, record) => record.runId || "n/a",
    },
    {
      title: "Prompt",
      dataIndex: "prompt",
      render: (_, record) => record.prompt || "n/a",
    },
  ];

export const runsWorkbenchShellStyle = {
  background:
    "linear-gradient(180deg, rgba(15, 23, 42, 0.03) 0%, rgba(15, 23, 42, 0.01) 100%)",
  display: "flex",
  flexDirection: "column",
  gap: 12,
  height: "calc(100vh - 64px)",
  overflow: "hidden",
  padding: 12,
  position: "relative",
} as const;

export const runsWorkbenchHeaderStyle = {
  alignItems: "center",
  backdropFilter: "blur(8px)",
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 14,
  display: "flex",
  flex: "0 0 auto",
  justifyContent: "space-between",
  minHeight: 52,
  padding: "0 16px",
  position: "sticky",
  top: 0,
  zIndex: 6,
} as const;

export const runsWorkbenchMainStyle = {
  display: "flex",
  flex: 1,
  minHeight: 0,
  overflow: "hidden",
} as const;

export const runsWorkbenchComposerRailStyle = {
  display: "flex",
  minWidth: 0,
  overflow: "hidden",
} as const;

export const runsWorkbenchResizeRailStyle = {
  alignItems: "stretch",
  background: "transparent",
  border: "none",
  cursor: "col-resize",
  display: "flex",
  flex: "0 0 20px",
  justifyContent: "center",
  outline: "none",
  padding: "0 6px",
  userSelect: "none",
} as const;

export const runsWorkbenchResizeHandleStyle = {
  background: "var(--ant-color-border-secondary)",
  borderRadius: 999,
  transition: "background-color 0.2s ease, transform 0.2s ease",
  width: 4,
} as const;

export const runsWorkbenchMonitorStyle = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 12,
  minWidth: 0,
  overflow: "hidden",
} as const;

export const workbenchCardStyle = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  minHeight: 0,
} as const;

export const workbenchCardBodyStyle = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  minHeight: 0,
  overflow: "hidden",
  padding: 12,
} as const;

export const workbenchScrollableBodyStyle = {
  flex: 1,
  minHeight: 0,
  overflowX: "hidden",
  overflowY: "auto",
  paddingRight: 4,
} as const;

export const workbenchHudCardStyle = {
  ...workbenchCardStyle,
  flex: "0 0 auto",
} as const;

export const workbenchHudBodyStyle = {
  ...workbenchCardBodyStyle,
  overflow: "visible",
} as const;

export const workbenchOverviewGridStyle = {
  flex: 1,
  minHeight: 0,
} as const;

export const workbenchOverviewCardStyle = {
  ...workbenchCardStyle,
  minHeight: 0,
} as const;

export const workbenchConsoleCardStyle = {
  ...workbenchCardStyle,
  flex: "0 0 calc((100vh - 64px) * 0.3)",
  minHeight: 260,
} as const;

export const workbenchConsoleBodyStyle = {
  ...workbenchCardBodyStyle,
  overflow: "hidden",
} as const;

export const workbenchConsoleViewportStyle = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  minHeight: 0,
} as const;

export const workbenchTraceTabPanelStyle = {
  display: "flex",
  flexDirection: "column",
  flex: 1,
  minHeight: 0,
} as const;

export const workbenchConsoleSurfaceStyle = {
  background:
    "linear-gradient(180deg, rgba(248, 250, 252, 0.96) 0%, rgba(255, 255, 255, 0.98) 100%)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  color: "var(--ant-color-text)",
  display: "flex",
  flex: 1,
  flexDirection: "column",
  fontFamily:
    "'Monaco', 'Consolas', 'SFMono-Regular', 'Liberation Mono', monospace",
  minHeight: 0,
  overflow: "hidden",
} as const;

export const workbenchConsoleScrollStyle = {
  flex: 1,
  minHeight: 0,
  overflowX: "hidden",
  overflowY: "auto",
  padding: 12,
} as const;

export const workbenchMessageListStyle = {
  display: "flex",
  flexDirection: "column",
  gap: 10,
} as const;

export const workbenchEventHeaderStyle = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  color: "var(--ant-color-text-secondary)",
  display: "grid",
  fontSize: 12,
  gap: 12,
  gridTemplateColumns: "220px 120px minmax(0, 1fr)",
  padding: "12px 12px 8px",
} as const;

export const workbenchEventRowStyle = {
  borderBottom: "1px solid rgba(5, 5, 5, 0.06)",
  display: "grid",
  gap: 12,
  gridTemplateColumns: "220px 120px minmax(0, 1fr)",
  padding: "10px 12px",
} as const;

export const recentRunColumns: ProColumns<RecentRunTableRow>[] = [
  {
    title: "Workflow",
    dataIndex: "workflowName",
    ellipsis: true,
  },
  {
    title: "Status",
    dataIndex: "statusValue",
    width: 120,
    valueType: "status" as any,
    valueEnum: runStatusValueEnum,
  },
  {
    title: "Recorded",
    dataIndex: "recordedAt",
    width: 220,
    valueType: "dateTime",
    render: (_, record) => formatDateTime(record.recordedAt),
  },
  {
    title: "RunId",
    dataIndex: "runId",
    width: 180,
    render: (_, record) => record.runId || "n/a",
  },
  {
    title: "Preview",
    dataIndex: "lastMessagePreview",
    ellipsis: true,
    render: (_, record) =>
      record.lastMessagePreview || record.prompt || "No preview recorded.",
  },
  {
    title: "Actions",
    valueType: "option",
    width: 160,
    render: (_, record) => [
      <Space key={`${record.id}-actions`}>
        <Button type="link" onClick={() => record.onRestore?.()}>
          Restore
        </Button>
        {record.actorId ? (
          <Button type="link" onClick={() => record.onOpenActor?.()}>
            Actor
          </Button>
        ) : null}
      </Space>,
    ],
  },
];

export function trimOptional(value?: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

export function formatElapsedDuration(totalMilliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return [hours, minutes, seconds]
      .map((value) => value.toString().padStart(2, "0"))
      .join(":");
  }

  return [minutes, seconds]
    .map((value) => value.toString().padStart(2, "0"))
    .join(":");
}

export function clampComposerWidth(
  requestedWidth: number,
  containerWidth: number
): number {
  const maxWidth = Math.max(
    composerRailMinWidth,
    Math.min(composerRailMaxWidth, containerWidth - monitorWorkbenchMinWidth)
  );

  return Math.min(Math.max(requestedWidth, composerRailMinWidth), maxWidth);
}

export function resolveResponsiveComposerWidth(
  requestedWidth: number,
  containerWidth: number
): number {
  const clampedWidth = clampComposerWidth(requestedWidth, containerWidth);
  const responsiveCap =
    containerWidth <= composerRailCompactBreakpoint
      ? composerRailCompactWidth
      : containerWidth <= composerRailComfortBreakpoint
      ? composerRailComfortWidth
      : composerRailMaxWidth;

  return Math.min(clampedWidth, responsiveCap);
}

export function readInitialRunFormValues(
  preferredWorkflow: string
): RunFormValues {
  if (typeof window === "undefined") {
    return {
      prompt: "",
      workflow: preferredWorkflow,
      actorId: undefined,
      transport: "sse",
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    prompt: params.get("prompt") ?? "",
    workflow: trimOptional(params.get("workflow")) ?? preferredWorkflow,
    actorId: trimOptional(params.get("actorId")),
    transport: trimOptional(params.get("transport")) === "ws" ? "ws" : "sse",
  };
}
