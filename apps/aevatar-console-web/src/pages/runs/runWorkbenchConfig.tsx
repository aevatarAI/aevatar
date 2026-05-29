import type { RunStatus } from "@aevatar-react-sdk/agui";
import type {
  ProColumns,
  ProDescriptionsItemProps,
} from "@ant-design/pro-components";
import { Button, Space, Tag, Typography } from "antd";
import React from "react";
import { loadStoredAuthSession } from "@/shared/auth/session";
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
  resolveRunEndpointId,
} from "@/shared/runs/endpointKinds";
import type { RecentRunEntry } from "@/shared/runs/recentRuns";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type { RunTransport } from "./runEventPresentation";
import {
  ConsoleMessage,
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from "@/shared/i18n/messages";

export type RunFormValues = {
  prompt: string;
  routeName?: string;
  scopeId?: string;
  serviceOverrideId?: string;
  endpointId?: string;
  endpointKind?: RunEndpointKind;
  payloadTypeUrl?: string;
  payloadBase64?: string;
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
  title: ConsoleMessageDescriptor;
  routeName: string;
  prompt: string;
  description: ConsoleMessageDescriptor;
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
  routeName: string;
  endpointId: string;
  endpointKind: RunEndpointKind;
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

export type SelectedRouteRecord = {
  routeName: string;
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

const runSummaryColumnMessages = {
  activeSteps: {
    id: "pages.runs.runworkbenchconfig.active.steps",
    defaultMessage: "Active steps",
  },
  currentFocus: {
    id: "pages.runs.runworkbenchconfig.current.focus",
    defaultMessage: "Current focus",
  },
  lastEvent: {
    id: "pages.runs.runworkbenchconfig.last.event",
    defaultMessage: "Last event",
  },
} satisfies Record<string, ConsoleMessageDescriptor>;

const waitingSignalColumnMessages = {
  signalName: {
    id: "pages.runs.runworkbenchconfig.signal.name",
    defaultMessage: "Signal name",
  },
} satisfies Record<string, ConsoleMessageDescriptor>;

export type ConsoleViewKey = "timeline" | "messages" | "events";

export const composerRailMinWidth = 320;
export const composerRailDefaultWidth = 360;
export const composerRailMaxWidth = 560;
export const composerRailKeyboardStep = 24;
export const monitorWorkbenchMinWidth = 520;
export const composerRailCompactWidth = 320;
export const composerRailComfortWidth = 336;
export const defaultRunRouteName = "direct";

const composerRailCompactBreakpoint = 1120;
const composerRailComfortBreakpoint = 1360;

export const builtInPresets: RunPreset[] = [
  {
    key: "direct",
    title: {
      id: "pages.runs.runworkbenchconfig.direct.chat",
      defaultMessage: "Direct chat",
    },
    routeName: "direct",
    prompt:
      "Summarize what this chat bundle can do and produce a concise execution result.",
    description: {
      id: "pages.runs.runworkbenchconfig.baseline.direct.chat.bundle.for",
      defaultMessage:
        "Baseline direct chat bundle for quick validation of the chat stream.",
    },
    tags: ["baseline", "llm"],
  },
  {
    key: "human-input",
    title: {
      id: "pages.runs.runworkbenchconfig.human.input.triage",
      defaultMessage: "Human input triage",
    },
    routeName: "human_input_manual_triage",
    prompt:
      "A production incident needs manual classification before the run can continue.",
    description: {
      id: "pages.runs.runworkbenchconfig.use.this.to.verify.human",
      defaultMessage: "Use this to verify human input prompts and resume flow.",
    },
    tags: ["human_input", "resume"],
  },
  {
    key: "human-approval",
    title: {
      id: "pages.runs.runworkbenchconfig.human.approval.gate",
      defaultMessage: "Human approval gate",
    },
    routeName: "human_approval_release_gate",
    prompt:
      "Prepare a release summary that requires explicit human approval before rollout.",
    description: {
      id: "pages.runs.runworkbenchconfig.use.this.to.verify.approval",
      defaultMessage: "Use this to verify approval flow and moderation checkpoints.",
    },
    tags: ["human_approval", "approval"],
  },
  {
    key: "wait-signal",
    title: {
      id: "pages.runs.runworkbenchconfig.wait.signal",
      defaultMessage: "Wait signal",
    },
    routeName: "wait_signal_manual_success",
    prompt: "Wait for an external readiness signal before completing the run.",
    description: {
      id: "pages.runs.runworkbenchconfig.use.this.to.verify.waiting",
      defaultMessage:
        "Use this to verify waiting_signal and manual signal delivery.",
    },
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
  sse: { text: "Service SSE", status: "Processing" },
  ws: { text: "Legacy WebSocket", status: "Default" },
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
    title: "Endpoint",
    dataIndex: "endpointId",
    render: (_, record) => record.endpointId || "chat",
  },
  {
    title: "Route",
    dataIndex: "routeName",
    render: (_, record) =>
      formatRunRouteLabel(
        record.routeName,
        record.endpointId,
        record.endpointKind
      ),
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
    title: <ConsoleMessage descriptor={runSummaryColumnMessages.currentFocus} />,
    dataIndex: "focusStatus",
    valueType: "status" as any,
    valueEnum: runFocusValueEnum,
    render: (_, record) => <Tag color="processing">{record.focusLabel}</Tag>,
  },
  {
    title: <ConsoleMessage descriptor={runSummaryColumnMessages.lastEvent} />,
    dataIndex: "lastEventAt",
    valueType: "dateTime",
    render: (_, record) => record.lastEventAt || "n/a",
  },
  {
    title: <ConsoleMessage descriptor={runSummaryColumnMessages.activeSteps} />,
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
        <Tag>{t("pages.runs.runworkbenchconfig.none", "None")}</Tag>
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

export const routeDescriptionColumns: ProDescriptionsItemProps<SelectedRouteRecord>[] =
  [
    {
      title: "Route",
      dataIndex: "routeName",
      render: (_, record) => (
        <Tag color="processing">{record.routeName}</Tag>
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
      title: <ConsoleMessage descriptor={waitingSignalColumnMessages.signalName} />,
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
  gap: 8,
  height: "calc(100vh - 64px)",
  overflow: "hidden",
  padding: 8,
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
  gap: 8,
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
  display: "flex",
  flex: 1,
  flexDirection: "column",
  minHeight: 0,
  overflow: "hidden",
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
  overflow: "hidden",
} as const;

export const workbenchTraceTabsStyle = {
  flex: 1,
  minHeight: 0,
} as const;

export const workbenchTraceTabsStyles = {
  content: {
    display: "flex",
    flex: 1,
    minHeight: 0,
    overflow: "hidden",
  },
  root: {
    display: "flex",
    flex: 1,
    flexDirection: "column",
    minHeight: 0,
  },
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

export const recentRunColumns: ProColumns<RecentRunTableRow>[] = [
  {
    title: "Route",
    dataIndex: "routeName",
    ellipsis: true,
    render: (_, record) =>
      formatRunRouteLabel(
        record.routeName,
        record.endpointId,
        record.endpointKind
      ),
  },
  {
    title: "Endpoint",
    dataIndex: "endpointId",
    ellipsis: true,
    render: (_, record) =>
      resolveRunEndpointId(record.endpointKind, record.endpointId),
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
          {t("pages.runs.runworkbenchconfig.restore", "Restore")}</Button>
        {record.actorId ? (
          <Button type="link" onClick={() => record.onOpenActor?.()}>
            {t("pages.runs.runworkbenchconfig.actor", "Actor")}</Button>
        ) : null}
      </Space>,
    ],
  },
];

export function trimOptional(value?: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

export function formatRunRouteLabel(
  routeName?: string | null,
  endpointId?: string | null,
  endpointKind?: string | null,
): string {
  const normalizedRouteName = trimOptional(routeName);
  const normalizedEndpointKind = normalizeRunEndpointKind(
    endpointKind,
    endpointId
  );
  const normalizedEndpointId = resolveRunEndpointId(
    normalizedEndpointKind,
    endpointId
  );

  if (normalizedEndpointKind === "chat") {
    return normalizedRouteName || normalizedEndpointId || "chat";
  }

  return normalizedRouteName &&
    normalizedRouteName !== normalizedEndpointId
    ? `${normalizedEndpointId} · ${normalizedRouteName}`
    : normalizedEndpointId;
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

export function readInitialRunFormValues(): RunFormValues {
  const defaultScopeId = loadStoredAuthSession()?.user.sub;

  if (typeof window === "undefined") {
    return {
      prompt: "",
      routeName: defaultRunRouteName,
      scopeId: defaultScopeId,
      serviceOverrideId: undefined,
      endpointKind: "chat",
      endpointId: "chat",
      payloadTypeUrl: undefined,
      payloadBase64: undefined,
      actorId: undefined,
      transport: "sse",
    };
  }

  const params = new URLSearchParams(window.location.search);
  const requestedEndpointKind = trimOptional(params.get("endpointKind"));
  const requestedEndpointId = trimOptional(params.get("endpointId"));
  const endpointKind =
    requestedEndpointKind || requestedEndpointId
      ? normalizeRunEndpointKind(requestedEndpointKind, requestedEndpointId)
      : "chat";
  return {
    prompt: params.get("prompt") ?? "",
    routeName:
      trimOptional(params.get("route")) ??
      trimOptional(params.get("workflow")) ??
      defaultRunRouteName,
    scopeId: trimOptional(params.get("scopeId")) ?? defaultScopeId,
    serviceOverrideId:
      trimOptional(params.get("serviceOverrideId")) ??
      trimOptional(params.get("serviceId")),
    endpointKind,
    endpointId: resolveRunEndpointId(
      endpointKind,
      requestedEndpointId
    ),
    payloadTypeUrl: trimOptional(params.get("payloadTypeUrl")),
    payloadBase64: trimOptional(params.get("payloadBase64")),
    actorId: trimOptional(params.get("actorId")),
    transport: "sse",
  };
}
