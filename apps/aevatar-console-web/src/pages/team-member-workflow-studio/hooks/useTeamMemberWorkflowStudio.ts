import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
  buildTeamsHref,
} from "@/shared/navigation/teamRoutes";
import {
  applyStepInspectorDraft,
  connectStepToTarget,
  createStepInspectorDraft,
  insertStepByType,
  removeStepConnection,
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
  readonly document: StudioWorkflowDocument;
  readonly memberId: string;
  readonly runMessage: string;
  readonly title: string;
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
  readonly workflowId: string;
};

type CreatedUnlinkedMemberDraft = {
  readonly savedDraft: SavedWorkflowDraft;
  readonly workflowId: string;
};

function getTeamMemberWorkflowStudioTeamQueryKey(
  scopeId: string,
  teamId: string,
) {
  return ["team-member-workflow-studio", "team", scopeId, teamId] as const;
}

function getTeamMemberWorkflowStudioWorkflowQueryKey(
  scopeId: string,
  workflowId: string,
) {
  return [
    "team-member-workflow-studio",
    "workflow",
    scopeId,
    workflowId,
  ] as const;
}

type WorkflowSourceSignature = {
  readonly updatedAtUtc: string;
  readonly workflowId: string;
  readonly yaml: string;
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
  readonly navigateToTeam: () => void;
  readonly navigateToTeams: () => void;
  readonly closeYamlImportPanel: () => void;
  readonly pasteYaml: (yaml: string) => Promise<void>;
  readonly yamlImportError: string;
  readonly yamlImportPanelOpen: boolean;
  readonly pasteYamlPending: boolean;
  readonly teamHref: string;
  readonly teamsHref: string;
  readonly canOpenDraftRunPanel: boolean;
  readonly canRunActiveMember: boolean;
  readonly canSave: boolean;
  readonly canViewYaml: boolean;
  readonly closeNodeLibrary: () => void;
  readonly closeYamlPanel: () => void;
  readonly connectNodes: (sourceNodeId: string, targetNodeId: string) => void;
  readonly currentYaml: string;
  readonly currentYamlError: string;
  readonly currentYamlPending: boolean;
  readonly deleteSelectedConnection: () => void;
  readonly deleteSelectedNode: () => void;
  readonly dirty: boolean;
  readonly emptyDescription: string;
  readonly runActiveMember: () => void;
  readonly activeMemberRunPending: boolean;
  readonly activeMemberRunPlaceholderReason: string;
  readonly executionDetail: StudioExecutionDetail | null;
  readonly executionError: string;
  readonly executionRunMessage: string;
  readonly executionStatus: WorkflowExecutionStatus;
  readonly graph: ReturnType<typeof buildStudioGraphElements>;
  readonly insertNode: (stepType: string) => void;
  readonly linkedWorkflowMissing: boolean;
  readonly loading: boolean;
  readonly moveNodes: (nodes: ReturnType<typeof buildStudioGraphElements>["nodes"]) => void;
  readonly mode: TeamMemberWorkflowStudioMode;
  readonly navigateBack: () => void;
  readonly nodeLibraryOpen: boolean;
  readonly openNodeLibrary: () => void;
  readonly openDraftRunPanel: () => void;
  readonly openYamlImportPanel: () => void;
  readonly openYamlPanel: () => void;
  readonly retryYaml: () => void;
  readonly save: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason: string;
  readonly draftRunPanelOpen: boolean;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
  readonly selectedStepDraft: StudioStepInspectorDraft | null;
  readonly selectedStepConfigurationError: string;
  readonly setSelectedStepConfigurationError: (error: string) => void;
  readonly updateSelectedStepConfiguration: (parametersText: string) => void;
  readonly selectCanvas: () => void;
  readonly selectEdge: (edgeId: string) => void;
  readonly selectNode: (nodeId: string) => void;
  readonly setExecutionRunMessage: (message: string) => void;
  readonly setWorkflowTitle: (title: string) => void;
  readonly teamName: string;
  readonly workflowTitle: string;
  readonly yamlPanelOpen: boolean;
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
  canonicalHref: string;
  memberId: string;
  mode: TeamMemberWorkflowStudioMode;
  scopeId: string;
  teamId: string;
  workflowId: string;
} {
  const segments =
    typeof window === "undefined"
      ? []
      : window.location.pathname.split("/").filter(Boolean).map(decodeURIComponent);
  const params =
    typeof window === "undefined"
      ? new URLSearchParams()
      : new URLSearchParams(window.location.search);
  const pathname =
    typeof window === "undefined" ? "" : window.location.pathname;
  const currentHref =
    typeof window === "undefined"
      ? pathname
      : `${window.location.pathname}${window.location.search}`;
  const hasScopedTeamPath =
    segments[0] === "scopes" && segments[2] === "teams";
  const scopedTeamsIndex = hasScopedTeamPath ? 2 : -1;
  const membersIndex =
    scopedTeamsIndex >= 0
      ? segments.indexOf("members", scopedTeamsIndex + 2)
      : -1;
  const scopeId =
    hasScopedTeamPath
      ? trimOptional(segments[1])
      : "";
  const teamId =
    scopedTeamsIndex >= 0
      ? trimOptional(segments[scopedTeamsIndex + 1])
      : "";
  const routeMemberId =
    membersIndex >= 0 ? trimOptional(segments[membersIndex + 1]) : "";
  const routeSurface =
    membersIndex >= 0 ? trimOptional(segments[membersIndex + 2]) : "";
  const isWorkflowEditorRoute = routeSurface === "workflow";
  const mode = routeMemberId === "new" ? "new" : "existing";
  const workflowId = trimOptional(params.get("workflowId"));
  const canonicalHref =
    isWorkflowEditorRoute && scopeId && teamId
      ? buildTeamMemberWorkflowStudioHref({
          memberId: mode === "existing" ? routeMemberId : undefined,
          mode: mode === "new" ? "create-member" : "edit-member",
          scopeId,
          teamId,
          workflowId,
        })
      : currentHref;

  return {
    canonicalHref,
    memberId: mode === "existing" ? routeMemberId : "",
    mode,
    scopeId,
    teamId,
    workflowId,
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

function isWorkflowDraftRouteId(value: string): boolean {
  return Boolean(value && !/\s/.test(value));
}

function readWorkflowSourceSignature(
  workflow: StudioWorkflowFile | null | undefined,
): WorkflowSourceSignature | null {
  if (!workflow) {
    return null;
  }

  return {
    updatedAtUtc: trimOptional(workflow.updatedAtUtc),
    workflowId: trimOptional(workflow.workflowId),
    yaml: workflow.yaml ?? "",
  };
}

function workflowSourceSignaturesMatch(
  left: WorkflowSourceSignature,
  right: WorkflowSourceSignature,
): boolean {
  return (
    left.workflowId === right.workflowId &&
    left.updatedAtUtc === right.updatedAtUtc &&
    left.yaml === right.yaml
  );
}

function buildSavedWorkflowCacheValue(
  saved: SavedWorkflowDraft,
): StudioWorkflowFile {
  return {
    ...saved.workflow,
    document: cloneWorkflowDocument(saved.document),
    draftExists: true,
    findings: saved.workflow.findings ?? [],
    layout: saved.layout,
    name: saved.title,
  };
}

function readStepIdFromGraphNodeId(nodeId: string): string {
  const normalized = trimOptional(nodeId);
  return normalized.startsWith("step:")
    ? normalized.slice("step:".length).trim()
    : normalized;
}

function readConnectionFromGraphEdgeId(edgeId: string): {
  readonly branchLabel: string | null;
  readonly sourceStepId: string;
  readonly targetStepId: string;
} | null {
  const normalized = trimOptional(edgeId);
  if (!normalized.startsWith("edge:")) {
    return null;
  }

  const [, sourceStepId, targetStepId, edgeKind, ...labelParts] =
    normalized.split(":");
  if (!sourceStepId || !targetStepId) {
    return null;
  }

  if (edgeKind === "linear") {
    return {
      branchLabel: null,
      sourceStepId,
      targetStepId,
    };
  }

  if (edgeKind === "branch") {
    const branchLabel = labelParts.join(":");
    if (!branchLabel) {
      return null;
    }

    return {
      branchLabel,
      sourceStepId,
      targetStepId,
    };
  }

  const legacyEdgeLabel = [edgeKind, ...labelParts]
    .filter(Boolean)
    .join(":");
  return {
    branchLabel:
      legacyEdgeLabel && legacyEdgeLabel !== "next" ? legacyEdgeLabel : null,
    sourceStepId,
    targetStepId,
  };
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
  readonly auditSource?: StudioExecutionDetail["auditSource"];
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
    auditSource: input.auditSource ?? "invoke-session",
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
  const queryClient = useQueryClient();
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    getLocationSnapshot,
  );
  const route = React.useMemo(readPathSegments, [locationSnapshot]);
  React.useEffect(() => {
    const currentHref = `${window.location.pathname}${window.location.search}`;
    if (route.canonicalHref && currentHref !== route.canonicalHref) {
      history.replace(route.canonicalHref);
    }
  }, [route.canonicalHref]);
  const [dirty, setDirty] = React.useState(false);
  const [editableDocument, setEditableDocument] =
    React.useState<StudioWorkflowDocument | null>(null);
  const [editableLayout, setEditableLayout] = React.useState<unknown>(null);
  const [executionDetail, setExecutionDetail] =
    React.useState<StudioExecutionDetail | null>(null);
  const [executionError, setExecutionError] = React.useState("");
  const [executionRunMessage, setExecutionRunMessage] = React.useState("");
  const [publishBindingRun, setPublishBindingRun] =
    React.useState<StudioMemberBindingRunStatusResponse | null>(null);
  const [publishError, setPublishError] = React.useState("");
  const [nodeLibraryOpen, setNodeLibraryOpen] = React.useState(false);
  const [draftRunPanelOpen, setDraftRunPanelOpen] = React.useState(false);
  const [yamlImportPanelOpen, setYamlImportPanelOpen] = React.useState(false);
  const [yamlImportError, setYamlImportError] = React.useState("");
  const [yamlPanelOpen, setYamlPanelOpen] = React.useState(false);
  const [currentYaml, setCurrentYaml] = React.useState("");
  const [currentYamlError, setCurrentYamlError] = React.useState("");
  const [currentYamlPending, setCurrentYamlPending] = React.useState(false);
  const [selectedEdgeId, setSelectedEdgeId] = React.useState("");
  const [selectedNodeId, setSelectedNodeId] = React.useState("");
  const [selectedStepConfigurationError, setSelectedStepConfigurationError] =
    React.useState("");
  const [workflowTitle, setWorkflowTitleState] =
    React.useState("Untitled member");
  const sourceKeyRef = React.useRef("");
  const yamlSourceSignatureRef = React.useRef("");
  const yamlInFlightSignatureRef = React.useRef("");
  const yamlRequestIdRef = React.useRef(0);
  const suppressedSourceSignatureRef =
    React.useRef<WorkflowSourceSignature | null>(null);
  const teamsHref = buildTeamsHref();
  const teamQuery = useQuery({
    enabled: Boolean(route.scopeId && route.teamId),
    queryKey: getTeamMemberWorkflowStudioTeamQueryKey(
      route.scopeId,
      route.teamId,
    ),
    queryFn: () => studioApi.getTeam(route.scopeId, route.teamId),
  });
  const refreshTeamMemberSurfaces = React.useCallback(
    async (scopeId: string, teamId: string) => {
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: getTeamMemberWorkflowStudioTeamQueryKey(scopeId, teamId),
        }),
        queryClient.invalidateQueries({
          queryKey: ["teams", "team-members", scopeId, teamId],
        }),
        queryClient.invalidateQueries({
          queryKey: ["teams", "team-summary", scopeId, teamId],
        }),
        queryClient.invalidateQueries({
          queryKey: ["teams", "members", scopeId],
        }),
        queryClient.invalidateQueries({
          queryKey: ["teams", "roster", scopeId],
        }),
      ]);
    },
    [queryClient],
  );
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
  const routeDraftWorkflowId =
    route.mode === "existing" &&
    isWorkflowDraftRouteId(trimOptional(route.workflowId))
      ? trimOptional(route.workflowId)
      : "";
  const workflowQueryKey = getTeamMemberWorkflowStudioWorkflowQueryKey(
    route.scopeId,
    routeDraftWorkflowId,
  );
  const workflowQuery = useQuery({
    enabled: Boolean(
      route.scopeId &&
        routeDraftWorkflowId &&
        !memberQuery.isLoading,
    ),
    queryKey: workflowQueryKey,
    queryFn: () => studioApi.getWorkflow(routeDraftWorkflowId, route.scopeId),
    retry: false,
  });
  const activeDraftWorkflowId =
    trimOptional(workflowQuery.data?.workflowId) ||
    (route.mode === "existing" && routeDraftWorkflowId
      ? routeDraftWorkflowId
      : "");
  const teamMembersReturnHref = buildTeamDetailHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    tab: "members",
    teamId: route.teamId,
    workflowId: activeDraftWorkflowId || undefined,
  });
  const backHref = teamMembersReturnHref;
  const teamHref = route.scopeId ? teamMembersReturnHref : teamsHref;
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
      workflowQuery.data?.workflowId ?? routeDraftWorkflowId,
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
  const activeMemberTitle =
    trimOptional(workflowTitle) ||
    trimOptional(memberQuery.data?.summary.displayName) ||
    routeFallbackTitle;
  const teamName =
    trimOptional(teamQuery.data?.displayName) || route.teamId || "Current team";
  const linkedWorkflowMissing =
    route.mode === "existing" &&
    !memberQuery.isLoading &&
    (!routeDraftWorkflowId || workflowQuery.isError) &&
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
      : workflowQuery.data
        ? [
            "workflow",
            workflowQuery.data.workflowId,
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

    const sourceSignature = readWorkflowSourceSignature(workflowQuery.data);
    const suppressedSourceSignature = suppressedSourceSignatureRef.current;
    if (
      sourceSignature &&
      suppressedSourceSignature &&
      workflowSourceSignaturesMatch(sourceSignature, suppressedSourceSignature)
    ) {
      suppressedSourceSignatureRef.current = null;
      sourceKeyRef.current = sourceKey;
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
    setSelectedEdgeId("");
    setSelectedNodeId("");
    setDraftRunPanelOpen(false);
    setYamlImportPanelOpen(false);
    setYamlImportError("");
    setYamlPanelOpen(false);
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
  const markSavedDraft = React.useCallback(
    (saved: SavedWorkflowDraft) => {
      const savedWorkflow = buildSavedWorkflowCacheValue(saved);
      const savedSignature = readWorkflowSourceSignature(savedWorkflow);
      if (savedSignature && route.scopeId && routeDraftWorkflowId) {
        suppressedSourceSignatureRef.current = savedSignature;
        queryClient.setQueryData(workflowQueryKey, savedWorkflow);
      }

      setEditableDocument((currentDocument) =>
        currentDocument
          ? trimOptional(currentDocument.name) === saved.title
            ? currentDocument
            : {
                ...currentDocument,
                name: saved.title,
              }
          : cloneWorkflowDocument(saved.document),
      );
      setWorkflowTitleState(saved.title);
      setDirty(false);
    },
    [
      queryClient,
      routeDraftWorkflowId,
      route.scopeId,
      workflowQueryKey,
    ],
  );

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
      markSavedDraft(saved);
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
      const savedWorkflowId = trimOptional(savedDraft.workflow.workflowId);
      if (!savedWorkflowId) {
        throw new Error("Workflow draft save did not return a stable workflow id.");
      }

      const createdMember = await studioApi.createMember({
        scopeId: route.scopeId,
        displayName: normalizedTitle,
        implementationKind: "workflow",
        teamId: route.teamId,
      });
      const createdMemberId = trimOptional(createdMember.memberId);
      if (!createdMemberId) {
        throw new Error("Workflow member creation did not return a stable member id.");
      }
      await studioApi.updateMemberImplementationRef({
        scopeId: route.scopeId,
        memberId: createdMemberId,
        implementationRef: {
          implementationKind: "workflow",
          workflowId: savedWorkflowId,
        },
      });
      return {
        memberId: createdMemberId,
        savedDraft,
        workflowId: savedWorkflowId,
      };
    },
    onError: (error) => {
      void message.error(
        error instanceof Error
          ? error.message
          : "Failed to create workflow member.",
      );
    },
    onSuccess: ({ memberId, savedDraft, workflowId }) => {
      applySavedDraft(savedDraft);
      void refreshTeamMemberSurfaces(route.scopeId, route.teamId);
      void message.success("Workflow member created.");
      history.replace(
        buildTeamMemberWorkflowStudioHref({
          memberId,
          mode: "edit-member",
          scopeId: route.scopeId,
          teamId: route.teamId,
          workflowId,
        }),
      );
    },
  });
  const createUnlinkedMemberDraftMutation = useMutation({
    mutationFn: async ({
      document,
      layout,
      title,
    }: Omit<SaveWorkflowDraftVariables, "workflow">): Promise<CreatedUnlinkedMemberDraft> => {
      if (!route.scopeId || !route.teamId || !route.memberId) {
        throw new Error("Resolve a Team member before saving this workflow draft.");
      }

      const normalizedTitle =
        trimOptional(title) || trimOptional(document.name) || routeFallbackTitle;
      const directoryId = resolveNewWorkflowDirectoryId({
        directories: workspaceSettingsQuery.data?.directories,
        scopeId: route.scopeId,
      });
      if (!directoryId) {
        throw new Error("Resolve a workflow directory before saving this draft.");
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
      const savedWorkflowId = trimOptional(savedDraft.workflow.workflowId);
      if (!savedWorkflowId) {
        throw new Error("Workflow draft save did not return a stable workflow id.");
      }

      await studioApi.updateMemberImplementationRef({
        scopeId: route.scopeId,
        memberId: route.memberId,
        implementationRef: {
          implementationKind: "workflow",
          workflowId: savedWorkflowId,
        },
      });

      history.replace(
        buildTeamMemberWorkflowStudioHref({
          memberId: route.memberId,
          mode: "edit-member",
          scopeId: route.scopeId,
          teamId: route.teamId,
          workflowId: savedWorkflowId,
        }),
      );

      return {
        savedDraft,
        workflowId: savedWorkflowId,
      };
    },
    onError: (error) => {
      void message.error(
        error instanceof Error
          ? error.message
          : "Failed to save workflow draft.",
      );
    },
    onSuccess: ({ savedDraft, workflowId }) => {
      applySavedDraft(savedDraft);
      void memberQuery.refetch();
      void refreshTeamMemberSurfaces(route.scopeId, route.teamId);
      void message.success("Workflow draft saved.");
    },
  });
  const pasteYamlMutation = useMutation({
    mutationFn: async (yaml: string) => {
      const normalizedYaml = yaml.trim();
      if (!normalizedYaml) {
        throw new Error("Paste workflow YAML before importing it.");
      }

      const parsed = await studioApi.parseYaml({
        yaml: normalizedYaml,
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      const parsedDocument = cloneWorkflowDocument(parsed.document);
      if (!parsedDocument) {
        const findingMessage = parsed.findings
          .map((finding) => finding.message)
          .filter(Boolean)
          .join(" ");
        throw new Error(
          findingMessage ||
            "The pasted YAML did not produce a workflow document.",
        );
      }

      return parsedDocument;
    },
    onError: (error) => {
      const errorMessage =
        error instanceof Error ? error.message : "Failed to import workflow YAML.";
      setYamlImportError(errorMessage);
      void message.error(errorMessage);
    },
    onSuccess: (parsedDocument) => {
      const nextTitle =
        trimOptional(parsedDocument.name) ||
        trimOptional(workflowTitle) ||
        routeFallbackTitle;
      const nextDocument: StudioWorkflowDocument = {
        ...parsedDocument,
        name: nextTitle,
      };
      const nextGraph = buildStudioGraphElements(nextDocument, editableLayout);
      setEditableDocument(nextDocument);
      setEditableLayout(
        buildStudioWorkflowLayout(nextTitle, nextGraph.nodes, editableLayout),
      );
      setWorkflowTitleState(nextTitle);
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
      setNodeLibraryOpen(false);
      setDirty(true);
      void message.success("Workflow YAML imported.");
    },
  });
  const activeMemberRunMutation = useMutation({
    mutationFn: async ({
      document,
      memberId,
      runMessage,
      title,
    }: RunActiveMemberVariables): Promise<StudioExecutionDetail> => {
      if (!route.scopeId || !memberId) {
        throw new Error("Resolve a workflow member before running its draft.");
      }

      const normalizedTitle = trimOptional(title) || "Workflow draft";
      const resolvedRunMessage = trimOptional(runMessage) || `Run ${normalizedTitle}`;
      const serialized = await studioApi.serializeYaml({
        document: {
          ...document,
          name: normalizedTitle,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      const startedAtUtc = new Date().toISOString();
      const executionId = `draft-run:${memberId}:${Date.now().toString(36)}`;
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
          auditSource: "draft-run-session",
          completedAtUtc,
          error,
          executionId,
          frames,
          runMessage: resolvedRunMessage,
          serviceId: "",
          startedAtUtc,
          status,
          workflowName: normalizedTitle,
        });

      setExecutionDetail(buildDetail("running"));

      try {
        const response = await runtimeRunsApi.streamDraftRun(
          route.scopeId,
          {
            prompt: resolvedRunMessage,
            workflowYamls: [serialized.yaml],
          },
          controller.signal,
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
          error instanceof Error ? error.message : "Workflow draft run failed.";
        const completedAtUtc = new Date().toISOString();
        return buildDetail("failed", completedAtUtc, errorMessage);
      }
    },
    onError: (error) => {
      setExecutionError(
        error instanceof Error
          ? error.message
          : "Failed to run workflow draft.",
      );
    },
    onMutate: () => {
      setExecutionError("");
    },
    onSuccess: (detail) => {
      setExecutionDetail(detail);
      setExecutionError("");
      if (detail.error) {
        void message.error("Workflow draft run failed.");
      } else {
        void message.success("Workflow draft run completed.");
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
      let workflowIdForPublish = trimOptional(workflow.workflowId);
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
        workflowIdForPublish =
          trimOptional(savedDraft.workflow.workflowId) || workflowIdForPublish;
      }

      if (!workflowIdForPublish) {
        throw new Error("Resolve a stable workflow draft id before publishing.");
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
        workflowId: workflowIdForPublish,
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
  const workflowLoading =
    route.mode === "existing" &&
    (memberQuery.isLoading ||
      (routeDraftWorkflowId &&
        (workflowQuery.isLoading || parseQuery.isLoading)));
  const workflowHasSteps = Boolean(editableDocument?.steps?.length);
  const canCreateUnlinkedMemberDraft = Boolean(
    route.mode === "existing" &&
      linkedWorkflowMissing &&
      route.scopeId &&
      route.teamId &&
      route.memberId &&
      !routeDraftWorkflowId &&
      editableDocument &&
      dirty &&
      workflowHasSteps &&
      !selectedStepConfigurationError &&
      !createUnlinkedMemberDraftMutation.isPending,
  );
  const canSave =
    route.mode === "new"
      ? Boolean(
          route.scopeId &&
            route.teamId &&
            editableDocument &&
            dirty &&
            workflowHasSteps &&
            !selectedStepConfigurationError &&
            !createWorkflowMemberMutation.isPending,
        )
      : Boolean(
          editableDocument &&
            dirty &&
            !selectedStepConfigurationError &&
            !saveMutation.isPending &&
            !createUnlinkedMemberDraftMutation.isPending &&
            ((workflowQuery.data && !linkedWorkflowMissing) ||
              canCreateUnlinkedMemberDraft),
        );
  const memberPublishedByQuery =
    memberQuery.data?.summary.lifecycleStage === "bind_ready" &&
    !linkedWorkflowMissing &&
    Boolean(workflowQuery.data);
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
      Boolean(selectedStepConfigurationError) ||
      publishMutation.isPending ||
      publishBindingStillInProgress ||
      (memberIsPublished && !publishHasDraftChanges),
  );
  const publishPlaceholderReason =
    route.mode === "new"
      ? "Create and link a workflow member before publishing."
      : linkedWorkflowMissing
        ? "No stable workflow draft is linked to this member yet."
        : selectedStepConfigurationError
          ? selectedStepConfigurationError
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
  const executionStatus = activeMemberRunMutation.isPending
    ? "running"
    : resolveWorkflowExecutionStatus(executionDetail);
  const serializeCurrentYaml = React.useCallback(async (options?: {
    readonly force?: boolean;
  }) => {
    if (!editableDocument) {
      yamlSourceSignatureRef.current = "";
      yamlInFlightSignatureRef.current = "";
      setCurrentYaml("");
      setCurrentYamlError("Load the workflow draft before viewing YAML.");
      setCurrentYamlPending(false);
      return;
    }

    const normalizedTitle =
      trimOptional(workflowTitle) ||
      trimOptional(editableDocument.name) ||
      routeFallbackTitle;
    const sourceSignature = JSON.stringify({
      document: {
        ...editableDocument,
        name: normalizedTitle,
      },
    });
    if (
      !options?.force &&
      yamlSourceSignatureRef.current === sourceSignature &&
      currentYaml.trim()
    ) {
      setCurrentYamlPending(false);
      return;
    }

    if (
      !options?.force &&
      yamlInFlightSignatureRef.current === sourceSignature
    ) {
      return;
    }

    const requestId = yamlRequestIdRef.current + 1;
    yamlRequestIdRef.current = requestId;
    yamlInFlightSignatureRef.current = sourceSignature;
    setCurrentYamlPending(true);
    setCurrentYamlError("");

    try {
      const serialized = await studioApi.serializeYaml({
        document: {
          ...editableDocument,
          name: normalizedTitle,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      if (yamlRequestIdRef.current === requestId) {
        yamlSourceSignatureRef.current = sourceSignature;
        setCurrentYaml(serialized.yaml);
        setCurrentYamlError("");
      }
    } catch (error) {
      if (yamlRequestIdRef.current === requestId) {
        setCurrentYamlError(
          error instanceof Error
            ? error.message
            : "Failed to build workflow YAML.",
        );
      }
    } finally {
      if (yamlInFlightSignatureRef.current === sourceSignature) {
        yamlInFlightSignatureRef.current = "";
      }
      if (yamlRequestIdRef.current === requestId) {
        setCurrentYamlPending(false);
      }
    }
  }, [
    currentYaml,
    editableDocument,
    routeFallbackTitle,
    workflowTitle,
  ]);
  React.useEffect(() => {
    if (!yamlPanelOpen) {
      return;
    }

    void serializeCurrentYaml();
  }, [serializeCurrentYaml, yamlPanelOpen]);
  const canRunActiveMember = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      editableDocument &&
      workflowHasSteps &&
      !activeMemberRunMutation.isPending,
  );
  const canOpenDraftRunPanel = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      editableDocument &&
      !workflowLoading,
  );
  const activeMemberRunPlaceholderReason =
    route.mode === "new"
      ? "Create and link a workflow member before running its draft."
      : !route.memberId
        ? "Resolve the workflow member before running its draft."
        : !editableDocument
          ? "Load the workflow draft before running it."
          : !route.scopeId
            ? "Resolve the current workspace before running the draft."
            : activeMemberRunMutation.isPending
              ? "Workflow draft run is already starting."
              : linkedWorkflowMissing
                ? "Run the local draft sketch. Saving remains limited until a stable workflow draft is linked."
                : !workflowHasSteps
                  ? "Add at least one step before running this workflow draft."
                  : "Run the current workflow draft.";
  const savePlaceholderReason =
    route.mode === "new"
      ? !editableDocument
        ? "Load the workflow draft before creating this member."
        : !workflowHasSteps
          ? "Add at least one step before creating this member."
        : !dirty
          ? "No changes to save."
        : "Save creates the workflow draft and Team member."
      : !editableDocument
        ? "Load the workflow draft before saving."
        : !dirty
          ? "No changes to save."
        : selectedStepConfigurationError
          ? selectedStepConfigurationError
        : linkedWorkflowMissing
          ? !workflowHasSteps
            ? "Add at least one step before saving a workflow draft."
            : routeDraftWorkflowId
              ? "Load the workflow draft before saving."
              : "Save creates a reusable workflow draft for this editor."
          : !workflowQuery.data
            ? "Load a workflow member before saving."
            : "Save updates the workflow draft.";
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
      executionDetail?.auditSource === "draft-run-session" ||
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
      setSelectedEdgeId("");
      setSelectedNodeId(result.nodeId);
      setNodeLibraryOpen(false);
      setYamlImportPanelOpen(false);
      setYamlPanelOpen(false);
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
      setSelectedEdgeId("");
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
    setSelectedEdgeId("");
    setSelectedNodeId(result.nodeId);
    setDirty(true);
  }, [editableDocument, selectedNodeId]);
  const deleteSelectedConnection = React.useCallback(() => {
    if (!editableDocument || !selectedEdgeId) {
      return;
    }

    const connection = readConnectionFromGraphEdgeId(selectedEdgeId);
    if (!connection) {
      return;
    }

    const result = removeStepConnection(
      editableDocument,
      connection.sourceStepId,
      connection.targetStepId,
      connection.branchLabel,
    );
    setEditableDocument(result.document);
    setSelectedEdgeId("");
    setSelectedNodeId("");
    setDirty(true);
  }, [editableDocument, selectedEdgeId]);
  const updateSelectedStepConfiguration = React.useCallback(
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
        setSelectedEdgeId("");
        setSelectedNodeId(result.nodeId);
        setSelectedStepConfigurationError("");
        setDirty(true);
      } catch (error) {
        setSelectedStepConfigurationError(
          error instanceof Error
            ? error.message
            : "Raw node configuration must be a JSON object.",
        );
      }
    },
    [editableDocument, selectedStepDraft],
  );

  return {
    publishMember: () => {
      if (
        workflowQuery.data &&
        editableDocument &&
        !selectedStepConfigurationError
      ) {
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
    navigateToTeam: () => {
      if (!dirty || confirmDiscardUnsavedChanges()) {
        history.push(teamHref);
      }
    },
    navigateToTeams: () => {
      if (!dirty || confirmDiscardUnsavedChanges()) {
        history.push(teamsHref);
      }
    },
    pasteYaml: async (yaml: string) => {
      await pasteYamlMutation.mutateAsync(yaml);
    },
    pasteYamlPending: pasteYamlMutation.isPending,
    closeYamlImportPanel: () => {
      if (!pasteYamlMutation.isPending) {
        setYamlImportPanelOpen(false);
        setYamlImportError("");
      }
    },
    yamlImportError,
    yamlImportPanelOpen,
    teamHref,
    teamsHref,
    canOpenDraftRunPanel,
    canRunActiveMember,
    canSave,
    canViewYaml: Boolean(editableDocument && !workflowLoading),
    closeNodeLibrary: () => setNodeLibraryOpen(false),
    closeYamlPanel: () => setYamlPanelOpen(false),
    connectNodes,
    currentYaml,
    currentYamlError,
    currentYamlPending,
    deleteSelectedConnection,
    deleteSelectedNode,
    dirty,
    emptyDescription:
      route.mode === "new"
        ? "Build the draft locally first, then save it as a linked Team workflow member."
        : linkedWorkflowMissing
          ? "No workflow draft is linked to this member yet. Build or paste a workflow, then save to create a reusable draft."
          : "Start this workflow by adding the first step.",
    runActiveMember: () => {
      if (route.memberId && editableDocument) {
        activeMemberRunMutation.mutate({
          document: editableDocument,
          memberId: route.memberId,
          runMessage: trimOptional(executionRunMessage),
          title: activeMemberTitle,
        });
      }
    },
    activeMemberRunPending: activeMemberRunMutation.isPending,
    activeMemberRunPlaceholderReason,
    executionDetail,
    executionError,
    executionRunMessage,
    executionStatus,
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
    openNodeLibrary: () => setNodeLibraryOpen(true),
    openDraftRunPanel: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
      setDraftRunPanelOpen(true);
    },
    openYamlImportPanel: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlPanelOpen(false);
      setYamlImportError("");
      setYamlImportPanelOpen(true);
    },
    openYamlPanel: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(true);
    },
    retryYaml: () => {
      void serializeCurrentYaml({ force: true });
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

      if (linkedWorkflowMissing && editableDocument && !routeDraftWorkflowId) {
        createUnlinkedMemberDraftMutation.mutate({
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
    savePending:
      saveMutation.isPending ||
      createWorkflowMemberMutation.isPending ||
      createUnlinkedMemberDraftMutation.isPending,
    savePlaceholderReason,
    draftRunPanelOpen,
    selectedEdgeId,
    selectedNodeId,
    selectedStepDraft,
    selectedStepConfigurationError,
    selectCanvas: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
    },
    selectEdge: (edgeId: string) => {
      setSelectedEdgeId(edgeId);
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
    },
    selectNode: (nodeId: string) => {
      setSelectedEdgeId("");
      setSelectedNodeId(nodeId);
      setSelectedStepConfigurationError("");
      setDraftRunPanelOpen(false);
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
    },
    setExecutionRunMessage,
    setSelectedStepConfigurationError,
    setWorkflowTitle,
    teamName,
    updateSelectedStepConfiguration,
    workflowTitle,
    yamlPanelOpen,
  };
}
