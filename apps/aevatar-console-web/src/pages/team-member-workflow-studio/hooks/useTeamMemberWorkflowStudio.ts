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
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
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
type WorkflowBindingCandidate = {
  readonly activeRevisionId?: string | null;
  readonly serviceKey?: string | null;
  readonly workflowId: string;
};

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

function getTeamMemberWorkflowStudioTeamQueryKey(
  scopeId: string,
  teamId: string,
) {
  return ["team-member-workflow-studio", "team", scopeId, teamId] as const;
}

function getTeamMemberWorkflowStudioWorkflowQueryKey(
  scopeId: string,
  workflowIds: readonly string[],
) {
  return [
    "team-member-workflow-studio",
    "workflow",
    scopeId,
    [...workflowIds],
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
  readonly pasteYaml: (yaml: string) => Promise<void>;
  readonly pasteYamlPending: boolean;
  readonly teamHref: string;
  readonly teamsHref: string;
  readonly canRunActiveMember: boolean;
  readonly canSave: boolean;
  readonly closeNodeLibrary: () => void;
  readonly connectNodes: (sourceNodeId: string, targetNodeId: string) => void;
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
  readonly openRunOptions: () => void;
  readonly save: () => void;
  readonly savePending: boolean;
  readonly savePlaceholderReason: string;
  readonly runOptionsOpen: boolean;
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

function resolveExplicitWorkflowId(
  memberDetail: StudioMemberDetail | null | undefined,
): string {
  const implementationRef = memberDetail?.implementationRef;
  if (
    trimOptional(implementationRef?.implementationKind).toLowerCase() !== "workflow"
  ) {
    return "";
  }

  return trimOptional(implementationRef?.workflowId);
}

function isWorkflowDraftRouteId(value: string): boolean {
  return Boolean(value && !/\s/.test(value));
}

function resolveBoundWorkflowRevisionId(
  memberDetail: StudioMemberDetail | null | undefined,
): string {
  if (
    trimOptional(memberDetail?.summary.implementationKind).toLowerCase() !==
    "workflow"
  ) {
    return "";
  }

  return (
    trimOptional(memberDetail?.implementationRef?.workflowRevision) ||
    trimOptional(memberDetail?.summary.lastBoundRevisionId) ||
    trimOptional(memberDetail?.lastBinding?.revisionId)
  );
}

function resolveMemberWorkflowOwnerId(
  memberDetail: StudioMemberDetail | null | undefined,
  routeMemberId?: string | null,
): string {
  return (
    trimOptional(memberDetail?.summary.memberId) ||
    trimOptional(routeMemberId)
  );
}

function isPublishedServiceWorkflowIdentity(input: {
  readonly memberId?: string | null;
  readonly publishedServiceId?: string | null;
  readonly workflowId?: string | null;
}): boolean {
  const workflowId = trimOptional(input.workflowId);
  if (!workflowId) {
    return false;
  }

  const publishedServiceId = trimOptional(input.publishedServiceId);
  if (publishedServiceId && workflowId === publishedServiceId) {
    return true;
  }

  const memberId = trimOptional(input.memberId);
  return Boolean(memberId && workflowId === `member-${memberId}`);
}

function resolveWorkflowDraftReloadIds(
  memberDetail: StudioMemberDetail | null | undefined,
  routeMemberId?: string | null,
  routeWorkflowId?: string | null,
  recoveredWorkflowId?: string | null,
): readonly string[] {
  const explicitWorkflowId = resolveExplicitWorkflowId(memberDetail);
  const memberId = resolveMemberWorkflowOwnerId(memberDetail, routeMemberId);
  const publishedServiceId = trimOptional(memberDetail?.summary.publishedServiceId);
  const routeDraftWorkflowId = trimOptional(routeWorkflowId);
  const recoveredDraftWorkflowId = trimOptional(recoveredWorkflowId);
  const recoveredIsReloadableWorkflowId = isReloadWorkflowIdAllowed({
    memberId,
    publishedServiceId,
    workflowId: recoveredDraftWorkflowId,
  });
  const routeIsReloadableWorkflowId = isReloadWorkflowIdAllowed({
    memberId,
    publishedServiceId,
    workflowId: routeDraftWorkflowId,
  });
  const explicitIsReloadableWorkflowId = isReloadWorkflowIdAllowed({
    memberId,
    publishedServiceId,
    workflowId: explicitWorkflowId,
  });
  const ids = [
    recoveredIsReloadableWorkflowId ? recoveredDraftWorkflowId : "",
    explicitIsReloadableWorkflowId ? explicitWorkflowId : "",
    routeIsReloadableWorkflowId ? routeDraftWorkflowId : "",
  ];

  return Array.from(new Set(ids.filter(Boolean)));
}

function selectPublishedService(
  services: readonly ServiceCatalogSnapshot[],
  publishedServiceId: string,
): ServiceCatalogSnapshot | null {
  const normalizedServiceId = trimOptional(publishedServiceId);
  if (!normalizedServiceId) {
    return null;
  }

  return (
    services.find(
      (service) => trimOptional(service.serviceId) === normalizedServiceId,
    ) ?? null
  );
}

function readServiceIdFromServiceKey(serviceKey: string): string {
  const normalized = trimOptional(serviceKey);
  if (!normalized) {
    return "";
  }

  return normalized.split(":").filter(Boolean).at(-1) ?? normalized;
}

function serviceKeysReferToSameService(
  leftServiceKey?: string | null,
  rightServiceKey?: string | null,
): boolean {
  const left = trimOptional(leftServiceKey);
  const right = trimOptional(rightServiceKey);
  if (!left || !right) {
    return false;
  }

  if (left === right) {
    return true;
  }

  const leftServiceId = readServiceIdFromServiceKey(left);
  const rightServiceId = readServiceIdFromServiceKey(right);
  return Boolean(leftServiceId && leftServiceId === rightServiceId);
}

function workflowMatchesPublishedService(
  workflow: WorkflowBindingCandidate,
  service: ServiceCatalogSnapshot,
): boolean {
  const workflowRevisionId = trimOptional(workflow.activeRevisionId);
  if (
    workflowRevisionId &&
    (trimOptional(service.activeServingRevisionId) === workflowRevisionId ||
      trimOptional(service.defaultServingRevisionId) === workflowRevisionId)
  ) {
    return true;
  }

  const workflowServiceKey = trimOptional(workflow.serviceKey);
  return serviceKeysReferToSameService(workflowServiceKey, service.serviceKey);
}

function isReloadWorkflowIdAllowed(input: {
  readonly memberId: string;
  readonly publishedServiceId: string;
  readonly workflowId?: string | null;
}): boolean {
  const workflowId = trimOptional(input.workflowId);
  if (!workflowId) {
    return false;
  }

  return (
    isWorkflowDraftRouteId(workflowId) &&
    !isPublishedServiceWorkflowIdentity({
      memberId: input.memberId,
      publishedServiceId: input.publishedServiceId,
      workflowId,
    })
  );
}

function selectWorkflowByRevision<TWorkflow extends WorkflowBindingCandidate>(
  workflows: readonly TWorkflow[],
  revisionId: string,
): TWorkflow | null {
  const normalizedRevisionId = trimOptional(revisionId);
  if (!normalizedRevisionId) {
    return null;
  }

  return (
    workflows.find(
      (workflow) => trimOptional(workflow.activeRevisionId) === normalizedRevisionId,
    ) ?? null
  );
}

function selectWorkflowForPublishedMember<TWorkflow extends WorkflowBindingCandidate>(input: {
  readonly boundWorkflowRevisionId: string;
  readonly publishedService: ServiceCatalogSnapshot | null;
  readonly workflows: readonly TWorkflow[];
}): TWorkflow | null {
  const normalizedRevisionId = trimOptional(input.boundWorkflowRevisionId);
  if (normalizedRevisionId) {
    const revisionMatch = selectWorkflowByRevision(
      input.workflows,
      normalizedRevisionId,
    );
    if (revisionMatch) {
      return revisionMatch;
    }
  }

  if (!input.publishedService) {
    return null;
  }
  const publishedService = input.publishedService;

  return (
    input.workflows.find(
      (workflow) =>
        workflowMatchesPublishedService(workflow, publishedService),
    ) ?? null
  );
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
  const [runOptionsOpen, setRunOptionsOpen] = React.useState(false);
  const [selectedEdgeId, setSelectedEdgeId] = React.useState("");
  const [selectedNodeId, setSelectedNodeId] = React.useState("");
  const [selectedStepConfigurationError, setSelectedStepConfigurationError] =
    React.useState("");
  const [workflowTitle, setWorkflowTitleState] =
    React.useState("Untitled member");
  const sourceKeyRef = React.useRef("");
  const suppressedSourceSignatureRef =
    React.useRef<WorkflowSourceSignature | null>(null);
  const backHref = buildTeamDetailHref({
    scopeId: route.scopeId,
    tab: "members",
    teamId: route.teamId,
  });
  const teamsHref = buildTeamsHref();
  const teamHref = route.scopeId
    ? buildTeamDetailHref({
        scopeId: route.scopeId,
        tab: "members",
        teamId: route.teamId,
      })
    : teamsHref;
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
  const explicitWorkflowId =
    route.mode === "existing" ? resolveExplicitWorkflowId(memberQuery.data) : "";
  const boundWorkflowRevisionId =
    route.mode === "existing"
      ? resolveBoundWorkflowRevisionId(memberQuery.data)
      : "";
  const memberPublishedServiceId = trimOptional(
    memberQuery.data?.summary.publishedServiceId,
  );
  const memberWorkflowOwnerId = resolveMemberWorkflowOwnerId(
    memberQuery.data,
    route.memberId,
  );
  const routeWorkflowIdIsPublishedService = isPublishedServiceWorkflowIdentity({
    memberId: memberWorkflowOwnerId,
    publishedServiceId: memberPublishedServiceId,
    workflowId: route.workflowId,
  });
  const explicitWorkflowIdIsPublishedService = isPublishedServiceWorkflowIdentity({
    memberId: memberWorkflowOwnerId,
    publishedServiceId: memberPublishedServiceId,
    workflowId: explicitWorkflowId,
  });
  const hasExplicitWorkflowId = Boolean(
    explicitWorkflowId && !explicitWorkflowIdIsPublishedService,
  );
  const shouldResolveWorkflowFromRevision = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      !memberQuery.isLoading &&
      memberQuery.data &&
      (!trimOptional(route.workflowId) || routeWorkflowIdIsPublishedService) &&
      (boundWorkflowRevisionId ||
        explicitWorkflowIdIsPublishedService ||
        (!hasExplicitWorkflowId &&
          (memberPublishedServiceId || routeWorkflowIdIsPublishedService))),
  );
  const workflowRevisionQuery = useQuery({
    enabled: shouldResolveWorkflowFromRevision,
    queryKey: [
      "team-member-workflow-studio",
      "workflow-by-binding",
      route.scopeId,
      boundWorkflowRevisionId,
      memberPublishedServiceId,
    ],
    queryFn: async () => {
      const [workflows, services] = await Promise.all([
        studioApi.listWorkflows(route.scopeId),
        memberPublishedServiceId
          ? scopeRuntimeApi.listServices(route.scopeId, { take: 200 })
          : Promise.resolve([] as ServiceCatalogSnapshot[]),
      ]);
      const publishedService = selectPublishedService(
        services,
        memberPublishedServiceId,
      );
      return selectWorkflowForPublishedMember({
        boundWorkflowRevisionId,
        publishedService,
        workflows,
      });
    },
    retry: false,
  });
  const recoveredWorkflowId = trimOptional(
    workflowRevisionQuery.data?.workflowId,
  );
  const recoveredWorkflowIdIsPublishedService = isPublishedServiceWorkflowIdentity({
    memberId: memberWorkflowOwnerId,
    publishedServiceId: memberPublishedServiceId,
    workflowId: recoveredWorkflowId,
  });
  const workflowDraftReloadIds =
    route.mode === "existing"
      ? resolveWorkflowDraftReloadIds(
          memberQuery.data,
          route.memberId,
          route.workflowId,
          recoveredWorkflowIdIsPublishedService ? "" : recoveredWorkflowId,
        )
      : [];
  const workflowQueryKey = getTeamMemberWorkflowStudioWorkflowQueryKey(
    route.scopeId,
    workflowDraftReloadIds,
  );
  const workflowQuery = useQuery({
    enabled: Boolean(
      route.scopeId &&
        workflowDraftReloadIds.length &&
        !memberQuery.isLoading &&
        !workflowRevisionQuery.isLoading,
    ),
    queryKey: workflowQueryKey,
    queryFn: async () => {
      let lastError: unknown = null;
      for (const workflowId of workflowDraftReloadIds) {
        try {
          return await studioApi.getWorkflow(workflowId, route.scopeId);
        } catch (error) {
          lastError = error;
        }
      }

      throw lastError instanceof Error
        ? lastError
        : new Error("Workflow draft was not found.");
    },
    retry: false,
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
      workflowQuery.data?.workflowId ?? workflowDraftReloadIds.join("|"),
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
    !workflowRevisionQuery.isLoading &&
    (!workflowDraftReloadIds.length || workflowQuery.isError) &&
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
  const markSavedDraft = React.useCallback(
    (saved: SavedWorkflowDraft) => {
      const savedWorkflow = buildSavedWorkflowCacheValue(saved);
      const savedSignature = readWorkflowSourceSignature(savedWorkflow);
      if (savedSignature && route.scopeId && workflowDraftReloadIds.length > 0) {
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
      route.scopeId,
      workflowDraftReloadIds.length,
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

      const createdMember = await studioApi.createMemberWithId({
        scopeId: route.scopeId,
        memberId: savedWorkflowId,
        displayName: normalizedTitle,
        implementationKind: "workflow",
        teamId: route.teamId,
      });
      const createdMemberId = trimOptional(createdMember.memberId);
      if (!createdMemberId) {
        throw new Error("Workflow member creation did not return a stable member id.");
      }
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
      void message.error(
        error instanceof Error ? error.message : "Failed to import workflow YAML.",
      );
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
      setRunOptionsOpen(false);
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
  const workflowLoading =
    route.mode === "existing" &&
    (memberQuery.isLoading ||
      workflowRevisionQuery.isLoading ||
      (workflowDraftReloadIds.length > 0 &&
        (workflowQuery.isLoading || parseQuery.isLoading)));
  const workflowHasSteps = Boolean(editableDocument?.steps?.length);
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
          workflowQuery.data &&
            editableDocument &&
            dirty &&
            !selectedStepConfigurationError &&
            !linkedWorkflowMissing &&
            !saveMutation.isPending,
        );
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
  const canRunActiveMember = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      editableDocument &&
      workflowHasSteps &&
      !activeMemberRunMutation.isPending,
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
    teamHref,
    teamsHref,
    canRunActiveMember,
    canSave,
    closeNodeLibrary: () => setNodeLibraryOpen(false),
    connectNodes,
    deleteSelectedConnection,
    deleteSelectedNode,
    dirty,
    emptyDescription:
      route.mode === "new"
        ? "Build the draft locally first, then save it as a linked Team workflow member."
        : linkedWorkflowMissing
          ? "No workflow draft is linked to this member yet. You can sketch locally, but saving requires a stable workflow reference."
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
    openRunOptions: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
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
    selectedEdgeId,
    selectedNodeId,
    selectedStepDraft,
    selectedStepConfigurationError,
    selectCanvas: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setRunOptionsOpen(false);
    },
    selectEdge: (edgeId: string) => {
      setSelectedEdgeId(edgeId);
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setRunOptionsOpen(false);
    },
    selectNode: (nodeId: string) => {
      setSelectedEdgeId("");
      setSelectedNodeId(nodeId);
      setSelectedStepConfigurationError("");
      setRunOptionsOpen(false);
    },
    setExecutionRunMessage,
    setSelectedStepConfigurationError,
    setWorkflowTitle,
    teamName,
    updateSelectedStepConfiguration,
    workflowTitle,
  };
}
