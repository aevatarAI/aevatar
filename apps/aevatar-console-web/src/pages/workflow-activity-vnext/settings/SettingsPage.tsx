import { ReloadOutlined, SaveOutlined } from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Descriptions, Select, Space, Tabs } from "antd";
import React from "react";
import {
  buildUserLlmSelectionOptions,
  cloneUserLlmSelection,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
  userLlmSelectionsEqual,
  type UserLlmSelectionDraft,
} from "@/pages/settings/userLlmSelection";
import { observeUserLlmSave } from "@/pages/settings/userLlmSaveObservation";
import { t } from "@/shared/i18n/messages";
import { history } from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import WorkflowActivityVNextShell from "../WorkflowActivityVNextShell";
import { useConsoleLocation } from "../hooks/useConsoleLocation";

type SettingsSection = "ai" | "account" | "advanced";
type SavePhase = "idle" | "saving" | "accepted" | "observed" | "delayed" | "failed";

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function readSection(search: string): SettingsSection {
  const value = new URLSearchParams(search).get("section");
  return value === "account" || value === "advanced" ? value : "ai";
}

const SettingsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const queryClient = useQueryClient();
  const [section, setSection] = React.useState<SettingsSection>(() => readSection(location.search));
  const llm = useQuery({ queryKey: ["workflow-activity-vnext", "settings", "llm"], queryFn: ({ signal }) => studioApi.getUserLlmSettings(signal), retry: false });
  const auth = useQuery({ queryKey: ["workflow-activity-vnext", "settings", "auth"], queryFn: () => studioApi.getAuthSession(), retry: false });
  const runtime = useQuery({ queryKey: ["workflow-activity-vnext", "settings", "runtime"], queryFn: () => studioApi.getUserConfigRuntime(), retry: false });
  const [draft, setDraft] = React.useState<UserLlmSelectionDraft | undefined>(undefined);
  const [baseline, setBaseline] = React.useState<UserLlmSelectionDraft | undefined>(undefined);
  const [savePhase, setSavePhase] = React.useState<SavePhase>("idle");
  const [saveMessage, setSaveMessage] = React.useState("");
  const saveTokenRef = React.useRef(0);
  const loadedRef = React.useRef(false);

  React.useEffect(() => {
    if (!llm.data || loadedRef.current) return;
    const saved = resolveSavedUserLlmSelection(llm.data);
    setDraft(saved);
    setBaseline(saved);
    loadedRef.current = true;
  }, [llm.data]);
  React.useEffect(() => () => {
    saveTokenRef.current += 1;
  }, []);

  const options = React.useMemo(() => buildUserLlmSelectionOptions(llm.data?.routeOptions ?? []), [llm.data?.routeOptions]);
  const selectedOption = draft ? options.find((item) => item.value === encodeUserLlmSelectionValue(draft)) : undefined;
  const modelIds = selectedOption?.modelCatalog.modelIds ?? [];
  const modelValue = draft?.modelSelection.kind === "explicit_model" ? draft.modelSelection.modelId : "provider-default";
  const dirty = !userLlmSelectionsEqual(draft, baseline);

  const selectRoute = (value: string) => {
    if (!value) {
      setDraft(undefined);
      return;
    }
    const next = decodeUserLlmSelectionValue(value, options);
    if (next) setDraft(cloneUserLlmSelection(next));
  };
  const selectModel = (value: string) => {
    if (!draft) return;
    setDraft({ ...draft, modelSelection: value === "provider-default" ? { kind: "provider_default" } : { kind: "explicit_model", modelId: value } } as UserLlmSelectionDraft);
  };

  const save = async () => {
    if (!dirty || savePhase === "saving" || savePhase === "accepted") return;
    const submitted = draft ? cloneUserLlmSelection(draft) : undefined;
    const token = ++saveTokenRef.current;
    setSavePhase("saving");
    setSaveMessage("");
    try {
      const receipt = await studioApi.saveUserLlmSettings(
        !submitted
          ? { action: "reset" }
          : submitted.routeKind === "gateway"
            ? { action: "select_gateway", gateway: { model: submitted.modelSelection } }
            : { action: "select_user_service", userService: { userServiceId: submitted.nyxIdUserServiceId, model: submitted.modelSelection } },
      );
      if (!receipt.accepted) throw new Error(t("workflowActivityVNext.settings.notAccepted", "The settings update was not accepted."));
      setSavePhase("accepted");
      setSaveMessage(receipt.commandId);
      const observation = await observeUserLlmSave({
        saveToken: token,
        isCurrent: (candidate) => candidate === saveTokenRef.current,
        read: (signal) => studioApi.getUserLlmSettings(signal),
        isObserved: (sample) => userLlmSelectionsEqual(resolveSavedUserLlmSelection(sample), submitted),
        onResponse: (sample) => queryClient.setQueryData(["workflow-activity-vnext", "settings", "llm"], sample),
      });
      if (token !== saveTokenRef.current) return;
      if (observation.phase === "observed") {
        setBaseline(submitted ? cloneUserLlmSelection(submitted) : undefined);
        setSavePhase("observed");
      } else if (observation.phase === "accepted_unobserved") {
        setSavePhase("delayed");
      }
    } catch (error) {
      if (token === saveTokenRef.current) {
        setSaveMessage(errorMessage(error));
        setSavePhase("failed");
      }
    }
  };

  const discard = () => {
    saveTokenRef.current += 1;
    setDraft(baseline ? cloneUserLlmSelection(baseline) : undefined);
    setSavePhase("idle");
    setSaveMessage("");
  };

  const changeSection = (key: string) => {
    const next = key as SettingsSection;
    setSection(next);
    history.replace(`${location.pathname}${next === "ai" ? "" : `?section=${next}`}`);
  };

  const aiPanel = llm.isPending ? <div className="wa-vnext__state"><p>{t("workflowActivityVNext.settings.llmLoading", "Loading AI defaults")}</p></div> : llm.isError ? <div className="wa-vnext__state" role="alert"><div><h2>{t("workflowActivityVNext.settings.llmUnavailable", "AI defaults unavailable")}</h2><p>{errorMessage(llm.error)}</p><Button onClick={() => void llm.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button></div></div> : <div className="wa-vnext__form"><Alert message={llm.data?.selectionStatus === "ready" ? t("workflowActivityVNext.settings.selectionReady", "Saved selection is ready") : llm.data?.savedRouteLabel || t("workflowActivityVNext.settings.systemDefault", "System default")} description={llm.data?.catalogStatus === "unavailable" ? t("workflowActivityVNext.settings.catalogUnavailable", "The provider catalogue is unavailable. Saved values remain visible, but unverifiable choices cannot be invented.") : undefined} showIcon type={llm.data?.catalogStatus === "unavailable" ? "warning" : "info"} /><div><span>{t("workflowActivityVNext.settings.route", "AI route")}</span><Select aria-label={t("workflowActivityVNext.settings.route", "AI route")} disabled={!llm.data?.capabilities.canEditRoute} onChange={selectRoute} options={[{ label: t("workflowActivityVNext.settings.systemDefault", "System default"), value: "" }, ...options.map((item) => ({ disabled: !item.allowed || !item.ready, label: item.label, value: item.value }))]} style={{ display: "block", marginTop: 6, width: "100%" }} value={draft ? encodeUserLlmSelectionValue(draft) : ""} /></div>{draft ? <div><span>{t("workflowActivityVNext.settings.model", "Model")}</span><Select aria-label={t("workflowActivityVNext.settings.model", "Model")} disabled={!llm.data?.capabilities.canEditModel || selectedOption?.modelCatalog.certainty === "unavailable"} onChange={selectModel} options={[{ label: t("workflowActivityVNext.settings.providerDefault", "Provider default"), value: "provider-default" }, ...modelIds.map((modelId) => ({ label: modelId, value: modelId }))]} style={{ display: "block", marginTop: 6, width: "100%" }} value={modelValue} /></div> : null}<Space className={dirty ? "wa-vnext__settings-actions wa-vnext__settings-actions--dirty" : "wa-vnext__settings-actions"} wrap><Button disabled={!dirty} onClick={discard}>{t("workflowActivityVNext.settings.discard", "Discard changes")}</Button><Button disabled={!dirty || !llm.data?.capabilities.canSave} icon={<SaveOutlined />} loading={savePhase === "saving"} onClick={() => void save()} type="primary">{t("workflowActivityVNext.settings.save", "Save AI defaults")}</Button><Button icon={<ReloadOutlined />} onClick={() => void llm.refetch()}>{t("workflowActivityVNext.common.refresh", "Refresh")}</Button></Space>{savePhase !== "idle" ? <Alert message={savePhase === "saving" ? t("workflowActivityVNext.settings.saving", "Saving AI defaults") : savePhase === "accepted" ? t("workflowActivityVNext.settings.accepted", "Update accepted; observing authoritative settings") : savePhase === "observed" ? t("workflowActivityVNext.settings.observed", "AI defaults observed") : savePhase === "delayed" ? t("workflowActivityVNext.settings.delayed", "Update accepted but not yet observed") : t("workflowActivityVNext.settings.failed", "AI defaults could not be saved")} description={saveMessage || undefined} showIcon type={savePhase === "failed" ? "error" : savePhase === "delayed" ? "warning" : savePhase === "observed" ? "success" : "info"} /> : null}</div>;

  const accountPanel = auth.isPending ? <div className="wa-vnext__state"><p>{t("workflowActivityVNext.settings.accountLoading", "Loading account session")}</p></div> : auth.isError ? <div className="wa-vnext__state" role="alert"><div><h2>{t("workflowActivityVNext.settings.accountUnavailable", "Account session unavailable")}</h2><p>{errorMessage(auth.error)}</p><Button onClick={() => void auth.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button></div></div> : <Descriptions bordered column={{ xs: 1, sm: 2 }} items={[{ key: "status", label: t("workflowActivityVNext.settings.authenticated", "Authenticated"), children: auth.data?.authenticated ? t("workflowActivityVNext.common.yes", "Yes") : t("workflowActivityVNext.common.no", "No") }, { key: "name", label: t("workflowActivityVNext.settings.name", "Name"), children: auth.data?.profile?.name || auth.data?.name || t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "email", label: t("workflowActivityVNext.settings.email", "Email"), children: auth.data?.profile?.email || auth.data?.email || t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "subject", label: t("workflowActivityVNext.settings.subject", "Subject"), children: <span className="wa-vnext__mono">{auth.data?.profile?.subject || t("workflowActivityVNext.common.unavailable", "Unavailable")}</span> }, { key: "provider", label: t("workflowActivityVNext.settings.provider", "Provider"), children: auth.data?.providerDisplayName || auth.data?.session?.providerDisplayName || t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "roles", label: t("workflowActivityVNext.settings.roles", "Roles"), children: auth.data?.profile?.roles.length ? auth.data.profile.roles.join(", ") : t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "groups", label: t("workflowActivityVNext.settings.groups", "Groups"), children: auth.data?.profile?.groups.length ? auth.data.profile.groups.join(", ") : t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "scope", label: t("workflowActivityVNext.scope", "Scope"), children: <span className="wa-vnext__mono">{auth.data?.session?.scopeId || auth.data?.scopeId || t("workflowActivityVNext.common.unavailable", "Unavailable")}</span> }, { key: "scopeSource", label: t("workflowActivityVNext.settings.scopeSource", "Scope source"), children: auth.data?.session?.scopeSource || auth.data?.scopeSource || t("workflowActivityVNext.common.unavailable", "Unavailable") }, { key: "expiry", label: t("workflowActivityVNext.settings.expires", "Expires"), children: auth.data?.session?.expiresAtUtc || t("workflowActivityVNext.common.unavailable", "Unavailable") }]} />;

  const runtimePanel = runtime.isPending ? <div className="wa-vnext__state"><p>{t("workflowActivityVNext.settings.runtimeLoading", "Loading effective runtime")}</p></div> : runtime.isError ? <div className="wa-vnext__state" role="alert"><div><h2>{t("workflowActivityVNext.settings.runtimeUnavailable", "Effective runtime unavailable")}</h2><p>{errorMessage(runtime.error)}</p><Button onClick={() => void runtime.refetch()}>{t("workflowActivityVNext.common.retry", "Retry")}</Button></div></div> : <Descriptions bordered column={1} items={[{ key: "mode", label: t("workflowActivityVNext.settings.runtimeMode", "Runtime mode"), children: runtime.data?.runtimeMode }, { key: "active", label: t("workflowActivityVNext.settings.activeRuntime", "Active runtime URL"), children: <span className="wa-vnext__mono">{runtime.data?.activeRuntimeBaseUrl}</span> }, { key: "local", label: t("workflowActivityVNext.settings.localRuntime", "Local runtime URL"), children: <span className="wa-vnext__mono">{runtime.data?.localRuntimeBaseUrl}</span> }, { key: "remote", label: t("workflowActivityVNext.settings.remoteRuntime", "Remote runtime URL"), children: <span className="wa-vnext__mono">{runtime.data?.remoteRuntimeBaseUrl}</span> }]} />;

  return <WorkflowActivityVNextShell activeSection="settings" description={t("workflowActivityVNext.settings.description", "Personal AI defaults, session identity, and effective runtime context.")} scopeId={scopeId} title={t("workflowActivityVNext.settings.title", "Settings")}><Tabs activeKey={section} onChange={changeSection} items={[{ key: "ai", label: t("workflowActivityVNext.settings.ai", "AI defaults"), children: aiPanel }, { key: "account", label: t("workflowActivityVNext.settings.account", "Account"), children: accountPanel }, { key: "advanced", label: t("workflowActivityVNext.settings.advanced", "Advanced"), children: runtimePanel }]} /></WorkflowActivityVNextShell>;
};

export default SettingsPage;
