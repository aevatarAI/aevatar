import { Modal } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import { t } from '@/shared/i18n/messages';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import TechnicalDetails from '../TechnicalDetails';
import { observeWorkflowArchival } from './workflowArchival';
import {
  ensureWorkflowObservationActive,
  WORKFLOW_OBSERVATION_DELAYS_MS,
  WORKFLOW_OBSERVATION_TIMEOUT_MS,
} from './workflowObservation';
import { observeWorkflowRemoval } from './workflowRemoval';

type Phase =
  | 'idle'
  | 'submitting'
  | 'observing'
  | 'delayed'
  | 'readFailed'
  | 'failed';

async function readWorkflowMatch(
  scopeId: string,
  workflowId: string,
  archive: boolean,
  signal: AbortSignal,
) {
  let cursor: string | undefined;
  const visitedCursors = new Set<string>();
  do {
    ensureWorkflowObservationActive(signal);
    const response = await scopesApi.queryWorkflowCatalogue(
      {
        scopeId,
        view: archive ? 'archived' : 'all',
        query: workflowId,
        cursor,
        take: 100,
      },
      signal,
    );
    ensureWorkflowObservationActive(signal);
    const target = response.items.find(
      (item) => item.workflowId === workflowId,
    );
    if (target)
      return [
        {
          workflowId: target.workflowId,
          activeRevisionId: target.committed?.activeRevisionId ?? '',
          deploymentId: target.committed?.deploymentId ?? '',
          deploymentStatus: target.committed?.deploymentStatus ?? '',
          hasCommittedSource: target.hasCommittedSource,
        },
      ];
    const nextCursor = response.nextPageToken ?? undefined;
    if (!nextCursor) return [];
    if (visitedCursors.has(nextCursor))
      throw new Error('Workflow catalogue returned a repeated page cursor.');
    visitedCursors.add(nextCursor);
    cursor = nextCursor;
  } while (cursor);
  return [];
}

// Key by scope, operation and workflow in the owner. Closing preserves acceptance
// for the selected operation; reopening can only check that same result.
const WorkflowMutationDialog: React.FC<{
  readonly kind: 'archive' | 'delete';
  readonly scopeId: string;
  readonly workflowId: string;
  readonly workflowName: string;
  readonly open: boolean;
  readonly onClose: () => void;
  readonly onObserved: () => void;
}> = ({
  kind,
  scopeId,
  workflowId,
  workflowName,
  open,
  onClose,
  onObserved,
}) => {
  const archive = kind === 'archive';
  const toast = useConsoleToast();
  const [phase, setPhase] = React.useState<Phase>('idle');
  const [error, setError] = React.useState<unknown>(null);
  const accepted = React.useRef(false);
  const active = React.useRef<AbortController | null>(null);
  const timer = React.useRef<number | undefined>(undefined);
  const stop = React.useCallback(() => {
    window.clearTimeout(timer.current);
    active.current?.abort();
    active.current = null;
  }, []);
  React.useEffect(() => {
    if (!open && accepted.current) setPhase('delayed');
    return stop;
  }, [open, stop]);

  const confirm = async () => {
    if (active.current) return;
    const controller = new AbortController();
    active.current = controller;
    const current = () =>
      active.current === controller && !controller.signal.aborted;
    setError(null);
    setPhase(accepted.current ? 'observing' : 'submitting');
    try {
      if (!accepted.current) {
        if (archive) await scopesApi.archiveWorkflow(scopeId, workflowId);
        else {
          try {
            await studioApi.deleteWorkflowDraft(workflowId, scopeId);
          } catch (cause) {
            if (!isStudioApiStatus(cause, 404)) throw cause;
          }
        }
        if (!current()) return;
        accepted.current = true;
      }
      setPhase('observing');
      timer.current = window.setTimeout(() => {
        if (!current()) return;
        stop();
        setPhase('delayed');
      }, WORKFLOW_OBSERVATION_TIMEOUT_MS);
      const input = {
        workflowId,
        signal: controller.signal,
        delaysMs: WORKFLOW_OBSERVATION_DELAYS_MS,
        readWorkflows: () =>
          readWorkflowMatch(scopeId, workflowId, archive, controller.signal),
      };
      const result = archive
        ? await observeWorkflowArchival(input)
        : await observeWorkflowRemoval(input);
      if (!current()) return;
      if (result.kind === 'delayed') {
        setPhase('delayed');
        return;
      }
      onObserved();
      onClose();
      toast.success(
        archive
          ? t(
              'workflowActivityVNext.workflows.archiveSuccess',
              'Workflow archived',
            )
          : t('workflowActivityVNext.workflows.deleteSuccess', 'Draft deleted'),
      );
    } catch (cause) {
      if (!current()) return;
      setError(cause);
      setPhase(accepted.current ? 'readFailed' : 'failed');
      toast.error(
        accepted.current
          ? t(
              'workflowActivityVNext.workflows.checkFailed',
              "Couldn't check whether the change has finished",
            )
          : archive
            ? t(
                'workflowActivityVNext.workflows.archiveFailed',
                "Workflow couldn't be archived",
              )
            : t(
                'workflowActivityVNext.workflows.deleteFailed',
                "Draft couldn't be deleted",
              ),
      );
    } finally {
      if (active.current === controller) stop();
    }
  };

  const busy = phase === 'submitting' || phase === 'observing';
  const pending =
    phase === 'observing' || phase === 'delayed' || phase === 'readFailed';
  return (
    <Modal
      open={open}
      title={
        archive
          ? t(
              'workflowActivityVNext.workflows.archiveTitle',
              'Archive this workflow?',
            )
          : t(
              'workflowActivityVNext.workflows.deleteTitle',
              'Delete editable draft?',
            )
      }
      cancelText={
        accepted.current
          ? t('workflowActivityVNext.common.close', 'Close')
          : t('workflowActivityVNext.common.cancel', 'Cancel')
      }
      cancelButtonProps={{ disabled: phase === 'submitting' }}
      closable={phase !== 'submitting'}
      keyboard={phase !== 'submitting'}
      mask={{ closable: false }}
      confirmLoading={busy}
      okButtonProps={{ danger: !accepted.current, disabled: busy }}
      okText={
        pending
          ? t(
              'workflowActivityVNext.workflows.archiveCheckAgain',
              'Check again',
            )
          : phase === 'failed'
            ? t('workflowActivityVNext.workflows.archiveTryAgain', 'Try again')
            : archive
              ? t(
                  'workflowActivityVNext.workflows.archiveConfirm',
                  'Archive workflow',
                )
              : t('workflowActivityVNext.workflows.deleteDraft', 'Delete draft')
      }
      onOk={() => void confirm()}
      onCancel={() => {
        if (phase !== 'submitting') {
          stop();
          onClose();
        }
      }}
    >
      <strong>{workflowName}</strong>
      <p>
        {archive
          ? t(
              'workflowActivityVNext.workflows.archiveDescription',
              'This stops new runs for the published workflow. Its editable draft, published revisions, and Activity history remain available. Publishing it again restores it.',
            )
          : t(
              'workflowActivityVNext.workflows.deleteDescription',
              'This deletes only the editable draft. Published versions and run history remain available.',
            )}
      </p>
      {pending ? (
        <div className="wa-vnext__notice" role="status">
          <strong>
            {phase === 'observing'
              ? t(
                  'workflowActivityVNext.workflows.changeObserving',
                  'Request accepted. Checking for completion…',
                )
              : phase === 'readFailed'
                ? t(
                    'workflowActivityVNext.workflows.checkFailed',
                    "Couldn't check whether the change has finished",
                  )
                : t(
                    'workflowActivityVNext.workflows.changeDelayed',
                    'This is taking longer than expected. Completion is still unconfirmed.',
                  )}
          </strong>
          <p>
            {phase === 'observing'
              ? t(
                  'workflowActivityVNext.workflows.checkingDescription',
                  'We will keep checking for up to 30 seconds. Closing this dialog does not undo the request.',
                )
              : t(
                  'workflowActivityVNext.workflows.checkAgainDescription',
                  'Check again to see the result. This only checks the status and does not submit another request.',
                )}
          </p>
        </div>
      ) : null}
      {phase === 'failed' ? (
        <p role="alert">
          {archive
            ? t(
                'workflowActivityVNext.workflows.archiveFailed',
                "Workflow couldn't be archived",
              )
            : t(
                'workflowActivityVNext.workflows.deleteFailed',
                "Draft couldn't be deleted",
              )}
        </p>
      ) : null}
      {error ? (
        <TechnicalDetails>
          {error instanceof Error ? error.message : String(error)}
        </TechnicalDetails>
      ) : null}
    </Modal>
  );
};

export default WorkflowMutationDialog;
