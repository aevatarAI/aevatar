import { AGUIEventType } from '@aevatar-react-sdk/types';
import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { t } from '@/shared/i18n/messages';
import { getLocationSnapshot, history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import {
  applyStepInspectorDraft,
  createStepInspectorDraft,
  insertStepByType,
} from '@/shared/studio/document';
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
} from '@/shared/studio/graph';
import type {
  StudioValidationFinding,
  StudioWorkflowDocument,
  StudioWorkflowFile,
} from '@/shared/studio/models';
import { buildWorkflowActivityEditorHref } from '../navigation';
import { hasBlockingFindings } from '../workflows/workflowCreation';
import { useDraftMaterialization } from './useDraftMaterialization';
import { useRunObservation } from './useRunObservation';

export type PublishedRunPhase =
  | 'idle'
  | 'submitting'
  | 'accepted'
  | 'stream_ended'
  | 'failed';

type SubmittedSaveSnapshot = {
  readonly document: StudioWorkflowDocument | null;
  readonly revision: number;
};

export type WorkflowPublishedInvocationTarget = {
  readonly publishedServiceId: string;
  readonly revisionId: string;
  readonly workflowId: string;
};

export type PublishedRunSnapshot = {
  readonly input: string;
  readonly target: WorkflowPublishedInvocationTarget;
};

export type WorkflowPublicationPreparation = {
  readonly documentVersion: number;
  readonly workflowId: string;
  readonly workflowName: string;
  readonly workflowYaml: string;
};

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function workflowSignature(workflow: StudioWorkflowFile): string {
  return `${workflow.workflowId}\u0000${workflow.updatedAtUtc}\u0000${workflow.yaml}`;
}

function currentLocationSuffix(): string {
  try {
    const location = new URL(
      getLocationSnapshot() || '/',
      'http://console.local',
    );
    return `${location.search}${location.hash}`;
  } catch {
    return '';
  }
}

function readSseRunId(event: unknown): string {
  if (!event || typeof event !== 'object') return '';
  const record = event as Record<string, unknown>;
  if (
    record.type !== AGUIEventType.RUN_STARTED &&
    record.type !== AGUIEventType.RUN_FINISHED &&
    record.type !== AGUIEventType.RUN_ERROR
  )
    return '';
  return typeof record.runId === 'string' ? record.runId.trim() : '';
}

function isSseRunError(event: unknown): event is Record<string, unknown> {
  if (!event || typeof event !== 'object') return false;
  const record = event as Record<string, unknown>;
  return record.type === AGUIEventType.RUN_ERROR;
}

function readSseRunError(event: Record<string, unknown>): string {
  return typeof event.message === 'string' ? event.message.trim() : '';
}

function readRunInputError(error: unknown): string {
  if (!error || typeof error !== 'object' || !('fieldErrors' in error))
    return '';
  const fieldErrors = error.fieldErrors;
  if (!fieldErrors || typeof fieldErrors !== 'object') return '';
  for (const [field, messages] of Object.entries(fieldErrors)) {
    const normalizedField = field.replace(/[^a-z]/gi, '').toLowerCase();
    if (normalizedField !== 'prompt' && normalizedField !== 'input') continue;
    if (Array.isArray(messages)) {
      const message = messages.find(
        (entry): entry is string =>
          typeof entry === 'string' && Boolean(entry.trim()),
      );
      if (message) return message.trim();
    }
    if (typeof messages === 'string' && messages.trim()) return messages.trim();
  }
  return '';
}

export function useWorkflowEditor(scopeId: string, routeWorkflowId: string) {
  const source = useQuery({
    queryKey: ['workflow-activity-vnext', 'workflow', scopeId, routeWorkflowId],
    queryFn: () => studioApi.getWorkflow(routeWorkflowId, scopeId),
    retry: false,
  });
  const workspace = useQuery({
    queryKey: ['workflow-activity-vnext', 'workspace', scopeId],
    queryFn: () => studioApi.getWorkspaceSettings(scopeId),
    retry: false,
  });
  const materialization = useDraftMaterialization(scopeId);
  const [workflow, setWorkflow] = React.useState<StudioWorkflowFile | null>(
    null,
  );
  const [workflowTitle, setWorkflowTitle] = React.useState('');
  const [yaml, setYaml] = React.useState('');
  const [document, setDocument] = React.useState<StudioWorkflowDocument | null>(
    null,
  );
  const [layout, setLayout] = React.useState<unknown>(null);
  const [findings, setFindings] = React.useState<
    readonly StudioValidationFinding[]
  >([]);
  const [dirty, setDirty] = React.useState(false);
  const [saving, setSaving] = React.useState(false);
  const [validating, setValidating] = React.useState(false);
  const [structuralMutationPending, setStructuralMutationPending] =
    React.useState(false);
  const [structuralMutationError, setStructuralMutationError] =
    React.useState('');
  const [failedNodeType, setFailedNodeType] = React.useState<string | null>(
    null,
  );
  const [saveError, setSaveError] = React.useState('');
  const [runInput, setRunInput] = React.useState('');
  const [runInputError, setRunInputError] = React.useState('');
  const [lastRunSnapshot, setLastRunSnapshot] =
    React.useState<PublishedRunSnapshot | null>(null);
  const [runPhase, setRunPhase] = React.useState<PublishedRunPhase>('idle');
  const [runError, setRunError] = React.useState('');
  const [sseRunId, setSseRunId] = React.useState('');
  const [selectedNodeId, setSelectedNodeId] = React.useState('');
  const [selectedStepConfigurationError, setSelectedStepConfigurationError] =
    React.useState('');
  const runControllerRef = React.useRef<AbortController | null>(null);
  const runInFlightRef = React.useRef(false);
  const runGenerationRef = React.useRef(0);
  const sseRunIdRef = React.useRef('');
  const savingRef = React.useRef(false);
  const configurationGenerationRef = React.useRef(0);
  const loadedSignatureRef = React.useRef('');
  const loadedRouteWorkflowIdRef = React.useRef('');
  const pendingRouteWorkflowIdRef = React.useRef<string | null>(null);
  const structuralMutationGenerationRef = React.useRef(0);
  const structuralMutationPendingRef = React.useRef(false);
  const dirtyRef = React.useRef(false);
  const localEditRevisionRef = React.useRef(0);
  const pendingMaterializationRef = React.useRef<SubmittedSaveSnapshot | null>(
    null,
  );
  const runObservation = useRunObservation(scopeId, sseRunId);
  const runObservationUnresolved = Boolean(
    sseRunId && runObservation.phase !== 'observed',
  );
  const runAwaitingIdentification = runPhase === 'stream_ended' && !sseRunId;
  const receiptPending =
    materialization.phase === 'accepted' ||
    materialization.phase === 'observing' ||
    materialization.phase === 'delayed' ||
    materialization.phase === 'failed';

  const markLocalEdit = React.useCallback(() => {
    dirtyRef.current = true;
    localEditRevisionRef.current += 1;
    setDirty(true);
  }, []);

  const markClean = React.useCallback(() => {
    dirtyRef.current = false;
    setDirty(false);
  }, []);

  React.useEffect(() => {
    if (!source.data) return;
    if (
      pendingRouteWorkflowIdRef.current &&
      pendingRouteWorkflowIdRef.current !== routeWorkflowId
    )
      return;
    if (
      pendingRouteWorkflowIdRef.current === routeWorkflowId &&
      source.data.workflowId !== routeWorkflowId
    )
      return;
    const signature = workflowSignature(source.data);
    const enteredDifferentRoute =
      loadedRouteWorkflowIdRef.current !== routeWorkflowId;
    if (
      loadedSignatureRef.current === signature ||
      (dirtyRef.current && !enteredDifferentRoute)
    )
      return;
    loadedSignatureRef.current = signature;
    loadedRouteWorkflowIdRef.current = routeWorkflowId;
    setWorkflow(source.data);
    setWorkflowTitle(source.data.name);
    setYaml(source.data.yaml);
    setDocument(source.data.document ?? null);
    setLayout(source.data.layout ?? null);
    setFindings(source.data.findings);
    markClean();
    setSaveError('');
    setStructuralMutationError('');
    setFailedNodeType(null);
    setSelectedNodeId('');
    setSelectedStepConfigurationError('');
    if (source.data.document) return;

    let cancelled = false;
    void studioApi
      .parseYaml({ yaml: source.data.yaml })
      .then((parsed) => {
        if (cancelled || loadedSignatureRef.current !== signature) return;
        setDocument(parsed.document ?? null);
        setFindings(parsed.findings);
      })
      .catch(() => {
        if (cancelled || loadedSignatureRef.current !== signature) return;
        setFindings([
          ...source.data.findings,
          {
            code: 'WORKFLOW_YAML_PARSE_FAILED',
            level: 'error',
            message: t(
              'workflowActivityVNext.editor.yamlReadFailed',
              'Workflow YAML could not be read.',
            ),
          },
        ]);
      });

    return () => {
      cancelled = true;
    };
  }, [markClean, routeWorkflowId, source.data]);

  React.useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (!dirty) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  React.useEffect(
    () => () => {
      runGenerationRef.current += 1;
      runControllerRef.current?.abort();
      runControllerRef.current = null;
      runInFlightRef.current = false;
    },
    [],
  );

  const updateYaml = (next: string) => {
    if (savingRef.current || structuralMutationPendingRef.current) return;
    setYaml(next);
    markLocalEdit();
  };

  const updateTitle = (next: string) => {
    if (savingRef.current || structuralMutationPendingRef.current) return;
    setWorkflowTitle(next);
    markLocalEdit();
  };

  const parseCurrentYaml = React.useCallback(
    async (shouldApply: () => boolean = () => true) => {
      const parsed = await studioApi.parseYaml({ yaml });
      if (!shouldApply()) return null;
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return null;
      setDocument(parsed.document ?? null);
      return parsed.document ?? null;
    },
    [yaml],
  );

  const adoptReadableWorkflow = React.useCallback(
    (
      saved: StudioWorkflowFile,
      fallbackDocument: StudioWorkflowDocument | null,
      submittedRevision?: number,
    ) => {
      const preserveLocalEdits =
        submittedRevision !== undefined &&
        localEditRevisionRef.current !== submittedRevision;
      loadedSignatureRef.current = workflowSignature(saved);
      loadedRouteWorkflowIdRef.current = saved.workflowId;
      pendingRouteWorkflowIdRef.current = saved.workflowId;
      setWorkflow(saved);
      setSaveError('');
      if (preserveLocalEdits) {
        dirtyRef.current = true;
        setDirty(true);
      } else {
        setWorkflowTitle(saved.name);
        setYaml(saved.yaml);
        setDocument(saved.document ?? fallbackDocument);
        setLayout(saved.layout ?? layout);
        setFindings(saved.findings);
        markClean();
        setSelectedStepConfigurationError('');
      }
      if (saved.workflowId !== routeWorkflowId) {
        history.replace(
          `${buildWorkflowActivityEditorHref(scopeId, saved.workflowId)}${currentLocationSuffix()}`,
        );
      }
    },
    [layout, markClean, routeWorkflowId, scopeId],
  );

  const discardForRouteChange = React.useCallback(
    (nextWorkflowId: string) => {
      configurationGenerationRef.current += 1;
      structuralMutationGenerationRef.current += 1;
      structuralMutationPendingRef.current = false;
      runGenerationRef.current += 1;
      runControllerRef.current?.abort();
      runControllerRef.current = null;
      runInFlightRef.current = false;
      pendingMaterializationRef.current = null;
      loadedSignatureRef.current = '';
      loadedRouteWorkflowIdRef.current = '';
      pendingRouteWorkflowIdRef.current = nextWorkflowId;
      dirtyRef.current = false;
      localEditRevisionRef.current = 0;
      materialization.reset();
      setWorkflow(null);
      setWorkflowTitle('');
      setYaml('');
      setDocument(null);
      setLayout(null);
      setFindings([]);
      setDirty(false);
      setSaving(false);
      setValidating(false);
      setStructuralMutationPending(false);
      setStructuralMutationError('');
      setFailedNodeType(null);
      setSaveError('');
      setRunInput('');
      setRunInputError('');
      setLastRunSnapshot(null);
      setRunPhase('idle');
      setRunError('');
      sseRunIdRef.current = '';
      setSseRunId('');
      setSelectedNodeId('');
      setSelectedStepConfigurationError('');
    },
    [materialization.reset],
  );

  const retryMaterialization = React.useCallback(async () => {
    const submittedSnapshot = pendingMaterializationRef.current;
    const saved = await materialization.retry();
    if (!saved) return false;
    pendingMaterializationRef.current = null;
    adoptReadableWorkflow(
      saved,
      submittedSnapshot?.document ?? document,
      submittedSnapshot?.revision,
    );
    return true;
  }, [adoptReadableWorkflow, document, materialization.retry]);

  const save = React.useCallback(async () => {
    const followsCanonicalRouteReplacement =
      pendingRouteWorkflowIdRef.current === workflow?.workflowId;
    if (
      !workflow ||
      (workflow.workflowId !== routeWorkflowId &&
        !followsCanonicalRouteReplacement) ||
      savingRef.current ||
      receiptPending ||
      structuralMutationPendingRef.current ||
      !workflowTitle.trim()
    )
      return false;
    savingRef.current = true;
    setSaving(true);
    setValidating(true);
    setSaveError('');
    try {
      const parsedDocument = await parseCurrentYaml();
      if (!parsedDocument) return false;
      const serialized = await studioApi.serializeYaml({
        document: parsedDocument,
      });
      setFindings(serialized.findings);
      if (hasBlockingFindings(serialized.document, serialized.findings))
        return false;
      setValidating(false);
      const directoryId =
        workflow.directoryId ||
        workspace.data?.directories[0]?.directoryId ||
        '';
      if (!directoryId)
        throw new Error(
          'No server workflow directory is available for saving this draft.',
        );
      const graph = buildStudioGraphElements(serialized.document, layout);
      const result = await studioApi.saveWorkflow({
        directoryId,
        draftExists: workflow.draftExists,
        fileName: workflow.fileName,
        layout: buildStudioWorkflowLayout(workflowTitle, graph.nodes, layout),
        scopeId,
        workflowId: workflow.workflowId,
        workflowName: workflowTitle,
        yaml: serialized.yaml,
      });
      const submittedSnapshot: SubmittedSaveSnapshot = {
        document: serialized.document ?? null,
        revision: localEditRevisionRef.current,
      };
      if (result.kind === 'materialized') {
        adoptReadableWorkflow(
          result.workflow,
          submittedSnapshot.document,
          submittedSnapshot.revision,
        );
        return true;
      }
      pendingMaterializationRef.current = submittedSnapshot;
      savingRef.current = false;
      setSaving(false);
      const saved = await materialization.observe(result.receipt);
      if (!saved) return false;
      pendingMaterializationRef.current = null;
      adoptReadableWorkflow(
        saved,
        submittedSnapshot.document,
        submittedSnapshot.revision,
      );
      return true;
    } catch (error) {
      setSaveError(toErrorMessage(error));
      return false;
    } finally {
      savingRef.current = false;
      setSaving(false);
      setValidating(false);
    }
  }, [
    adoptReadableWorkflow,
    materialization.observe,
    layout,
    parseCurrentYaml,
    receiptPending,
    routeWorkflowId,
    saving,
    scopeId,
    workflow,
    workflowTitle,
    workspace.data,
  ]);

  const addNode = React.useCallback(
    async (stepType: string): Promise<boolean> => {
      if (savingRef.current || structuralMutationPendingRef.current)
        return false;
      const generation = ++structuralMutationGenerationRef.current;
      structuralMutationPendingRef.current = true;
      setStructuralMutationPending(true);
      setStructuralMutationError('');
      setFailedNodeType(null);
      try {
        const current = document ?? (await parseCurrentYaml());
        if (!current || generation !== structuralMutationGenerationRef.current)
          return false;
        const inserted = insertStepByType(current, stepType);
        const serialized = await studioApi.serializeYaml({
          document: inserted.document,
        });
        if (generation !== structuralMutationGenerationRef.current)
          return false;
        setDocument(serialized.document);
        setYaml(serialized.yaml);
        setFindings(serialized.findings);
        setSelectedNodeId(inserted.nodeId);
        markLocalEdit();
        return true;
      } catch (error) {
        if (generation === structuralMutationGenerationRef.current) {
          setStructuralMutationError(toErrorMessage(error));
          setFailedNodeType(stepType);
        }
        return false;
      } finally {
        if (generation === structuralMutationGenerationRef.current) {
          structuralMutationPendingRef.current = false;
          setStructuralMutationPending(false);
        }
      }
    },
    [document, markLocalEdit, parseCurrentYaml],
  );

  const retryNodeInsertion = React.useCallback(
    () => (failedNodeType ? addNode(failedNodeType) : Promise.resolve(false)),
    [addNode, failedNodeType],
  );

  const graph = React.useMemo(
    () => buildStudioGraphElements(document, layout),
    [document, layout],
  );
  const selectedStepDraft = React.useMemo(() => {
    const selectedStepId = selectedNodeId.startsWith('step:')
      ? selectedNodeId.slice('step:'.length).trim()
      : selectedNodeId.trim();
    const selectedStep = graph.steps.find((step) => step.id === selectedStepId);
    return selectedStep ? createStepInspectorDraft(selectedStep) : null;
  }, [graph.steps, selectedNodeId]);

  const updateSelectedStepConfiguration = React.useCallback(
    async (parametersText: string): Promise<boolean> => {
      if (
        savingRef.current ||
        structuralMutationPendingRef.current ||
        !document ||
        !selectedStepDraft
      )
        return false;
      const generation = ++configurationGenerationRef.current;
      try {
        const updated = applyStepInspectorDraft(
          document,
          selectedStepDraft.id,
          { ...selectedStepDraft, parametersText },
        );
        const serialized = await studioApi.serializeYaml({
          document: updated.document,
        });
        if (generation !== configurationGenerationRef.current) return false;
        setDocument(serialized.document);
        setYaml(serialized.yaml);
        setFindings(serialized.findings);
        setSelectedNodeId(updated.nodeId);
        setSelectedStepConfigurationError('');
        markLocalEdit();
        return true;
      } catch (error) {
        if (generation === configurationGenerationRef.current) {
          setSelectedStepConfigurationError(toErrorMessage(error));
        }
        return false;
      }
    },
    [document, markLocalEdit, selectedStepDraft],
  );

  const canRun = Boolean(
    document?.steps?.length &&
      !hasBlockingFindings(document, findings) &&
      !selectedStepConfigurationError &&
      !structuralMutationPending,
  );

  const preparePublication =
    React.useCallback(async (): Promise<WorkflowPublicationPreparation> => {
      const followsCanonicalRouteReplacement =
        pendingRouteWorkflowIdRef.current === workflow?.workflowId;
      if (
        !workflow ||
        !workflow.draftExists ||
        (workflow.workflowId !== routeWorkflowId &&
          !followsCanonicalRouteReplacement) ||
        dirty ||
        savingRef.current ||
        receiptPending ||
        structuralMutationPendingRef.current ||
        !workflowTitle.trim()
      ) {
        throw new Error(
          'This workflow must be saved before it can be published.',
        );
      }

      const parsed = await studioApi.parseYaml({ yaml });
      setFindings(parsed.findings);
      const parsedDocument = parsed.document ?? null;
      if (
        !parsedDocument ||
        hasBlockingFindings(parsedDocument, parsed.findings)
      ) {
        throw new Error(
          'This workflow needs valid executable steps before publishing.',
        );
      }

      const serialized = await studioApi.serializeYaml({
        document: parsedDocument,
      });
      setFindings(serialized.findings);
      if (hasBlockingFindings(serialized.document, serialized.findings)) {
        throw new Error(
          'This workflow needs valid executable steps before publishing.',
        );
      }

      return {
        documentVersion: localEditRevisionRef.current,
        workflowId: workflow.workflowId,
        workflowName: workflowTitle.trim(),
        workflowYaml: serialized.yaml,
      };
    }, [dirty, receiptPending, routeWorkflowId, workflow, workflowTitle, yaml]);

  const submitRun = React.useCallback(
    async (
      target: WorkflowPublishedInvocationTarget,
      snapshot?: PublishedRunSnapshot,
    ) => {
      if (
        runInFlightRef.current ||
        savingRef.current ||
        structuralMutationPendingRef.current ||
        runPhase === 'submitting' ||
        runPhase === 'accepted' ||
        runObservationUnresolved ||
        runAwaitingIdentification
      )
        return;
      const activeWorkflowId = workflow?.workflowId ?? routeWorkflowId;
      if (
        !target.publishedServiceId.trim() ||
        !target.revisionId.trim() ||
        !target.workflowId.trim() ||
        target.workflowId !== activeWorkflowId
      ) {
        setRunError(
          t(
            'workflowActivityVNext.editor.publishedTargetUnavailable',
            'Publish this workflow before running it.',
          ),
        );
        setRunPhase('failed');
        return;
      }
      const normalizedInput = (snapshot?.input ?? runInput).trim();
      if (!normalizedInput) {
        setRunInputError(
          t(
            'workflowActivityVNext.editor.runInputRequired',
            'Input is required.',
          ),
        );
        return;
      }
      const generation = ++runGenerationRef.current;
      const controller = new AbortController();
      runControllerRef.current?.abort();
      runControllerRef.current = controller;
      runInFlightRef.current = true;
      setRunPhase('submitting');
      setRunError('');
      setRunInputError('');
      sseRunIdRef.current = '';
      setSseRunId('');
      if (snapshot) setRunInput(snapshot.input);
      const ownsRun = () =>
        runGenerationRef.current === generation &&
        runControllerRef.current === controller &&
        !controller.signal.aborted;
      try {
        const submittedSnapshot: PublishedRunSnapshot = {
          input: normalizedInput,
          target,
        };
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          { prompt: submittedSnapshot.input },
          controller.signal,
          { serviceId: target.publishedServiceId },
        );
        if (!ownsRun()) return;
        setLastRunSnapshot(submittedSnapshot);
        setRunPhase('accepted');
        let sawRunError = false;
        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          if (!ownsRun()) return;
          const reportedRunId = readSseRunId(event);
          if (reportedRunId && !sseRunIdRef.current) {
            sseRunIdRef.current = reportedRunId;
            setSseRunId(reportedRunId);
          }
          if (isSseRunError(event)) {
            sawRunError = true;
            const streamedRunError = readSseRunError(event);
            if (streamedRunError) setRunError(streamedRunError);
            setRunPhase('failed');
          }
        }
        if (ownsRun() && !sawRunError) setRunPhase('stream_ended');
      } catch (error) {
        if (ownsRun()) {
          setRunInputError(readRunInputError(error));
          setRunError(toErrorMessage(error));
          setRunPhase('failed');
        }
      } finally {
        if (
          runGenerationRef.current === generation &&
          runControllerRef.current === controller
        ) {
          runInFlightRef.current = false;
          runControllerRef.current = null;
        }
      }
    },
    [
      routeWorkflowId,
      runInput,
      runObservationUnresolved,
      runPhase,
      scopeId,
      workflow?.workflowId,
    ],
  );

  const run = React.useCallback(
    (target: WorkflowPublishedInvocationTarget) => submitRun(target),
    [submitRun],
  );
  const runAgain = React.useCallback(
    (target: WorkflowPublishedInvocationTarget) =>
      lastRunSnapshot
        ? submitRun(target, { ...lastRunSnapshot, target })
        : Promise.resolve(),
    [lastRunSnapshot, submitRun],
  );

  return {
    addNode,
    canRun,
    documentVersion: localEditRevisionRef.current,
    discardForRouteChange,
    dirty,
    document,
    findings,
    graph,
    loading: source.isPending,
    loadError: source.error,
    materialization,
    nodeInsertionError: structuralMutationError,
    preparePublication,
    receiptPending,
    retryMaterialization,
    retryNodeInsertion,
    run,
    runError,
    runInput,
    runInputError,
    lastRunSnapshot,
    runObservation,
    runObservationUnresolved,
    runAwaitingIdentification,
    runPhase,
    runAgain,
    save,
    saveError,
    saving,
    validating,
    structuralMutationPending,
    sseRunId,
    selectedNodeId,
    selectedStepConfigurationError,
    selectedStepDraft,
    selectCanvas: () => {
      setSelectedNodeId('');
      setSelectedStepConfigurationError('');
    },
    selectNode: (nodeId: string) => {
      setSelectedNodeId(nodeId);
      setSelectedStepConfigurationError('');
    },
    setRunInput: (next: string) => {
      setRunInput(next);
      setRunInputError('');
    },
    setSelectedStepConfigurationError,
    updateSelectedStepConfiguration,
    updateTitle,
    updateYaml,
    workflow,
    workflowTitle,
    yaml,
  } as const;
}
