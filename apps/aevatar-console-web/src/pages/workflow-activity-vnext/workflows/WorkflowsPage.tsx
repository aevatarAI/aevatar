import { PlusOutlined, ReloadOutlined, SearchOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Input, Space } from "antd";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { t } from "@/shared/i18n/messages";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import type { StudioWorkflowDraftSummary } from "@/shared/studio/models";
import type { ScopeWorkflowSummary } from "@/shared/models/scopes";
import WorkflowActivityVNextShell from "../WorkflowActivityVNextShell";
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySectionHref,
} from "../navigation";

type WorkflowRow = {
  readonly description: string;
  readonly hasCommittedSource: boolean;
  readonly name: string;
  readonly source: "draft" | "committed";
  readonly stepCount?: number;
  readonly updatedAtUtc: string | null;
  readonly workflowId: string;
};

function toDraftRow(
  item: StudioWorkflowDraftSummary,
  committed?: WorkflowRow,
): WorkflowRow {
  return {
    description: item.description,
    hasCommittedSource: Boolean(committed),
    name: item.name,
    source: "draft",
    stepCount: item.stepCount,
    updatedAtUtc: item.updatedAtUtc,
    workflowId: item.workflowId,
  };
}

function toCommittedRow(item: ScopeWorkflowSummary): WorkflowRow {
  return {
    description: item.workflowName,
    hasCommittedSource: true,
    name: item.displayName || item.workflowName,
    source: "committed",
    updatedAtUtc: item.updatedAt,
    workflowId: item.workflowId,
  };
}

function formatDate(value: string | null): string {
  if (!value) return t("workflowActivityVNext.common.unavailable", "Unavailable");
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date);
}

const WorkflowsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const [query, setQuery] = React.useState("");
  const [activityWorkflowId, setActivityWorkflowId] = React.useState("");
  const [activityError, setActivityError] = React.useState("");
  const drafts = useQuery({
    queryKey: ["workflow-activity-vnext", "drafts", scopeId],
    queryFn: () => studioApi.listWorkflowDrafts(scopeId),
    retry: false,
  });
  const committed = useQuery({
    queryKey: ["workflow-activity-vnext", "committed", scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
    retry: false,
  });

  const loading = drafts.isPending || committed.isPending;
  const rows = React.useMemo(() => {
    const merged = new Map<string, WorkflowRow>();
    for (const item of committed.data ?? []) merged.set(item.workflowId, toCommittedRow(item));
    for (const item of drafts.data ?? []) merged.set(item.workflowId, toDraftRow(item, merged.get(item.workflowId)));
    const normalized = query.trim().toLowerCase();
    return [...merged.values()]
      .filter((item) =>
        !normalized || [item.name, item.description, item.workflowId].some((value) => value.toLowerCase().includes(normalized)),
      )
      .sort((left, right) => Date.parse(right.updatedAtUtc ?? "") - Date.parse(left.updatedAtUtc ?? ""));
  }, [committed.data, drafts.data, query]);
  const totalFailure = drafts.isError && committed.isError;

  const retry = () => {
    void drafts.refetch();
    void committed.refetch();
  };

  const openActivity = async (row: WorkflowRow) => {
    const activityHref = buildWorkflowActivitySectionHref(scopeId, "activity");
    setActivityError("");
    if (!row.hasCommittedSource) {
      history.push(`${activityHref}?workflowFilter=unavailable`);
      return;
    }

    setActivityWorkflowId(row.workflowId);
    try {
      const detail = await scopesApi.getWorkflowDetail(scopeId, row.workflowId);
      const definitionActorId = detail.source?.definitionActorId.trim() ?? "";
      history.push(
        definitionActorId
          ? `${activityHref}?definition=${encodeURIComponent(definitionActorId)}`
          : `${activityHref}?workflowFilter=unavailable`,
      );
    } catch (error) {
      setActivityError(error instanceof Error ? error.message : String(error));
    } finally {
      setActivityWorkflowId("");
    }
  };

  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      description={t("workflowActivityVNext.workflows.description", "Create, edit, and run scoped workflow drafts.")}
      headerActions={
        <Button icon={<PlusOutlined />} onClick={() => history.push(buildWorkflowActivityNewHref(scopeId))} type="primary">
          {t("workflowActivityVNext.workflows.new", "New workflow")}
        </Button>
      }
      scopeId={scopeId}
      title={t("workflowActivityVNext.workflows.title", "Workflows")}
    >
      <div className="wa-vnext__toolbar">
        <Input
          allowClear
          aria-label={t("workflowActivityVNext.workflows.searchAria", "Search workflows")}
          onChange={(event) => setQuery(event.target.value)}
          placeholder={t("workflowActivityVNext.workflows.search", "Search workflows")}
          prefix={<SearchOutlined />}
          role="searchbox"
          style={{ width: 360 }}
          value={query}
        />
        <Button aria-label={t("workflowActivityVNext.workflows.refreshAria", "Refresh workflows")} icon={<ReloadOutlined />} onClick={retry}>
          {t("workflowActivityVNext.common.refresh", "Refresh")}
        </Button>
      </div>

      {drafts.isError && !committed.isError ? (
        <Alert message={t("workflowActivityVNext.workflows.draftsUnavailable", "Draft catalogue unavailable")} showIcon type="warning" />
      ) : null}
      {committed.isError && !drafts.isError ? (
        <Alert message={t("workflowActivityVNext.workflows.committedUnavailable", "Committed catalogue unavailable")} showIcon type="warning" />
      ) : null}
      {activityError ? (
        <Alert
          message={t("workflowActivityVNext.workflows.activityResolutionFailed", "Workflow Activity filter unavailable")}
          description={activityError}
          showIcon
          type="error"
        />
      ) : null}

      {loading ? (
        <div aria-live="polite" className="wa-vnext__state">
          <p>{t("workflowActivityVNext.workflows.loading", "Loading workflows")}</p>
        </div>
      ) : totalFailure ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>{t("workflowActivityVNext.workflows.unavailable", "Workflows unavailable")}</h2>
            <p>{t("workflowActivityVNext.workflows.unavailableDescription", "Neither authoritative catalogue source could be loaded.")}</p>
            <Button aria-label={t("workflowActivityVNext.workflows.retryAria", "Retry workflows")} icon={<ReloadOutlined />} onClick={retry}>
              {t("workflowActivityVNext.common.retry", "Retry")}
            </Button>
          </div>
        </div>
      ) : rows.length === 0 ? (
        <div className="wa-vnext__state">
          <div>
            <h2>{query ? t("workflowActivityVNext.workflows.noMatch", "No matching workflows") : t("workflowActivityVNext.workflows.empty", "No workflows yet")}</h2>
            <p>{query ? t("workflowActivityVNext.workflows.noMatchDescription", "Try a different search.") : t("workflowActivityVNext.workflows.emptyDescription", "Create a persisted draft to begin.")}</p>
            {!query ? <Button icon={<PlusOutlined />} onClick={() => history.push(buildWorkflowActivityNewHref(scopeId))} type="primary">{t("workflowActivityVNext.workflows.new", "New workflow")}</Button> : null}
          </div>
        </div>
      ) : (
        <div className="wa-vnext__table-wrap">
          <table className="wa-vnext__table">
            <thead><tr><th>{t("workflowActivityVNext.workflows.columnWorkflow", "Workflow")}</th><th>{t("workflowActivityVNext.workflows.columnState", "State")}</th><th>{t("workflowActivityVNext.workflows.columnUpdated", "Updated")}</th><th>{t("workflowActivityVNext.workflows.columnActions", "Actions")}</th></tr></thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.workflowId}>
                  <td><span className="wa-vnext__title">{row.name}</span><span className="wa-vnext__sub">{row.description || row.workflowId}</span></td>
                  <td><span className={`wa-vnext__status wa-vnext__status--${row.source}`}>{row.source === "draft" ? t("workflowActivityVNext.workflows.draft", "Draft") : t("workflowActivityVNext.workflows.committed", "Committed")}</span></td>
                  <td>{formatDate(row.updatedAtUtc)}</td>
                  <td><Space><Button aria-label={t("workflowActivityVNext.workflows.openAria", "Open {name}", { name: row.name })} onClick={() => history.push(buildWorkflowActivityEditorHref(scopeId, row.workflowId))}>{t("workflowActivityVNext.common.open", "Open")}</Button><Button loading={activityWorkflowId === row.workflowId} onClick={() => void openActivity(row)} title={!row.hasCommittedSource ? t("workflowActivityVNext.workflows.activityFilterUnavailable", "Activity filtering requires a backend definition actor ID.") : undefined}>{t("workflowActivityVNext.workflows.viewActivity", "Activity")}</Button><Button disabled title={t("workflowActivityVNext.workflows.runFromEditor", "Open the editor to validate and run this workflow.")}>{t("workflowActivityVNext.common.run", "Run")}</Button></Space></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowsPage;
