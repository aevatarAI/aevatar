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
type WorkflowActivationTone = "default" | "processing" | "success" | "warning" | "error";
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

type ExecuteWorkflowDraftVariables = {
  readonly memberId: string;
  readonly runInput: string;
  readonly serviceId: string;
  readonly title: string;
  readonly workflowId?: string;
};

type ActivateWorkflowVariables = {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
};

type ActivatedWorkflow = {
  readonly run: StudioMemberBindingRunStatusResponse | null;
  readonly savedDraft: SavedWorkflowDraft | null;
};

type CreatedWorkflowMember = {
  readonly memberId: string;
  readonly savedDraft: SavedWorkflowDraft;
};

type TeamMemberWorkflowStudioState = {
  readonly activate: () => void;
  readonly activationChecked: boolean;
  readonly activationDisabled: boolean;
  readonly activationNotice: string;
  readonly activationPending: boolean;
  readonly activationPlaceholderReason: string;
  readonly activationTone: WorkflowActivationTone;
  readonly backHref: string;
  readonly canExecute: boolean;
  readonly canSave: boolean;
  readonly canSetTeamEntry: boolean;
  readonly closeNodeLibrary: () => void;
  readonly connectNodes: (sourceNodeId: string, targetNodeId: string) => void;
  readonly deleteSelectedNode: () => void;
  readonly dirty: boolean;
  readonly emptyDescription: string;
  readonly execute: () => void;
  readonly executePending: boolean;
  readonly executePlaceholderReason: string;
  readonly executionDetail: StudioExecutionDetail | null;
  readonly executionError: string;
  readonly executionRunInput: string;
  readonly executionStatus: WorkflowExecutionStatus;
  readonly executions: readonly StudioExecutionSummary[];
  readonly executionsEmptyReason: string;
  readonly executionsError: string;
  readonly executionsLoading: boolean;
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
  readonly save: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason: string;
  readonly selectedNodeId: string;
  readonly selectedStepDraft: StudioStepInspectorDraft | null;
  readonly selectedStepParameterError: string;
  readonly selectedTab: "editor" | "executions";
  readonly setSelectedTab: (tab: "editor" | "executions") => void;
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
    return "Activation status is still pending.";
  }

  if (run.failure?.message) {
    return run.failure.message;
  }

  if (run.status === "rejected") {
    return "Activation request was rejected by the member authority.";
  }

  if (run.status === "failed") {
    return "Activation failed while publishing the member workflow.";
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
  readonly prompt: string;
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
    prompt: input.prompt,
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
  const [activationRun, setActivationRun] =
    React.useState<StudioMemberBindingRunStatusResponse | null>(null);
  const [activationError, setActivationError] = React.useState("");
  const [teamEntryNotice, setTeamEntryNotice] = React.useState("");
  const [nodeLibraryOpen, setNodeLibraryOpen] = React.useState(false);
  const [selectedNodeId, setSelectedNodeId] = React.useState("");
  const [selectedTab, setSelectedTab] =
    React.useState<"editor" | "executions">("editor");
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
    enabled: selectedTab === "executions" && Boolean(route.scopeId),
    queryKey: [
      "team-member-workflow-studio",
      "executions",
      route.scopeId,
      stableWorkflowId,
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
  const executeMutation = useMutation({
    mutationFn: async ({
      memberId,
      runInput,
      serviceId,
      title,
      workflowId,
    }: ExecuteWorkflowDraftVariables): Promise<StudioExecutionDetail> => {
      if (!route.scopeId || !memberId) {
        throw new Error("Resolve an active workflow member before executing.");
      }

      const normalizedTitle = trimOptional(title) || "Workflow run";
      const normalizedPrompt = trimOptional(runInput) || `Execute ${normalizedTitle}`;
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
          prompt: normalizedPrompt,
          serviceId,
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
            prompt: normalizedPrompt,
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
          : "Failed to execute workflow draft.",
      );
    },
    onMutate: () => {
      setExecutionError("");
    },
    onSuccess: (detail) => {
      setExecutionDetail(detail);
      setExecutionError("");
      if (detail.error) {
        void message.error("Workflow execution failed.");
      } else {
        void message.success("Workflow executed successfully.");
      }
    },
  });
  const activateMutation = useMutation({
    mutationFn: async ({
      document,
      layout,
      title,
      workflow,
    }: ActivateWorkflowVariables): Promise<ActivatedWorkflow> => {
      if (!route.scopeId || !route.memberId) {
        throw new Error("Resolve an existing workflow member before activating.");
      }

      let savedDraft: SavedWorkflowDraft | null = null;
      let documentForActivation = document;
      let titleForActivation = trimOptional(title) || trimOptional(document.name);
      if (dirty) {
        savedDraft = await saveWorkflowDraft({
          document,
          layout,
          routeScopeId: route.scopeId,
          title,
          workflow,
        });
        documentForActivation = savedDraft.document;
        titleForActivation = savedDraft.title;
      }

      const serialized = await studioApi.serializeYaml({
        document: {
          ...documentForActivation,
          name: titleForActivation,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      const receipt = await studioApi.bindMemberWorkflow({
        scopeId: route.scopeId,
        memberId: route.memberId,
        displayName: titleForActivation,
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
          setActivationRun(lastRun);
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
      setActivationError(
        error instanceof Error ? error.message : "Failed to activate workflow.",
      );
    },
    onMutate: () => {
      setActivationError("");
      setActivationRun(null);
    },
    onSuccess: ({ run, savedDraft }) => {
      if (savedDraft) {
        applySavedDraft(savedDraft);
      }
      setActivationRun(run);
      void memberQuery.refetch();
      void workflowQuery.refetch();
      if (run?.status === "succeeded") {
        void message.success("Workflow member activated.");
      } else {
        void message.info("Activation was accepted and is still publishing.");
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
  const memberIsActive =
    memberQuery.data?.summary.lifecycleStage === "bind_ready" ||
    Boolean(trimOptional(memberQuery.data?.summary.publishedServiceId)) ||
    activationRun?.status === "succeeded";
  const activationPending =
    activateMutation.isPending ||
    Boolean(
      activationRun &&
        activationRun.status !== "succeeded" &&
        !isTerminalBindingRun(activationRun),
    );
  const activationChecked = memberIsActive;
  const activationDisabled = Boolean(
    route.mode === "new" ||
      linkedWorkflowMissing ||
      !workflowQuery.data ||
      !editableDocument ||
      !workflowHasSteps ||
      activateMutation.isPending ||
      memberIsActive,
  );
  const activationPlaceholderReason =
    route.mode === "new"
      ? "Create and link a workflow member before activating."
      : linkedWorkflowMissing
        ? "No stable workflow draft is linked to this member yet."
        : memberIsActive
          ? "This member is already active. Deactivation is not available in Phase 7 because no safe backend API was found."
          : !workflowQuery.data || !editableDocument
            ? "Load the workflow draft before activating."
            : !workflowHasSteps
              ? "Add at least one step before activating."
              : "Activate saves the draft, binds the member workflow, and observes binding readiness.";
  const activationTone: WorkflowActivationTone = activationError
    ? "error"
    : activationPending
      ? "processing"
      : memberIsActive
        ? "success"
        : "default";
  const activationNotice =
    activationError ||
    (activationPending
      ? `Activation publishing status: ${activationRun?.status ?? "accepted"}.`
      : memberIsActive
        ? "Active member: workflow is published and serviceable."
        : "Inactive member: activation is separate from Team entry.");
  const isTeamEntryMember =
    Boolean(route.memberId) &&
    trimOptional(teamQuery.data?.entryMemberId) === route.memberId;
  const canSetTeamEntry = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.teamId &&
      route.memberId &&
      memberIsActive &&
      !isTeamEntryMember &&
      !setTeamEntryMutation.isPending,
  );
  const resolvedTeamEntryNotice =
    teamEntryNotice ||
    (isTeamEntryMember
      ? "Team entry"
      : memberIsActive
        ? "Team entry available"
        : "Team entry needs Active");
  const memberPublishedServiceId = trimOptional(
    memberQuery.data?.summary.publishedServiceId,
  );
  const scopedExecutions = React.useMemo(() => {
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
  const executionsEmptyReason =
    route.mode === "new"
      ? "Execution history is unavailable until this draft is saved as a linked Team member."
      : !stableWorkflowId && !memberPublishedServiceId
        ? "Execution history requires a stable workflow id or published service id. No safely scoped history can be shown yet."
        : executionsQuery.isLoading
          ? "Loading executions."
          : scopedExecutions.length === 0
            ? "No safely scoped executions were found for this workflow member."
            : "";
  const executionsError =
    executionsQuery.error instanceof Error
      ? executionsQuery.error.message
      : executionsQuery.isError
        ? "Failed to load execution history."
        : "";
  const executionStatus = executeMutation.isPending
    ? "running"
    : resolveWorkflowExecutionStatus(executionDetail);
  const canExecute = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      memberPublishedServiceId &&
      !executeMutation.isPending,
  );
  const executePlaceholderReason =
    route.mode === "new"
        ? "Create and activate a workflow member before executing."
        : !route.memberId
          ? "Resolve the workflow member before executing."
          : !memberPublishedServiceId
            ? "Activate this workflow member before executing it."
            : !route.scopeId
          ? "Resolve the current workspace before running the workflow."
          : executeMutation.isPending
            ? "Workflow execution is already starting."
            : linkedWorkflowMissing
              ? "Execute the active published member. Editing remains limited until a stable workflow draft is linked."
              : !workflowHasSteps
                ? "Execute the active published member. Add steps before saving editor changes."
                : "Execute the active workflow member.";
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
                : "Failed to refresh workflow execution.",
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
    activate: () => {
      if (workflowQuery.data && editableDocument) {
        activateMutation.mutate({
          document: editableDocument,
          layout:
            editableLayout ??
            buildStudioWorkflowLayout(workflowTitle, graph.nodes, workflowQuery.data.layout),
          title: workflowTitle,
          workflow: workflowQuery.data,
        });
      }
    },
    activationChecked,
    activationDisabled,
    activationNotice,
    activationPending,
    activationPlaceholderReason,
    activationTone,
    backHref,
    canExecute,
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
    execute: () => {
      if (route.memberId && memberPublishedServiceId) {
        executeMutation.mutate({
          memberId: route.memberId,
          runInput: trimOptional(executionRunInput),
          serviceId: memberPublishedServiceId,
          title: workflowTitle,
          workflowId: stableWorkflowId || undefined,
        });
      }
    },
    executePending: executeMutation.isPending,
    executePlaceholderReason,
    executionDetail,
    executionError,
    executionRunInput,
    executionStatus,
    executions: scopedExecutions,
    executionsEmptyReason,
    executionsError,
    executionsLoading: executionsQuery.isLoading,
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
              : "Failed to open workflow execution.",
          );
        });
    },
    openNodeLibrary: () => setNodeLibraryOpen(true),
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
    selectedNodeId,
    selectedStepDraft,
    selectedStepParameterError,
    selectedTab,
    selectCanvas: () => setSelectedNodeId(""),
    selectNode: (nodeId: string) => setSelectedNodeId(nodeId),
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
