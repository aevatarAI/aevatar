import { Alert, Spin } from "antd";
import React from "react";
import WorkflowStudioCanvas from "./components/WorkflowStudioCanvas";
import WorkflowStudioExecutionPanel from "./components/WorkflowStudioExecutionPanel";
import WorkflowStudioHeader from "./components/WorkflowStudioHeader";
import WorkflowStudioMemberRunsPanel from "./components/WorkflowStudioMemberRunsPanel";
import WorkflowStudioNodeDetailPanel from "./components/WorkflowStudioNodeDetailPanel";
import WorkflowStudioNodeLibrary from "./components/WorkflowStudioNodeLibrary";
import WorkflowStudioRunOptionsPanel from "./components/WorkflowStudioRunOptionsPanel";
import { useTeamMemberWorkflowStudio } from "./hooks/useTeamMemberWorkflowStudio";
import { t } from "@/shared/i18n/messages";

const TeamMemberWorkflowStudioPage: React.FC = () => {
  const studio = useTeamMemberWorkflowStudio();

  return (
    <main
      data-testid="team-member-workflow-studio"
      style={{
        background: "#f3f4f6",
        display: "flex",
        flexDirection: "column",
        height: "100vh",
        minHeight: 0,
        width: "100%",
      }}
    >
      <WorkflowStudioHeader
        memberPublished={studio.memberPublished}
        publishDisabled={studio.publishDisabled}
        publishNotice={studio.publishNotice}
        publishPending={studio.publishPending}
        publishPlaceholderReason={studio.publishPlaceholderReason}
        publishTone={studio.publishTone}
        canRunActiveMember={studio.canRunActiveMember}
        canSave={studio.canSave}
        dirty={studio.dirty}
        activeMemberRunPending={studio.activeMemberRunPending}
        activeMemberRunPlaceholderReason={studio.activeMemberRunPlaceholderReason}
        onPublishMember={studio.publishMember}
        onAddNode={studio.openNodeLibrary}
        onDeleteConnection={studio.deleteSelectedConnection}
        onDeleteNode={studio.deleteSelectedNode}
        onOpenRunOptions={studio.openRunOptions}
        onRunActiveMember={studio.runActiveMember}
        onNavigateBack={studio.navigateBack}
        onSave={studio.save}
        onTitleChange={studio.setWorkflowTitle}
        savePending={studio.savePending}
        savePlaceholderReason={studio.savePlaceholderReason}
        selectedEdgeId={studio.selectedEdgeId}
        selectedNodeId={studio.selectedNodeId}
        selectedTab={studio.selectedTab}
        onTabChange={studio.setSelectedTab}
        teamName={studio.teamName}
        workflowTitle={studio.workflowTitle}
      />
      {studio.linkedWorkflowMissing ? (
        <Alert
          banner
          message={t(
            "teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.title",
            "No workflow draft is linked to this member yet.",
          )}
          description={t(
            "teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.description",
            "This Phase 1 page only loads workflow members through a stable workflow reference. Add that backend/read-model reference before editing this member here.",
          )}
          type="warning"
        />
      ) : null}
      {studio.selectedTab === "editor" ? (
        <section
          style={{
            display: "flex",
            flex: 1,
            minHeight: 0,
            overflow: "hidden",
            position: "relative",
          }}
        >
          {studio.loading ? (
            <div
              style={{
                alignItems: "center",
                display: "flex",
                flex: 1,
                justifyContent: "center",
              }}
            >
              <Spin />
            </div>
          ) : (
            <WorkflowStudioCanvas
              edges={studio.graph.edges}
              emptyDescription={studio.emptyDescription}
              nodes={studio.graph.nodes}
              onAddFirstStep={studio.openNodeLibrary}
              onCanvasSelect={studio.selectCanvas}
              onConnectNodes={studio.connectNodes}
              onDeleteEdges={(edgeIds) => {
                if (edgeIds.includes(studio.selectedEdgeId)) {
                  studio.deleteSelectedConnection();
                }
              }}
              onDeleteNodes={(nodeIds) => {
                if (nodeIds.includes(studio.selectedNodeId)) {
                  studio.deleteSelectedNode();
                }
              }}
              onEdgeSelect={studio.selectEdge}
              onNodeLayoutChange={studio.moveNodes}
              onNodeSelect={studio.selectNode}
              selectedEdgeId={studio.selectedEdgeId}
              selectedNodeId={studio.selectedNodeId}
            />
          )}
          <WorkflowStudioNodeLibrary
            onClose={studio.closeNodeLibrary}
            onInsertNode={studio.insertNode}
            open={studio.nodeLibraryOpen}
          />
          <WorkflowStudioRunOptionsPanel
            onClose={studio.selectCanvas}
            onRunMessageChange={studio.setExecutionRunMessage}
            open={studio.runOptionsOpen}
            runMessage={studio.executionRunMessage}
          />
          {studio.runOptionsOpen ? null : (
            <WorkflowStudioNodeDetailPanel
              error={studio.selectedStepConfigurationError}
              onClose={studio.selectCanvas}
              onConfigurationChange={studio.updateSelectedStepConfiguration}
              stepDraft={studio.selectedStepDraft}
            />
          )}
        </section>
      ) : (
        <WorkflowStudioMemberRunsPanel
          emptyReason={studio.memberRunsEmptyReason}
          error={studio.memberRunsError}
          executions={studio.memberRuns}
          loading={studio.memberRunsLoading}
          onOpenExecution={studio.openExecution}
        />
      )}
      <WorkflowStudioExecutionPanel
        detail={studio.executionDetail}
        error={studio.executionError}
      />
    </main>
  );
};

export default TeamMemberWorkflowStudioPage;
