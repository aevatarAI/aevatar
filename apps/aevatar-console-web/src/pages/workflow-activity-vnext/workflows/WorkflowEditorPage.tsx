import {
  ArrowLeftOutlined,
  CalendarOutlined,
  CodeOutlined,
  NodeIndexOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  RocketOutlined,
  SaveOutlined,
  UpOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Input, Modal, Segmented, Space } from 'antd';
import React from 'react';
import WorkflowStudioEditorSurface from '@/pages/team-member-workflow-studio/components/WorkflowStudioEditorSurface';
import { scopesApi } from '@/shared/api/scopesApi';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import { getLocationSnapshot, history } from '@/shared/navigation/history';
import { isStudioApiErrorCode, studioApi } from '@/shared/studio/api';
import { buildWorkflowExecutionNodeSnapshots } from '@/shared/studio/execution';
import { createWorkflowRevisionIdentityCandidate } from '@/shared/studio/explicitRequestConfirmation';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  adaptActivityRunToExecutionLogs,
  isTerminalActivityRunStatus,
} from '@/shared/workflows/activityExecution';
import { adaptExecutionDetailToLogs } from '@/shared/workflows/executionDetail';
import { useWorkflowPanelResize } from '@/shared/workflows/useWorkflowPanelResize';
import WorkflowExecutionLogsPanel, {
  type WorkflowExecutionLogsModel,
} from '@/shared/workflows/WorkflowExecutionLogsPanel';
import WorkflowPanelResizeHandle from '@/shared/workflows/WorkflowPanelResizeHandle';
import WorkflowRunInputPanel from '@/shared/workflows/WorkflowRunInputPanel';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import {
  useWorkflowEditor,
  type WorkflowPublishedInvocationTarget,
} from '../hooks/useWorkflowEditor';
import {
  useWorkflowPublication,
  type WorkflowPublicationReceipt,
} from '../hooks/useWorkflowPublication';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import WorkflowNodeInspector, {
  type WorkflowNodeInspectorHandle,
} from './WorkflowNodeInspector';
import WorkflowScheduleSurface from './WorkflowScheduleSurface';

const PUBLISHED_RUN_CONSOLE_ID = 'workflow-published-run-console';

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

type PendingNavigation = {
  readonly routeScopeId?: string;
  readonly routeWorkflowId?: string;
  readonly target: string;
};

type ActiveEditorRoute = {
  readonly scopeId: string;
  readonly target: string;
  readonly workflowId: string;
};

type PublicationStage = 'idle' | 'submitting' | 'accepted' | 'failed';

type PublishReadinessIssue = {
  readonly id: string;
  readonly message: string;
};

function hasNonBlankIdentifier(value: unknown): value is string {
  return typeof value === 'string' && Boolean(value.trim());
}

function restorePublishedInvocationTarget(
  scopeId: string,
  workflowId: string,
  detail: Awaited<ReturnType<typeof scopesApi.getWorkflowDetail>> | undefined,
): WorkflowPublishedInvocationTarget | null {
  const published = detail?.workflow;
  if (
    detail?.available !== true ||
    detail.scopeId !== scopeId ||
    !published ||
    published.scopeId !== scopeId ||
    published.workflowId !== workflowId ||
    !hasNonBlankIdentifier(published.activeRevisionId) ||
    !hasNonBlankIdentifier(published.publishedServiceId)
  ) {
    return null;
  }

  return {
    publishedServiceId: published.publishedServiceId,
    revisionId: published.activeRevisionId,
    workflowId: published.workflowId,
  };
}

const WorkflowEditorPage: React.FC<{
  readonly scopeId: string;
  readonly workflowId: string;
}> = ({ scopeId, workflowId }) => {
  const location = useConsoleLocation();
  const [activeEditorRoute, setActiveEditorRoute] =
    React.useState<ActiveEditorRoute>(() => ({
      scopeId,
      target:
        getLocationSnapshot() ||
        buildWorkflowActivityEditorHref(scopeId, workflowId),
      workflowId,
    }));
  const activeScopeId = activeEditorRoute.scopeId;
  const activeWorkflowId = activeEditorRoute.workflowId;
  const editor = useWorkflowEditor(activeScopeId, activeWorkflowId);
  const restoredPublication = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'workflow-publication-current',
      activeScopeId,
      activeWorkflowId,
    ],
    queryFn: () => scopesApi.getWorkflowDetail(activeScopeId, activeWorkflowId),
    retry: false,
  });
  const toast = useConsoleToast();
  const [mode, setMode] = React.useState<'canvas' | 'yaml'>('canvas');
  const [nodeLibraryOpen, setNodeLibraryOpen] = React.useState(false);
  const [publicationStage, setPublicationStage] =
    React.useState<PublicationStage>('idle');
  const [publicationError, setPublicationError] = React.useState<unknown>(null);
  const [publicationReceipt, setPublicationReceipt] =
    React.useState<WorkflowPublicationReceipt | null>(null);
  const [publishedDocumentVersion, setPublishedDocumentVersion] =
    React.useState<number | null>(null);
  const [runPanelOpen, setRunPanelOpen] = React.useState(false);
  const [schedulePanelOpen, setSchedulePanelOpen] = React.useState(false);
  const [runConsoleVisible, setRunConsoleVisible] = React.useState(false);
  const [runConsoleExpanded, setRunConsoleExpanded] = React.useState(false);
  const [activeRunLogIndex, setActiveRunLogIndex] = React.useState<
    number | null
  >(null);
  const [pendingNavigation, setPendingNavigation] =
    React.useState<PendingNavigation | null>(null);
  const [hasUnappliedNodeChanges, setHasUnappliedNodeChanges] =
    React.useState(false);
  const workflowNameRef = React.useRef<React.ElementRef<typeof Input>>(null);
  const collapseRunConsoleButtonRef =
    React.useRef<React.ElementRef<typeof Button>>(null);
  const expandRunConsoleButtonRef =
    React.useRef<React.ElementRef<typeof Button>>(null);
  const pendingRunConsoleFocusRef = React.useRef<'collapse' | 'expand' | null>(
    null,
  );
  const workflowMainRef = React.useRef<HTMLElement | null>(null);
  const runWorkspaceRef = React.useRef<HTMLDivElement | null>(null);
  const saveStatusRef = React.useRef<HTMLSpanElement>(null);
  const inspectorRef = React.useRef<WorkflowNodeInspectorHandle>(null);
  const publicationGenerationRef = React.useRef(0);
  const publicationInFlightRef = React.useRef(false);
  const shownPublicationToastKeysRef = React.useRef(new Set<string>());
  const {
    executionPanelHandleProps,
    executionPanelHeight,
    sidePanelHandleProps,
    sidePanelWidth,
  } = useWorkflowPanelResize({
    editorRegionRef: runWorkspaceRef,
    executionPanelMaxHeight: 640,
    executionPanelMaxHeightRatio: 0.72,
    initialExecutionPanelHeight: 310,
    mainRef: workflowMainRef,
  });
  const runRequested = new URLSearchParams(location.search).get('run') === '1';
  const editorWriteLocked = editor.saving || editor.structuralMutationPending;
  const publication = useWorkflowPublication(publicationReceipt);
  const publicationPhase = publicationReceipt
    ? publication.phase
    : publicationStage;
  const publicationValidationRejected =
    publicationReceipt === null &&
    publicationStage === 'failed' &&
    isStudioApiErrorCode(
      publicationError,
      400,
      'INVALID_USER_WORKFLOW_REQUEST',
    );
  React.useEffect(() => {
    const pendingFocus = pendingRunConsoleFocusRef.current;
    if (!pendingFocus) return;
    pendingRunConsoleFocusRef.current = null;
    if (pendingFocus === 'collapse') {
      collapseRunConsoleButtonRef.current?.focus();
      return;
    }
    expandRunConsoleButtonRef.current?.focus();
  }, [runConsoleExpanded]);

  React.useEffect(() => {
    let toastIntent: 'error' | 'success' | null = null;
    let toastMessage: string | null = null;
    let toastKey: string | null = null;

    if (publicationReceipt) {
      switch (publication.phase) {
        case 'observed':
          toastIntent = 'success';
          toastMessage = t(
            'workflowActivityVNext.publish.success',
            'Workflow published',
          );
          break;
        case 'unauthorized':
          toastIntent = 'error';
          toastMessage = t(
            'workflowActivityVNext.state.unauthorized',
            'Sign in to continue',
          );
          break;
        case 'forbidden':
          toastIntent = 'error';
          toastMessage = t(
            'workflowActivityVNext.state.forbidden',
            "You don't have access to this workspace",
          );
          break;
        case 'failed':
          toastIntent = 'error';
          toastMessage = t(
            'workflowActivityVNext.publish.failed',
            "Publication couldn't be confirmed",
          );
          break;
        default:
          return;
      }
      toastKey = [
        'workflow-publication',
        publicationGenerationRef.current,
        publicationReceipt.scopeId,
        publicationReceipt.workflowId,
        publicationReceipt.revisionId,
        publication.phase,
      ].join(':');
    } else if (publicationStage === 'failed') {
      toastIntent = 'error';
      toastMessage = publicationValidationRejected
        ? t(
            'workflowActivityVNext.publish.validationRejected',
            "Workflow isn't ready to publish",
          )
        : t(
            'workflowActivityVNext.publish.submissionFailed',
            "Workflow couldn't be submitted",
          );
      toastKey = [
        'workflow-publication',
        publicationGenerationRef.current,
        'submission',
        'failed',
      ].join(':');
    }

    if (!toastIntent || !toastMessage || !toastKey) return;
    if (shownPublicationToastKeysRef.current.has(toastKey)) return;

    shownPublicationToastKeysRef.current.add(toastKey);
    if (toastIntent === 'success')
      toast.success(toastMessage, { key: toastKey });
    else toast.error(toastMessage, { key: toastKey });
  }, [
    publication.phase,
    publicationReceipt,
    publicationStage,
    publicationValidationRejected,
    toast,
  ]);
  const publicationObserved = publication.phase === 'observed';
  const restoredPublishedInvocationTarget = restorePublishedInvocationTarget(
    activeScopeId,
    activeWorkflowId,
    restoredPublication.data,
  );
  const observedPublishedInvocationTarget =
    publicationReceipt &&
    publicationObserved &&
    hasNonBlankIdentifier(publication.publishedServiceId)
      ? {
          publishedServiceId: publication.publishedServiceId,
          revisionId: publicationReceipt.revisionId,
          workflowId: publicationReceipt.workflowId,
        }
      : null;
  const publishedTargetDocumentVersion = publicationReceipt
    ? publishedDocumentVersion
    : restoredPublishedInvocationTarget
      ? 0
      : null;
  const publicationStale = Boolean(
    publishedTargetDocumentVersion !== null &&
      publishedTargetDocumentVersion !== editor.documentVersion,
  );
  const publishedInvocationTarget =
    !publicationStale && publicationReceipt
      ? observedPublishedInvocationTarget
      : !publicationStale && publicationStage !== 'submitting'
        ? restoredPublishedInvocationTarget
        : null;
  const canOpenPublishedRun = Boolean(
    publishedInvocationTarget &&
      editor.canRun &&
      !editor.dirty &&
      !editorWriteLocked &&
      !hasUnappliedNodeChanges,
  );

  const invalidatePublication = React.useCallback(() => {
    publicationGenerationRef.current += 1;
    publicationInFlightRef.current = false;
  }, []);

  const clearPublication = React.useCallback(() => {
    invalidatePublication();
    setPublicationError(null);
    setPublicationReceipt(null);
    setPublishedDocumentVersion(null);
    setPublicationStage('idle');
  }, [invalidatePublication]);

  React.useEffect(() => {
    if (runRequested && canOpenPublishedRun) setRunPanelOpen(true);
  }, [canOpenPublishedRun, runRequested]);

  React.useEffect(() => {
    if (editorWriteLocked) setNodeLibraryOpen(false);
  }, [editorWriteLocked]);

  const acceptRouteWorkflowChange = React.useCallback(
    (nextScopeId: string, nextWorkflowId: string, nextTarget: string) => {
      editor.discardForRouteChange(nextWorkflowId);
      clearPublication();
      setHasUnappliedNodeChanges(false);
      setNodeLibraryOpen(false);
      setRunPanelOpen(false);
      setSchedulePanelOpen(false);
      setRunConsoleVisible(false);
      setRunConsoleExpanded(false);
      setActiveRunLogIndex(null);
      setActiveEditorRoute({
        scopeId: nextScopeId,
        target: nextTarget,
        workflowId: nextWorkflowId,
      });
    },
    [clearPublication, editor.discardForRouteChange],
  );

  const publicationRouteKey = `${activeScopeId}\u0000${activeWorkflowId}`;
  const previousPublicationRouteRef = React.useRef(publicationRouteKey);
  React.useEffect(() => {
    if (previousPublicationRouteRef.current === publicationRouteKey) return;
    previousPublicationRouteRef.current = publicationRouteKey;
    clearPublication();
  }, [clearPublication, publicationRouteKey]);

  React.useEffect(() => {
    const incomingRouteTarget =
      getLocationSnapshot() ||
      buildWorkflowActivityEditorHref(scopeId, workflowId);
    const isActiveRoute =
      scopeId === activeScopeId && workflowId === activeWorkflowId;
    if (isActiveRoute || editorWriteLocked) return;

    // A receipt can replace a committed source route with the newly materialized
    // draft. That transition retains the same in-memory document by design.
    if (
      scopeId === activeScopeId &&
      editor.workflow?.workflowId === workflowId
    ) {
      setActiveEditorRoute({
        scopeId,
        target: incomingRouteTarget,
        workflowId,
      });
      return;
    }

    if (editor.dirty || hasUnappliedNodeChanges) {
      setPendingNavigation((current) =>
        current?.routeScopeId === scopeId &&
        current.routeWorkflowId === workflowId
          ? current
          : {
              routeScopeId: scopeId,
              routeWorkflowId: workflowId,
              target: incomingRouteTarget,
            },
      );
      return;
    }

    acceptRouteWorkflowChange(scopeId, workflowId, incomingRouteTarget);
  }, [
    acceptRouteWorkflowChange,
    activeScopeId,
    activeWorkflowId,
    editor.dirty,
    editor.workflow?.workflowId,
    editorWriteLocked,
    hasUnappliedNodeChanges,
    scopeId,
    workflowId,
  ]);

  const continueNavigation = React.useCallback(
    (target: string) => {
      if (editorWriteLocked) return;
      if (editor.dirty) setPendingNavigation({ target });
      else history.push(target);
    },
    [editor.dirty, editorWriteLocked],
  );

  const requestInspectorDiscard = React.useCallback((proceed: () => void) => {
    const inspector = inspectorRef.current;
    if (inspector) {
      inspector.requestDiscardOrProceed(proceed);
      return;
    }
    proceed();
  }, []);

  const requestNavigation = React.useCallback(
    (target: string) => {
      requestInspectorDiscard(() => continueNavigation(target));
    },
    [continueNavigation, requestInspectorDiscard],
  );

  const requestCanvasSelect = React.useCallback(() => {
    requestInspectorDiscard(editor.selectCanvas);
  }, [editor.selectCanvas, requestInspectorDiscard]);

  const requestNodeSelect = React.useCallback(
    (nodeId: string) => {
      if (nodeId === editor.selectedNodeId) return;
      requestInspectorDiscard(() => editor.selectNode(nodeId));
    },
    [editor.selectNode, editor.selectedNodeId, requestInspectorDiscard],
  );

  const requestEditorMode = React.useCallback(
    (nextMode: 'canvas' | 'yaml') => {
      if (nextMode === mode) return;
      requestInspectorDiscard(() => setMode(nextMode));
    },
    [mode, requestInspectorDiscard],
  );

  React.useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (!hasUnappliedNodeChanges && !editorWriteLocked) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [editorWriteLocked, hasUnappliedNodeChanges]);

  const saveWorkflow = React.useCallback(async () => {
    return editor.save();
  }, [editor.save]);

  React.useEffect(() => {
    if (!editor.saveError) return;
    toast.error(
      t(
        'workflowActivityVNext.editor.saveFailed',
        "Workflow couldn't be saved",
      ),
    );
  }, [editor.saveError, toast]);

  React.useEffect(() => {
    if (!editor.nodeInsertionError) return;
    toast.error(
      <Space size="small">
        <span>
          {t('workflowActivityVNext.editor.addNodeFailed', "Couldn't add node")}
        </span>
        <Button
          onClick={() => void editor.retryNodeInsertion()}
          size="small"
          type="link"
        >
          {t('workflowActivityVNext.common.retry', 'Retry')}
        </Button>
      </Space>,
    );
  }, [editor.nodeInsertionError, editor.retryNodeInsertion, toast]);

  React.useEffect(() => {
    if (!editor.canvasMutationError) return;
    toast.error(
      t(
        'workflowActivityVNext.editor.canvasUpdateFailed',
        "Couldn't update workflow",
      ),
    );
  }, [editor.canvasMutationError, toast]);

  const retryMaterialization = React.useCallback(async () => {
    await editor.retryMaterialization();
  }, [editor.retryMaterialization]);

  const publishWorkflow = React.useCallback(async (): Promise<void> => {
    if (publicationInFlightRef.current) return;

    publicationInFlightRef.current = true;
    const publicationGeneration = ++publicationGenerationRef.current;
    const isCurrentPublication = () =>
      publicationGeneration === publicationGenerationRef.current;
    setPublicationError(null);
    setPublicationReceipt(null);
    setPublishedDocumentVersion(null);
    setPublicationStage('submitting');

    try {
      const preparation = await editor.preparePublication();
      if (!isCurrentPublication()) return;

      const revisionId = createWorkflowRevisionIdentityCandidate();
      if (!hasNonBlankIdentifier(revisionId)) {
        throw new Error('A publication revision could not be prepared.');
      }

      const preview = await studioApi.previewExplicitRequests({
        executionMode: 'interactive',
        revisionId,
        scopeId: activeScopeId,
        workflowId: preparation.workflowId,
        workflowYaml: preparation.workflowYaml,
      });
      if (!isCurrentPublication()) return;
      if (
        editor.documentVersion !== preparation.documentVersion ||
        preview.workflowId !== preparation.workflowId ||
        preview.revisionId !== revisionId
      ) {
        throw new Error(
          'The publication preparation does not match the saved workflow.',
        );
      }
      if (
        preview.items.some(
          (item) => !item.allowedExecutionModes.includes('interactive'),
        )
      ) {
        throw new Error(
          'An external request is unavailable for interactive publication.',
        );
      }

      const explicitRequestConfirmations = preview.items.map((item) => ({
        workflowId: preview.workflowId,
        revisionId: preview.revisionId,
        callSiteId: item.callSiteId,
        requestContractDigest: item.requestContractDigest,
        attestedRisk: item.effectiveRisk,
      }));
      const result = await studioApi.publishWorkflow({
        displayName: preparation.workflowName,
        explicitRequestConfirmations,
        inlineWorkflowYamls: {},
        revisionId: preview.revisionId,
        scopeId: activeScopeId,
        workflowId: preparation.workflowId,
        workflowName: preparation.workflowName,
        workflowYaml: preparation.workflowYaml,
      });
      if (!isCurrentPublication()) return;

      if (
        result.acceptanceStage !== 'accepted' ||
        result.scopeId !== activeScopeId ||
        !hasNonBlankIdentifier(result.workflowId) ||
        result.workflowId !== preparation.workflowId ||
        !hasNonBlankIdentifier(result.revisionId) ||
        result.revisionId !== preview.revisionId
      ) {
        throw new Error(
          'The accepted publication response does not match the submitted workflow.',
        );
      }

      setPublicationReceipt({
        scopeId: result.scopeId,
        revisionId: result.revisionId,
        workflowId: result.workflowId,
      });
      setPublishedDocumentVersion(preparation.documentVersion);
      setPublicationStage('accepted');
    } catch (error) {
      if (!isCurrentPublication()) return;
      setPublicationError(error);
      setPublicationStage('failed');
    } finally {
      if (isCurrentPublication()) publicationInFlightRef.current = false;
    }
  }, [activeScopeId, editor.documentVersion, editor.preparePublication]);

  const saveAndLeave = async () => {
    if (await saveWorkflow()) {
      const pending = pendingNavigation;
      setPendingNavigation(null);
      if (!pending) return;
      if (pending.routeScopeId && pending.routeWorkflowId) {
        acceptRouteWorkflowChange(
          pending.routeScopeId,
          pending.routeWorkflowId,
          pending.target,
        );
        if (getLocationSnapshot() !== pending.target)
          history.push(pending.target);
        return;
      }
      history.push(pending.target);
    }
  };

  const discardAndLeave = () => {
    const pending = pendingNavigation;
    setPendingNavigation(null);
    if (!pending) return;
    if (pending.routeScopeId && pending.routeWorkflowId) {
      acceptRouteWorkflowChange(
        pending.routeScopeId,
        pending.routeWorkflowId,
        pending.target,
      );
      if (getLocationSnapshot() !== pending.target)
        history.push(pending.target);
      return;
    }
    history.push(pending.target);
  };

  const stayInEditor = () => {
    const pending = pendingNavigation;
    setPendingNavigation(null);
    if (
      pending?.routeScopeId === scopeId &&
      pending.routeWorkflowId === workflowId
    ) {
      if (getLocationSnapshot() !== activeEditorRoute.target)
        history.push(activeEditorRoute.target);
    }
  };

  if (editor.loading) {
    return (
      <WorkflowActivityVNextShell
        activeSection="workflows"
        description={t(
          'workflowActivityVNext.editor.loadingDescription',
          'Preparing the editor…',
        )}
        scopeId={activeScopeId}
        title={t('workflowActivityVNext.editor.loading', 'Loading workflow…')}
      >
        <div className="wa-vnext__state">
          <p>
            {t('workflowActivityVNext.editor.loading', 'Loading workflow…')}
          </p>
        </div>
      </WorkflowActivityVNextShell>
    );
  }
  if (editor.loadError || !editor.workflow) {
    return (
      <WorkflowActivityVNextShell
        activeSection="workflows"
        description={t(
          'workflowActivityVNext.editor.unavailableDescription',
          "This workflow couldn't be loaded.",
        )}
        scopeId={activeScopeId}
        title={t(
          'workflowActivityVNext.editor.unavailable',
          'Workflow unavailable',
        )}
      >
        <div className="wa-vnext__state" role="alert">
          <div>
            <p>
              {t(
                'workflowActivityVNext.editor.unavailableGuidance',
                'Try again to reopen the workflow.',
              )}
            </p>
            <Button onClick={() => window.location.reload()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
            {editor.loadError ? (
              <TechnicalDetails>
                {errorMessage(editor.loadError)}
              </TechnicalDetails>
            ) : null}
          </div>
        </div>
      </WorkflowActivityVNextShell>
    );
  }

  const runBusy = editor.runRequestActive;
  const observedRun = editor.runObservation.run;
  const liveRunExecution = adaptExecutionDetailToLogs(editor.liveRunExecution);
  const observedRunExecution = observedRun
    ? adaptActivityRunToExecutionLogs(observedRun)
    : null;
  const observedRunTerminal = Boolean(
    observedRun && isTerminalActivityRunStatus(observedRun.summary.status),
  );
  const runExecution: WorkflowExecutionLogsModel | null = runConsoleVisible
    ? observedRunTerminal
      ? observedRunExecution
      : liveRunExecution || observedRunExecution
    : null;
  const runWorkflowNodes = editor.document
    ? buildWorkflowExecutionNodeSnapshots(editor.document)
    : [];
  const runConsoleError =
    runConsoleVisible && editor.runPhase === 'failed' && !observedRunTerminal
      ? editor.runError ||
        t('workflowActivityVNext.editor.runFailed', 'Run failed')
      : undefined;
  const publicationObservationPending =
    publicationReceipt !== null && publication.phase !== 'observed';
  const publicationActionPending =
    publicationStage === 'submitting' ||
    (publicationReceipt !== null &&
      (publication.phase === 'observing' || publication.phase === 'delayed'));
  const canRetryPublicationSubmission =
    publicationReceipt === null && publicationStage === 'failed';
  const blockingFindings = editor.findings.filter(
    (finding) => String(finding.level).toLowerCase() === 'error',
  );
  const publishReadinessIssues: PublishReadinessIssue[] = [];
  if (!editor.workflow.draftExists) {
    publishReadinessIssues.push({
      id: 'draft',
      message: t(
        'workflowActivityVNext.publish.saveBeforePublishing',
        'Save this workflow before publishing.',
      ),
    });
  }
  if (editor.dirty) {
    publishReadinessIssues.push({
      id: 'dirty',
      message: t(
        'workflowActivityVNext.publish.saveChangesBeforePublishing',
        'Save your changes before publishing.',
      ),
    });
  }
  if (hasUnappliedNodeChanges) {
    publishReadinessIssues.push({
      id: 'node-configuration',
      message: t(
        'workflowActivityVNext.publish.applyNodeChanges',
        'Apply or discard node configuration before publishing.',
      ),
    });
  }
  for (const [findingIndex, finding] of blockingFindings.entries()) {
    publishReadinessIssues.push({
      id: `finding-${finding.code}-${finding.path ?? findingIndex}`,
      message: finding.message,
    });
  }
  if (!editor.document?.steps?.length && blockingFindings.length === 0) {
    publishReadinessIssues.push({
      id: 'steps',
      message: t(
        'workflowActivityVNext.publish.addExecutableStep',
        'Add at least one executable step before publishing.',
      ),
    });
  }
  if (editor.validating || editor.saving) {
    publishReadinessIssues.push({
      id: 'save-in-progress',
      message: t(
        'workflowActivityVNext.publish.waitForSave',
        'Wait for workflow validation and saving to finish.',
      ),
    });
  } else if (editor.structuralMutationPending) {
    publishReadinessIssues.push({
      id: 'editor-update-in-progress',
      message: t(
        'workflowActivityVNext.publish.waitForEditorUpdate',
        'Wait for the workflow step update to finish.',
      ),
    });
  } else if (editor.receiptPending) {
    publishReadinessIssues.push({
      id: 'save-observation',
      message: t(
        'workflowActivityVNext.publish.waitForSavedDraft',
        'Wait for the saved draft to become readable.',
      ),
    });
  }
  if (publicationActionPending) {
    publishReadinessIssues.push({
      id: 'publication-in-progress',
      message: t(
        'workflowActivityVNext.publish.waitForPublication',
        'Wait for the current publication to finish.',
      ),
    });
  } else if (publicationObservationPending) {
    publishReadinessIssues.push({
      id: 'publication-unresolved',
      message: t(
        'workflowActivityVNext.publish.resolvePublication',
        'Resolve the current publication status before publishing again.',
      ),
    });
  }
  const publicationCurrent = Boolean(publishedInvocationTarget);
  const canPublish = publishReadinessIssues.length === 0 && !publicationCurrent;
  const publishLabel = publicationActionPending
    ? t('workflowActivityVNext.publish.publishing', 'Publishing')
    : publishReadinessIssues.length > 0
      ? publishReadinessIssues.length === 1
        ? t(
            'workflowActivityVNext.publish.blockedOne',
            'Publish blocked · 1 issue',
          )
        : t(
            'workflowActivityVNext.publish.blocked',
            'Publish blocked · {count} issues',
            { count: publishReadinessIssues.length },
          )
      : t('workflowActivityVNext.editor.publish', 'Publish');
  const publishedTargetResolving =
    publicationReceipt !== null ||
    publicationStage === 'submitting' ||
    restoredPublication.isPending;
  const runDisabledReason = publicationStale
    ? t(
        'workflowActivityVNext.editor.publishLatestBeforeRun',
        'Save and publish the latest changes before running.',
      )
    : !publishedInvocationTarget
      ? publishedTargetResolving
        ? t(
            'workflowActivityVNext.editor.waitForPublishedRun',
            'Wait for the published revision to become available.',
          )
        : t(
            'workflowActivityVNext.editor.publishBeforeRun',
            'Publish this workflow before running it.',
          )
      : editor.dirty || hasUnappliedNodeChanges
        ? t(
            'workflowActivityVNext.editor.publishLatestBeforeRun',
            'Save and publish the latest changes before running.',
          )
        : !editor.canRun
          ? t(
              'workflowActivityVNext.editor.runUnavailable',
              'Add at least one valid step before running.',
            )
          : editorWriteLocked
            ? t(
                'workflowActivityVNext.editor.waitForEditorBeforeRun',
                'Wait for the workflow update to finish.',
              )
            : undefined;
  const saveStatus = editor.validating
    ? t('workflowActivityVNext.editor.validating', 'Validating workflow…')
    : editor.saving || editor.receiptPending
      ? t('workflowActivityVNext.editor.savingProgress', 'Saving workflow…')
      : editor.saveError
        ? t('workflowActivityVNext.editor.saveStatusFailed', 'Save failed')
        : editor.dirty
          ? t('workflowActivityVNext.editor.unsavedChanges', 'Unsaved changes')
          : t('workflowActivityVNext.editor.savedAt', 'Saved at {updatedAt}', {
              updatedAt: formatUtcDateTime(editor.workflow.updatedAtUtc),
            });
  const publishButton = (
    <Button
      aria-disabled={!canPublish}
      icon={<RocketOutlined />}
      onClick={() => {
        if (canPublish) void publishWorkflow();
      }}
    >
      {publishLabel}
    </Button>
  );
  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      footer={
        runConsoleExpanded && (runExecution || runConsoleError) ? (
          <div className="wa-vnext__logs-dock wa-vnext__logs-dock--expanded">
            <WorkflowPanelResizeHandle
              ariaLabel={t(
                'workflowActivityVNext.editor.resizeRunConsole',
                'Resize workflow run console',
              )}
              className="wa-vnext__panel-resize-handle"
              orientation="horizontal"
              {...executionPanelHandleProps}
            />
            <WorkflowExecutionLogsPanel
              activeLogIndex={activeRunLogIndex}
              collapseButtonRef={collapseRunConsoleButtonRef}
              collapseControlsId={PUBLISHED_RUN_CONSOLE_ID}
              error={runConsoleError}
              execution={runExecution}
              height={executionPanelHeight}
              id={PUBLISHED_RUN_CONSOLE_ID}
              onClear={() => {
                setRunConsoleVisible(false);
                setRunConsoleExpanded(false);
                setActiveRunLogIndex(null);
              }}
              onCollapse={() => {
                pendingRunConsoleFocusRef.current = 'expand';
                setRunConsoleExpanded(false);
              }}
              onSelectLog={setActiveRunLogIndex}
              workflowNodes={runWorkflowNodes}
            />
          </div>
        ) : (
          <div className="wa-vnext__logs-dock-bar">
            <strong>
              {t('teamMemberWorkflowStudio.executionPanel.logs', 'Logs')}
            </strong>
            <Button
              aria-label={t(
                'workflowActivityVNext.editor.expandRunConsole',
                'Expand workflow logs',
              )}
              aria-controls={PUBLISHED_RUN_CONSOLE_ID}
              aria-expanded={false}
              disabled={!runConsoleVisible}
              icon={<UpOutlined />}
              onClick={() => {
                pendingRunConsoleFocusRef.current = 'collapse';
                setRunConsoleExpanded(true);
              }}
              ref={expandRunConsoleButtonRef}
              size="small"
              type="text"
            />
          </div>
        )
      }
      heading={
        <Input
          aria-label={t('workflowActivityVNext.new.name', 'Workflow name')}
          className="wa-vnext__editor-name"
          disabled={editorWriteLocked}
          onChange={(event) => editor.updateTitle(event.target.value)}
          ref={workflowNameRef}
          value={editor.workflowTitle}
          variant="borderless"
        />
      }
      headerActions={
        <>
          <Button
            aria-label={t(
              'workflowActivityVNext.editor.backAria',
              'Back to workflows',
            )}
            disabled={editorWriteLocked}
            icon={<ArrowLeftOutlined />}
            onClick={() =>
              requestNavigation(
                buildWorkflowActivitySectionHref(activeScopeId, 'workflows'),
              )
            }
          />
          <Button
            disabled={
              !editor.dirty ||
              editorWriteLocked ||
              editor.receiptPending ||
              hasUnappliedNodeChanges
            }
            icon={<SaveOutlined />}
            loading={editor.saving}
            onClick={() => void saveWorkflow()}
            title={
              hasUnappliedNodeChanges
                ? t(
                    'workflowActivityVNext.nodeInspector.applyBeforeSave',
                    'Apply changes before saving this workflow.',
                  )
                : undefined
            }
            type="primary"
          >
            {t('workflowActivityVNext.editor.save', 'Save workflow')}
          </Button>
          <Button
            disabled={!canOpenPublishedRun}
            icon={<PlayCircleOutlined />}
            onClick={() => setRunPanelOpen(true)}
            title={runDisabledReason}
          >
            {t('workflowActivityVNext.common.run', 'Run')}
          </Button>
          <Button
            aria-label={t(
              'workflowActivityVNext.schedule.openAria',
              'Manage schedules for {name}',
              { name: editor.workflowTitle },
            )}
            disabled={!publicationCurrent}
            icon={<CalendarOutlined />}
            onClick={() => setSchedulePanelOpen(true)}
            title={
              publicationCurrent
                ? undefined
                : t(
                    'workflowActivityVNext.schedule.publishBeforeOpen',
                    'Publish this Workflow before managing schedules.',
                  )
            }
          >
            {t('workflowActivityVNext.schedule.open', 'Schedules')}
          </Button>
          {!publicationCurrent &&
            (publishReadinessIssues.length > 0 ? (
              <AevatarTooltip
                placement="bottomRight"
                title={
                  <div className="wa-vnext__publish-readiness">
                    <ul>
                      {publishReadinessIssues.map((issue) => (
                        <li key={issue.id}>{issue.message}</li>
                      ))}
                    </ul>
                  </div>
                }
                trigger={['hover', 'focus']}
              >
                {publishButton}
              </AevatarTooltip>
            ) : (
              publishButton
            ))}
        </>
      }
      mainRef={workflowMainRef}
      onNavigate={requestNavigation}
      scopeId={activeScopeId}
      title={
        editor.workflowTitle ||
        t('workflowActivityVNext.editor.untitled', 'Untitled workflow')
      }
    >
      <div className="wa-vnext__toolbar wa-vnext__editor-toolbar">
        <div className="wa-vnext__editor-toolbar-meta">
          {publicationCurrent ? (
            <span
              aria-label={t(
                'workflowActivityVNext.editor.publicationStatusAria',
                'Workflow publication status',
              )}
              aria-live="polite"
              className="wa-vnext__status wa-vnext__status--succeeded"
              role="status"
            >
              {t('workflowActivityVNext.publish.published', 'Published')}
            </span>
          ) : null}
          <span
            aria-atomic="true"
            aria-label={t(
              'workflowActivityVNext.editor.saveStatusAria',
              'Workflow save status',
            )}
            aria-live="polite"
            className={`wa-vnext__status wa-vnext__status--${editor.saveError ? 'failed' : editor.dirty || editor.validating || editor.saving || editor.receiptPending ? 'pending' : 'succeeded'}`}
            ref={saveStatusRef}
            role="status"
            tabIndex={-1}
          >
            {saveStatus}
          </span>
          <Segmented
            aria-label={`${t('workflowActivityVNext.editor.canvas', 'Canvas')} / ${t(
              'workflowActivityVNext.editor.yaml',
              'YAML',
            )}`}
            className="wa-vnext__editor-mode-control"
            disabled={editorWriteLocked}
            onChange={(value) => requestEditorMode(value as 'canvas' | 'yaml')}
            options={[
              {
                label: t('workflowActivityVNext.editor.canvas', 'Canvas'),
                value: 'canvas',
                icon: <NodeIndexOutlined />,
              },
              {
                label: t('workflowActivityVNext.editor.yaml', 'YAML'),
                value: 'yaml',
                icon: <CodeOutlined />,
              },
            ]}
            value={mode}
          />
        </div>
      </div>
      {(editor.materialization.phase === 'readable' ||
        editor.materialization.phase === 'delayed' ||
        editor.materialization.phase === 'failed') &&
      editor.materialization.receipt ? (
        <Alert
          action={
            editor.materialization.phase === 'delayed' ||
            editor.materialization.phase === 'failed' ? (
              <Button onClick={() => void retryMaterialization()}>
                {t('workflowActivityVNext.new.retryObservation', 'Try again')}
              </Button>
            ) : undefined
          }
          description={
            editor.materialization.error ? (
              <TechnicalDetails>
                {errorMessage(editor.materialization.error)}
              </TechnicalDetails>
            ) : undefined
          }
          title={
            editor.materialization.phase === 'delayed'
              ? t(
                  'workflowActivityVNext.editor.saveDelayed',
                  'Save is taking longer than expected',
                )
              : editor.materialization.phase === 'failed'
                ? t(
                    'workflowActivityVNext.editor.saveOpenFailed',
                    "Workflow was saved but couldn't be reopened",
                  )
                : t('workflowActivityVNext.editor.saved', 'Saved')
          }
          showIcon
          type={
            editor.materialization.phase === 'failed'
              ? 'error'
              : editor.materialization.phase === 'delayed'
                ? 'warning'
                : 'success'
          }
        />
      ) : null}
      {publicationPhase === 'failed' ||
      publicationPhase === 'unauthorized' ||
      publicationPhase === 'forbidden' ? (
        <Alert
          action={
            canRetryPublicationSubmission ? (
              <Button onClick={() => void publishWorkflow()}>
                {t('workflowActivityVNext.common.retry', 'Retry')}
              </Button>
            ) : undefined
          }
          description={
            publicationValidationRejected && publicationError ? (
              <>
                {t(
                  'workflowActivityVNext.publish.validationRejectedDescription',
                  'Fix the workflow configuration below, then publish again.',
                )}
                <TechnicalDetails>
                  {errorMessage(publicationError)}
                </TechnicalDetails>
              </>
            ) : (
              <>
                {publicationPhase === 'unauthorized'
                  ? t(
                      'workflowActivityVNext.publish.unauthorizedDescription',
                      'Sign in again to check this publication.',
                    )
                  : publicationPhase === 'forbidden'
                    ? t(
                        'workflowActivityVNext.publish.forbiddenDescription',
                        "You don't have access to check this publication.",
                      )
                    : publicationReceipt
                      ? t(
                          'workflowActivityVNext.publish.failedDescription',
                          'Resolve the error or try publishing again.',
                        )
                      : t(
                          'workflowActivityVNext.publish.submissionFailedDescription',
                          'Review the error and try publishing again.',
                        )}
                {publicationError || publication.error ? (
                  <TechnicalDetails>
                    {errorMessage(publicationError ?? publication.error)}
                  </TechnicalDetails>
                ) : null}
              </>
            )
          }
          title={
            publicationPhase === 'unauthorized'
              ? t(
                  'workflowActivityVNext.state.unauthorized',
                  'Sign in to continue',
                )
              : publicationPhase === 'forbidden'
                ? t(
                    'workflowActivityVNext.state.forbidden',
                    "You don't have access to this workspace",
                  )
                : publicationValidationRejected
                  ? t(
                      'workflowActivityVNext.publish.validationRejected',
                      "Workflow isn't ready to publish",
                    )
                  : publicationReceipt
                    ? t(
                        'workflowActivityVNext.publish.failed',
                        "Publication couldn't be confirmed",
                      )
                    : t(
                        'workflowActivityVNext.publish.submissionFailed',
                        "Workflow couldn't be submitted",
                      )
          }
          id="workflow-publication-status"
          showIcon
          type="error"
        />
      ) : null}
      {editor.findings.length > 0 ? (
        <div aria-live="polite" className="wa-vnext__editor-alerts">
          {editor.findings.map((finding) => (
            <Alert
              key={[
                finding.code,
                finding.path,
                finding.level,
                finding.message,
              ].join('|')}
              title={finding.message}
              showIcon
              type={
                String(finding.level).toLowerCase() === 'error'
                  ? 'error'
                  : 'warning'
              }
            />
          ))}
        </div>
      ) : null}
      <div className="wa-vnext__run-workspace" ref={runWorkspaceRef}>
        {mode === 'canvas' ? (
          <WorkflowStudioEditorSurface
            ariaLabel={t(
              'workflowActivityVNext.editor.canvasAria',
              'Workflow canvas',
            )}
            edges={editor.graph.edges}
            emptyDescription={t(
              'workflowActivityVNext.editor.emptyCanvas',
              'Add the first executable node to make this workflow runnable.',
            )}
            addFirstStepDisabled={editorWriteLocked}
            nodes={editor.graph.nodes}
            nodeLibraryOpen={nodeLibraryOpen && !editorWriteLocked}
            onAddFirstStep={() => {
              if (!editorWriteLocked) setNodeLibraryOpen(true);
            }}
            onCanvasSelect={requestCanvasSelect}
            onConnectNodes={(sourceNodeId, targetNodeId) => {
              requestInspectorDiscard(() => {
                void editor.connectNodes(sourceNodeId, targetNodeId);
              });
            }}
            onCloseNodeLibrary={() => setNodeLibraryOpen(false)}
            onDeleteEdges={(edgeIds) => {
              requestInspectorDiscard(() => {
                void editor.deleteEdges(edgeIds);
              });
            }}
            onDeleteNodes={(nodeIds) => {
              requestInspectorDiscard(() => {
                void editor.deleteNodes(nodeIds);
              });
            }}
            onEdgeSelect={(edgeId) => {
              requestInspectorDiscard(() => editor.selectEdge(edgeId));
            }}
            onInsertNode={(stepType) => {
              requestInspectorDiscard(() => {
                void editor.addNode(stepType);
                setNodeLibraryOpen(false);
              });
            }}
            onNodeLayoutChange={editor.moveNodes}
            onNodeSelect={requestNodeSelect}
            selectedEdgeId={editor.selectedEdgeId}
            selectedNodeId={editor.selectedNodeId}
            style={{
              border: '1px solid var(--wa-line)',
              flex: '1 1 auto',
              height: '100%',
              minHeight: 440,
              minWidth: 0,
            }}
          >
            <Button
              className="wa-vnext__editor-add"
              disabled={editorWriteLocked}
              icon={<PlusOutlined />}
              onClick={() => {
                if (!editorWriteLocked) setNodeLibraryOpen(true);
              }}
            >
              {t('workflowActivityVNext.editor.addNode', 'Add node')}
            </Button>
            <WorkflowNodeInspector
              disabled={editorWriteLocked}
              error={editor.selectedStepConfigurationError}
              ref={inspectorRef}
              onClose={editor.selectCanvas}
              onConfigurationChange={editor.updateSelectedStepConfiguration}
              onConfigurationErrorChange={
                editor.setSelectedStepConfigurationError
              }
              onUnappliedChangesChange={setHasUnappliedNodeChanges}
              scopeId={activeScopeId}
              stepDraft={editor.selectedStepDraft}
            />
          </WorkflowStudioEditorSurface>
        ) : (
          <Input.TextArea
            aria-label={t('workflowActivityVNext.new.yaml', 'Workflow YAML')}
            className="wa-vnext__editor-yaml"
            disabled={editorWriteLocked}
            onChange={(event) => editor.updateYaml(event.target.value)}
            style={{
              border: '1px solid var(--wa-line)',
              height: '100%',
              minHeight: 440,
              resize: 'none',
            }}
            value={editor.yaml}
          />
        )}
        {runPanelOpen && publishedInvocationTarget ? (
          <WorkflowPanelResizeHandle
            ariaLabel={t(
              'workflowActivityVNext.editor.resizePublishedRunPanel',
              'Resize published run panel',
            )}
            className="wa-vnext__panel-resize-handle"
            orientation="vertical"
            {...sidePanelHandleProps}
          />
        ) : null}
        <WorkflowRunInputPanel
          canRun={
            !runBusy &&
            Boolean(publishedInvocationTarget) &&
            editor.canRun &&
            !editorWriteLocked
          }
          height="100%"
          inputDisabled={runBusy || editorWriteLocked}
          onClose={() => setRunPanelOpen(false)}
          onRun={() => {
            if (!publishedInvocationTarget) return;
            setRunConsoleVisible(true);
            setRunConsoleExpanded(true);
            setActiveRunLogIndex(null);
            void editor.run(publishedInvocationTarget);
          }}
          onRunMessageChange={editor.setRunInput}
          open={runPanelOpen && publishedInvocationTarget !== null}
          pending={editor.runPhase === 'submitting'}
          runMessage={editor.runInput}
          variant={{
            files: editor.runFiles,
            inputError: editor.runInputError,
            kind: 'published',
            onFilesAdd: editor.addRunFiles,
            onFileRemove: editor.removeRunFile,
          }}
          width={sidePanelWidth}
        />
      </div>
      <WorkflowScheduleSurface
        available={publicationCurrent}
        mode="panel"
        onClose={() => setSchedulePanelOpen(false)}
        open={schedulePanelOpen}
        scopeId={activeScopeId}
        workflowId={activeWorkflowId}
        workflowName={editor.workflowTitle}
      />
      <Modal
        aria-label={t(
          'workflowActivityVNext.editor.unsavedTitle',
          'Unsaved workflow changes',
        )}
        footer={[
          <Button key="stay" onClick={stayInEditor}>
            {t('workflowActivityVNext.editor.stay', 'Stay')}
          </Button>,
          <Button key="discard" onClick={discardAndLeave}>
            {t(
              'workflowActivityVNext.editor.discardLeave',
              'Discard and leave',
            )}
          </Button>,
          <Button
            disabled={
              editorWriteLocked ||
              editor.receiptPending ||
              hasUnappliedNodeChanges
            }
            key="save"
            loading={editor.saving}
            onClick={() => void saveAndLeave()}
            type="primary"
          >
            {t('workflowActivityVNext.editor.saveLeave', 'Save and leave')}
          </Button>,
        ]}
        onCancel={stayInEditor}
        open={Boolean(pendingNavigation)}
        title={t(
          'workflowActivityVNext.editor.unsavedTitle',
          'Unsaved workflow changes',
        )}
      >
        <p>
          {t(
            'workflowActivityVNext.editor.unsavedDescription',
            'Save your changes, discard them, or stay in the editor.',
          )}
        </p>
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowEditorPage;
