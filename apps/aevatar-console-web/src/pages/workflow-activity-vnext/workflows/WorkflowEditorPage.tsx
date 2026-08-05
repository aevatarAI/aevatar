import {
  ArrowLeftOutlined,
  CodeOutlined,
  NodeIndexOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  RocketOutlined,
  SaveOutlined,
} from '@ant-design/icons';
import { Alert, Button, Input, Modal, Segmented, Space } from 'antd';
import React from 'react';
import WorkflowStudioCanvas from '@/pages/team-member-workflow-studio/components/WorkflowStudioCanvas';
import WorkflowStudioNodeDetailPanel from '@/pages/team-member-workflow-studio/components/WorkflowStudioNodeDetailPanel';
import WorkflowStudioNodeLibrary from '@/pages/team-member-workflow-studio/components/WorkflowStudioNodeLibrary';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { useWorkflowEditor } from '../hooks/useWorkflowEditor';
import { buildWorkflowActivitySectionHref } from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const WorkflowEditorPage: React.FC<{
  readonly scopeId: string;
  readonly workflowId: string;
}> = ({ scopeId, workflowId }) => {
  const editor = useWorkflowEditor(scopeId, workflowId);
  const [mode, setMode] = React.useState<'canvas' | 'yaml'>('canvas');
  const [nodeLibraryOpen, setNodeLibraryOpen] = React.useState(false);
  const [runPanelOpen, setRunPanelOpen] = React.useState(false);
  const [pendingNavigation, setPendingNavigation] = React.useState('');

  const requestNavigation = React.useCallback(
    (target: string) => {
      if (editor.dirty) setPendingNavigation(target);
      else history.push(target);
    },
    [editor.dirty],
  );

  const saveAndLeave = async () => {
    if (await editor.save()) {
      const target = pendingNavigation;
      setPendingNavigation('');
      if (target) history.push(target);
    }
  };

  const discardAndLeave = () => {
    const target = pendingNavigation;
    setPendingNavigation('');
    if (target) history.push(target);
  };

  if (editor.loading) {
    return (
      <WorkflowActivityVNextShell
        activeSection="workflows"
        description={t(
          'workflowActivityVNext.editor.loadingDescription',
          'Preparing the editor…',
        )}
        scopeId={scopeId}
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
        scopeId={scopeId}
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
    editor.runPhase === 'submitting' || editor.runPhase === 'accepted';
  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      description={t(
        'workflowActivityVNext.editor.description',
        'Build, test, and refine this workflow.',
      )}
      headerActions={
        <>
          <Button
            aria-label={t(
              'workflowActivityVNext.editor.backAria',
              'Back to workflows',
            )}
            icon={<ArrowLeftOutlined />}
            onClick={() =>
              requestNavigation(
                buildWorkflowActivitySectionHref(scopeId, 'workflows'),
              )
            }
          />
          <Button
            icon={<SaveOutlined />}
            loading={editor.saving}
            onClick={() => void editor.save()}
            type="primary"
          >
            {t('workflowActivityVNext.editor.save', 'Save workflow')}
          </Button>
          <Button
            disabled={!editor.canRun}
            icon={<PlayCircleOutlined />}
            onClick={() => setRunPanelOpen(true)}
            title={
              !editor.canRun
                ? t(
                    'workflowActivityVNext.editor.runUnavailable',
                    'Add at least one valid step before running.',
                  )
                : undefined
            }
          >
            {t('workflowActivityVNext.common.run', 'Run')}
          </Button>
          <Button
            disabled
            icon={<RocketOutlined />}
            title={t(
              'workflowActivityVNext.editor.publishUnavailable',
              "Publishing isn't available for this workflow yet.",
            )}
          >
            {t('workflowActivityVNext.editor.publish', 'Publish')}
          </Button>
        </>
      }
      onNavigate={requestNavigation}
      scopeId={scopeId}
      title={
        editor.workflowTitle ||
        t('workflowActivityVNext.editor.untitled', 'Untitled workflow')
      }
    >
      <div className="wa-vnext__toolbar">
        <Input
          aria-label={t('workflowActivityVNext.new.name', 'Workflow name')}
          onChange={(event) => editor.updateTitle(event.target.value)}
          style={{ maxWidth: 420 }}
          value={editor.workflowTitle}
        />
        <Space>
          <span
            className={`wa-vnext__status wa-vnext__status--${editor.dirty ? 'pending' : 'succeeded'}`}
          >
            {editor.dirty
              ? t('workflowActivityVNext.editor.unsaved', 'Unsaved')
              : t('workflowActivityVNext.editor.saved', 'Saved')}
          </span>
          <Segmented
            onChange={(value) => setMode(value as 'canvas' | 'yaml')}
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
        </Space>
      </div>
      {editor.saveError ? (
        <Alert
          description={<TechnicalDetails>{editor.saveError}</TechnicalDetails>}
          message={t(
            'workflowActivityVNext.editor.saveFailed',
            "Workflow couldn't be saved",
          )}
          showIcon
          type="error"
        />
      ) : null}
      {editor.materialization.phase !== 'idle' &&
      editor.materialization.receipt ? (
        <Alert
          action={
            editor.materialization.phase === 'delayed' ||
            editor.materialization.phase === 'failed' ? (
              <Button onClick={() => void editor.materialization.retry()}>
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
            editor.materialization.phase === 'accepted' ||
            editor.materialization.phase === 'observing'
              ? t(
                  'workflowActivityVNext.editor.savingProgress',
                  'Saving workflow…',
                )
              : editor.materialization.phase === 'delayed'
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
                : editor.materialization.phase === 'readable'
                  ? 'success'
                  : 'info'
          }
        />
      ) : null}
      {editor.findings.length > 0 ? (
        <div
          aria-live="polite"
          style={{ display: 'grid', gap: 6, marginBottom: 12 }}
        >
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
      <div
        style={{
          border: '1px solid var(--wa-line)',
          height: 'min(620px, calc(100vh - 260px))',
          minHeight: 440,
          position: 'relative',
        }}
      >
        {mode === 'canvas' ? (
          <>
            <div style={{ height: '100%' }}>
              <WorkflowStudioCanvas
                edges={editor.graph.edges}
                emptyDescription={t(
                  'workflowActivityVNext.editor.emptyCanvas',
                  'Add the first executable node to make this workflow runnable.',
                )}
                nodes={editor.graph.nodes}
                onAddFirstStep={() => setNodeLibraryOpen(true)}
                onCanvasSelect={editor.selectCanvas}
                onNodeSelect={editor.selectNode}
                selectedNodeId={editor.selectedNodeId}
              />
            </div>
            <Button
              icon={<PlusOutlined />}
              onClick={() => setNodeLibraryOpen(true)}
              style={{ left: 16, position: 'absolute', top: 16, zIndex: 5 }}
            >
              {t('workflowActivityVNext.editor.addNode', 'Add node')}
            </Button>
            <WorkflowStudioNodeLibrary
              onClose={() => setNodeLibraryOpen(false)}
              onInsertNode={(stepType) => {
                void editor.addNode(stepType);
                setNodeLibraryOpen(false);
              }}
              open={nodeLibraryOpen}
            />
            <WorkflowStudioNodeDetailPanel
              error={editor.selectedStepConfigurationError}
              onClose={editor.selectCanvas}
              onConfigurationChange={(parametersText) =>
                void editor.updateSelectedStepConfiguration(parametersText)
              }
              onConfigurationErrorChange={
                editor.setSelectedStepConfigurationError
              }
              stepDraft={editor.selectedStepDraft}
            />
          </>
        ) : (
          <Input.TextArea
            aria-label={t('workflowActivityVNext.new.yaml', 'Workflow YAML')}
            onChange={(event) => editor.updateYaml(event.target.value)}
            style={{
              border: 0,
              borderRadius: 0,
              fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace',
              height: '100%',
              resize: 'none',
            }}
            value={editor.yaml}
          />
        )}
      </div>
      {runPanelOpen ? (
        <section
          aria-label={t('workflowActivityVNext.editor.runPanel', 'Test run')}
          className="wa-vnext__panel"
          style={{ marginTop: 16 }}
        >
          <Space direction="vertical" style={{ width: '100%' }}>
            <strong>
              {t('workflowActivityVNext.editor.runPanel', 'Test run')}
            </strong>
            <Input.TextArea
              aria-label={t(
                'workflowActivityVNext.editor.runInput',
                'Test input',
              )}
              disabled={runBusy}
              onChange={(event) => editor.setRunInput(event.target.value)}
              rows={4}
              value={editor.runInput}
            />
            <Space>
              <Button
                disabled={runBusy || !editor.canRun}
                loading={editor.runPhase === 'submitting'}
                onClick={() => void editor.run()}
                type="primary"
              >
                {t('workflowActivityVNext.editor.submitRun', 'Start run')}
              </Button>
              <Button onClick={() => setRunPanelOpen(false)}>
                {t('workflowActivityVNext.common.close', 'Close')}
              </Button>
              <Button
                onClick={() =>
                  history.push(
                    buildWorkflowActivitySectionHref(scopeId, 'activity'),
                  )
                }
              >
                {t(
                  'workflowActivityVNext.editor.openActivity',
                  'Open Activity',
                )}
              </Button>
            </Space>
            {editor.runPhase !== 'idle' ? (
              <Alert
                message={
                  editor.runPhase === 'submitting'
                    ? t(
                        'workflowActivityVNext.editor.runSubmitting',
                        'Starting run…',
                      )
                    : editor.runPhase === 'accepted'
                      ? t(
                          'workflowActivityVNext.editor.runAccepted',
                          'Run started',
                        )
                      : editor.runPhase === 'stream_ended'
                        ? t(
                            'workflowActivityVNext.editor.streamEnded',
                            'Live updates ended. Open Activity to check the latest status.',
                          )
                        : t(
                            'workflowActivityVNext.editor.runFailed',
                            'Run failed',
                          )
                }
                description={
                  editor.runError ? (
                    <TechnicalDetails>{editor.runError}</TechnicalDetails>
                  ) : (
                    t(
                      'workflowActivityVNext.editor.eventCount',
                      'Received {count} updates',
                      { count: editor.runEventCount },
                    )
                  )
                }
                showIcon
                type={editor.runPhase === 'failed' ? 'error' : 'info'}
              />
            ) : null}
          </Space>
        </section>
      ) : null}
      <Modal
        aria-label={t(
          'workflowActivityVNext.editor.unsavedTitle',
          'Unsaved workflow changes',
        )}
        footer={[
          <Button key="stay" onClick={() => setPendingNavigation('')}>
            {t('workflowActivityVNext.editor.stay', 'Stay')}
          </Button>,
          <Button key="discard" onClick={discardAndLeave}>
            {t(
              'workflowActivityVNext.editor.discardLeave',
              'Discard and leave',
            )}
          </Button>,
          <Button
            key="save"
            loading={editor.saving}
            onClick={() => void saveAndLeave()}
            type="primary"
          >
            {t('workflowActivityVNext.editor.saveLeave', 'Save and leave')}
          </Button>,
        ]}
        onCancel={() => setPendingNavigation('')}
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
