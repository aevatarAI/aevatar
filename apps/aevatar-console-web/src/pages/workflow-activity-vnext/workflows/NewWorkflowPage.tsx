import {
  ArrowLeftOutlined,
  FileAddOutlined,
  FileTextOutlined,
  ImportOutlined,
  RobotOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Input, Select, Space, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import type { StudioValidationFinding, StudioWorkflowSaveResult } from "@/shared/studio/models";
import WorkflowActivityVNextShell from "../WorkflowActivityVNextShell";
import { buildWorkflowActivityEditorHref, buildWorkflowActivitySectionHref } from "../navigation";
import { useDraftMaterialization } from "../hooks/useDraftMaterialization";
import {
  BUNDLED_WORKFLOW_TEMPLATES,
  createBlankWorkflowYaml,
  hasBlockingFindings,
  slugifyWorkflowFileName,
  type WorkflowCreationMode,
} from "./workflowCreation";

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const NewWorkflowPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const modeItems: readonly {
    readonly key: WorkflowCreationMode;
    readonly icon: React.ReactNode;
    readonly label: string;
    readonly description: string;
  }[] = [
    { key: "describe", icon: <RobotOutlined />, label: t("workflowActivityVNext.new.mode.describe", "Describe"), description: t("workflowActivityVNext.new.mode.describe.description", "Generate a draft from a goal, review it, then persist it.") },
    { key: "blank", icon: <FileAddOutlined />, label: t("workflowActivityVNext.new.mode.blank", "Start blank"), description: t("workflowActivityVNext.new.mode.blank.description", "Persist an empty workflow document and add its first node in the editor.") },
    { key: "import", icon: <ImportOutlined />, label: t("workflowActivityVNext.new.mode.import", "Import YAML"), description: t("workflowActivityVNext.new.mode.import.description", "Validate YAML with the editor API before creating a draft.") },
    { key: "template", icon: <FileTextOutlined />, label: t("workflowActivityVNext.new.mode.template", "Use template"), description: t("workflowActivityVNext.new.mode.template.description", "Copy versioned bundled product content into an independent persisted draft.") },
  ];
  const [mode, setMode] = React.useState<WorkflowCreationMode | null>(null);
  const [name, setName] = React.useState("");
  const [prompt, setPrompt] = React.useState("");
  const [yaml, setYaml] = React.useState("");
  const [generatedYaml, setGeneratedYaml] = React.useState("");
  const [generatedReady, setGeneratedReady] = React.useState(false);
  const [templateId, setTemplateId] = React.useState(BUNDLED_WORKFLOW_TEMPLATES[0]?.id ?? "");
  const [directoryId, setDirectoryId] = React.useState("");
  const [findings, setFindings] = React.useState<readonly StudioValidationFinding[]>([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [failure, setFailure] = React.useState("");
  const materialization = useDraftMaterialization(scopeId);
  const workspace = useQuery({
    queryKey: ["workflow-activity-vnext", "workspace", scopeId],
    queryFn: () => studioApi.getWorkspaceSettings(scopeId),
    retry: false,
  });

  React.useEffect(() => {
    if (!directoryId && workspace.data?.directories[0]?.directoryId) {
      setDirectoryId(workspace.data.directories[0].directoryId);
    }
  }, [directoryId, workspace.data]);

  const navigateToWorkflow = React.useCallback(
    (workflowId: string) => history.push(buildWorkflowActivityEditorHref(scopeId, workflowId)),
    [scopeId],
  );

  const finishSave = React.useCallback(
    async (result: StudioWorkflowSaveResult) => {
      if (result.kind === "materialized") {
        navigateToWorkflow(result.workflow.workflowId);
        return;
      }
      const readable = await materialization.observe(result.receipt);
      if (readable) navigateToWorkflow(readable.workflowId);
    },
    [materialization.observe, navigateToWorkflow],
  );

  const persist = async (nextYaml: string, suggestedName?: string) => {
    const workflowName = (name || suggestedName || "").trim();
    if (!workflowName || !directoryId || submitting) return;
    setSubmitting(true);
    setFailure("");
    try {
      await finishSave(
        await studioApi.createWorkflowDraft({
          directoryId,
          fileName: slugifyWorkflowFileName(workflowName),
          scopeId,
          workflowName,
          yaml: nextYaml,
        }),
      );
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const validateAndPersist = async (nextYaml: string) => {
    if (!nextYaml.trim() || submitting) return;
    setSubmitting(true);
    setFailure("");
    setFindings([]);
    try {
      const parsed = await studioApi.parseYaml({ yaml: nextYaml });
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      const parsedName = String(parsed.document?.name ?? "").trim();
      setSubmitting(false);
      await persist(nextYaml, parsedName);
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const generate = async () => {
    if (!prompt.trim() || submitting) return;
    setSubmitting(true);
    setFailure("");
    setFindings([]);
    setGeneratedYaml("");
    setGeneratedReady(false);
    try {
      const generated = await studioApi.authorWorkflow(
        { prompt },
        { onText: setGeneratedYaml },
      );
      const parsed = await studioApi.parseYaml({ yaml: generated });
      setGeneratedYaml(generated);
      setFindings(parsed.findings);
      if (hasBlockingFindings(parsed.document, parsed.findings)) return;
      setGeneratedReady(true);
      if (!name.trim()) setName(String(parsed.document?.name ?? "").trim());
    } catch (error) {
      setFailure(errorMessage(error));
    } finally {
      setSubmitting(false);
    }
  };

  const retryObservation = async () => {
    const readable = await materialization.retry();
    if (readable) navigateToWorkflow(readable.workflowId);
  };

  const selectedTemplate = BUNDLED_WORKFLOW_TEMPLATES.find((item) => item.id === templateId);
  const templateName = t("workflowActivityVNext.new.templateName.incidentTriage", "Incident triage");
  const templateDescription = t("workflowActivityVNext.new.templateDescription.incidentTriage", "Classify an incident, prepare a response, and request human approval.");
  const disabledByWorkspace = workspace.isPending || workspace.isError || workspace.data?.directories.length === 0;

  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      description={t("workflowActivityVNext.new.description", "Choose a direct path to a persisted workflow draft.")}
      headerActions={<Button icon={<ArrowLeftOutlined />} onClick={() => history.push(buildWorkflowActivitySectionHref(scopeId, "workflows"))}>{t("workflowActivityVNext.new.back", "Back to workflows")}</Button>}
      scopeId={scopeId}
      title={t("workflowActivityVNext.new.title", "New workflow")}
    >
      {workspace.isPending ? <Alert message={t("workflowActivityVNext.new.workspaceLoading", "Loading workspace directories")} showIcon type="info" /> : null}
      {workspace.isError ? <Alert action={<Button onClick={() => void workspace.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button>} message={t("workflowActivityVNext.new.workspaceUnavailable", "Workspace directories unavailable")} showIcon type="error" /> : null}
      {workspace.data?.directories.length === 0 ? <Alert message={t("workflowActivityVNext.new.noDirectories", "No server workflow directory is available for draft creation.")} showIcon type="warning" /> : null}

      {!mode ? (
        <fieldset aria-label={t("workflowActivityVNext.new.chooserAria", "Workflow creation methods")} style={{ border: 0, display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", margin: 0, minWidth: 0, padding: 0 }}>
          {modeItems.map((item) => (
            <button aria-label={item.label} disabled={disabledByWorkspace} key={item.key} onClick={() => setMode(item.key)} style={{ background: "#fff", border: "1px solid var(--wa-line)", minHeight: 150, padding: 20, textAlign: "left" }} type="button">
              <span style={{ color: "var(--wa-blue)", fontSize: 22 }}>{item.icon}</span>
              <strong style={{ display: "block", fontSize: 16, marginTop: 16 }}>{item.label}</strong>
              <span style={{ color: "var(--wa-muted)", display: "block", lineHeight: 1.5, marginTop: 7 }}>{item.description}</span>
            </button>
          ))}
        </fieldset>
      ) : (
        <section className="wa-vnext__panel">
          <div className="wa-vnext__form">
            <Space><Button icon={<ArrowLeftOutlined />} onClick={() => setMode(null)} type="text">{t("workflowActivityVNext.new.changeMethod", "Change method")}</Button><Typography.Title level={3} style={{ fontSize: 18, margin: 0 }}>{modeItems.find((item) => item.key === mode)?.label ?? mode}</Typography.Title></Space>
            <div><span>{t("workflowActivityVNext.new.directory", "Workflow directory")}</span><Select aria-label={t("workflowActivityVNext.new.directory", "Workflow directory")} onChange={setDirectoryId} options={(workspace.data?.directories ?? []).map((item) => ({ label: item.label, value: item.directoryId }))} style={{ display: "block", marginTop: 6, width: "100%" }} value={directoryId || undefined} /></div>
            <div><span>{t("workflowActivityVNext.new.name", "Workflow name")}</span><Input aria-label={t("workflowActivityVNext.new.name", "Workflow name")} onChange={(event) => setName(event.target.value)} style={{ marginTop: 6 }} value={name} /></div>

            {mode === "describe" ? <><div><span>{t("workflowActivityVNext.new.goal", "Automation goal")}</span><Input.TextArea aria-label={t("workflowActivityVNext.new.goal", "Automation goal")} onChange={(event) => setPrompt(event.target.value)} rows={5} style={{ marginTop: 6 }} value={prompt} /></div>{generatedYaml ? <div><span>{t("workflowActivityVNext.new.generatedYaml", "Generated YAML")}</span><Input.TextArea aria-label={t("workflowActivityVNext.new.generatedYaml", "Generated YAML")} onChange={(event) => { setGeneratedYaml(event.target.value); setGeneratedReady(false); }} rows={12} style={{ fontFamily: "ui-monospace, monospace", marginTop: 6 }} value={generatedYaml} /></div> : null}<div className="wa-vnext__form-actions"><Button disabled={!prompt.trim()} loading={submitting} onClick={() => void generate()}>{t("workflowActivityVNext.new.generate", "Generate draft")}</Button>{generatedYaml && generatedReady ? <Button disabled={!name.trim()} loading={submitting} onClick={() => void persist(generatedYaml)} type="primary">{t("workflowActivityVNext.new.createGenerated", "Create generated draft")}</Button> : null}</div></> : null}

            {mode === "blank" ? <Button disabled={!name.trim()} loading={submitting} onClick={() => void persist(createBlankWorkflowYaml(name))} type="primary">{t("workflowActivityVNext.new.createBlank", "Create blank draft")}</Button> : null}

            {mode === "import" ? <><div><span>{t("workflowActivityVNext.new.yaml", "Workflow YAML")}</span><Input.TextArea aria-label={t("workflowActivityVNext.new.yaml", "Workflow YAML")} onChange={(event) => setYaml(event.target.value)} rows={16} style={{ fontFamily: "ui-monospace, monospace", marginTop: 6 }} value={yaml} /></div><Button disabled={!yaml.trim()} loading={submitting} onClick={() => void validateAndPersist(yaml)} type="primary">{t("workflowActivityVNext.new.validateCreate", "Validate and create")}</Button></> : null}

            {mode === "template" ? <><div><span>{t("workflowActivityVNext.new.template", "Template")}</span><Select aria-label={t("workflowActivityVNext.new.template", "Template")} onChange={setTemplateId} options={BUNDLED_WORKFLOW_TEMPLATES.map((item) => ({ label: `${templateName} · ${item.version}`, value: item.id }))} style={{ display: "block", marginTop: 6, width: "100%" }} value={templateId} /></div>{selectedTemplate ? <div><strong>{templateName}</strong><p style={{ color: "var(--wa-muted)" }}>{templateDescription}</p><span className="wa-vnext__mono">{t("workflowActivityVNext.new.templateVersion", "Bundled template version {version}", { version: selectedTemplate.version })}</span></div> : null}<Button disabled={!selectedTemplate || !name.trim()} loading={submitting} onClick={() => selectedTemplate && void validateAndPersist(selectedTemplate.yaml)} type="primary">{t("workflowActivityVNext.new.createTemplate", "Create from template")}</Button></> : null}

            {findings.length > 0 ? <div aria-live="polite">{findings.map((finding) => <Alert key={[finding.code, finding.path, finding.level, finding.message].join("|")} message={finding.message} showIcon type={String(finding.level).toLowerCase() === "error" ? "error" : "warning"} />)}</div> : null}
            {failure ? <Alert message={failure} showIcon type="error" /> : null}
            {materialization.phase !== "idle" && materialization.receipt ? <div className={materialization.phase === "failed" ? "wa-vnext__notice wa-vnext__notice--error" : "wa-vnext__notice"} role="status"><strong>{materialization.phase === "delayed" ? t("workflowActivityVNext.new.projectionDelayed", "Projection is delayed") : materialization.phase === "failed" ? t("workflowActivityVNext.new.observationFailed", "Draft observation failed") : t("workflowActivityVNext.new.observing", "Draft accepted; observing readability")}</strong><p>{t("workflowActivityVNext.new.receipt", "Workflow {workflowId} · command {commandId}", { workflowId: materialization.receipt.workflowId, commandId: materialization.receipt.commandId })}</p>{materialization.error ? <p>{errorMessage(materialization.error)}</p> : null}{materialization.phase === "delayed" || materialization.phase === "failed" ? <Button onClick={() => void retryObservation()}>{t("workflowActivityVNext.new.retryObservation", "Retry draft observation")}</Button> : null}</div> : null}
          </div>
        </section>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default NewWorkflowPage;
