import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { message } from "antd";
import React from "react";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEventAccumulator,
} from "@/shared/agui/runtimeEventSemantics";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { t } from "@/shared/i18n/messages";
import {
  buildTeamDetailHref,
  buildTeamMemberAutomationsHref,
  buildTeamMemberPublishedRunsHref,
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
  buildExecutionTrace,
  createStudioExecutionFrame,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  type ExecutionTrace,
} from "@/shared/studio/execution";
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
  STUDIO_GRAPH_CATEGORIES,
} from "@/shared/studio/graph";
import { isStudioApiStatus, studioApi } from "@/shared/studio/api";
import { normalizeStudioMemberLifecycleStage } from "@/shared/studio/models";
import type {
  StudioExecutionDetail,
  StudioExecutionFrame,
  StudioMemberBindingRunStatusResponse,
  StudioMemberDetail,
  StudioSaveAndBindWorkflowAcceptedResult,
  StudioWorkflowDraftCreateAcceptedReceipt,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowSaveResult,
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

type RunCurrentDraftVariables = {
  readonly document: StudioWorkflowDocument;
  readonly files?: readonly File[];
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

type SavedAndBoundWorkflow = {
  readonly materializedWorkflow: StudioWorkflowFile | null;
  readonly result: StudioSaveAndBindWorkflowAcceptedResult;
  readonly savedDraft: SavedWorkflowDraft;
};

class PublishWorkflowStatusError extends Error {
  readonly showAsError: boolean;

  constructor(message: string, showAsError: boolean) {
    super(message);
    this.name = "PublishWorkflowStatusError";
    this.showAsError = showAsError;
  }
}

type CreatedWorkflowMember = {
  readonly memberId: string;
  readonly savedDraft: SavedWorkflowDraft;
  readonly workflowId: string;
};

type PendingCreatedWorkflowMemberLink = CreatedWorkflowMember;

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

function getTeamMemberWorkflowStudioMemberQueryKey(
  scopeId: string,
  memberId: string,
) {
  return ["team-member-workflow-studio", "member", scopeId, memberId] as const;
}

function getTeamMemberWorkflowStudioWorkflowQueryKey(
  scopeId: string,
  workflowId: string,
  source: "draft" | "published" = "draft",
) {
  return [
    "team-member-workflow-studio",
    "workflow",
    scopeId,
    workflowId,
    source,
  ] as const;
}

type WorkflowSourceSignature = {
  readonly updatedAtUtc: string;
  readonly workflowId: string;
  readonly yaml: string;
};

type TeamMemberWorkflowStudioState = {
  readonly automationsHref: string;
  readonly canOpenAutomations: boolean;
  readonly automationsPlaceholderReason: string;
  readonly canOpenPublishedRuns: boolean;
  readonly publishedRunsHref: string;
  readonly publishedRunsPlaceholderReason: string;
  readonly publishMember: () => void;
  readonly memberPublished: boolean;
  readonly publishDisabled: boolean;
  readonly publishNotice: string;
  readonly publishPending: boolean;
  readonly publishPlaceholderReason: string;
  readonly publishTone: WorkflowPublishTone;
  readonly refreshPublishStatus: () => void;
  readonly refreshPublishStatusPending: boolean;
  readonly showRefreshPublishStatus: boolean;
  readonly backHref: string;
  readonly navigateToTeam: () => void;
  readonly navigateToPublishedRuns: () => void;
  readonly navigateToAutomations: () => void;
  readonly navigateToTeams: () => void;
  readonly closeYamlImportPanel: () => void;
  readonly pasteYaml: (yaml: string) => Promise<void>;
  readonly yamlImportError: string;
  readonly yamlImportPanelOpen: boolean;
  readonly pasteYamlPending: boolean;
  readonly teamHref: string;
  readonly teamsHref: string;
  readonly canOpenDraftRunPanel: boolean;
  readonly canRunCurrentDraft: boolean;
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
  readonly runCurrentDraft: () => void;
  readonly currentDraftRunPending: boolean;
  readonly currentDraftRunPlaceholderReason: string;
  readonly executionDetail: StudioExecutionDetail | null;
  readonly executionError: string;
  readonly draftRunFiles: readonly File[];
  readonly executionRunMessage: string;
  readonly executionStatus: WorkflowExecutionStatus;
  readonly activeExecutionLogIndex: number | null;
  readonly clearExecutionLogs: () => void;
  readonly addDraftRunFiles: (files: readonly File[]) => void;
  readonly removeDraftRunFile: (index: number) => void;
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
  readonly selectExecutionLog: (index: number | null) => void;
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
const CREATED_MEMBER_MATERIALIZATION_ATTEMPTS = 8;
const CREATED_MEMBER_MATERIALIZATION_DELAY_MS = 450;
const WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS = 10;
const WORKFLOW_DRAFT_MATERIALIZATION_DELAY_MS = 900;
const SAVE_AND_BIND_WORKFLOW_MATERIALIZATION_ATTEMPTS = 12;
const SAVE_AND_BIND_WORKFLOW_MATERIALIZATION_DELAY_MS = 1_000;
const SAVED_WORKFLOW_QUERY_STALE_MS = 30_000;

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function normalizeWorkflowSaveResult(
  result: StudioWorkflowSaveResult | StudioWorkflowFile,
): StudioWorkflowSaveResult {
  if ("kind" in result) {
    return result;
  }

  return {
    kind: "materialized",
    workflow: result,
  };
}

function describeWorkflowDraftAcceptedReceipt(
  receipt: StudioWorkflowDraftCreateAcceptedReceipt,
): string {
  return (
    trimOptional(receipt.readiness.message) ||
    `Workflow draft ${receipt.workflowId} was accepted. Studio is waiting for the scoped workspace projection.`
  );
}

function hasCurrentCompletedMemberBinding(
  detail: StudioMemberDetail | null | undefined,
): boolean {
  if (
    normalizeStudioMemberLifecycleStage(detail?.summary.lifecycleStage) !==
    "bind_ready"
  ) {
    return false;
  }

  return Boolean(
    trimOptional(detail?.summary.lastBoundRevisionId) ||
      trimOptional(detail?.lastBinding?.revisionId),
  );
}

function hasActiveMemberBindingRun(
  detail: StudioMemberDetail | null | undefined,
): boolean {
  return Boolean(
    detail?.currentBindingRun &&
      !isTerminalBindingRun(detail.currentBindingRun),
  );
}

function readActiveMemberBindingRunId(
  detail: StudioMemberDetail | null | undefined,
): string {
  if (!hasActiveMemberBindingRun(detail)) {
    return "";
  }

  return trimOptional(detail?.currentBindingRun?.bindingRunId);
}

function readFallbackBindingRun(
  detail: StudioMemberDetail | null | undefined,
  bindingRunId: string,
): StudioMemberBindingRunStatusResponse | null {
  const currentRun = detail?.currentBindingRun ?? null;
  if (!isSameBindingRun(currentRun, bindingRunId)) {
    return null;
  }

  return currentRun;
}

function readPathSegments(): {
  canonicalHref: string;
  memberId: string;
  mode: TeamMemberWorkflowStudioMode;
  scopeId: string;
  teamId: string;
  workflowId: string;
  workflowSource: "draft" | "published";
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
  const workflowSource =
    trimOptional(params.get("workflowSource")) === "published"
      ? "published"
      : "draft";
  const canonicalHref =
    isWorkflowEditorRoute && scopeId && teamId
      ? buildTeamMemberWorkflowStudioHref({
          memberId: mode === "existing" ? routeMemberId : undefined,
          mode: mode === "new" ? "create-member" : "edit-member",
          scopeId,
          teamId,
          workflowId,
          workflowSource:
            mode === "existing" && workflowSource === "published"
              ? "published"
              : undefined,
        })
      : currentHref;

  return {
    canonicalHref,
    memberId: mode === "existing" ? routeMemberId : "",
    mode,
    scopeId,
    teamId,
    workflowId,
    workflowSource,
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

function waitForCreatedMemberMaterializationTick(): Promise<void> {
  return new Promise((resolve) => {
    const testEnvironment =
      typeof process !== "undefined" && process.env.NODE_ENV === "test";
    window.setTimeout(
      resolve,
      testEnvironment ? 0 : CREATED_MEMBER_MATERIALIZATION_DELAY_MS,
    );
  });
}

function waitForWorkflowDraftMaterializationTick(): Promise<void> {
  return new Promise((resolve) => {
    const testEnvironment =
      typeof process !== "undefined" && process.env.NODE_ENV === "test";
    window.setTimeout(
      resolve,
      testEnvironment ? 0 : WORKFLOW_DRAFT_MATERIALIZATION_DELAY_MS,
    );
  });
}

function waitForSaveAndBindWorkflowMaterializationTick(): Promise<void> {
  return new Promise((resolve) => {
    const testEnvironment =
      typeof process !== "undefined" && process.env.NODE_ENV === "test";
    window.setTimeout(
      resolve,
      testEnvironment ? 0 : SAVE_AND_BIND_WORKFLOW_MATERIALIZATION_DELAY_MS,
    );
  });
}

function workflowYamlMatches(left: string | null | undefined, right: string): boolean {
  return String(left || "").trim() === right.trim();
}

async function waitForSaveAndBindWorkflowMaterialized(input: {
  readonly expectedYaml: string;
  readonly scopeId: string;
  readonly workflowId: string;
}): Promise<StudioWorkflowFile | null> {
  for (
    let attempt = 0;
    attempt < SAVE_AND_BIND_WORKFLOW_MATERIALIZATION_ATTEMPTS;
    attempt += 1
  ) {
    try {
      const workflow = await studioApi.getPublishedWorkflow(
        input.workflowId,
        input.scopeId,
      );
      if (workflowYamlMatches(workflow.yaml, input.expectedYaml)) {
        return workflow;
      }
    } catch (error) {
      if (!isStudioApiStatus(error, 404)) {
        throw error;
      }
    }

    if (attempt < SAVE_AND_BIND_WORKFLOW_MATERIALIZATION_ATTEMPTS - 1) {
      await waitForSaveAndBindWorkflowMaterializationTick();
    }
  }

  return null;
}

async function loadPublishedWorkflowWithDraftFallback(input: {
  readonly scopeId: string;
  readonly workflowId: string;
}): Promise<StudioWorkflowFile> {
  try {
    return await studioApi.getPublishedWorkflow(input.workflowId, input.scopeId);
  } catch (error) {
    if (!isStudioApiStatus(error, 404)) {
      throw error;
    }

    return studioApi.getWorkflow(input.workflowId, input.scopeId);
  }
}

async function waitForCreatedMemberVisible(input: {
  readonly memberId: string;
  readonly scopeId: string;
}): Promise<StudioMemberDetail> {
  let lastNotFound: unknown = null;

  for (
    let attempt = 0;
    attempt < CREATED_MEMBER_MATERIALIZATION_ATTEMPTS;
    attempt += 1
  ) {
    try {
      return await studioApi.getMember(input.scopeId, input.memberId);
    } catch (error) {
      if (!isStudioApiStatus(error, 404)) {
        throw error;
      }

      lastNotFound = error;
      if (attempt < CREATED_MEMBER_MATERIALIZATION_ATTEMPTS - 1) {
        await waitForCreatedMemberMaterializationTick();
      }
    }
  }

  throw lastNotFound instanceof Error
    ? new Error(
        `Workflow member ${input.memberId} was created but is not visible yet. Retry saving in a moment.`,
        { cause: lastNotFound },
      )
    : new Error(
        `Workflow member ${input.memberId} was created but is not visible yet. Retry saving in a moment.`,
      );
}

async function waitForWorkflowDraftMaterialized(input: {
  readonly receipt: StudioWorkflowDraftCreateAcceptedReceipt;
  readonly scopeId: string;
}): Promise<StudioWorkflowFile> {
  let lastNotFound: unknown = null;

  for (
    let attempt = 0;
    attempt < WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS;
    attempt += 1
  ) {
    try {
      return await studioApi.getWorkflowDraftFile(
        input.receipt.workflowId,
        input.scopeId,
      );
    } catch (error) {
      if (!isStudioApiStatus(error, 404)) {
        throw error;
      }

      lastNotFound = error;
      if (attempt < WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS - 1) {
        await waitForWorkflowDraftMaterializationTick();
      }
    }
  }

  throw lastNotFound instanceof Error
    ? new Error(
        `Workflow draft ${input.receipt.workflowId} was accepted but is not readable yet. Retry saving in a moment.`,
        { cause: lastNotFound },
      )
    : new Error(
        `Workflow draft ${input.receipt.workflowId} was accepted but is not readable yet. Retry saving in a moment.`,
      );
}

async function linkCreatedWorkflowMemberDraft(input: {
  readonly memberId: string;
  readonly scopeId: string;
  readonly workflowId: string;
}): Promise<void> {
  await waitForCreatedMemberVisible({
    memberId: input.memberId,
    scopeId: input.scopeId,
  });

  try {
    await studioApi.updateMemberImplementationRef({
      scopeId: input.scopeId,
      memberId: input.memberId,
      implementationRef: {
        implementationKind: "workflow",
        workflowId: input.workflowId,
      },
    });
  } catch (error) {
    if (!isStudioApiStatus(error, 404)) {
      throw error;
    }

    await waitForCreatedMemberVisible({
      memberId: input.memberId,
      scopeId: input.scopeId,
    });
    await studioApi.updateMemberImplementationRef({
      scopeId: input.scopeId,
      memberId: input.memberId,
      implementationRef: {
        implementationKind: "workflow",
        workflowId: input.workflowId,
      },
    });
  }
}

function isTerminalBindingRun(
  run: StudioMemberBindingRunStatusResponse | null,
): boolean {
  return run?.status === "succeeded" || run?.status === "failed" || run?.status === "rejected";
}

function isSameBindingRun(
  run: StudioMemberBindingRunStatusResponse | null,
  bindingRunId: string,
): boolean {
  return Boolean(run && trimOptional(run.bindingRunId) === bindingRunId);
}

function readBindingRunFailureMessage(
  run: StudioMemberBindingRunStatusResponse | null,
): string {
  if (!run) {
    return "Binding run status is still pending.";
  }

  if (run.failure?.message) {
    return run.failure.message;
  }

  if (run.status === "rejected") {
    return "Binding run was rejected by the member authority.";
  }

  if (run.status === "failed") {
    return "Binding run failed while binding the member workflow.";
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
  const saveResult = normalizeWorkflowSaveResult(
    await studioApi.saveWorkflow({
      workflowId: workflow.workflowId,
      draftExists: workflow.draftExists,
      scopeId: routeScopeId,
      directoryId: workflow.directoryId,
      workflowName: normalizedTitle,
      fileName: workflow.fileName,
      yaml: serialized.yaml,
      layout: nextLayout,
    }),
  );
  if (saveResult.kind === "accepted") {
    void message.info(describeWorkflowDraftAcceptedReceipt(saveResult.receipt));
  }
  const savedWorkflow =
    saveResult.kind === "accepted"
      ? await waitForWorkflowDraftMaterialized({
          receipt: saveResult.receipt,
          scopeId: routeScopeId,
        })
      : saveResult.workflow;

  return {
    document: savedDocument,
    layout: nextLayout,
    title: normalizedTitle,
    workflow: savedWorkflow,
  };
}

async function saveAndBindPublishedWorkflowDraft(input: {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly routeScopeId: string;
  readonly serviceId?: string | null;
  readonly title: string;
  readonly workflow: StudioWorkflowFile;
}): Promise<SavedAndBoundWorkflow> {
  const { document, layout, routeScopeId, serviceId, title, workflow } = input;
  const workflowId = trimOptional(workflow.workflowId);
  if (!workflowId) {
    throw new Error("Resolve a stable workflow id before saving the published workflow.");
  }

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
  const result = await studioApi.saveAndBindWorkflow({
    scopeId: routeScopeId,
    workflowId,
    workflowYaml: serialized.yaml,
    workflowName: normalizedTitle,
    displayName: normalizedTitle,
    inlineWorkflowYamls: {},
    appId: "studio",
    serviceId,
    exposureDesired: true,
  });
  const resultWorkflowId = trimOptional(result.workflowId) || workflowId;
  const materializedWorkflow = await waitForSaveAndBindWorkflowMaterialized({
    expectedYaml: serialized.yaml,
    scopeId: routeScopeId,
    workflowId: resultWorkflowId,
  });
  const savedWorkflow: StudioWorkflowFile = materializedWorkflow
    ? {
        ...materializedWorkflow,
        document:
          cloneWorkflowDocument(materializedWorkflow.document) ?? savedDocument,
        layout: materializedWorkflow.layout ?? nextLayout,
      }
    : {
        ...workflow,
        workflowId: resultWorkflowId,
        name: normalizedTitle,
        yaml: serialized.yaml,
        layout: nextLayout,
        document: savedDocument,
        findings: [],
      };

  return {
    materializedWorkflow,
    result,
    savedDraft: {
      document: savedDocument,
      layout: nextLayout,
      title: normalizedTitle,
      workflow: savedWorkflow,
    },
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
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] =
    React.useState<number | null>(null);
  const [executionRunMessage, setExecutionRunMessage] = React.useState("");
  const [draftRunFiles, setDraftRunFiles] = React.useState<File[]>([]);
  const [pendingCreatedWorkflowMemberLink, setPendingCreatedWorkflowMemberLink] =
    React.useState<PendingCreatedWorkflowMemberLink | null>(null);
  const [publishBindingRun, setPublishBindingRun] =
    React.useState<StudioMemberBindingRunStatusResponse | null>(null);
  const [publishError, setPublishError] = React.useState("");
  const [publishErrorVisible, setPublishErrorVisible] = React.useState(true);
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
  const closeDraftRunPanel = React.useCallback(() => {
    setDraftRunPanelOpen(false);
  }, []);
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
  const updateMemberDisplayNameCache = React.useCallback(
    (
      scopeId: string,
      teamId: string,
      memberId: string,
      displayName: string,
    ) => {
      queryClient.setQueryData<StudioMemberDetail | undefined>(
        getTeamMemberWorkflowStudioMemberQueryKey(scopeId, memberId),
        (current) =>
          current
            ? {
                ...current,
                summary: {
                  ...current.summary,
                  displayName,
                },
              }
            : current,
      );
      const patchRoster = (current: unknown) => {
        if (
          !current ||
          typeof current !== "object" ||
          !Array.isArray((current as { members?: unknown }).members)
        ) {
          return current;
        }

        return {
          ...current,
          members: (current as { members: unknown[] }).members.map((member) =>
            member &&
            typeof member === "object" &&
            (member as { memberId?: unknown }).memberId === memberId
              ? {
                  ...member,
                  displayName,
                }
              : member,
          ),
        };
      };
      queryClient.setQueryData(
        ["teams", "team-members", scopeId, teamId],
        patchRoster,
      );
      queryClient.setQueryData(["teams", "members", scopeId], patchRoster);
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
    queryKey: getTeamMemberWorkflowStudioMemberQueryKey(
      route.scopeId,
      route.memberId,
    ),
    queryFn: () => studioApi.getMember(route.scopeId, route.memberId),
  });
  const routeDraftWorkflowId =
    route.mode === "existing" &&
    isWorkflowDraftRouteId(trimOptional(route.workflowId))
      ? trimOptional(route.workflowId)
      : "";
  const shouldLoadPublishedWorkflow =
    route.mode === "existing" && route.workflowSource === "published";
  const workflowQueryKey = getTeamMemberWorkflowStudioWorkflowQueryKey(
    route.scopeId,
    routeDraftWorkflowId,
    shouldLoadPublishedWorkflow ? "published" : "draft",
  );
  const workflowQuery = useQuery({
    enabled: Boolean(
      route.scopeId &&
        routeDraftWorkflowId &&
        !memberQuery.isLoading,
    ),
    queryKey: workflowQueryKey,
    queryFn: () =>
      shouldLoadPublishedWorkflow
        ? loadPublishedWorkflowWithDraftFallback({
            scopeId: route.scopeId,
            workflowId: routeDraftWorkflowId,
          })
        : studioApi.getWorkflow(routeDraftWorkflowId, route.scopeId),
    staleTime: SAVED_WORKFLOW_QUERY_STALE_MS,
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
  const automationsHref = buildTeamMemberAutomationsHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    teamId: route.teamId,
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
  const workflowDraftTitle =
    route.mode === "existing"
      ? trimOptional(workflowQuery.data?.name) ||
        trimOptional(loadedDocument?.name)
      : "";
  const routeFallbackTitle =
    route.mode === "new"
      ? "Untitled member"
      : workflowDraftTitle ||
        trimOptional(memberQuery.data?.summary.displayName) ||
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
            workflowQuery.data.name,
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
      workflowDraftTitle ||
      trimOptional(nextDocument.name) ||
      routeFallbackTitle;
    sourceKeyRef.current = sourceKey;
    setEditableDocument({
      ...nextDocument,
      name: nextTitle,
    });
    setEditableLayout(workflowQuery.data?.layout ?? null);
    setWorkflowTitleState(nextTitle);
    setSelectedEdgeId("");
    setSelectedNodeId("");
    closeDraftRunPanel();
    setYamlImportPanelOpen(false);
    setYamlImportError("");
    setYamlPanelOpen(false);
    setDirty(false);
  }, [
    routeFallbackTitle,
    sourceDocument,
    sourceKey,
    workflowDraftTitle,
    workflowQuery.data?.layout,
    closeDraftRunPanel,
  ]);

  const graph = React.useMemo(
    () => buildStudioGraphElements(editableDocument, editableLayout),
    [editableDocument, editableLayout],
  );
  const executionTrace = React.useMemo<ExecutionTrace | null>(
    () => buildExecutionTrace(executionDetail),
    [executionDetail],
  );
  React.useEffect(() => {
    if (!executionTrace?.logs.length) {
      setActiveExecutionLogIndex(null);
      return;
    }

    setActiveExecutionLogIndex((currentIndex) => {
      if (
        typeof currentIndex === "number" &&
        currentIndex >= 0 &&
        currentIndex < executionTrace.logs.length
      ) {
        return currentIndex;
      }

      return executionTrace.defaultLogIndex;
    });
  }, [executionTrace]);
  const graphWithExecution = React.useMemo(
    () => ({
      ...graph,
      edges: decorateEdgesForExecution(
        graph.edges,
        graph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
      nodes: decorateNodesForExecution(
        graph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    }),
    [activeExecutionLogIndex, executionTrace, graph],
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
  const cacheSavedWorkflowDraft = React.useCallback(
    (
      saved: SavedWorkflowDraft,
      sources: readonly ("draft" | "published")[] = ["draft"],
    ) => {
      const savedWorkflow = buildSavedWorkflowCacheValue(saved);
      const savedSignature = readWorkflowSourceSignature(savedWorkflow);
      const savedWorkflowId = trimOptional(savedWorkflow.workflowId);
      if (!savedSignature || !route.scopeId || !savedWorkflowId) {
        return;
      }

      suppressedSourceSignatureRef.current = savedSignature;
      for (const source of sources) {
        queryClient.setQueryData(
          getTeamMemberWorkflowStudioWorkflowQueryKey(
            route.scopeId,
            savedWorkflowId,
            source,
          ),
          savedWorkflow,
        );
      }
    },
    [queryClient, route.scopeId],
  );
  const invalidateSavedWorkflowDraft = React.useCallback(
    (saved: SavedWorkflowDraft) => {
      const savedWorkflowId = trimOptional(saved.workflow.workflowId);
      if (!route.scopeId || !savedWorkflowId) {
        return;
      }

      void queryClient.invalidateQueries({
        queryKey: getTeamMemberWorkflowStudioWorkflowQueryKey(
          route.scopeId,
          savedWorkflowId,
        ),
      });
    },
    [queryClient, route.scopeId],
  );
  const markSavedDraft = React.useCallback(
    (
      saved: SavedWorkflowDraft,
      sources?: readonly ("draft" | "published")[],
    ) => {
      cacheSavedWorkflowDraft(saved, sources);

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
      cacheSavedWorkflowDraft,
    ],
  );
  const renameExistingMemberFromTitle = React.useCallback(
    async (displayName: string) => {
      const scopeId = trimOptional(route.scopeId);
      const teamId = trimOptional(route.teamId);
      const memberId = trimOptional(route.memberId);
      const normalizedDisplayName = trimOptional(displayName);
      const currentDisplayName = trimOptional(
        memberQuery.data?.summary.displayName,
      );
      if (
        route.mode !== "existing" ||
        !scopeId ||
        !teamId ||
        !memberId ||
        !normalizedDisplayName ||
        normalizedDisplayName === currentDisplayName
      ) {
        return;
      }

      await studioApi.updateMemberDisplayName({
        scopeId,
        memberId,
        displayName: normalizedDisplayName,
      });
      updateMemberDisplayNameCache(
        scopeId,
        teamId,
        memberId,
        normalizedDisplayName,
      );
      await refreshTeamMemberSurfaces(scopeId, teamId);
    },
    [
      memberQuery.data?.summary.displayName,
      refreshTeamMemberSurfaces,
      route.memberId,
      route.mode,
      route.scopeId,
      route.teamId,
      updateMemberDisplayNameCache,
    ],
  );

  const saveMutation = useMutation({
    mutationFn: async (variables: SaveWorkflowDraftVariables) => {
      const saved = await saveWorkflowDraft({
        ...variables,
        routeScopeId: route.scopeId,
      });
      await renameExistingMemberFromTitle(saved.title);
      return saved;
    },
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
  const saveAndBindMutation = useMutation({
    mutationFn: async (variables: SaveWorkflowDraftVariables) => {
      if (!route.scopeId) {
        throw new Error("Resolve a Team workspace before saving this workflow.");
      }

      const savedAndBound = await saveAndBindPublishedWorkflowDraft({
        ...variables,
        routeScopeId: route.scopeId,
        serviceId: memberQuery.data?.summary.publishedServiceId,
      });
      await renameExistingMemberFromTitle(savedAndBound.savedDraft.title);
      return savedAndBound;
    },
    onError: (error) => {
      void message.error(
        error instanceof Error
          ? error.message
          : "Failed to save and publish workflow.",
      );
    },
    onSuccess: ({ materializedWorkflow, savedDraft }) => {
      markSavedDraft(savedDraft, ["published"]);
      void memberQuery.refetch();
      if (materializedWorkflow) {
        void message.success("Published workflow saved.");
      } else {
        void message.info(
          "Published workflow save was accepted. Studio is waiting for the workflow read model.",
        );
      }
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

      if (pendingCreatedWorkflowMemberLink) {
        const savedDraft = await saveWorkflowDraft({
          document,
          layout,
          routeScopeId: route.scopeId,
          title,
          workflow: pendingCreatedWorkflowMemberLink.savedDraft.workflow,
        });
        const savedWorkflowId = trimOptional(savedDraft.workflow.workflowId);
        if (!savedWorkflowId) {
          throw new Error("Workflow draft save did not return a stable workflow id.");
        }

        const currentLink: PendingCreatedWorkflowMemberLink = {
          memberId: pendingCreatedWorkflowMemberLink.memberId,
          savedDraft,
          workflowId: savedWorkflowId,
        };
        setPendingCreatedWorkflowMemberLink(currentLink);
        await linkCreatedWorkflowMemberDraft({
          scopeId: route.scopeId,
          memberId: currentLink.memberId,
          workflowId: currentLink.workflowId,
        });
        return currentLink;
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
      const createdLink: PendingCreatedWorkflowMemberLink = {
        memberId: createdMemberId,
        savedDraft,
        workflowId: savedWorkflowId,
      };
      setPendingCreatedWorkflowMemberLink(createdLink);
      await linkCreatedWorkflowMemberDraft({
        scopeId: route.scopeId,
        memberId: createdMemberId,
        workflowId: savedWorkflowId,
      });
      return createdLink;
    },
    onError: (error) => {
      void message.error(
        error instanceof Error
          ? error.message
          : "Failed to create workflow member.",
      );
    },
    onSuccess: ({ memberId, savedDraft, workflowId }) => {
      setPendingCreatedWorkflowMemberLink(null);
      cacheSavedWorkflowDraft(savedDraft);
      invalidateSavedWorkflowDraft(savedDraft);
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
      await renameExistingMemberFromTitle(normalizedTitle);

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
      cacheSavedWorkflowDraft(savedDraft);
      applySavedDraft(savedDraft);
      history.replace(
        buildTeamMemberWorkflowStudioHref({
          memberId: route.memberId,
          mode: "edit-member",
          scopeId: route.scopeId,
          teamId: route.teamId,
          workflowId,
        }),
      );
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
      closeDraftRunPanel();
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
      setNodeLibraryOpen(false);
      setDirty(true);
      void message.success("Workflow YAML imported.");
    },
  });
  const currentDraftRunMutation = useMutation({
    mutationFn: async ({
      document,
      files,
      runMessage,
      title,
    }: RunCurrentDraftVariables): Promise<StudioExecutionDetail> => {
      if (!route.scopeId) {
        throw new Error("Resolve the current workspace before running the draft.");
      }

      const normalizedTitle = trimOptional(title) || "Workflow draft";
      const userRunMessage = trimOptional(runMessage);
      const serialized = await studioApi.serializeYaml({
        document: {
          ...document,
          name: normalizedTitle,
        },
        availableStepTypes: AVAILABLE_STEP_TYPES,
      });
      const startedAtUtc = new Date().toISOString();
      const executionScopeKey = trimOptional(route.memberId) || "current-workflow";
      const executionId = `draft-run:${executionScopeKey}:${Date.now().toString(36)}`;
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
          runMessage: userRunMessage,
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
            prompt: userRunMessage,
            workflowYamls: [serialized.yaml],
            files: files && files.length > 0 ? files : undefined,
          },
          controller.signal,
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          frames.push(createStudioExecutionFrame(event));
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

      const currentMember = await studioApi.getMember(route.scopeId, route.memberId);
      queryClient.setQueryData(
        getTeamMemberWorkflowStudioMemberQueryKey(route.scopeId, route.memberId),
        currentMember,
      );
      if (!dirty && hasCurrentCompletedMemberBinding(currentMember)) {
        throw new PublishWorkflowStatusError(
          "This member workflow is already published. Refresh status to check readiness.",
          false,
        );
      }

      if (hasActiveMemberBindingRun(currentMember)) {
        throw new PublishWorkflowStatusError(
          "Publish is already in progress for this member. Refresh status before publishing again.",
          false,
        );
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
      await renameExistingMemberFromTitle(titleForPublish);
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
        if (attempt < MEMBER_BINDING_RUN_POLL_ATTEMPTS - 1) {
          await waitForBindingRunPollTick();
        }
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
      setPublishErrorVisible(
        !(error instanceof PublishWorkflowStatusError && !error.showAsError),
      );
      setPublishError(
        error instanceof Error ? error.message : "Failed to publish workflow member.",
      );
    },
    onMutate: () => {
      setPublishError("");
      setPublishErrorVisible(true);
      setPublishBindingRun(null);
    },
    onSuccess: ({ run, savedDraft }) => {
      if (savedDraft) {
        applySavedDraft(savedDraft);
      }
      setPublishBindingRun(run);
      void memberQuery.refetch();
      if (!shouldLoadPublishedWorkflow) {
        void workflowQuery.refetch();
      }
      if (run?.status === "succeeded") {
        void message.success("Workflow member published.");
      } else {
        void message.info(
          "Binding candidate was accepted for dispatch. Studio is waiting for the binding-run read model.",
        );
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
            !createWorkflowMemberMutation.isPending &&
            !saveAndBindMutation.isPending,
        )
      : Boolean(
          editableDocument &&
            dirty &&
            !selectedStepConfigurationError &&
            !saveMutation.isPending &&
            !saveAndBindMutation.isPending &&
            !createUnlinkedMemberDraftMutation.isPending &&
            ((workflowQuery.data && !linkedWorkflowMissing) ||
              canCreateUnlinkedMemberDraft),
        );
  const authoritativeMemberPublished = hasCurrentCompletedMemberBinding(
    memberQuery.data,
  );
  const authoritativeBindingRunId = readActiveMemberBindingRunId(
    memberQuery.data,
  );
  const publishBindingRunTerminal = isTerminalBindingRun(publishBindingRun);
  const publishBindingRunFailed = Boolean(
    publishBindingRun?.status === "failed" ||
      publishBindingRun?.status === "rejected",
  );
  const authoritativeBindingRunInProgress = Boolean(
    authoritativeBindingRunId &&
      (!publishBindingRunTerminal ||
        !isSameBindingRun(publishBindingRun, authoritativeBindingRunId)),
  );
  const memberPublishedByQuery = authoritativeMemberPublished;
  const memberIsPublished =
    memberPublishedByQuery ||
    publishBindingRun?.status === "succeeded";
  const publishStatusStillInProgress = Boolean(
    authoritativeBindingRunInProgress ||
      (publishBindingRun &&
        publishBindingRun.status !== "succeeded" &&
        !publishBindingRunTerminal),
  );
  const publishPending = publishMutation.isPending;
  const memberPublished = memberIsPublished;
  const memberPublishedServiceId = trimOptional(
    memberQuery.data?.summary.publishedServiceId,
  );
  const publishedRunsHref = buildTeamMemberPublishedRunsHref({
    memberId: route.memberId || undefined,
    scopeId: route.scopeId,
    teamId: route.teamId,
  });
  const canOpenPublishedRuns = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.teamId &&
      route.memberId &&
      memberIsPublished &&
      memberPublishedServiceId,
  );
  const publishedRunsPlaceholderReason = !route.memberId
    ? t(
        "teamMemberWorkflowStudio.header.publishedRuns.saveFirst",
        "Save this member before viewing published runs.",
      )
    : !memberIsPublished || !memberPublishedServiceId
      ? t(
          "teamMemberWorkflowStudio.header.publishedRuns.publishFirst",
          "Publish this member to start recording published runs.",
        )
      : t(
          "teamMemberWorkflowStudio.header.publishedRuns.open",
          "View runs from the published member service.",
        );
  const canOpenAutomations = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.teamId &&
      route.memberId &&
      memberQuery.data?.summary.lifecycleStage === "bind_ready" &&
      memberPublishedServiceId,
  );
  const automationsPlaceholderReason = !route.memberId
    ? t(
        "teamMemberWorkflowStudio.header.automations.saveFirst",
        "Save this member before adding recurring work.",
      )
    : !memberPublishedServiceId
      ? t(
          "teamMemberWorkflowStudio.header.automations.publishFirst",
          "Publish this member before adding recurring work.",
        )
      : t(
          "teamMemberWorkflowStudio.header.openAutomations",
          "Open recurring work for this member",
        );
  const publishVisibleError =
    publishError &&
    (publishErrorVisible ||
      publishBindingRunFailed ||
      (!memberIsPublished && !publishStatusStillInProgress))
      ? publishError
      : "";
  const publishDisabled = Boolean(
    route.mode === "new" ||
      linkedWorkflowMissing ||
      !workflowQuery.data ||
      !editableDocument ||
      !workflowHasSteps ||
      Boolean(selectedStepConfigurationError) ||
      publishMutation.isPending ||
      publishStatusStillInProgress ||
      memberIsPublished,
  );
  const publishPlaceholderReason =
    route.mode === "new"
      ? "Create and link a workflow member before publishing."
      : linkedWorkflowMissing
        ? "No stable workflow draft is linked to this member yet."
        : selectedStepConfigurationError
          ? selectedStepConfigurationError
        : publishPending
          ? "Binding candidate accepted for dispatch; waiting for binding-run status."
        : publishStatusStillInProgress
          ? "Binding run is still in progress. Use Refresh status before publishing again."
        : memberIsPublished
          ? "This member workflow is already published. Save changes to update the bound workflow."
        : !workflowQuery.data || !editableDocument
            ? "Load the workflow draft before publishing."
            : !workflowHasSteps
              ? "Add at least one step before publishing."
              : dirty
                ? "Publish saves draft changes, dispatches a candidate binding run, and observes the read model."
                : "Publish dispatches a candidate binding run for the saved workflow draft and observes the read model.";
  const publishTone: WorkflowPublishTone = publishError
    ? publishVisibleError
      ? "error"
      : publishStatusStillInProgress
        ? "processing"
        : memberIsPublished
          ? "success"
          : "default"
    : publishPending || publishStatusStillInProgress
      ? "processing"
      : memberIsPublished
        ? "success"
        : "default";
  const publishNotice =
    publishVisibleError ||
    (publishPending
      ? `Binding candidate dispatch accepted; run status: ${publishBindingRun?.status ?? "accepted"}.`
      : publishStatusStillInProgress
        ? `Binding run is still in progress (${memberQuery.data?.currentBindingRun?.status ?? publishBindingRun?.status ?? "accepted"}). Use Refresh status to check readiness.`
      : memberIsPublished
        ? "Published member workflow is serviceable."
        : "Draft member workflow is not published to the active member yet.");
  const showRefreshPublishStatus = Boolean(
    route.mode === "existing" &&
      route.scopeId &&
      route.memberId &&
      (publishStatusStillInProgress ||
        memberIsPublished ||
        publishError ||
        publishBindingRun),
  );
  const refreshPublishStatusPending = Boolean(
    memberQuery.isFetching || workflowQuery.isFetching,
  );
  const refreshPublishStatus = React.useCallback(async () => {
    if (route.mode !== "existing" || !route.scopeId || !route.memberId) {
      return;
    }

    const memberResult = await memberQuery.refetch();
    if (routeDraftWorkflowId) {
      await workflowQuery.refetch();
    }

    const refreshedMember = memberResult.data;
    const activeRunId = readActiveMemberBindingRunId(refreshedMember);
    if (activeRunId) {
      let refreshedRun: StudioMemberBindingRunStatusResponse | null = null;
      try {
        refreshedRun = await studioApi.getMemberBindingRun(
          route.scopeId,
          route.memberId,
          activeRunId,
        );
      } catch (error) {
        if (!isStudioApiStatus(error, 404)) {
          throw error;
        }

        refreshedRun = readFallbackBindingRun(refreshedMember, activeRunId);
      }

      if (!refreshedRun) {
        void message.info("Binding-run status is not materialized yet. Try refreshing again.");
        return;
      }

      setPublishBindingRun(refreshedRun);
      if (refreshedRun.status === "failed" || refreshedRun.status === "rejected") {
        setPublishError(readBindingRunFailureMessage(refreshedRun));
        void message.error(readBindingRunFailureMessage(refreshedRun));
        return;
      }

      if (refreshedRun.status === "succeeded") {
        void message.success("Published member status refreshed.");
        return;
      }

      void message.info("Binding run is still in progress.");
      return;
    }

    if (hasCurrentCompletedMemberBinding(refreshedMember)) {
      setPublishBindingRun((currentRun) =>
        currentRun && !isTerminalBindingRun(currentRun)
          ? {
              ...currentRun,
              status: "succeeded",
            }
          : currentRun,
      );
      void message.success("Published member status refreshed.");
      return;
    }

    void message.info("No published member status is visible yet.");
  }, [memberQuery, route.memberId, route.mode, route.scopeId, routeDraftWorkflowId, workflowQuery]);
  const executionStatus = currentDraftRunMutation.isPending
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
  const canRunCurrentDraft = Boolean(
    route.scopeId &&
      editableDocument &&
      workflowHasSteps &&
      !selectedStepConfigurationError &&
      !currentDraftRunMutation.isPending,
  );
  const canOpenDraftRunPanel = Boolean(
    route.scopeId &&
      editableDocument &&
      !selectedStepConfigurationError &&
      !workflowLoading,
  );
  const currentDraftRunPlaceholderReason =
    !editableDocument
      ? "Load the workflow draft before running it."
      : !route.scopeId
        ? "Resolve the current workspace before running the draft."
        : currentDraftRunMutation.isPending
          ? "Workflow draft run is already starting."
          : selectedStepConfigurationError
            ? selectedStepConfigurationError
          : !workflowHasSteps
            ? "Add at least one step before running this workflow draft."
            : linkedWorkflowMissing
              ? "Run the local draft sketch. Saving remains limited until a stable workflow draft is linked."
              : route.mode === "new"
                ? "Run the current unsaved workflow draft."
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
          : memberPublished
            ? "Save updates the workflow draft and published binding."
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
  const selectExecutionLog = React.useCallback(
    (index: number | null) => {
      if (index === null) {
        setActiveExecutionLogIndex(null);
        return;
      }

      setActiveExecutionLogIndex(
        index >= 0 && index < (executionTrace?.logs.length ?? 0)
          ? index
          : null,
      );
    },
    [executionTrace?.logs.length],
  );

  return {
    automationsHref,
    canOpenAutomations,
    automationsPlaceholderReason,
    canOpenPublishedRuns,
    publishedRunsHref,
    publishedRunsPlaceholderReason,
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
    refreshPublishStatus,
    refreshPublishStatusPending,
    showRefreshPublishStatus,
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
    navigateToPublishedRuns: () => {
      if (canOpenPublishedRuns && (!dirty || confirmDiscardUnsavedChanges())) {
        history.push(publishedRunsHref);
      }
    },
    navigateToAutomations: () => {
      if (canOpenAutomations && (!dirty || confirmDiscardUnsavedChanges())) {
        history.push(automationsHref);
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
    canRunCurrentDraft,
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
    runCurrentDraft: () => {
      if (
        !route.scopeId ||
        !editableDocument ||
        !workflowHasSteps ||
        selectedStepConfigurationError ||
        currentDraftRunMutation.isPending
      ) {
        return;
      }

      const emptyFile = draftRunFiles.find((file) => file.size <= 0);
      if (emptyFile) {
        const errorMessage = t(
          "teamMemberWorkflowStudio.draftRunPanel.removeEmptyFile",
          "Remove empty file {name} before starting the draft run.",
          {
            name:
              emptyFile.name ||
              t("teamMemberWorkflowStudio.draftRunPanel.thisFile", "this file"),
          },
        );
        setExecutionError(errorMessage);
        void message.error(errorMessage);
        return;
      }

      currentDraftRunMutation.mutate({
        document: editableDocument,
        files: draftRunFiles,
        runMessage: trimOptional(executionRunMessage),
        title: activeMemberTitle,
      });
    },
    currentDraftRunPending: currentDraftRunMutation.isPending,
    currentDraftRunPlaceholderReason,
    executionDetail,
    executionError,
    draftRunFiles,
    executionRunMessage,
    executionStatus,
    activeExecutionLogIndex,
    clearExecutionLogs: () => {
      setExecutionDetail(null);
      setExecutionError("");
      setActiveExecutionLogIndex(null);
    },
    addDraftRunFiles: (files: readonly File[]) => {
      if (files.length > 0) {
        setDraftRunFiles((current) => [...current, ...files]);
      }
    },
    removeDraftRunFile: (index: number) => {
      setDraftRunFiles((current) =>
        current.filter((_, itemIndex) => itemIndex !== index),
      );
    },
    graph: graphWithExecution,
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
      if (!canOpenDraftRunPanel) {
        return;
      }

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
      closeDraftRunPanel();
      setYamlPanelOpen(false);
      setYamlImportError("");
      setYamlImportPanelOpen(true);
    },
    openYamlPanel: () => {
      setSelectedEdgeId("");
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      closeDraftRunPanel();
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
        const mutation = memberPublished ? saveAndBindMutation : saveMutation;

        mutation.mutate({
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
      saveAndBindMutation.isPending ||
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
      setActiveExecutionLogIndex(null);
      closeDraftRunPanel();
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
    },
    selectEdge: (edgeId: string) => {
      setSelectedEdgeId(edgeId);
      setSelectedNodeId("");
      setSelectedStepConfigurationError("");
      setActiveExecutionLogIndex(null);
      closeDraftRunPanel();
      setYamlImportPanelOpen(false);
      setYamlImportError("");
      setYamlPanelOpen(false);
    },
    selectExecutionLog,
    selectNode: (nodeId: string) => {
      setSelectedEdgeId("");
      setSelectedNodeId(nodeId);
      setSelectedStepConfigurationError("");
      const selectedStepId = readStepIdFromGraphNodeId(nodeId);
      setActiveExecutionLogIndex(
        selectedStepId
          ? findExecutionLogIndexForStep(executionTrace, selectedStepId)
          : null,
      );
      closeDraftRunPanel();
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
