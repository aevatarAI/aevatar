import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { history } from '@/shared/navigation/history';
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

export type DraftRunPhase =
  | 'idle'
  | 'submitting'
  | 'accepted'
  | 'stream_ended'
  | 'failed';

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
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
  const [saveError, setSaveError] = React.useState('');
  const [runInput, setRunInput] = React.useState('');
  const [runPhase, setRunPhase] = React.useState<DraftRunPhase>('idle');
  const [runError, setRunError] = React.useState('');
  const [runEventCount, setRunEventCount] = React.useState(0);
  const [selectedNodeId, setSelectedNodeId] = React.useState('');
  const [selectedStepConfigurationError, setSelectedStepConfigurationError] =
    React.useState('');
  const runControllerRef = React.useRef<AbortController | null>(null);
  const configurationGenerationRef = React.useRef(0);
  const loadedSignatureRef = React.useRef('');

  React.useEffect(() => {
    if (!source.data) return;
    const signature = `${source.data.workflowId}\u0000${source.data.updatedAtUtc}\u0000${source.data.yaml}`;
    if (loadedSignatureRef.current === signature) return;
    loadedSignatureRef.current = signature;
    setWorkflow(source.data);
    setWorkflowTitle(source.data.name);
    setYaml(source.data.yaml);
    setDocument(source.data.document ?? null);
    setLayout(source.data.layout ?? null);
    setFindings(source.data.findings);
    setDirty(false);
    setSaveError('');
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
      .catch((error) => {
        if (cancelled || loadedSignatureRef.current !== signature) return;
        setFindings([
          {
            code: 'WORKFLOW_YAML_PARSE_FAILED',
            level: 'error',
            message: toErrorMessage(error),
          },
        ]);
      });

    return () => {
      cancelled = true;
    };
  }, [source.data]);

  React.useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (!dirty) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  React.useEffect(() => () => runControllerRef.current?.abort(), []);

  const updateYaml = (next: string) => {
    setYaml(next);
    setDirty(true);
  };

  const updateTitle = (next: string) => {
    setWorkflowTitle(next);
    setDirty(true);
  };

  const parseCurrentYaml = React.useCallback(async () => {
    const parsed = await studioApi.parseYaml({ yaml });
    setFindings(parsed.findings);
    if (hasBlockingFindings(parsed.document, parsed.findings)) return null;
    setDocument(parsed.document ?? null);
    return parsed.document ?? null;
  }, [yaml]);

  const save = React.useCallback(async () => {
    if (!workflow || saving || !workflowTitle.trim()) return false;
    setSaving(true);
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
        workflowId: routeWorkflowId,
        workflowName: workflowTitle,
        yaml: serialized.yaml,
      });
      let saved: StudioWorkflowFile | null = null;
      if (result.kind === 'materialized') saved = result.workflow;
      else saved = await materialization.observe(result.receipt);
      if (!saved) return false;
      setWorkflow(saved);
      setYaml(saved.yaml);
      setDocument(saved.document ?? serialized.document);
      setLayout(saved.layout ?? layout);
      setFindings(saved.findings);
      setDirty(false);
      if (saved.workflowId !== routeWorkflowId) {
        history.replace(
          buildWorkflowActivityEditorHref(scopeId, saved.workflowId),
        );
      }
      return true;
    } catch (error) {
      setSaveError(toErrorMessage(error));
      return false;
    } finally {
      setSaving(false);
    }
  }, [
    layout,
    materialization.observe,
    parseCurrentYaml,
    routeWorkflowId,
    saving,
    scopeId,
    workflow,
    workflowTitle,
    workspace.data,
  ]);

  const addNode = React.useCallback(
    async (stepType: string) => {
      const current = document ?? (await parseCurrentYaml());
      if (!current) return;
      const inserted = insertStepByType(current, stepType);
      const serialized = await studioApi.serializeYaml({
        document: inserted.document,
      });
      setDocument(serialized.document);
      setYaml(serialized.yaml);
      setFindings(serialized.findings);
      setSelectedNodeId(inserted.nodeId);
      setDirty(true);
    },
    [document, parseCurrentYaml],
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
    async (parametersText: string) => {
      if (!document || !selectedStepDraft) return;
      const generation = ++configurationGenerationRef.current;
      try {
        const updated = applyStepInspectorDraft(
          document,
          selectedStepDraft.id,
          { ...selectedStepDraft, parametersText },
        );
        setDocument(updated.document);
        setSelectedNodeId(updated.nodeId);
        setSelectedStepConfigurationError('');
        setDirty(true);
        const serialized = await studioApi.serializeYaml({
          document: updated.document,
        });
        if (generation !== configurationGenerationRef.current) return;
        setDocument(serialized.document);
        setYaml(serialized.yaml);
        setFindings(serialized.findings);
      } catch (error) {
        if (generation === configurationGenerationRef.current) {
          setSelectedStepConfigurationError(toErrorMessage(error));
        }
      }
    },
    [document, selectedStepDraft],
  );

  const canRun = Boolean(
    document?.steps?.length &&
      !hasBlockingFindings(document, findings) &&
      !selectedStepConfigurationError,
  );

  const run = React.useCallback(async () => {
    if (runPhase === 'submitting' || runPhase === 'accepted') return;
    setRunPhase('submitting');
    setRunError('');
    setRunEventCount(0);
    const controller = new AbortController();
    runControllerRef.current?.abort();
    runControllerRef.current = controller;
    try {
      const current = await parseCurrentYaml();
      if (!current) {
        setRunPhase('failed');
        return;
      }
      const serialized = await studioApi.serializeYaml({ document: current });
      setFindings(serialized.findings);
      if (hasBlockingFindings(serialized.document, serialized.findings)) {
        setRunPhase('failed');
        return;
      }
      const response = await runtimeRunsApi.streamDraftRun(
        scopeId,
        { prompt: runInput, workflowYamls: [serialized.yaml] },
        controller.signal,
      );
      setRunPhase('accepted');
      for await (const _event of parseBackendSSEStream(response, {
        signal: controller.signal,
      })) {
        setRunEventCount((count) => count + 1);
      }
      if (!controller.signal.aborted) setRunPhase('stream_ended');
    } catch (error) {
      if (!controller.signal.aborted) {
        setRunError(toErrorMessage(error));
        setRunPhase('failed');
      }
    } finally {
      if (runControllerRef.current === controller)
        runControllerRef.current = null;
    }
  }, [parseCurrentYaml, runInput, runPhase, scopeId]);

  return {
    addNode,
    canRun,
    dirty,
    document,
    findings,
    graph,
    loading: source.isPending,
    loadError: source.error,
    materialization,
    run,
    runError,
    runEventCount,
    runInput,
    runPhase,
    save,
    saveError,
    saving,
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
    setRunInput,
    setSelectedStepConfigurationError,
    updateSelectedStepConfiguration,
    updateTitle,
    updateYaml,
    workflow,
    workflowTitle,
    yaml,
  } as const;
}
