import { Alert, Spin } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import WorkflowStudioCanvas from "./components/WorkflowStudioCanvas";
import WorkflowStudioAiProposalPanel from "./components/WorkflowStudioAiProposalPanel";
import WorkflowStudioDraftRunPanel from "./components/WorkflowStudioDraftRunPanel";
import WorkflowStudioExecutionPanel from "./components/WorkflowStudioExecutionPanel";
import WorkflowStudioHeader from "./components/WorkflowStudioHeader";
import WorkflowStudioNodeDetailPanel from "./components/WorkflowStudioNodeDetailPanel";
import WorkflowStudioNodeLibrary from "./components/WorkflowStudioNodeLibrary";
import WorkflowStudioYamlPanel from "./components/WorkflowStudioYamlPanel";
import { useTeamMemberWorkflowStudio } from "./hooks/useTeamMemberWorkflowStudio";

const SIDE_PANEL_DEFAULT_WIDTH = 420;
const SIDE_PANEL_MIN_WIDTH = 320;
const SIDE_PANEL_MAX_WIDTH = 640;
const WORKFLOW_CANVAS_MIN_WIDTH = 360;
const EXECUTION_PANEL_DEFAULT_HEIGHT = 210;
const EXECUTION_PANEL_MIN_HEIGHT = 160;
const EXECUTION_PANEL_MAX_HEIGHT = 520;
const RESIZE_KEYBOARD_STEP = 24;

function clampDimension(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function resolveSidePanelMaxWidth(container: HTMLElement | null) {
  if (!container?.clientWidth) {
    return SIDE_PANEL_MAX_WIDTH;
  }

  return Math.max(
    SIDE_PANEL_MIN_WIDTH,
    Math.min(SIDE_PANEL_MAX_WIDTH, container.clientWidth - WORKFLOW_CANVAS_MIN_WIDTH),
  );
}

function resolveExecutionPanelMaxHeight(container: HTMLElement | null) {
  if (!container?.clientHeight) {
    return EXECUTION_PANEL_MAX_HEIGHT;
  }

  return Math.max(
    EXECUTION_PANEL_MIN_HEIGHT,
    Math.min(EXECUTION_PANEL_MAX_HEIGHT, Math.floor(container.clientHeight * 0.6)),
  );
}

const TeamMemberWorkflowStudioPage: React.FC = () => {
  const studio = useTeamMemberWorkflowStudio();
  const mainRef = React.useRef<HTMLElement | null>(null);
  const editorRegionRef = React.useRef<HTMLElement | null>(null);
  const resizeCleanupRef = React.useRef<(() => void) | null>(null);
  const [sidePanelWidth, setSidePanelWidth] = React.useState(
    SIDE_PANEL_DEFAULT_WIDTH,
  );
  const [executionPanelHeight, setExecutionPanelHeight] = React.useState(
    EXECUTION_PANEL_DEFAULT_HEIGHT,
  );
  const sidePanelOpen =
    studio.aiProposalOpen || studio.draftRunPanelOpen || studio.yamlPanelOpen;
  const executionPanelOpen = Boolean(studio.executionDetail || studio.executionError);

  React.useEffect(
    () => () => {
      resizeCleanupRef.current?.();
    },
    [],
  );

  const attachResizeListeners = React.useCallback(
    (cursor: "col-resize" | "row-resize", onMouseMove: (event: MouseEvent) => void) => {
      resizeCleanupRef.current?.();

      const previousCursor = document.body.style.cursor;
      const previousUserSelect = document.body.style.userSelect;
      document.body.style.cursor = cursor;
      document.body.style.userSelect = "none";

      const cleanup = () => {
        window.removeEventListener("mousemove", onMouseMove);
        window.removeEventListener("mouseup", cleanup);
        document.body.style.cursor = previousCursor;
        document.body.style.userSelect = previousUserSelect;
        if (resizeCleanupRef.current === cleanup) {
          resizeCleanupRef.current = null;
        }
      };

      resizeCleanupRef.current = cleanup;
      window.addEventListener("mousemove", onMouseMove);
      window.addEventListener("mouseup", cleanup);
    },
    [],
  );

  const updateSidePanelWidth = React.useCallback((nextWidth: number) => {
    setSidePanelWidth(
      clampDimension(
        nextWidth,
        SIDE_PANEL_MIN_WIDTH,
        resolveSidePanelMaxWidth(editorRegionRef.current),
      ),
    );
  }, []);

  const updateExecutionPanelHeight = React.useCallback((nextHeight: number) => {
    setExecutionPanelHeight(
      clampDimension(
        nextHeight,
        EXECUTION_PANEL_MIN_HEIGHT,
        resolveExecutionPanelMaxHeight(mainRef.current),
      ),
    );
  }, []);

  const startSidePanelResize = React.useCallback(
    (event: React.MouseEvent<HTMLHRElement>) => {
      event.preventDefault();
      event.currentTarget.focus();

      const startX = event.clientX;
      const startWidth = sidePanelWidth;
      const maxWidth = resolveSidePanelMaxWidth(editorRegionRef.current);

      attachResizeListeners("col-resize", (moveEvent) => {
        setSidePanelWidth(
          clampDimension(
            startWidth + (startX - moveEvent.clientX),
            SIDE_PANEL_MIN_WIDTH,
            maxWidth,
          ),
        );
      });
    },
    [attachResizeListeners, sidePanelWidth],
  );

  const startExecutionPanelResize = React.useCallback(
    (event: React.MouseEvent<HTMLHRElement>) => {
      event.preventDefault();
      event.currentTarget.focus();

      const startY = event.clientY;
      const startHeight = executionPanelHeight;
      const maxHeight = resolveExecutionPanelMaxHeight(mainRef.current);

      attachResizeListeners("row-resize", (moveEvent) => {
        setExecutionPanelHeight(
          clampDimension(
            startHeight + (startY - moveEvent.clientY),
            EXECUTION_PANEL_MIN_HEIGHT,
            maxHeight,
          ),
        );
      });
    },
    [attachResizeListeners, executionPanelHeight],
  );

  const handleSidePanelResizeKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLHRElement>) => {
      if (event.key === "ArrowLeft") {
        event.preventDefault();
        updateSidePanelWidth(sidePanelWidth + RESIZE_KEYBOARD_STEP);
        return;
      }

      if (event.key === "ArrowRight") {
        event.preventDefault();
        updateSidePanelWidth(sidePanelWidth - RESIZE_KEYBOARD_STEP);
        return;
      }

      if (event.key === "Home") {
        event.preventDefault();
        updateSidePanelWidth(SIDE_PANEL_MIN_WIDTH);
        return;
      }

      if (event.key === "End") {
        event.preventDefault();
        updateSidePanelWidth(resolveSidePanelMaxWidth(editorRegionRef.current));
      }
    },
    [sidePanelWidth, updateSidePanelWidth],
  );

  const handleExecutionPanelResizeKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLHRElement>) => {
      if (event.key === "ArrowUp") {
        event.preventDefault();
        updateExecutionPanelHeight(executionPanelHeight + RESIZE_KEYBOARD_STEP);
        return;
      }

      if (event.key === "ArrowDown") {
        event.preventDefault();
        updateExecutionPanelHeight(executionPanelHeight - RESIZE_KEYBOARD_STEP);
        return;
      }

      if (event.key === "Home") {
        event.preventDefault();
        updateExecutionPanelHeight(EXECUTION_PANEL_MIN_HEIGHT);
        return;
      }

      if (event.key === "End") {
        event.preventDefault();
        updateExecutionPanelHeight(resolveExecutionPanelMaxHeight(mainRef.current));
      }
    },
    [executionPanelHeight, updateExecutionPanelHeight],
  );

  return (
    <main
      data-testid="team-member-workflow-studio"
      ref={mainRef}
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
        automationsHref={studio.automationsHref}
        automationsPlaceholderReason={studio.automationsPlaceholderReason}
        canOpenAutomations={studio.canOpenAutomations}
        canAskAi={studio.canAskAi}
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
        currentDraftRunPlaceholderReason={studio.currentDraftRunPlaceholderReason}
        onOpenAutomations={studio.navigateToAutomations}
        onOpenAiProposal={studio.openAiProposalPanel}
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
            "teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.title",
            "No workflow draft is linked to this member yet.",
          )}
          description={studio.linkedWorkflowMissingDescription}
          type="warning"
        />
      ) : null}
      {studio.linkedWorkflowLoadFailed ? (
        <Alert
          banner
          message={t(
            "teamMemberWorkflowStudio.alerts.linkedWorkflowLoadFailed.title",
            "Workflow draft could not be loaded.",
          )}
          description={studio.linkedWorkflowLoadFailureDescription}
          type="error"
        />
      ) : null}
      <section
        ref={editorRegionRef}
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
        {sidePanelOpen ? (
          <hr
            aria-label={t(
              "teamMemberWorkflowStudio.resize.sidePanel",
              "Resize side panel",
            )}
            aria-orientation="vertical"
            aria-valuemax={resolveSidePanelMaxWidth(editorRegionRef.current)}
            aria-valuemin={SIDE_PANEL_MIN_WIDTH}
            aria-valuenow={sidePanelWidth}
            onKeyDown={handleSidePanelResizeKeyDown}
            onMouseDown={startSidePanelResize}
            style={{
              alignItems: "center",
              background: "#cbd5e1",
              borderBottom: 0,
              borderLeft: "3px solid #ffffff",
              borderRight: "3px solid #ffffff",
              borderTop: 0,
              boxSizing: "border-box",
              cursor: "col-resize",
              display: "flex",
              flex: "0 0 10px",
              height: "100%",
              justifyContent: "center",
              margin: 0,
              minHeight: 0,
              position: "relative",
              zIndex: 2,
            }}
            tabIndex={0}
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
        <WorkflowStudioAiProposalPanel
          error={studio.aiProposalError}
          hasConflict={studio.aiProposalHasConflict}
          onApply={studio.applyAiProposal}
          onClose={studio.closeAiProposalPanel}
          onGenerate={studio.generateAiProposal}
          onPromptChange={studio.setAiProposalPrompt}
          open={studio.aiProposalOpen}
          pending={studio.aiProposalPending}
          prompt={studio.aiProposalPrompt}
          proposalYaml={studio.aiProposalYaml}
          reasoning={studio.aiProposalReasoning}
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
        {studio.aiProposalOpen ||
        studio.draftRunPanelOpen ||
        studio.yamlPanelOpen ? null : (
          <WorkflowStudioNodeDetailPanel
            error={studio.selectedStepConfigurationError}
            onClose={studio.selectCanvas}
            onConfigurationChange={studio.updateSelectedStepConfiguration}
            onConfigurationErrorChange={studio.setSelectedStepConfigurationError}
            stepDraft={studio.selectedStepDraft}
          />
        )}
      </section>
      {executionPanelOpen ? (
        <hr
          aria-label={t(
            "teamMemberWorkflowStudio.resize.executionPanel",
            "Resize run console",
          )}
          aria-orientation="horizontal"
          aria-valuemax={resolveExecutionPanelMaxHeight(mainRef.current)}
          aria-valuemin={EXECUTION_PANEL_MIN_HEIGHT}
          aria-valuenow={executionPanelHeight}
          onKeyDown={handleExecutionPanelResizeKeyDown}
          onMouseDown={startExecutionPanelResize}
          style={{
            alignItems: "flex-start",
            background: "#cbd5e1",
            borderBottom: "4px solid #ffffff",
            borderLeft: 0,
            borderRight: 0,
            borderTop: "4px solid #ffffff",
            boxSizing: "border-box",
            cursor: "row-resize",
            display: "flex",
            flex: "0 0 12px",
            height: 12,
            justifyContent: "center",
            margin: 0,
            position: "relative",
            zIndex: 3,
          }}
          tabIndex={0}
        />
      ) : null}
      <WorkflowStudioExecutionPanel
        activeLogIndex={studio.activeExecutionLogIndex}
        clearDisabled={studio.currentDraftRunPending}
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
