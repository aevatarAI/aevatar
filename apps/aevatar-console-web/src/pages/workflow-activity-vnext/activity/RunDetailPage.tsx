import { ArrowLeftOutlined, ReloadOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Descriptions, Modal, Space, Tabs } from "antd";
import React from "react";
import {
  workflowActivityApi,
  WorkflowActivityApiError,
} from "@/shared/api/workflowActivityApi";
import { t } from "@/shared/i18n/messages";
import { history } from "@/shared/navigation/history";
import type { WorkflowRunForkAcceptedReceipt } from "@/shared/models/workflowActivity";
import WorkflowActivityVNextShell from "../WorkflowActivityVNextShell";
import { buildWorkflowActivitySectionHref } from "../navigation";
import { resolveRunRecovery } from "./runRecovery";

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function failureTitle(error: unknown): string {
  if (error instanceof WorkflowActivityApiError) {
    if (error.status === 401) {
      return t("workflowActivityVNext.state.unauthorized", "Authentication required");
    }
    if (error.status === 403) {
      return t("workflowActivityVNext.state.forbidden", "Scope access forbidden");
    }
  }
  return t("workflowActivityVNext.run.unavailable", "Run unavailable");
}

const RunDetailPage: React.FC<{ readonly runId: string; readonly scopeId: string }> = ({ runId, scopeId }) => {
  const detail = useQuery({ queryKey: ["workflow-activity-vnext", "run-detail", scopeId, runId], queryFn: () => workflowActivityApi.getRun(scopeId, runId), retry: false });
  const graph = useQuery({ queryKey: ["workflow-activity-vnext", "run-graph", scopeId, runId], queryFn: () => workflowActivityApi.getRunGraph(scopeId, runId), retry: false });
  const [forking, setForking] = React.useState(false);
  const [forkError, setForkError] = React.useState("");
  const [receipt, setReceipt] = React.useState<WorkflowRunForkAcceptedReceipt | null>(null);
  const [pendingRecovery, setPendingRecovery] = React.useState<{
    readonly kind: "retry" | "run_again";
    readonly stepId: string;
  } | null>(null);

  const recovery = resolveRunRecovery(detail.data?.steps ?? [], graph.data);
  const fork = async (startAtStepId: string): Promise<boolean> => {
    if (forking) return false;
    setForking(true);
    setForkError("");
    try {
      setReceipt(await workflowActivityApi.forkRun({ sourceRunId: runId, startAtStepId, input: detail.data?.input }));
      return true;
    } catch (error) {
      setForkError(errorMessage(error));
      return false;
    } finally {
      setForking(false);
    }
  };

  const confirmFork = async () => {
    if (!pendingRecovery) return;
    if (await fork(pendingRecovery.stepId)) setPendingRecovery(null);
  };

  if (detail.isPending) return <WorkflowActivityVNextShell activeSection="activity" description={t("workflowActivityVNext.run.loadingDescription", "Loading committed run facts.")} scopeId={scopeId} title={t("workflowActivityVNext.run.loading", "Loading run")}><div className="wa-vnext__state"><p>{t("workflowActivityVNext.run.loading", "Loading run")}</p></div></WorkflowActivityVNextShell>;
  if (detail.isError || !detail.data) return <WorkflowActivityVNextShell activeSection="activity" description={t("workflowActivityVNext.run.unavailableDescription", "The immutable run detail is unavailable.")} scopeId={scopeId} title={failureTitle(detail.error)}><div className="wa-vnext__state" role="alert"><div><h2>{failureTitle(detail.error)}</h2><p>{errorMessage(detail.error)}</p><Button onClick={() => void detail.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button></div></div></WorkflowActivityVNextShell>;

  const run = detail.data;
  return (
    <WorkflowActivityVNextShell activeSection="activity" description={t("workflowActivityVNext.run.immutable", "Immutable observed run · source facts are never edited by recovery actions")}
      headerActions={<><Button aria-label={t("workflowActivityVNext.run.backAria", "Back to Activity")} icon={<ArrowLeftOutlined />} onClick={() => history.push(buildWorkflowActivitySectionHref(scopeId, "activity"))} /><Button icon={<ReloadOutlined />} onClick={() => { void detail.refetch(); void graph.refetch(); }}>{t("workflowActivityVNext.common.refresh", "Refresh")}</Button></>} scopeId={scopeId} title={run.summary.workflowName || run.summary.runId}>
      <div className="wa-vnext__toolbar"><Space wrap><span className={`wa-vnext__status wa-vnext__status--${["running", "succeeded", "failed"].includes(run.summary.status.toLowerCase()) ? run.summary.status.toLowerCase() : "unknown"}`}>{run.summary.status}</span><span className="wa-vnext__mono">{run.summary.runId}</span><span>{t("workflowActivityVNext.run.stateVersion", "State version {version}", { version: run.summary.stateVersion })}</span></Space><Space wrap><Button disabled={!recovery.retryStepId} loading={forking} onClick={() => recovery.retryStepId && setPendingRecovery({ kind: "retry", stepId: recovery.retryStepId })} title={!recovery.retryStepId ? t("workflowActivityVNext.run.retryUnavailable", "Retry requires exactly one failed step ID.") : undefined} danger>{t("workflowActivityVNext.run.retry", "Retry failed step")}</Button><Button disabled={!recovery.runAgainStepId} loading={forking} onClick={() => recovery.runAgainStepId && setPendingRecovery({ kind: "run_again", stepId: recovery.runAgainStepId })} title={!recovery.runAgainStepId ? t("workflowActivityVNext.run.runAgainUnavailable", "Run again requires an explicit first execution step.") : undefined}>{t("workflowActivityVNext.run.runAgain", "Run again")}</Button></Space></div>
      {forkError ? <Alert message={forkError} showIcon type="error" /> : null}
      {receipt ? <Alert action={<Button onClick={() => history.push(buildWorkflowActivitySectionHref(scopeId, "activity"))}>{t("workflowActivityVNext.editor.openActivity", "Open Activity")}</Button>} description={<Descriptions column={1} size="small" items={[{ key: "actor", label: t("workflowActivityVNext.run.newActorId", "New run actor ID"), children: <span className="wa-vnext__mono">{receipt.newRunActorId}</span> }, { key: "command", label: t("workflowActivityVNext.run.commandId", "Command ID"), children: <span className="wa-vnext__mono">{receipt.acceptedCommandId}</span> }, { key: "correlation", label: t("workflowActivityVNext.run.correlationId", "Correlation ID"), children: <span className="wa-vnext__mono">{receipt.correlationId}</span> }, { key: "status", label: t("workflowActivityVNext.run.statusUrl", "Observation status URL"), children: <span className="wa-vnext__mono">{receipt.statusUrl}</span> }]} />} message={t("workflowActivityVNext.run.forkAccepted", "New run accepted; waiting for independent Activity observation")} showIcon type="info" /> : null}
      <Descriptions bordered column={{ xs: 1, sm: 2 }} items={[{ key: "origin", label: t("workflowActivityVNext.activity.columnOrigin", "Origin"), children: run.summary.runOrigin }, { key: "input", label: t("workflowActivityVNext.run.input", "Input"), children: run.input || t("workflowActivityVNext.common.empty", "Empty") }, { key: "output", label: t("workflowActivityVNext.run.output", "Final output"), children: run.finalOutput || t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "error", label: t("workflowActivityVNext.run.error", "Final error"), children: run.finalError || t("workflowActivityVNext.common.unavailable", "Unavailable") }]} />
      <Tabs items={[
        {
          key: "steps",
          label: t("workflowActivityVNext.run.steps", "Steps"),
          children: run.steps.length ? <div className="wa-vnext__table-wrap"><table className="wa-vnext__table"><thead><tr><th>{t("workflowActivityVNext.run.step", "Step")}</th><th>{t("workflowActivityVNext.run.type", "Type")}</th><th>{t("workflowActivityVNext.activity.columnStatus", "Status")}</th><th>{t("workflowActivityVNext.run.output", "Output")}</th><th>{t("workflowActivityVNext.run.requestParameters", "Request parameters")}</th></tr></thead><tbody>{run.steps.map((step) => <tr key={step.stepId}><td className="wa-vnext__mono">{step.stepId}</td><td>{step.stepType}</td><td>{step.success === null ? t("workflowActivityVNext.common.pending", "Pending") : step.success ? t("workflowActivityVNext.common.succeeded", "Succeeded") : t("workflowActivityVNext.common.failed", "Failed")}</td><td>{step.error || step.outputPreview || t("workflowActivityVNext.common.unavailable", "Unavailable")}</td><td><pre className="wa-vnext__mono">{Object.keys(step.requestParameters).length ? JSON.stringify(step.requestParameters, null, 2) : t("workflowActivityVNext.common.empty", "Empty")}</pre></td></tr>)}</tbody></table></div> : <div className="wa-vnext__state"><p>{t("workflowActivityVNext.run.noSteps", "No committed steps are visible yet.")}</p></div>,
        },
        {
          key: "diagnostics",
          label: t("workflowActivityVNext.run.diagnostics", "Diagnostics"),
          children: run.diagnostics.length ? <div className="wa-vnext__table-wrap"><table className="wa-vnext__table"><thead><tr><th>{t("workflowActivityVNext.run.severity", "Severity")}</th><th>{t("workflowActivityVNext.run.code", "Code")}</th><th>{t("workflowActivityVNext.run.step", "Step")}</th><th>{t("workflowActivityVNext.run.message", "Message")}</th></tr></thead><tbody>{run.diagnostics.map((diagnostic) => <tr key={[diagnostic.timestampUtc, diagnostic.code, diagnostic.stepId, diagnostic.message].join("|")}><td>{diagnostic.severity}</td><td className="wa-vnext__mono">{diagnostic.code}</td><td className="wa-vnext__mono">{diagnostic.stepId || t("workflowActivityVNext.common.unavailable", "Unavailable")}</td><td>{diagnostic.message}{diagnostic.hint ? <span className="wa-vnext__sub">{diagnostic.hint}</span> : null}</td></tr>)}</tbody></table></div> : <div className="wa-vnext__state"><p>{t("workflowActivityVNext.run.noDiagnostics", "No diagnostics were returned.")}</p></div>,
        },
        {
          key: "timeline",
          label: t("workflowActivityVNext.run.timeline", "Timeline"),
          children: run.timeline.length ? <ol>{run.timeline.map((event) => <li key={[event.timestampUtc, event.kind, event.stepId, event.message].join("|")}><span className="wa-vnext__mono">{event.timestampUtc}</span> · {event.kind} · {event.message}{event.content ? <pre className="wa-vnext__mono">{event.content}</pre> : null}{event.toolCall ? <pre className="wa-vnext__mono">{JSON.stringify(event.toolCall, null, 2)}</pre> : null}</li>)}</ol> : <div className="wa-vnext__state"><p>{t("workflowActivityVNext.run.noTimeline", "No timeline events are visible yet.")}</p></div>,
        },
        {
          key: "statistics",
          label: t("workflowActivityVNext.run.statisticsUsage", "Statistics and usage"),
          children: <Descriptions bordered column={{ xs: 1, sm: 2 }} items={[{ key: "totalSteps", label: t("workflowActivityVNext.run.totalSteps", "Total steps"), children: run.statistics.totalSteps }, { key: "requestedSteps", label: t("workflowActivityVNext.run.requestedSteps", "Requested steps"), children: run.statistics.requestedSteps }, { key: "completedSteps", label: t("workflowActivityVNext.run.completedSteps", "Completed steps"), children: run.statistics.completedSteps }, { key: "roleReplies", label: t("workflowActivityVNext.run.roleReplies", "Role replies"), children: run.statistics.roleReplyCount }, { key: "promptTokens", label: t("workflowActivityVNext.run.promptTokens", "Prompt tokens"), children: run.usageTotals.promptTokens }, { key: "completionTokens", label: t("workflowActivityVNext.run.completionTokens", "Completion tokens"), children: run.usageTotals.completionTokens }, { key: "totalTokens", label: t("workflowActivityVNext.run.totalTokens", "Total tokens"), children: run.usageTotals.totalTokens }, { key: "cost", label: t("workflowActivityVNext.run.cost", "Returned cost"), children: run.usageTotals.cost }]} />,
        },
        {
          key: "graph",
          label: t("workflowActivityVNext.run.graph", "Graph"),
          children: graph.isPending ? <p>{t("workflowActivityVNext.run.graphLoading", "Loading run graph")}</p> : graph.isError ? <Alert action={<Button onClick={() => void graph.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button>} message={t("workflowActivityVNext.run.graphUnavailable", "Run graph unavailable; committed detail remains visible") } showIcon type="warning" /> : <div><p>{t("workflowActivityVNext.run.graphSummary", "{nodes} nodes · {edges} edges", { nodes: graph.data?.nodes.length ?? 0, edges: graph.data?.edges.length ?? 0 })}</p><ul>{graph.data?.nodes.map((node) => <li className="wa-vnext__mono" key={node.nodeId}>{node.nodeId}{node.stepId ? ` · ${node.stepId}` : ""}</li>)}</ul></div>,
        },
      ]} />
      <Modal
        aria-label={t("workflowActivityVNext.run.confirmTitle", "Confirm new run")}
        cancelText={t("workflowActivityVNext.common.cancel", "Cancel")}
        confirmLoading={forking}
        okText={pendingRecovery?.kind === "retry" ? t("workflowActivityVNext.run.confirmRetry", "Confirm retry") : t("workflowActivityVNext.run.confirmRunAgain", "Confirm run again")}
        onCancel={() => !forking && setPendingRecovery(null)}
        onOk={() => void confirmFork()}
        open={Boolean(pendingRecovery)}
        title={t("workflowActivityVNext.run.confirmTitle", "Confirm new run")}
      >
        <Alert message={t("workflowActivityVNext.run.sourceImmutable", "The source run remains immutable. This command requests a new run.")} showIcon type="info" />
        <Descriptions column={1} items={[{ key: "source", label: t("workflowActivityVNext.run.sourceRunId", "Source run ID"), children: <span className="wa-vnext__mono">{run.summary.runId}</span> }, { key: "step", label: t("workflowActivityVNext.run.startStepId", "Start step ID"), children: <span className="wa-vnext__mono">{pendingRecovery?.stepId}</span> }, { key: "input", label: t("workflowActivityVNext.run.input", "Input"), children: run.input || t("workflowActivityVNext.common.empty", "Empty") }]} />
        {forkError ? <Alert message={forkError} showIcon type="error" /> : null}
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default RunDetailPage;
