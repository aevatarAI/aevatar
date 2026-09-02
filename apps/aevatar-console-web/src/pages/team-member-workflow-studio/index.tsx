import { Alert, Spin } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import { useWorkflowPanelResize } from '@/shared/workflows/useWorkflowPanelResize';
import WorkflowPanelResizeHandle from '@/shared/workflows/WorkflowPanelResizeHandle';
import WorkflowStudioDraftRunPanel from './components/WorkflowStudioDraftRunPanel';
import WorkflowStudioEditorSurface from './components/WorkflowStudioEditorSurface';
import WorkflowStudioExecutionPanel from './components/WorkflowStudioExecutionPanel';
import WorkflowStudioHeader from './components/WorkflowStudioHeader';
import WorkflowStudioNodeDetailPanel from './components/WorkflowStudioNodeDetailPanel';
import WorkflowStudioYamlPanel from './components/WorkflowStudioYamlPanel';
import { useTeamMemberWorkflowStudio } from './hooks/useTeamMemberWorkflowStudio';

const TeamMemberWorkflowStudioPage: React.FC = () => {
  const studio = useTeamMemberWorkflowStudio();
  const mainRef = React.useRef<HTMLElement | null>(null);
  const editorRegionRef = React.useRef<HTMLElement | null>(null);
  const {
    executionPanelHandleProps,
    executionPanelHeight,
    sidePanelHandleProps,
    sidePanelWidth,
  } = useWorkflowPanelResize({
    editorRegionRef,
    initialExecutionPanelHeight: 210,
    mainRef,
  });
  const sidePanelOpen = studio.draftRunPanelOpen || studio.yamlPanelOpen;
  const executionPanelOpen = Boolean(
    studio.executionDetail || studio.executionError,
  );

  return (
    <main
      data-testid="team-member-workflow-studio"
      ref={mainRef}
      style={{
        background: '#f3f4f6',
        display: 'flex',
        flexDirection: 'column',
        height: '100vh',
        minHeight: 0,
        width: '100%',
      }}
    >
      <WorkflowStudioHeader
        automationsHref={studio.automationsHref}
        automationsPlaceholderReason={studio.automationsPlaceholderReason}
        canOpenAutomations={studio.canOpenAutomations}
        canOpenInvoke={studio.canOpenInvoke}
        canOpenPublishedRuns={studio.canOpenPublishedRuns}
        invokeHref={studio.invokeHref}
        invokePlaceholderReason={studio.invokePlaceholderReason}
        memberPublished={studio.memberPublished}
        publishedRunsHref={studio.publishedRunsHref}
        publishedRunsPlaceholderReason={studio.publishedRunsPlaceholderReason}
        publishDisabled={studio.publishDisabled}
        publishNotice={studio.publishNotice}
        publishPending={studio.publishPending}
        publishPlaceholderReason={studio.publishPlaceholderReason}
        publishTone={studio.publishTone}
        refreshPublishStatusPending={studio.refreshPublishStatusPending}
        showRefreshPublishStatus={studio.showRefreshPublishStatus}
        canOpenDraftRunPanel={studio.canOpenDraftRunPanel}
        canSave={studio.canSave}
        canEditYaml={studio.canEditYaml}
        dirty={studio.dirty}
        currentDraftRunPlaceholderReason={
          studio.currentDraftRunPlaceholderReason
        }
        onOpenAutomations={studio.navigateToAutomations}
        onOpenInvoke={studio.navigateToInvoke}
        onOpenPublishedRuns={studio.navigateToPublishedRuns}
        onPublishMember={studio.publishMember}
        onRefreshPublishStatus={studio.refreshPublishStatus}
        onAddNode={studio.openNodeLibrary}
        onDeleteConnection={studio.deleteSelectedConnection}
        onDeleteNode={studio.deleteSelectedNode}
        onOpenDraftRunPanel={studio.openDraftRunPanel}
        onEditYaml={studio.openYamlPanel}
        onNavigateBack={studio.navigateBack}
        onNavigateToTeam={studio.navigateToTeam}
        onNavigateToTeams={studio.navigateToTeams}
        onSave={studio.save}
        onTitleChange={studio.setWorkflowTitle}
        savePending={studio.savePending}
        savePlaceholderReason={studio.savePlaceholderReason}
        selectedEdgeId={studio.selectedEdgeId}
        selectedNodeId={studio.selectedNodeId}
        teamHref={studio.teamHref}
        teamName={studio.teamName}
        teamsHref={studio.teamsHref}
        workflowTitle={studio.workflowTitle}
      />
      {studio.linkedWorkflowMissing ? (
        <Alert
          banner
          message={t(
            'teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.title',
            'No workflow draft is linked to this member yet.',
          )}
          description={studio.linkedWorkflowMissingDescription}
          type="warning"
        />
      ) : null}
      {studio.linkedWorkflowLoadFailed ? (
        <Alert
          banner
          message={t(
            'teamMemberWorkflowStudio.alerts.linkedWorkflowLoadFailed.title',
            'Workflow draft could not be loaded.',
          )}
          description={studio.linkedWorkflowLoadFailureDescription}
          type="error"
        />
      ) : null}
      <section
        ref={editorRegionRef}
        style={{
          display: 'flex',
          flex: 1,
          minHeight: 0,
          overflow: 'hidden',
          position: 'relative',
        }}
      >
        {studio.loading ? (
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              flex: 1,
              justifyContent: 'center',
            }}
          >
            <Spin />
          </div>
        ) : (
          <WorkflowStudioEditorSurface
            edges={studio.graph.edges}
            emptyDescription={studio.emptyDescription}
            nodeLibraryOpen={studio.nodeLibraryOpen}
            nodes={studio.graph.nodes}
            onAddFirstStep={studio.openNodeLibrary}
            onCanvasSelect={studio.selectCanvas}
            onCloseNodeLibrary={studio.closeNodeLibrary}
            onConnectNodes={studio.connectNodes}
            onDeleteEdges={(edgeIds) => {
              const [edgeId] = edgeIds;
              if (edgeId) {
                studio.deleteSelectedConnection(edgeId);
              }
            }}
            onDeleteNodes={(nodeIds) => {
              if (nodeIds.includes(studio.selectedNodeId)) {
                studio.deleteSelectedNode();
              }
            }}
            onEdgeSelect={studio.selectEdge}
            onInsertNode={studio.insertNode}
            onNodeLayoutChange={studio.moveNodes}
            onNodeSelect={studio.selectNode}
            selectedEdgeId={studio.selectedEdgeId}
            selectedNodeId={studio.selectedNodeId}
          >
            {studio.draftRunPanelOpen || studio.yamlPanelOpen ? null : (
              <WorkflowStudioNodeDetailPanel
                error={studio.selectedStepConfigurationError}
                onClose={studio.selectCanvas}
                onConfigurationChange={studio.updateSelectedStepConfiguration}
                onConfigurationErrorChange={
                  studio.setSelectedStepConfigurationError
                }
                stepDraft={studio.selectedStepDraft}
              />
            )}
          </WorkflowStudioEditorSurface>
        )}
        {sidePanelOpen ? (
          <WorkflowPanelResizeHandle
            ariaLabel={t(
              'teamMemberWorkflowStudio.resize.sidePanel',
              'Resize side panel',
            )}
            orientation="vertical"
            {...sidePanelHandleProps}
          />
        ) : null}
        <WorkflowStudioDraftRunPanel
          canRun={studio.canRunCurrentDraft}
          disabledReason={studio.currentDraftRunPlaceholderReason}
          files={studio.draftRunFiles}
          onFilesAdd={studio.addDraftRunFiles}
          onFileRemove={studio.removeDraftRunFile}
          onClose={studio.selectCanvas}
          onRun={studio.runCurrentDraft}
          onRunMessageChange={studio.setExecutionRunMessage}
          open={studio.draftRunPanelOpen}
          pending={studio.currentDraftRunPending}
          runMessage={studio.executionRunMessage}
          width={sidePanelWidth}
        />
        <WorkflowStudioYamlPanel
          applying={studio.yamlEditApplying}
          buffer={studio.yamlEditBuffer}
          diagnostics={studio.yamlEditDiagnostics}
          error={studio.yamlEditError}
          hasBlockingFindings={studio.yamlEditHasBlockingFindings}
          hasConflict={studio.yamlEditHasConflict}
          hasUnappliedChanges={studio.yamlEditHasUnappliedChanges}
          editorLoading={studio.yamlEditOpening}
          loading={studio.yamlEditOpening || studio.yamlEditPending}
          onApply={studio.applyYamlEdit}
          onBufferChange={studio.setYamlEditBuffer}
          onClose={studio.closeYamlPanel}
          open={studio.yamlPanelOpen}
          width={sidePanelWidth}
        />
      </section>
      {executionPanelOpen ? (
        <WorkflowPanelResizeHandle
          ariaLabel={t(
            'teamMemberWorkflowStudio.resize.executionPanel',
            'Resize run console',
          )}
          orientation="horizontal"
          {...executionPanelHandleProps}
        />
      ) : null}
      <WorkflowStudioExecutionPanel
        activeLogIndex={studio.activeExecutionLogIndex}
        detail={studio.executionDetail}
        error={studio.executionError}
        height={executionPanelHeight}
        onClear={studio.clearExecutionLogs}
        onSelectLog={studio.selectExecutionLog}
        workflowNodes={studio.executionWorkflowNodes}
      />
    </main>
  );
};

export default TeamMemberWorkflowStudioPage;
