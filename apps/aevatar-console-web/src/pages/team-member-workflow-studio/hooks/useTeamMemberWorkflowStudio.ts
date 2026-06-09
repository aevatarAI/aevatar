import { useMutation, useQuery } from "@tanstack/react-query";
import { message } from "antd";
import { AGUIEventType } from "@aevatar-react-sdk/types";
import React from "react";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
  type RuntimeEventAccumulator,
} from "@/shared/agui/runtimeEventSemantics";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import {
  applyStepInspectorDraft,
  connectStepToTarget,
  createStepInspectorDraft,
  insertStepByType,
  removeStep,
  type StudioStepInspectorDraft,
} from "@/shared/studio/document";
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
  STUDIO_GRAPH_CATEGORIES,
} from "@/shared/studio/graph";
import { isStudioApiStatus, studioApi } from "@/shared/studio/api";
import type {
  StudioExecutionDetail,
  StudioExecutionFrame,
  StudioExecutionSummary,
  StudioMemberBindingRunStatusResponse,
  StudioMemberDetail,
  StudioWorkflowDocument,
  StudioWorkflowFile,
} from "@/shared/studio/models";

type TeamMemberWorkflowStudioMode = "new" | "existing";
type WorkflowPublishTone = "default" | "processing" | "success" | "warning" | "error";
type WorkflowExecutionStatus = "idle" | "running" | "succeeded" | "failed";

type SaveWorkflowDraftVariables = {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
};

type SavedWorkflowDraft = {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
};

type RunActiveMemberVariables = {
  readonly memberId: string;
  readonly runInput: string;
  readonly publishedServiceId: string;
  readonly title: string;
  readonly workflowId?: string;
};

type PublishWorkflowVariables = {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
};

type PublishedWorkflow = {
  readonly run: StudioMemberBindingRunStatusResponse | null;
  readonly savedDraft: SavedWorkflowDraft | null;
};

type CreatedWorkflowMember = {
  readonly memberId: string;
  readonly savedDraft: SavedWorkflowDraft;
};

type TeamMemberWorkflowStudioState = {
  readonly publishMember: () => void;
  readonly memberPublished: boolean;
  readonly publishDisabled: boolean;
  readonly publishNotice: string;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason: string;
  readonly publishTone: WorkflowPublishTone;
  readonly backHref: string;
  readonly canRunActiveMember: boolean;
  readonly canSave: boolean;
  readonly canSetTeamEntry: boolean;
  readonly closeNodeLibrary: () => void;
  readonly connectNodes: (sourceNodeId: string, targetNodeId: string) => void;
  readonly deleteSelectedNode: () => void;
  readonly dirty: boolean;
  readonly emptyDescription: string;
  readonly runActiveMember: () => void;
  readonly activeMemberRunPending: boolean;
  readonly activeMemberRunPlaceholderReason: string;
  readonly executionDetail: StudioExecutionDetail | null;
  readonly executionError: string;
  readonly executionRunInput: string;
  readonly executionStatus: WorkflowExecutionStatus;
  readonly memberRuns: readonly StudioExecutionSummary[];
  readonly memberRunsEmptyReason: string;
  readonly memberRunsError: string;
  readonly memberRunsLoading: boolean;
  readonly openExecution: (executionId: string) => void;
  readonly graph: ReturnType<typeof buildStudioGraphElements>;
  readonly insertNode: (stepType: string) => void;
  readonly linkedWorkflowMissing: boolean;
  readonly loading: boolean;
  readonly moveNodes: (nodes: ReturnType<typeof buildStudioGraphElements>["nodes"]) => void;
  readonly mode: TeamMemberWorkflowStudioMode;
  readonly navigateBack: () => void;
  readonly nodeLibraryOpen: boolean;
  readonly openNodeLibrary: () => void;
  readonly openRunOptions: () => void;
  readonly save: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason: string;
  readonly runOptionsOpen: boolean;
  readonly selectedNodeId: string;
  readonly selectedStepDraft: StudioStepInspectorDraft | null;
  readonly selectedStepParameterError: string;
  readonly selectedTab: "editor" | "runs";
  readonly setSelectedTab: (tab: "editor" | "runs") => void;
  readonly updateSelectedStepParameters: (parametersText: string) => void;
  readonly selectCanvas: () => void;
  readonly selectNode: (nodeId: string) => void;
  readonly setExecutionRunInput: (input: string) => void;
  readonly setTeamEntry: () => void;
  readonly setWorkflowTitle: (title: string) => void;
  readonly teamEntryNotice: string;
  readonly teamEntryPending: boolean;
  readonly teamName: string;
  readonly workflowTitle: string;
};

const AVAILABLE_STEP_TYPES = STUDIO_GRAPH_CATEGORIES.flatMap(
  (category) => category.items,
);
const MEMBER_BINDING_RUN_POLL_ATTEMPTS = 8;
const MEMBER_BINDING_RUN_POLL_DELAY_MS = 900;

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function readPathSegments(): {
  memberId: string;
  mode: TeamMemberWorkflowStudioMode;
  scopeId: string;
  teamId: string;
} {
  const segments =
    typeof window === "undefined"
      ? []
      : window.location.pathname.split("/").filter(Boolean).map(decodeURIComponent);
  const scopeId = trimOptional(segments[1]);
  const teamId = trimOptional(segments[2]);
  const routeMemberId = trimOptional(segments[4]);
  const mode = routeMemberId === "new" ? "new" : "existing";

  return {
    memberId: mode === "existing" ? routeMemberId : "",
    mode,
    scopeId,
    teamId,
  };
}

function normalizeWorkflowDocument(
  value: StudioWorkflowDocument | null | undefined,
): StudioWorkflowDocument | null {
  return value && typeof value === "object" ? value : null;
}

function cloneWorkflowDocument(
  value: StudioWorkflowDocument | null | undefined,
): StudioWorkflowDocument | null {
  const document = normalizeWorkflowDocument(value);
  if (!document) {
    return null;
  }

  return JSON.parse(JSON.stringify(document)) as StudioWorkflowDocument;
}

function buildBlankWorkflowDocument(name: string): StudioWorkflowDocument {
  return {
    name,
    roles: [],
    steps: [],
  };
}

function buildNewWorkflowDraftFileName(title: string): string {
  const normalized = trimOptional(title) || "workflow";
  return `${normalized}.yaml`;
}

function resolveNewWorkflowDirectoryId(input: {
  readonly directories?: readonly { directoryId?: string | null }[] | null;
  readonly scopeId: string;
}): string {
  return (
    trimOptional(input.directories?.[0]?.directoryId) ||
    (trimOptional(input.scopeId) ? `scope:${trimOptional(input.scopeId)}` : "")
  );
}

function resolveWorkflowId(memberDetail: StudioMemberDetail | null | undefined): string {
  const implementationRef = memberDetail?.implementationRef;
  if (
    trimOptional(implementationRef?.implementationKind).toLowerCase() !== "workflow"
  ) {
    return "";
  }

  return trimOptional(implementationRef?.workflowId);
}

function readStepIdFromGraphNodeId(nodeId: string): string {
  const normalized = trimOptional(nodeId);
  return normalized.startsWith("step:")
    ? normalized.slice("step:".length).trim()
    : normalized;
}

function waitForBindingRunPollTick(): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, MEMBER_BINDING_RUN_POLL_DELAY_MS);
  });
}

function isTerminalBindingRun(
  run: StudioMemberBindingRunStatusResponse | null,
): boolean {
  return run?.status === "succeeded" || run?.status === "failed" || run?.status === "rejected";
}

function readBindingRunFailureMessage(
  run: StudioMemberBindingRunStatusResponse | null,
): string {
  if (!run) {
    return "Publish binding status is still pending.";
  }

  if (run.failure?.message) {
    return run.failure.message;
  }

  if (run.status === "rejected") {
    return "Publish binding request was rejected by the member authority.";
  }

  if (run.status === "failed") {
    return "Publish failed while binding the member workflow.";
  }

  return "";
}

function resolveWorkflowExecutionStatus(
  detail: StudioExecutionDetail | null,
): WorkflowExecutionStatus {
  if (!detail) {
    return "idle";
  }

  const status = trimOptional(detail.status).toLowerCase();
  if (
    detail.error ||
    status.includes("fail") ||
    status.includes("error") ||
    status.includes("cancel") ||
    status.includes("stop")
  ) {
    return "failed";
  }

  if (
    detail.completedAtUtc ||
    status.includes("success") ||
    status.includes("succeed") ||
    status.includes("complete")
  ) {
    return "succeeded";
  }

  return "running";
}

function readRuntimeEventString(
  event: RuntimeEvent,
  ...keys: string[]
): string {
  const record = event as unknown as Record<string, unknown>;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) {
      return value;
    }
  }

  return "";
}

function readRuntimeEventTimestamp(event: RuntimeEvent): number {
  const value = (event as unknown as { timestamp?: unknown }).timestamp;
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string") {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return Date.now();
}

function formatRuntimeEventTimestamp(event: RuntimeEvent): string {
  const date = new Date(readRuntimeEventTimestamp(event));
  return Number.isFinite(date.getTime())
    ? date.toISOString()
    : new Date().toISOString();
}

function serializeRuntimeEventFrame(event: RuntimeEvent): string {
  const record = event as unknown as Record<string, unknown>;
  const timestamp = readRuntimeEventTimestamp(event);

  switch (event.type) {
    case AGUIEventType.CUSTOM:
      return JSON.stringify({
        timestamp,
        custom: {
          name: readRuntimeEventString(event, "name"),
          payload: record.payload ?? record.value,
        },
      });
    case AGUIEventType.RUN_STARTED:
      return JSON.stringify({
        timestamp,
        runStarted: {
          actorId: readRuntimeEventString(event, "actorId", "threadId"),
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          runId: readRuntimeEventString(event, "runId"),
          threadId: readRuntimeEventString(event, "threadId", "actorId"),
        },
      });
    case AGUIEventType.RUN_FINISHED:
      return JSON.stringify({
        timestamp,
        runFinished: {
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          result: record.result,
          runId: readRuntimeEventString(event, "runId"),
          threadId: readRuntimeEventString(event, "threadId", "actorId"),
        },
      });
    case AGUIEventType.RUN_ERROR:
      return JSON.stringify({
        timestamp,
        runError: {
          code: readRuntimeEventString(event, "code", "errorCode", "error_code"),
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          message: readRuntimeEventString(event, "message"),
          runId: readRuntimeEventString(event, "runId"),
        },
      });
    case AGUIEventType.STEP_STARTED:
      return JSON.stringify({
        timestamp,
        stepStarted: {
          stepName: readRuntimeEventString(event, "stepName"),
        },
      });
    case AGUIEventType.STEP_FINISHED:
      return JSON.stringify({
        timestamp,
        stepFinished: {
          stepName: readRuntimeEventString(event, "stepName"),
        },
      });
    default:
      return JSON.stringify({
        ...record,
        timestamp,
      });
  }
}

function createWorkflowInvokeExecutionDetail(input: {
  readonly accumulator: RuntimeEventAccumulator;
  readonly completedAtUtc?: string | null;
  readonly error?: string | null;
  readonly executionId: string;
  readonly frames: readonly StudioExecutionFrame[];
  readonly runMessage: string;
  readonly serviceId: string;
  readonly startedAtUtc: string;
  readonly status: string;
  readonly workflowName: string;
}): StudioExecutionDetail {
  const output =
    input.error ||
    input.accumulator.finalOutput ||
    input.accumulator.assistantText ||
    "";

  return {
    auditSource: "invoke-session",
    actorId: input.accumulator.actorId || null,
    completedAtUtc: input.completedAtUtc ?? null,
    error: input.error ?? null,
    executionId: input.accumulator.runId || input.executionId,
    frames: [...input.frames],
    output,
    prompt: input.runMessage,
    serviceId: input.serviceId || null,
    startedAtUtc: input.startedAtUtc,
    status: input.status,
    workflowName: input.workflowName,
  };
}

function readExecutionWorkflowId(execution: StudioExecutionSummary): string {
  return trimOptional(
    (execution as StudioExecutionSummary & { workflowId?: string | null })
      .workflowId,
  );
}

function confirmDiscardUnsavedChanges(): boolean {
  if (typeof window === "undefined") {
    return true;
  }

  return window.confirm(
    "You have unsaved workflow changes. Leave this editor and discard them?",
  );
}

async function saveWorkflowDraft(input: {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly routeScopeId: string;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
}): Promise<SavedWorkflowDraft> {
  const { document, layout, routeScopeId, title, workflow } = input;
  const normalizedTitle =
    trimOptional(title) || trimOptional(document.name) || workflow.name || "draft";
  const documentWithTitle: StudioWorkflowDocument = {
    ...document,
    name: normalizedTitle,
  };
  const serialized = await studioApi.serializeYaml({
    document: documentWithTitle,
    availableStepTypes: AVAILABLE_STEP_TYPES,
  });
  const savedDocument =
    cloneWorkflowDocument(serialized.document) ?? documentWithTitle;
  const graphForLayout = buildStudioGraphElements(savedDocument, layout);
  const nextLayout = buildStudioWorkflowLayout(
    normalizedTitle,
    graphForLayout.nodes,
    layout ?? workflow.layout,
  );
  const savedWorkflow = await studioApi.saveWorkflow({
    workflowId: workflow.workflowId,
    draftExists: workflow.draftExists,
    scopeId: routeScopeId,
    directoryId: workflow.directoryId,
    workflowName: normalizedTitle,
    fileName: workflow.fileName,
    yaml: serialized.yaml,
    layout: nextLayout,
  });

  return {
    document: savedDocument,
    layout: nextLayout,
    title: normalizedTitle,
    workflow: savedWorkflow,
  };
}

export function useTeamMemberWorkflowStudio(): TeamMemberWorkflowStudioState {
  const route = React.useMemo(readPathSegments, []);
  const [dirty, setDirty] = React.useState(false);
  const [editableDocument, setEditableDocument] =
    React.useState<StudioWorkflowDocument | null>(null);
  const [editableLayout, setEditableLayout] = React.useState<unknown>(null);
  const [executionDetail, setExecutionDetail] =
    React.useState<StudioExecutionDetail | null>(null);
  const [executionError, setExecutionError] = React.useState("");
  const [executionRunInput, setExecutionRunInput] = React.useState("");
  const [publishBindingRun, setPublishBindingRun] =
    React.useState<StudioMemberBindingRunStatusResponse | null>(null);
  const [publishError, setPublishError] = React.useState("");
  const [teamEntryNotice, setTeamEntryNotice] = React.useState("");
  const [nodeLibraryOpen, setNodeLibraryOpen] = React.useState(false);
  const [runOptionsOpen, setRunOptionsOpen] = React.useState(false);
  const [selectedNodeId, setSelectedNodeId] = React.useState("");
  const [selectedTab, setSelectedTab] =
    React.useState<"editor" | "runs">("editor");
  const [selectedStepParameterError, setSelectedStepParameterError] =
    React.useState("");
  const [workflowTitle, setWorkflowTitleState] =
    React.useState("Untitled member");
  const sourceKeyRef = React.useRef("");
  const backHref = buildTeamDetailHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    tab: "members",
    teamId: route.teamId,
  });
  const teamQuery = useQuery({
    enabled: Boolean(route.scopeId && route.teamId),
    queryKey: ["team-member-workflow-studio", "team", route.scopeId, route.teamId],
    queryFn: () => studioApi.getTeam(route.scopeId, route.teamId),
  });
  const workspaceSettingsQuery = useQuery({
    enabled: Boolean(route.scopeId),
    queryKey: [
      "team-member-workflow-studio",
      "workspace-settings",
      route.scopeId,
    ],
    queryFn: () => studioApi.getWorkspaceSettings(route.scopeId),
  });
  const memberQuery = useQuery({
    enabled: route.mode === "existing" && Boolean(route.scopeId && route.memberId),
    queryKey: [
      "team-member-workflow-studio",
      "member",
      route.scopeId,
      route.memberId,
    ],
    queryFn: () => studioApi.getMember(route.scopeId, route.memberId),
  });
  const stableWorkflowId =
    route.mode === "existing" ? resolveWorkflowId(memberQuery.data) : "";
  const memberPublishedServiceId = trimOptional(
    memberQuery.data?.summary.publishedServiceId,
  );
  const workflowQuery = useQuery({
    enabled: Boolean(route.scopeId && stableWorkflowId),
    queryKey: [
      "team-member-workflow-studio",
      "workflow",
      route.scopeId,
      stableWorkflowId,
    ],
    queryFn: () => studioApi.getWorkflow(stableWorkflowId, route.scopeId),
  });
  const executionsQuery = useQuery({
    enabled: Boolean(
      selectedTab === "runs" &&
        route.scopeId &&
        (stableWorkflowId || memberPublishedServiceId),
    ),
    queryKey: [
      "team-member-workflow-studio",
      "executions",
      route.scopeId,
      stableWorkflowId,
      memberPublishedServiceId,
      route.memberId,
    ],
    queryFn: () => studioApi.listExecutions(),
  });
  const parseQuery = useQuery({
    enabled: Boolean(
      workflowQuery.data &&
        !workflowQuery.data.document &&
        trimOptional(workflowQuery.data.yaml),
    ),
    queryKey: [
      "team-member-workflow-studio",
      "parse-yaml",
      route.scopeId,
      stableWorkflowId,
      workflowQuery.data?.yaml,
    ],
    queryFn: () =>
      studioApi.parseYaml({
        yaml: workflowQuery.data?.yaml ?? "",
      }),
  });
  const loadedDocument =
    normalizeWorkflowDocument(workflowQuery.data?.document) ??
    normalizeWorkflowDocument(parseQuery.data?.document);
  const routeFallbackTitle =
    route.mode === "new"
      ? "Untitled member"
      : trimOptional(memberQuery.data?.summary.displayName) ||
        route.memberId ||
        "Workflow member";
  const teamName =
    trimOptional(teamQuery.data?.displayName) || route.teamId || "Current team";
  const linkedWorkflowMissing =
    route.mode === "existing" &&
    !memberQuery.isLoading &&
    !stableWorkflowId &&
    Boolean(memberQuery.data);
  const sourceDocument =
    route.mode === "new"
      ? buildBlankWorkflowDocument("Untitled member")
      : loadedDocument ??
        (linkedWorkflowMissing
          ? buildBlankWorkflowDocument(routeFallbackTitle)
          : null);
  const sourceKey =
    route.mode === "new"
      ? `new:${route.scopeId}:${route.teamId}`
      : stableWorkflowId && workflowQuery.data
        ? [
            "workflow",
            stableWorkflowId,
            workflowQuery.data.updatedAtUtc,
            workflowQuery.data.yaml,
            parseQuery.data ? "parsed" : "document",
          ].join(":")
        : linkedWorkflowMissing
          ? `missing:${route.scopeId}:${route.memberId}`
          : "";

  React.useEffect(() => {
    if (!sourceDocument || !sourceKey || sourceKeyRef.current === sourceKey) {
      return;
    }

    const nextDocument =
      cloneWorkflowDocument(sourceDocument) ??
      buildBlankWorkflowDocument(routeFallbackTitle);
    const nextTitle =
      route.mode === "existing"
        ? routeFallbackTitle
        : trimOptional(nextDocument.name) || routeFallbackTitle;
    sourceKeyRef.current = sourceKey;
    setEditableDocument({
      ...nextDocument,
      name: nextTitle,
    });
    setEditableLayout(workflowQuery.data?.layout ?? null);
    setWorkflowTitleState(nextTitle);
    setSelectedNodeId("");
    setRunOptionsOpen(false);
    setDirty(false);
  }, [routeFallbackTitle, sourceDocument, sourceKey, workflowQuery.data?.layout]);

  const graph = React.useMemo(
    () => buildStudioGraphElements(editableDocument, editableLayout),
    [editableDocument, editableLayout],
  );
  const selectedStepDraft = React.useMemo(() => {
    const selectedStepId = readStepIdFromGraphNodeId(selectedNodeId);
    const selectedStep = graph.steps.find((step) => step.id === selectedStepId);
    return selectedStep ? createStepInspectorDraft(selectedStep) : null;
  }, [graph.steps, selectedNodeId]);
  const applySavedDraft = React.useCallback((saved: SavedWorkflowDraft) => {
    setEditableDocument(cloneWorkflowDocument(saved.document));
    setEditableLayout(saved.layout);
    setWorkflowTitleState(saved.title);
    setDirty(false);
  }, []);

  const saveMutation = useMutation({
    mutationFn: (variables: SaveWorkflowDraftVariables) =>
      saveWorkflowDraft({
        ...variables,
        routeScopeId: route.scopeId,
      }),
    onError: (error) => {
      void message.error(
        error instanceof Error ? error.message : "Failed to save workflow draft.",
      );
    },
    onSuccess: (saved) => {
      applySavedDraft(saved);
      void workflowQuery.refetch();
      void message.success("Workflow draft saved.");
    },
  });
  const createWorkflowMemberMutation = useMutation({
    mutationFn: async ({
      document,
      layout,
      title,
    }: Omit<SaveWorkflowDraftVariables, "workflow">): Promise<CreatedWorkflowMember> => {
      if (!route.scopeId || !route.teamId) {
        throw new Error("Resolve a Team workspace before creating a workflow member.");
      }

      const normalizedTitle =
        trimOptional(title) || trimOptional(document.name) || "Untitled member";
      const directoryId = resolveNewWorkflowDirectoryId({
        directories: workspaceSettingsQuery.data?.directories,
        scopeId: route.scopeId,
      });
      if (!directoryId) {
        throw new Error("Resolve a workflow directory before saving this member.");
      }

      const newWorkflowShell: StudioWorkflowFile = {
        directoryId,
        directoryLabel: "",
        draftExists: false,
        fileName: buildNewWorkflowDraftFileName(normalizedTitle),
        filePath: "",
        findings: [],
        layout: null,
        name: normalizedTitle,
        updatedAtUtc: "",
        workflowId: "",
        yaml: "",
      };
      const savedDraft = await saveWorkflowDraft({
        document,
        layout,
        routeScopeId: route.scopeId,
        title: normalizedTitle,
        workflow: newWorkflowShell,
      });
      const memberId = trimOptional(savedDraft.workflow.workflowId);
      if (!memberId) {
        throw new Error("Workflow draft save did not return a stable member id.");
      }

      const createdMember = await studioApi.createMember({
        scopeId: route.scopeId,
        displayName: savedDraft.title,
        implementationKind: "workflow",
        description: trimOptional(savedDraft.document.description),
        memberId,
        teamId: route.teamId,
      });
      const createdMemberId = trimOptional(createdMember.memberId) || memberId;
      const serialized = await studioApi.serializeYaml({
        document: {
          ...savedDraft.document,
          name: savedDraft.title,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      await studioApi.bindMemberWorkflow({
        scopeId: route.scopeId,
        memberId: createdMemberId,
        displayName: savedDraft.title,
        workflowYamls: [serialized.yaml],
      });

      return {
        memberId: createdMemberId,
        savedDraft,
      };
    },
    onError: (error) => {
      void message.error(
        error instanceof Error
          ? error.message
          : "Failed to create workflow member.",
      );
    },
    onSuccess: ({ memberId, savedDraft }) => {
      applySavedDraft(savedDraft);
      void teamQuery.refetch();
      void message.success("Workflow member created.");
      history.replace(
        buildTeamMemberWorkflowStudioHref({
          memberId,
          mode: "edit-member",
          scopeId: route.scopeId,
          teamId: route.teamId,
        }),
      );
    },
  });
  const activeMemberRunMutation = useMutation({
    mutationFn: async ({
      memberId,
      publishedServiceId,
      runInput,
      title,
      workflowId,
    }: RunActiveMemberVariables): Promise<StudioExecutionDetail> => {
      if (!route.scopeId || !memberId) {
        throw new Error("Resolve an active workflow member before running it.");
      }

      const normalizedTitle = trimOptional(title) || "Member run";
      const runMessage = trimOptional(runInput) || `Run ${normalizedTitle}`;
      const startedAtUtc = new Date().toISOString();
      const executionId = `invoke:${memberId}:${Date.now().toString(36)}`;
      const frames: StudioExecutionFrame[] = [];
      const accumulator = createRuntimeEventAccumulator();
      const controller = new AbortController();
      const buildDetail = (
        status: string,
        completedAtUtc: string | null = null,
        error: string | null = null,
      ) =>
        createWorkflowInvokeExecutionDetail({
          accumulator,
          completedAtUtc,
          error,
          executionId,
          frames,
          runMessage,
          serviceId: publishedServiceId,
          startedAtUtc,
          status,
          workflowName: normalizedTitle,
        });

      setExecutionDetail(buildDetail("running"));

      try {
        const response = await runtimeRunsApi.streamChat(
          route.scopeId,
          {
            metadata: workflowId
              ? {
                  workflowId,
                }
              : undefined,
            prompt: runMessage,
          },
          controller.signal,
          {
            memberId,
          },
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          frames.push({
            payload: serializeRuntimeEventFrame(event),
            receivedAtUtc: formatRuntimeEventTimestamp(event),
          });
          setExecutionDetail(
            buildDetail(accumulator.errorText ? "failed" : "running"),
          );
        }

        const completedAtUtc = new Date().toISOString();
        return buildDetail(
          accumulator.errorText ? "failed" : "succeeded",
          completedAtUtc,
          accumulator.errorText || null,
        );
      } catch (error) {
        const errorMessage =
          error instanceof Error ? error.message : "Workflow invoke failed.";
        const completedAtUtc = new Date().toISOString();
        return buildDetail("failed", completedAtUtc, errorMessage);
      }
    },
    onError: (error) => {
      setExecutionError(
        error instanceof Error
          ? error.message
          : "Failed to run active workflow member.",
      );
    },
    onMutate: () => {
      setExecutionError("");
    },
    onSuccess: (detail) => {
      setExecutionDetail(detail);
      setExecutionError("");
      if (detail.error) {
        void message.error("Active member run failed.");
      } else {
        void message.success("Active member run completed.");
      }
    },
  });
  const publishMutation = useMutation({
    mutationFn: async ({
      document,
      layout,
      title,
      workflow,
    }: PublishWorkflowVariables): Promise<PublishedWorkflow> => {
      if (!route.scopeId || !route.memberId) {
        throw new Error("Resolve an existing workflow member before publishing.");
      }

      let savedDraft: SavedWorkflowDraft | null = null;
      let documentForPublish = document;
      let titleForPublish = trimOptional(title) || trimOptional(document.name);
      if (dirty) {
        savedDraft = await saveWorkflowDraft({
          document,
          layout,
          routeScopeId: route.scopeId,
          title,
          workflow,
        });
        documentForPublish = savedDraft.document;
        titleForPublish = savedDraft.title;
      }

      const serialized = await studioApi.serializeYaml({
        document: {
          ...documentForPublish,
          name: titleForPublish,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      const receipt = await studioApi.bindMemberWorkflow({
        scopeId: route.scopeId,
        memberId: route.memberId,
        displayName: titleForPublish,
        workflowYamls: [serialized.yaml],
      });

      let lastRun: StudioMemberBindingRunStatusResponse | null = null;
      for (let attempt = 0; attempt < MEMBER_BINDING_RUN_POLL_ATTEMPTS; attempt += 1) {
        try {
          lastRun = await studioApi.getMemberBindingRun(
            receipt.scopeId,
            receipt.memberId,
            receipt.bindingRunId,
          );
          setPublishBindingRun(lastRun);
          if (isTerminalBindingRun(lastRun)) {
            break;
          }
        } catch (error) {
          if (!isStudioApiStatus(error, 404)) {
            throw error;
          }
        }
        await waitForBindingRunPollTick();
      }

      if (lastRun?.status === "failed" || lastRun?.status === "rejected") {
        throw new Error(readBindingRunFailureMessage(lastRun));
      }

      return {
        run: lastRun,
        savedDraft,
      };
    },
    onError: (error) => {
      setPublishError(
        error instanceof Error ? error.message : "Failed to publish workflow member.",
      );
    },
    onMutate: () => {
      setPublishError("");
      setPublishBindingRun(null);
    },
    onSuccess: ({ run, savedDraft }) => {
      if (savedDraft) {
        applySavedDraft(savedDraft);
      }
      setPublishBindingRun(run);
      void memberQuery.refetch();
      void workflowQuery.refetch();
      if (run?.status === "succeeded") {
        void message.success("Workflow member published.");
      } else {
        void message.info("Publish was accepted and is still binding.");
      }
    },
  });
  const setTeamEntryMutation = useMutation({
    mutationFn: async () => {
      if (!route.scopeId || !route.teamId || !route.memberId) {
        throw new Error("Resolve an existing Team member before setting Team entry.");
      }

      return studioApi.setTeamEntryMember(
        route.scopeId,
        route.teamId,
        route.memberId,
      );
    },
    onError: (error) => {
      const errorMessage =
        error instanceof Error ? error.message : "Failed to set Team entry member.";
      setTeamEntryNotice(errorMessage);
      void message.error(errorMessage);
    },
    onMutate: () => {
      setTeamEntryNotice("");
    },
    onSuccess: () => {
      void teamQuery.refetch();
      setTeamEntryNotice("Team entry change submitted.");
      void message.success("Team entry change submitted.");
    },
  });

  const workflowLoading =
    route.mode === "existing" &&
    (memberQuery.isLoading ||
      (Boolean(stableWorkflowId) &&
        (workflowQuery.isLoading || parseQuery.isLoading)));
  const canSave =
    route.mode === "new"
      ? Boolean(
          route.scopeId &&
            route.teamId &&
            editableDocument &&
            dirty &&
            !createWorkflowMemberMutation.isPending,
        )
      : Boolean(
          workflowQuery.data &&
            editableDocument &&
            dirty &&
            !linkedWorkflowMissing &&
            !saveMutation.isPending,
        );
  const workflowHasSteps = Boolean(editableDocument?.steps?.length);
  const memberPublishedByQuery =
    memberQuery.data?.summary.lifecycleStage === "bind_ready" ||
    Boolean(trimOptional(memberQuery.data?.summary.publishedServiceId));
  const memberIsPublished =
    memberPublishedByQuery ||
    publishBindingRun?.status === "succeeded";
  const publishBindingStillInProgress = Boolean(
    !memberPublishedByQuery &&
      publishBindingRun &&
      publishBindingRun.status !== "succeeded" &&
      !isTerminalBindingRun(publishBindingRun),
  );
  const publishHasDraftChanges = Boolean(dirty && workflowHasSteps);
  const publishPending = publishMutation.isPending;
  const memberPublished = memberIsPublished;
  const publishDisabled = Boolean(
    route.mode === "new" ||
      linkedWorkflowMissing ||
      !workflowQuery.data ||
      !editableDocument ||
      !workflowHasSteps ||
      publishMutation.isPending ||
      publishBindingStillInProgress ||
      (memberIsPublished && !publishHasDraftChanges),
  );
  const publishPlaceholderReason =
    route.mode === "new"
      ? "Create and link a workflow member before publishing."
      : linkedWorkflowMissing
        ? "No stable workflow draft is linked to this member yet."
        : publishBindingStillInProgress
          ? "Publish was accepted and binding is still in progress. Refresh later before publishing again."
        : memberIsPublished && !publishHasDraftChanges
          ? "This member workflow is already published. Edit the draft before publishing a new version."
        : !workflowQuery.data || !editableDocument
            ? "Load the workflow draft before publishing."
            : !workflowHasSteps
              ? "Add at least one step before publishing."
              : dirty
                ? "Publish saves draft changes, binds the member workflow, and observes readiness."
                : "Publish binds the saved workflow draft to this member and observes readiness.";
  const publishTone: WorkflowPublishTone = publishError
    ? "error"
    : publishPending || publishBindingStillInProgress
      ? "processing"
      : memberIsPublished
        ? "success"
        : "default";
  const publishNotice =
    publishError ||
    (publishPending
      ? `Publish binding status: ${publishBindingRun?.status ?? "accepted"}.`
      : publishBindingStillInProgress
        ? `Publish was accepted and binding is still in progress (${publishBindingRun?.status ?? "accepted"}). Refresh later to check readiness.`
      : memberIsPublished
        ? "Published member workflow is serviceable."
        : "Draft member workflow is not published to the active member yet.");
  const isTeamEntryMember =
    Boolean(route.memberId) &&
    trimOptional(teamQuery.data?.entryMemberId) === route.memberId;
  const canSetTeamEntry = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.teamId &&
      route.memberId &&
      memberIsPublished &&
      !isTeamEntryMember &&
      !setTeamEntryMutation.isPending,
  );
  const resolvedTeamEntryNotice =
    teamEntryNotice ||
    (isTeamEntryMember
      ? "Team entry"
      : memberIsPublished
        ? "Team entry available"
        : "Team entry needs a published member");
  const scopedMemberRuns = React.useMemo(() => {
    const executions = executionsQuery.data ?? [];
    if (!executions.length) {
      return [];
    }

    return executions.filter((execution) => {
      const executionWorkflowId = readExecutionWorkflowId(execution);
      if (stableWorkflowId && executionWorkflowId) {
        return executionWorkflowId === stableWorkflowId;
      }

      const executionServiceId = trimOptional(execution.serviceId);
      if (memberPublishedServiceId && executionServiceId) {
        return executionServiceId === memberPublishedServiceId;
      }

      return false;
    });
  }, [executionsQuery.data, memberPublishedServiceId, stableWorkflowId]);
  const memberRunsEmptyReason =
    route.mode === "new"
      ? "Run history is unavailable until this draft is saved as a linked Team member."
      : !stableWorkflowId && !memberPublishedServiceId
        ? "Run history is available after this member has a saved workflow link or active published version."
        : executionsQuery.isLoading
          ? "Loading runs."
          : scopedMemberRuns.length === 0
            ? "No runs are linked to this workflow member yet."
            : "";
  const memberRunsError =
    executionsQuery.error instanceof Error
      ? executionsQuery.error.message
      : executionsQuery.isError
        ? "Failed to load run history."
        : "";
  const executionStatus = activeMemberRunMutation.isPending
    ? "running"
    : resolveWorkflowExecutionStatus(executionDetail);
  const canRunActiveMember = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      memberPublishedServiceId &&
      !activeMemberRunMutation.isPending,
  );
  const activeMemberRunPlaceholderReason =
    route.mode === "new"
      ? "Create and publish a workflow member before running it."
      : !route.memberId
        ? "Resolve the workflow member before running it."
        : !memberPublishedServiceId
          ? "Publish this workflow member before running it."
          : !route.scopeId
            ? "Resolve the current workspace before running the active member."
            : activeMemberRunMutation.isPending
              ? "Active member run is already starting."
              : linkedWorkflowMissing
                ? "Run the active published member. Editing remains limited until a stable workflow draft is linked."
                : !workflowHasSteps
                  ? "Run the active published member. Add steps before saving editor changes."
                  : "Run the active workflow member.";
  const savePlaceholderReason =
    route.mode === "new"
      ? !editableDocument
        ? "Load the workflow draft before creating this member."
        : !dirty
          ? "No changes to save."
          : "Save creates the workflow draft, Team member, and member binding."
      : linkedWorkflowMissing
        ? "No stable workflow draft is linked to this member yet."
        : dirty
          ? "Load a workflow member before saving."
          : "No changes to save.";
  React.useEffect(() => {
    if (!dirty || typeof window === "undefined") {
      return;
    }

    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", handleBeforeUnload);

    return () => {
      window.removeEventListener("beforeunload", handleBeforeUnload);
    };
  }, [dirty]);
  React.useEffect(() => {
    const executionId = trimOptional(executionDetail?.executionId);
    if (
      !executionId ||
      executionDetail?.auditSource === "invoke-session" ||
      resolveWorkflowExecutionStatus(executionDetail) !== "running"
    ) {
      return;
    }

    let cancelled = false;
    const intervalId = window.setInterval(() => {
      void studioApi
        .getExecution(executionId)
        .then((detail) => {
          if (!cancelled) {
            setExecutionDetail(detail);
            setExecutionError("");
          }
        })
        .catch((error) => {
          if (!cancelled) {
            setExecutionError(
              error instanceof Error
                ? error.message
                : "Failed to refresh member run.",
            );
          }
        });
    }, 2500);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
    };
  }, [executionDetail]);
  const setWorkflowTitle = React.useCallback((title: string) => {
    setWorkflowTitleState(title);
    setEditableDocument((currentDocument) => ({
      ...(currentDocument ?? buildBlankWorkflowDocument(title || "Untitled member")),
      name: title,
    }));
    setDirty(true);
  }, []);
  const insertNode = React.useCallback(
    (stepType: string) => {
      const currentDocument =
        editableDocument ??
        buildBlankWorkflowDocument(trimOptional(workflowTitle) || "Untitled member");
      const afterStepId = selectedNodeId.startsWith("step:")
        ? selectedNodeId.slice("step:".length)
        : null;
      const result = insertStepByType(currentDocument, stepType, {
        afterStepId,
        targetRoleId: graph.roles[0]?.id ?? null,
      });
      const nextTitle =
        trimOptional(workflowTitle) ||
        trimOptional(result.document.name) ||
        routeFallbackTitle;
      const nextDocument: StudioWorkflowDocument = {
        ...result.document,
        name: nextTitle,
      };
      const nextGraph = buildStudioGraphElements(nextDocument, editableLayout);
      const nextLayout = buildStudioWorkflowLayout(
        nextTitle,
        nextGraph.nodes,
        editableLayout,
      );

      setEditableDocument(nextDocument);
      setEditableLayout(nextLayout);
      setSelectedNodeId(result.nodeId);
      setNodeLibraryOpen(false);
      setDirty(true);
    },
    [
      editableDocument,
      editableLayout,
      graph.roles,
      routeFallbackTitle,
      selectedNodeId,
      workflowTitle,
    ],
  );
  const connectNodes = React.useCallback(
    (sourceNodeId: string, targetNodeId: string) => {
      if (!editableDocument) {
        return;
      }

      const sourceStepId = readStepIdFromGraphNodeId(sourceNodeId);
      const targetStepId = readStepIdFromGraphNodeId(targetNodeId);
      if (!sourceStepId || !targetStepId || sourceStepId === targetStepId) {
        return;
      }

      const result = connectStepToTarget(
        editableDocument,
        sourceStepId,
        targetStepId,
      );
      setEditableDocument(result.document);
      setSelectedNodeId(result.nodeId);
      setDirty(true);
    },
    [editableDocument],
  );
  const moveNodes = React.useCallback(
    (nodes: ReturnType<typeof buildStudioGraphElements>["nodes"]) => {
      const nextLayout = buildStudioWorkflowLayout(
        workflowTitle,
        nodes,
        editableLayout,
      );
      setEditableLayout(nextLayout);
      setDirty(true);
    },
    [editableLayout, workflowTitle],
  );
  const deleteSelectedNode = React.useCallback(() => {
    if (!editableDocument || !selectedNodeId) {
      return;
    }

    const selectedStepId = readStepIdFromGraphNodeId(selectedNodeId);
    if (!selectedStepId) {
      return;
    }

    const result = removeStep(editableDocument, selectedStepId);
    setEditableDocument(result.document);
    setSelectedNodeId(result.nodeId);
    setDirty(true);
  }, [editableDocument, selectedNodeId]);
  const updateSelectedStepParameters = React.useCallback(
    (parametersText: string) => {
      if (!editableDocument || !selectedStepDraft) {
        return;
      }

      try {
        const result = applyStepInspectorDraft(
          editableDocument,
          selectedStepDraft.id,
          {
            ...selectedStepDraft,
            parametersText,
          },
        );
        setEditableDocument(result.document);
        setSelectedNodeId(result.nodeId);
        setSelectedStepParameterError("");
        setDirty(true);
      } catch (error) {
        setSelectedStepParameterError(
          error instanceof Error
            ? error.message
            : "Step parameters must be a JSON object.",
        );
      }
    },
    [editableDocument, selectedStepDraft],
  );

  return {
    publishMember: () => {
      if (workflowQuery.data && editableDocument) {
        publishMutation.mutate({
          document: editableDocument,
          layout:
            editableLayout ??
            buildStudioWorkflowLayout(workflowTitle, graph.nodes, workflowQuery.data.layout),
          title: workflowTitle,
          workflow: workflowQuery.data,
        });
      }
    },
    memberPublished,
    publishDisabled,
    publishNotice,
    publishPending,
    publishPlaceholderReason,
    publishTone,
    backHref,
    canRunActiveMember,
    canSave,
    canSetTeamEntry,
    closeNodeLibrary: () => setNodeLibraryOpen(false),
    connectNodes,
    deleteSelectedNode,
    dirty,
    emptyDescription:
      route.mode === "new"
        ? "Build the draft locally first, then save it as a linked Team workflow member."
        : linkedWorkflowMissing
          ? "No workflow draft is linked to this member yet. You can sketch locally, but saving requires a stable workflow reference."
          : "Start this workflow by adding the first step.",
    runActiveMember: () => {
      if (route.memberId && memberPublishedServiceId) {
        activeMemberRunMutation.mutate({
          memberId: route.memberId,
          publishedServiceId: memberPublishedServiceId,
          runInput: trimOptional(executionRunInput),
          title: workflowTitle,
          workflowId: stableWorkflowId || undefined,
        });
      }
    },
    activeMemberRunPending: activeMemberRunMutation.isPending,
    activeMemberRunPlaceholderReason,
    executionDetail,
    executionError,
    executionRunInput,
    executionStatus,
    memberRuns: scopedMemberRuns,
    memberRunsEmptyReason,
    memberRunsError,
    memberRunsLoading: executionsQuery.isLoading,
    graph,
    insertNode,
    linkedWorkflowMissing,
    loading: Boolean(workflowLoading),
    mode: route.mode,
    moveNodes,
    navigateBack: () => {
      if (!dirty || confirmDiscardUnsavedChanges()) {
        history.push(backHref);
      }
    },
    nodeLibraryOpen,
    openExecution: (executionId: string) => {
      const normalizedExecutionId = trimOptional(executionId);
      if (!normalizedExecutionId) {
        return;
      }

      void studioApi
        .getExecution(normalizedExecutionId)
        .then((detail) => {
          setExecutionDetail(detail);
          setExecutionError("");
        })
        .catch((error) => {
          setExecutionError(
            error instanceof Error
              ? error.message
              : "Failed to open member run.",
          );
        });
    },
    openNodeLibrary: () => setNodeLibraryOpen(true),
    openRunOptions: () => {
      setSelectedTab("editor");
      setSelectedNodeId("");
      setRunOptionsOpen(true);
    },
    save: () => {
      if (route.mode === "new" && editableDocument) {
        createWorkflowMemberMutation.mutate({
          document: editableDocument,
          layout:
            editableLayout ??
            buildStudioWorkflowLayout(workflowTitle, graph.nodes),
          title: workflowTitle,
        });
        return;
      }

      if (workflowQuery.data && editableDocument) {
        saveMutation.mutate({
          document: editableDocument,
          layout:
            editableLayout ??
            buildStudioWorkflowLayout(workflowTitle, graph.nodes, workflowQuery.data.layout),
          title: workflowTitle,
          workflow: workflowQuery.data,
        });
      }
    },
    savePending: saveMutation.isPending || createWorkflowMemberMutation.isPending,
    savePlaceholderReason,
    runOptionsOpen,
    selectedNodeId,
    selectedStepDraft,
    selectedStepParameterError,
    selectedTab,
    selectCanvas: () => {
      setSelectedNodeId("");
      setRunOptionsOpen(false);
    },
    selectNode: (nodeId: string) => {
      setSelectedNodeId(nodeId);
      setRunOptionsOpen(false);
    },
    setExecutionRunInput,
    setSelectedTab,
    setTeamEntry: () => {
      if (canSetTeamEntry) {
        setTeamEntryMutation.mutate();
      }
    },
    setWorkflowTitle,
    teamEntryNotice: resolvedTeamEntryNotice,
    teamEntryPending: setTeamEntryMutation.isPending,
    teamName,
    updateSelectedStepParameters,
    workflowTitle,
  };
}
