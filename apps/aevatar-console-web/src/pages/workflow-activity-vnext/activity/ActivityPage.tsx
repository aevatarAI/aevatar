import { ReloadOutlined, SearchOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Input, Select, Space } from "antd";
import React from "react";
import { workflowActivityApi, WorkflowActivityApiError } from "@/shared/api/workflowActivityApi";
import { t } from "@/shared/i18n/messages";
import { history } from "@/shared/navigation/history";
import WorkflowActivityVNextShell from "../WorkflowActivityVNextShell";
import { buildWorkflowActivityRunHref } from "../navigation";
import { useConsoleLocation } from "../hooks/useConsoleLocation";

function formatDate(value: string | null): string {
  if (!value) return t("workflowActivityVNext.common.unavailable", "Unavailable");
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function failureTitle(error: unknown): string {
  if (error instanceof WorkflowActivityApiError) {
    if (error.status === 401) return t("workflowActivityVNext.state.unauthorized", "Authentication required");
    if (error.status === 403) return t("workflowActivityVNext.state.forbidden", "Scope access forbidden");
  }
  return t("workflowActivityVNext.activity.unavailable", "Activity unavailable");
}

const ActivityPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const initialParams = React.useMemo(() => new URLSearchParams(location.search), [location.search]);
  const [status, setStatus] = React.useState(initialParams.get("status") ?? "");
  const [origin, setOrigin] = React.useState(initialParams.get("origin") ?? "");
  const [definition, setDefinition] = React.useState(initialParams.get("definition") ?? "");
  const [workflowFilter, setWorkflowFilter] = React.useState(
    initialParams.get("workflowFilter") ?? "",
  );
  const [search, setSearch] = React.useState("");
  const runs = useQuery({
    queryKey: ["workflow-activity-vnext", "runs", scopeId, status, origin, definition],
    queryFn: () => workflowActivityApi.listRuns(scopeId, {
      status: status || undefined,
      origins: origin ? [origin] : undefined,
      definitionActorIds: definition ? [definition] : undefined,
      take: 100,
    }),
    retry: false,
  });

  React.useEffect(() => {
    const params = new URLSearchParams(location.search);
    setStatus(params.get("status") ?? "");
    setOrigin(params.get("origin") ?? "");
    setDefinition(params.get("definition") ?? "");
    setWorkflowFilter(params.get("workflowFilter") ?? "");
  }, [location.search]);

  React.useEffect(() => {
    const params = new URLSearchParams();
    if (status) params.set("status", status);
    if (origin) params.set("origin", origin);
    if (definition) params.set("definition", definition);
    if (workflowFilter) params.set("workflowFilter", workflowFilter);
    const suffix = params.toString();
    history.replace(`${location.pathname}${suffix ? `?${suffix}` : ""}`);
  }, [definition, location.pathname, origin, status, workflowFilter]);

  const filtered = (runs.data ?? []).filter((run) => {
    const normalized = search.trim().toLowerCase();
    return !normalized || [run.workflowName, run.runId, run.status].some((value) => value.toLowerCase().includes(normalized));
  });

  return (
    <WorkflowActivityVNextShell activeSection="activity" description={t("workflowActivityVNext.activity.description", "Immutable run records observed by the workflow read model.")} headerActions={<Button icon={<ReloadOutlined />} onClick={() => void runs.refetch()}>{t("workflowActivityVNext.common.refresh", "Refresh")}</Button>} scopeId={scopeId} title={t("workflowActivityVNext.activity.title", "Activity")}>
      {workflowFilter === "unavailable" ? (
        <Alert
          closable
          message={t("workflowActivityVNext.activity.workflowFilterUnavailable", "Workflow filter unavailable; showing unfiltered Activity")}
          onClose={() => setWorkflowFilter("")}
          showIcon
          type="warning"
        />
      ) : null}
      <div className="wa-vnext__toolbar">
        <Input allowClear aria-label={t("workflowActivityVNext.activity.searchAria", "Search observed runs")} onChange={(event) => setSearch(event.target.value)} placeholder={t("workflowActivityVNext.activity.search", "Search observed runs")} prefix={<SearchOutlined />} role="searchbox" style={{ width: 320 }} value={search} />
        <Space wrap>
          <Select aria-label={t("workflowActivityVNext.activity.statusFilter", "Run status")} onChange={setStatus} options={[{ label: t("workflowActivityVNext.activity.allStatuses", "All statuses"), value: "" }, ...["running", "succeeded", "failed", "waiting"].map((value) => ({ label: value, value }))]} style={{ width: 150 }} value={status} />
          <Select aria-label={t("workflowActivityVNext.activity.originFilter", "Run origin")} onChange={setOrigin} options={[{ label: t("workflowActivityVNext.activity.allOrigins", "All origins"), value: "" }, { label: t("workflowActivityVNext.activity.originDraft", "Draft"), value: "draft" }, { label: t("workflowActivityVNext.activity.originMember", "Member invoke"), value: "member-invoke" }, { label: t("workflowActivityVNext.activity.originSchedule", "Schedule"), value: "schedule" }]} style={{ width: 160 }} value={origin} />
          {definition ? <Button onClick={() => setDefinition("")}>{t("workflowActivityVNext.activity.clearWorkflowFilter", "Clear workflow filter")}</Button> : null}
        </Space>
      </div>
      <p style={{ color: "var(--wa-muted)" }}>{t("workflowActivityVNext.activity.window", "Showing up to 100 recently observed runs. This is not a lifetime total.")}</p>
      {runs.isPending ? <div aria-live="polite" className="wa-vnext__state"><p>{t("workflowActivityVNext.activity.loading", "Loading observed runs")}</p></div> : runs.isError ? <div className="wa-vnext__state" role="alert"><div><h2>{failureTitle(runs.error)}</h2><p>{runs.error instanceof Error ? runs.error.message : String(runs.error)}</p><Button onClick={() => void runs.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button></div></div> : filtered.length === 0 ? <div className="wa-vnext__state"><div><h2>{runs.data?.length ? t("workflowActivityVNext.activity.noMatch", "No matching observed runs") : t("workflowActivityVNext.activity.empty", "No observed runs")}</h2><p>{t("workflowActivityVNext.activity.emptyDescription", "Activity appears only after the observatory read model returns a run.")}</p></div></div> : <div className="wa-vnext__table-wrap"><table className="wa-vnext__table"><thead><tr><th>{t("workflowActivityVNext.activity.columnRun", "Run")}</th><th>{t("workflowActivityVNext.activity.columnStatus", "Status")}</th><th>{t("workflowActivityVNext.activity.columnOrigin", "Origin")}</th><th>{t("workflowActivityVNext.activity.columnUpdated", "Observed update")}</th><th>{t("workflowActivityVNext.activity.columnVersion", "State version")}</th></tr></thead><tbody>{filtered.map((run) => <tr key={run.runId}><td><button aria-label={t("workflowActivityVNext.activity.openRunAria", "Open run {runId}", { runId: run.runId })} onClick={() => history.push(buildWorkflowActivityRunHref(scopeId, run.runId))} style={{ background: "transparent", border: 0, color: "var(--wa-blue)", padding: 0, textAlign: "left" }} type="button"><span className="wa-vnext__title">{run.workflowName || t("workflowActivityVNext.activity.unnamed", "Unnamed workflow")}</span><span className="wa-vnext__sub wa-vnext__mono">{run.runId}</span></button></td><td><span className={`wa-vnext__status wa-vnext__status--${["running", "succeeded", "failed"].includes(run.status.toLowerCase()) ? run.status.toLowerCase() : "unknown"}`}>{run.status || t("workflowActivityVNext.common.unknown", "Unknown")}</span></td><td>{run.runOrigin || t("workflowActivityVNext.common.unknown", "Unknown")}</td><td>{formatDate(run.updatedAtUtc)}</td><td className="wa-vnext__mono">{run.stateVersion}</td></tr>)}</tbody></table></div>}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
