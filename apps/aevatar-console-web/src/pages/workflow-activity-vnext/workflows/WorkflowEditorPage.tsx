import {
  ArrowLeftOutlined,
  CodeOutlined,
  NodeIndexOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  RocketOutlined,
  SaveOutlined,
} from '@ant-design/icons';
import { Alert, Button, Input, Modal, Segmented, Space, Tooltip } from 'antd';
import React from 'react';
import WorkflowStudioCanvasRegion from '@/pages/team-member-workflow-studio/components/WorkflowStudioCanvasRegion';
import WorkflowStudioNodeLibrary from '@/pages/team-member-workflow-studio/components/WorkflowStudioNodeLibrary';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import { getLocationSnapshot, history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import { createWorkflowRevisionIdentityCandidate } from '@/shared/studio/explicitRequestConfirmation';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  getRunStatusPresentation,
  isRunStatusInProgress,
  isRunStatusTerminal,
} from '../activity/runPresentation';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { useWorkflowEditor } from '../hooks/useWorkflowEditor';
import {
  useWorkflowPublication,
  type WorkflowPublicationReceipt,
} from '../hooks/useWorkflowPublication';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityRunHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import WorkflowNodeInspector, {
  type WorkflowNodeInspectorHandle,
} from './WorkflowNodeInspector';
import WorkflowPublishedRunDrawer from './WorkflowPublishedRunDrawer';

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
  const [pendingNavigation, setPendingNavigation] =
    React.useState<PendingNavigation | null>(null);
  const [hasUnappliedNodeChanges, setHasUnappliedNodeChanges] =
    React.useState(false);
  const workflowNameRef = React.useRef<React.ElementRef<typeof Input>>(null);
  const saveStatusRef = React.useRef<HTMLSpanElement>(null);
  const inspectorRef = React.useRef<WorkflowNodeInspectorHandle>(null);
  const publicationGenerationRef = React.useRef(0);
  const publicationInFlightRef = React.useRef(false);
  const runRequested = new URLSearchParams(location.search).get('run') === '1';
  const editorWriteLocked = editor.saving || editor.structuralMutationPending;
  const publication = useWorkflowPublication(publicationReceipt);
  const publicationObserved = publication.phase === 'observed';
  const publicationStale = Boolean(
    publicationReceipt &&
      publishedDocumentVersion !== null &&
      publishedDocumentVersion !== editor.documentVersion,
  );
  const publishedInvocationTarget =
    publicationReceipt && publicationObserved && !publicationStale
      ? publicationReceipt
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
        result.revisionId !== preview.revisionId ||
        !hasNonBlankIdentifier(result.publishedServiceId)
      ) {
        throw new Error(
          'The accepted publication response does not match the submitted workflow.',
        );
      }

      setPublicationReceipt({
        publishedServiceId: result.publishedServiceId,
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

  const runBusy =
    editor.runPhase === 'submitting' ||
    editor.runPhase === 'accepted' ||
    editor.runObservationUnresolved ||
    editor.runAwaitingIdentification;
  const observedRun = editor.runObservation.run;
  const observedStatus = observedRun
    ? getRunStatusPresentation(observedRun.summary.status)
    : null;
  const observedRunInProgress = observedRun
    ? isRunStatusInProgress(observedRun.summary.status)
    : false;
  const observedRunTerminal = observedRun
    ? isRunStatusTerminal(observedRun.summary.status)
    : false;
  const currentStep = observedRun?.steps.find(
    (step) => step.requestedAtUtc && !step.completedAtUtc,
  );
  const runDetailsHref = editor.sseRunId
    ? buildWorkflowActivityRunHref(activeScopeId, editor.sseRunId)
    : '';
  const publicationPhase = publicationReceipt
    ? publication.phase
    : publicationStage;
  const publicationObservationPending =
    publicationReceipt !== null && publication.phase !== 'observed';
  const publicationActionPending =
    publicationStage === 'submitting' || publication.phase === 'observing';
  const canRetryPublicationObservation =
    publicationReceipt !== null &&
    (publication.phase === 'delayed' ||
      publication.phase === 'failed' ||
      publication.phase === 'unauthorized' ||
      publication.phase === 'forbidden');
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
  const publicationCurrent = publicationObserved && !publicationStale;
  const canPublish = publishReadinessIssues.length === 0 && !publicationCurrent;
  const publishLabel = publicationCurrent
    ? t('workflowActivityVNext.publish.published', 'Published')
    : publicationActionPending
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
  const runDisabledReason = publicationStale
    ? t(
        'workflowActivityVNext.editor.publishLatestBeforeRun',
        'Save and publish the latest changes before running.',
      )
    : !publicationReceipt
      ? t(
          'workflowActivityVNext.editor.publishBeforeRun',
          'Publish this workflow before running it.',
        )
      : !publicationObserved
        ? t(
            'workflowActivityVNext.editor.waitForPublishedRun',
            'Wait for the published revision to become available.',
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
          {publishReadinessIssues.length > 0 ? (
            <Tooltip
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
            </Tooltip>
          ) : (
            publishButton
          )}
        </>
      }
      onNavigate={requestNavigation}
      scopeId={activeScopeId}
      title={
        editor.workflowTitle ||
        t('workflowActivityVNext.editor.untitled', 'Untitled workflow')
      }
    >
      <div className="wa-vnext__toolbar wa-vnext__editor-toolbar">
        <div className="wa-vnext__editor-toolbar-meta">
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
          message={
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
      {publicationPhase === 'delayed' ||
      publicationPhase === 'failed' ||
      publicationPhase === 'unauthorized' ||
      publicationPhase === 'forbidden' ? (
        <Alert
          action={
            canRetryPublicationObservation || canRetryPublicationSubmission ? (
              <Space size="small">
                {canRetryPublicationObservation ? (
                  <Button onClick={() => void publication.retry()}>
                    {t(
                      'workflowActivityVNext.publish.checkAgain',
                      'Check again',
                    )}
                  </Button>
                ) : null}
                {canRetryPublicationSubmission ||
                publication.phase === 'failed' ? (
                  <Button onClick={() => void publishWorkflow()}>
                    {t('workflowActivityVNext.common.retry', 'Retry')}
                  </Button>
                ) : null}
              </Space>
            ) : undefined
          }
          description={
            <>
              {publicationPhase === 'delayed'
                ? t(
                    'workflowActivityVNext.publish.delayedDescription',
                    'Check again for the latest publication status.',
                  )
                : publicationPhase === 'unauthorized'
                  ? t(
                      'workflowActivityVNext.publish.unauthorizedDescription',
                      'Sign in again to check this publication.',
                    )
                  : publicationPhase === 'forbidden'
                    ? t(
                        'workflowActivityVNext.publish.forbiddenDescription',
                        "You don't have access to check this publication.",
                      )
                    : t(
                        'workflowActivityVNext.publish.failedDescription',
                        'Resolve the error or try publishing again.',
                      )}
              {publicationError || publication.error ? (
                <TechnicalDetails>
                  {errorMessage(publicationError ?? publication.error)}
                </TechnicalDetails>
              ) : null}
            </>
          }
          message={
            publicationPhase === 'delayed'
              ? t(
                  'workflowActivityVNext.publish.delayed',
                  'Publication is taking longer to appear',
                )
              : publicationPhase === 'unauthorized'
                ? t(
                    'workflowActivityVNext.state.unauthorized',
                    'Sign in to continue',
                  )
                : publicationPhase === 'forbidden'
                  ? t(
                      'workflowActivityVNext.state.forbidden',
                      "You don't have access to this workspace",
                    )
                  : t(
                      'workflowActivityVNext.publish.failed',
                      "Publication couldn't be confirmed",
                    )
          }
          id="workflow-publication-status"
          showIcon
          type={publicationPhase === 'delayed' ? 'warning' : 'error'}
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
              message={finding.message}
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
      {mode === 'canvas' ? (
        <WorkflowStudioCanvasRegion
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
          onAddFirstStep={() => {
            if (!editorWriteLocked) setNodeLibraryOpen(true);
          }}
          onCanvasSelect={requestCanvasSelect}
          onNodeSelect={requestNodeSelect}
          selectedNodeId={editor.selectedNodeId}
          style={{
            border: '1px solid var(--wa-line)',
            flex: 'none',
            height: 'min(620px, calc(100dvh - 248px))',
            minHeight: 440,
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
          <WorkflowStudioNodeLibrary
            onClose={() => setNodeLibraryOpen(false)}
            onInsertNode={(stepType) => {
              requestInspectorDiscard(() => {
                void editor.addNode(stepType);
                setNodeLibraryOpen(false);
              });
            }}
            open={nodeLibraryOpen && !editorWriteLocked}
          />
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
            stepDraft={editor.selectedStepDraft}
          />
        </WorkflowStudioCanvasRegion>
      ) : (
        <Input.TextArea
          aria-label={t('workflowActivityVNext.new.yaml', 'Workflow YAML')}
          className="wa-vnext__editor-yaml"
          disabled={editorWriteLocked}
          onChange={(event) => editor.updateYaml(event.target.value)}
          style={{
            border: '1px solid var(--wa-line)',
            height: 'min(620px, calc(100dvh - 248px))',
            minHeight: 440,
            resize: 'none',
          }}
          value={editor.yaml}
        />
      )}
      <WorkflowPublishedRunDrawer
        input={editor.runInput}
        inputDisabled={runBusy || editorWriteLocked || observedRunInProgress}
        inputError={editor.runInputError}
        onClose={() => setRunPanelOpen(false)}
        onInputChange={editor.setRunInput}
        onOpenActivity={() =>
          requestNavigation(
            buildWorkflowActivitySectionHref(activeScopeId, 'activity'),
          )
        }
        onStart={() => {
          if (publishedInvocationTarget) {
            void editor.run(publishedInvocationTarget);
          }
        }}
        open={runPanelOpen && publishedInvocationTarget !== null}
        startDisabled={
          runBusy ||
          observedRunInProgress ||
          !publishedInvocationTarget ||
          !editor.canRun ||
          !editor.runInput.trim() ||
          editorWriteLocked
        }
        starting={editor.runPhase === 'submitting'}
        target={publishedInvocationTarget}
      >
        {editor.runPhase === 'failed' && !observedRun ? (
          <Alert
            message={t('workflowActivityVNext.editor.runFailed', 'Run failed')}
            description={
              editor.runError ? (
                <TechnicalDetails>{editor.runError}</TechnicalDetails>
              ) : undefined
            }
            showIcon
            type="error"
          />
        ) : null}
        {editor.sseRunId &&
        editor.runObservation.phase !== 'idle' &&
        editor.runObservation.phase !== 'observing' ? (
          <Alert
            action={
              <Space wrap>
                <Button
                  href={runDetailsHref}
                  onClick={(event) => {
                    event.preventDefault();
                    requestNavigation(runDetailsHref);
                  }}
                  type={
                    editor.runObservation.phase === 'observed'
                      ? 'primary'
                      : 'default'
                  }
                >
                  {t(
                    'workflowActivityVNext.editor.openRunDetails',
                    'Open run details',
                  )}
                </Button>
                {editor.runObservation.phase === 'observed' &&
                observedRunInProgress ? (
                  <Button onClick={() => void editor.runObservation.retry()}>
                    {t(
                      'workflowActivityVNext.editor.checkLatestStatus',
                      'Check latest status',
                    )}
                  </Button>
                ) : editor.runObservation.phase !== 'observed' ? (
                  <Button onClick={() => void editor.runObservation.retry()}>
                    {t(
                      'workflowActivityVNext.editor.retryActivityObservation',
                      'Check again',
                    )}
                  </Button>
                ) : null}
              </Space>
            }
            description={
              <>
                {editor.runObservation.phase === 'observed'
                  ? t(
                      'workflowActivityVNext.editor.activityObservedDescription',
                      'You can review its details and progress.',
                    )
                  : editor.runObservation.phase === 'delayed'
                    ? t(
                        'workflowActivityVNext.editor.activityDelayedDescription',
                        'Try again to check the latest status.',
                      )
                    : editor.runObservation.phase === 'unauthorized'
                      ? t(
                          'workflowActivityVNext.state.unauthorized',
                          'Sign in to continue',
                        )
                      : editor.runObservation.phase === 'forbidden'
                        ? t(
                            'workflowActivityVNext.state.forbidden',
                            "You don't have access to this workspace",
                          )
                        : t(
                            'workflowActivityVNext.editor.activityUnavailableDescription',
                            'Try again to check the latest status.',
                          )}
                {editor.runObservation.error ? (
                  <TechnicalDetails>
                    {errorMessage(editor.runObservation.error)}
                  </TechnicalDetails>
                ) : null}
              </>
            }
            message={
              editor.runObservation.phase === 'observed'
                ? t(
                    'workflowActivityVNext.editor.activityObserved',
                    'Observed in Activity',
                  )
                : editor.runObservation.phase === 'delayed'
                  ? t(
                      'workflowActivityVNext.editor.activityDelayed',
                      'This run is taking longer to appear in Activity',
                    )
                  : editor.runObservation.phase === 'unauthorized'
                    ? t(
                        'workflowActivityVNext.state.unauthorized',
                        'Sign in to continue',
                      )
                    : editor.runObservation.phase === 'forbidden'
                      ? t(
                          'workflowActivityVNext.state.forbidden',
                          "You don't have access to this workspace",
                        )
                      : t(
                          'workflowActivityVNext.editor.activityUnavailable',
                          'Activity unavailable',
                        )
            }
            showIcon
            type={
              editor.runObservation.phase === 'observed'
                ? 'success'
                : editor.runObservation.phase === 'delayed'
                  ? 'warning'
                  : 'error'
            }
          />
        ) : null}
        {observedRun && observedStatus ? (
          <section
            aria-label={t(
              'workflowActivityVNext.editor.runResult',
              'Run result',
            )}
            className="wa-vnext__run-result"
          >
            <div className="wa-vnext__run-result-heading">
              <span
                className={`wa-vnext__status wa-vnext__status--${observedStatus.className}`}
              >
                {observedStatus.label}
              </span>
              <code className="wa-vnext__mono">
                {observedRun.summary.runId}
              </code>
            </div>
            {currentStep ? (
              <p>
                <strong>
                  {t(
                    'workflowActivityVNext.editor.currentStep',
                    'Current step',
                  )}
                </strong>{' '}
                <code className="wa-vnext__mono">{currentStep.stepId}</code>
              </p>
            ) : null}
            {editor.lastRunSnapshot ? (
              <div className="wa-vnext__run-snapshot">
                <strong>
                  {t(
                    'workflowActivityVNext.editor.submittedInput',
                    'Submitted input',
                  )}
                </strong>
                <span>{editor.lastRunSnapshot.input}</span>
                <small>
                  {t(
                    'workflowActivityVNext.editor.snapshotNotice',
                    'Run again uses this exact input with the current published revision.',
                  )}
                </small>
              </div>
            ) : null}
            {observedRun.finalOutput ? (
              <div className="wa-vnext__run-outcome">
                <strong>
                  {t(
                    'workflowActivityVNext.editor.outputSummary',
                    'Output summary',
                  )}
                </strong>
                <p>{observedRun.finalOutput}</p>
              </div>
            ) : null}
            {observedRun.finalError ? (
              <div className="wa-vnext__run-outcome">
                <strong>
                  {t(
                    'workflowActivityVNext.editor.failureSummary',
                    'Failure summary',
                  )}
                </strong>
                <p>{observedRun.finalError}</p>
              </div>
            ) : null}
            {observedRunTerminal ? (
              <Button
                disabled={
                  runBusy || editorWriteLocked || !publishedInvocationTarget
                }
                onClick={() => {
                  if (publishedInvocationTarget) {
                    void editor.runAgain(publishedInvocationTarget);
                  }
                }}
              >
                {t('workflowActivityVNext.editor.runAgain', 'Run again')}
              </Button>
            ) : null}
            <p className="wa-vnext__run-details-note">
              {t(
                'workflowActivityVNext.editor.fullDetailsNotice',
                'Open run details for the full timeline, diagnostics, and recovery actions.',
              )}
            </p>
          </section>
        ) : null}
      </WorkflowPublishedRunDrawer>
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
