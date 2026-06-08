import { Alert, Spin } from "antd";
import React from "react";
import WorkflowStudioCanvas from "./components/WorkflowStudioCanvas";
import WorkflowStudioExecutionPanel from "./components/WorkflowStudioExecutionPanel";
import WorkflowStudioExecutionsPanel from "./components/WorkflowStudioExecutionsPanel";
import WorkflowStudioHeader from "./components/WorkflowStudioHeader";
import WorkflowStudioNodeDetailPanel from "./components/WorkflowStudioNodeDetailPanel";
import WorkflowStudioNodeLibrary from "./components/WorkflowStudioNodeLibrary";
import { useTeamMemberWorkflowStudio } from "./hooks/useTeamMemberWorkflowStudio";

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
        activationChecked={studio.activationChecked}
        activationDisabled={studio.activationDisabled}
        activationNotice={studio.activationNotice}
        activationPending={studio.activationPending}
        activationPlaceholderReason={studio.activationPlaceholderReason}
        activationTone={studio.activationTone}
        canExecute={studio.canExecute}
        canSave={studio.canSave}
        canSetTeamEntry={studio.canSetTeamEntry}
        dirty={studio.dirty}
        executionRunId={studio.executionDetail?.executionId ?? ""}
        executionStartedAt={studio.executionDetail?.startedAtUtc ?? ""}
        executionStatus={studio.executionStatus}
        executePending={studio.executePending}
        executePlaceholderReason={studio.executePlaceholderReason}
        onActivate={studio.activate}
        onAddNode={studio.openNodeLibrary}
        onDeleteNode={studio.deleteSelectedNode}
        onExecute={studio.execute}
        onNavigateBack={studio.navigateBack}
        onRunInputChange={studio.setExecutionRunInput}
        onSave={studio.save}
        onSetTeamEntry={studio.setTeamEntry}
        onTitleChange={studio.setWorkflowTitle}
        runInput={studio.executionRunInput}
        savePending={studio.savePending}
        savePlaceholderReason={studio.savePlaceholderReason}
        selectedNodeId={studio.selectedNodeId}
        selectedTab={studio.selectedTab}
        onTabChange={studio.setSelectedTab}
        teamEntryNotice={studio.teamEntryNotice}
        teamEntryPending={studio.teamEntryPending}
        teamName={studio.teamName}
        workflowTitle={studio.workflowTitle}
      />
      {studio.linkedWorkflowMissing ? (
        <Alert
          banner
          message="No workflow draft is linked to this member yet."
          description="This Phase 1 page only loads workflow members through a stable workflow reference. Add that backend/read-model reference before editing this member here."
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
              onDeleteNodes={(nodeIds) => {
                if (nodeIds.includes(studio.selectedNodeId)) {
                  studio.deleteSelectedNode();
                }
              }}
              onNodeLayoutChange={studio.moveNodes}
              onNodeSelect={studio.selectNode}
              selectedNodeId={studio.selectedNodeId}
            />
          )}
          <WorkflowStudioNodeLibrary
            onClose={studio.closeNodeLibrary}
            onInsertNode={studio.insertNode}
            open={studio.nodeLibraryOpen}
          />
          <WorkflowStudioNodeDetailPanel
            error={studio.selectedStepParameterError}
            onClose={studio.selectCanvas}
            onParametersChange={studio.updateSelectedStepParameters}
            stepDraft={studio.selectedStepDraft}
          />
        </section>
      ) : (
        <WorkflowStudioExecutionsPanel
          emptyReason={studio.executionsEmptyReason}
          error={studio.executionsError}
          executions={studio.executions}
          loading={studio.executionsLoading}
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
