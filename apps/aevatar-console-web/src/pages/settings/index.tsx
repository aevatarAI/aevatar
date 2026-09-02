import {
  CommentOutlined,
  ExperimentOutlined,
  LockOutlined,
  ReloadOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Collapse,
  Grid,
  Input,
  Select,
  Space,
  Typography,
  theme,
} from "antd";
import type { CollapseProps, SelectProps } from "antd";
import React from "react";
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import {
  LLM_MODEL_HEADER_KEY,
  LLM_ROUTE_HEADER_KEY,
  trimConversationValue,
} from "@/pages/chat/chatConversationConfig";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import type {
  StudioUserLlmRouteOption,
  StudioUserLlmSettings,
} from "@/shared/studio/models";
import {
  formatStudioUserConfigRuntimeModeLabel,
  normalizeStudioUserConfigRuntimeMode,
} from "@/shared/studio/userConfigRuntime";
import {
  aevatarMonoFontFamily,
  truncateMiddle,
} from "@/shared/ui/compactText";
import { describeError } from "@/shared/ui/errorText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import { useConsoleToast } from "@/shared/ui/ConsoleToast";
import { codeBlockStyle } from "@/shared/ui/proComponents";
import AccountSettingsContent from "./accountContent";
import {
  buildUserLlmSelectionOptions,
  cloneUserLlmSelection,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
  userLlmSelectionsEqual,
} from "./userLlmSelection";
import type {
  UserLlmSelectionDraft,
  UserLlmSelectionOption,
} from "./userLlmSelection";
import { observeUserLlmSave } from "./userLlmSaveObservation";
import type { PendingUserLlmSave } from "./userLlmSaveObservation";
import {
  buildSettingsInsetCardStyle,
  buildSettingsPanelStyle,
  buildSettingsSwitchButtonStyle,
  buildSettingsSwitchRailStyle,
  SettingsPageShell,
  SummaryField,
  SummaryMetric,
} from "./shared";
import { t } from "@/shared/i18n/messages";

type SettingsSection = "llm" | "account";

type SettingsDraft = {
  readonly preferredLlmSelection: UserLlmSelectionDraft | undefined;
};

type PendingSettingsSave = PendingUserLlmSave<SettingsDraft>;

type SettingsDraftState = {
  readonly baseline: SettingsDraft;
  readonly pendingSave: PendingSettingsSave | null;
  readonly draftRevision: number;
  readonly saveError: string | null;
  readonly value: SettingsDraft;
};

type SettingsSaveRequest = {
  readonly pendingSave: PendingSettingsSave;
};

type ScopeChipProps = {
  readonly icon: React.ReactNode;
  readonly label: string;
};

type FieldMetaPillProps = {
  readonly label: string;
  readonly tone?: "default" | "info" | "success" | "warning";
};

type TechnicalPreviewRow = {
  readonly keyLabel: string;
  readonly value: string;
};

const llmTabKey = "llm";
const accountTabKey = "account";
const platformDefaultModelValue = "__platform_default__";

const tabBodyStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 16,
  minHeight: 0,
};

const panelStackStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 18,
  minHeight: 0,
};

const formSectionStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 8,
};

const fieldCardStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 10,
};

const fieldHeaderRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "space-between",
};

const providerRailStyle: React.CSSProperties = {
  display: "flex",
  flexWrap: "wrap",
  gap: 10,
};

const previewRowStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "grid",
  gap: 12,
  gridTemplateColumns: "minmax(140px, 180px) minmax(0, 1fr)",
  paddingBlock: 10,
};

const previewKeyStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
  fontWeight: 600,
  letterSpacing: "0.04em",
  textTransform: "uppercase",
};

const codePreviewStyle: React.CSSProperties = {
  ...codeBlockStyle,
  display: "flex",
  flexDirection: "column",
  gap: 0,
  marginTop: 0,
};

const previewValueStyle: React.CSSProperties = {
  display: "inline-block",
  fontFamily: aevatarMonoFontFamily,
  maxWidth: "100%",
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

const statusCopyStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 13,
  lineHeight: 1.6,
  margin: 0,
};

const readOnlyFieldHeaderStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
};

function readSettingsSection(snapshot?: string): SettingsSection {
  const currentSearch =
    typeof snapshot === "string" && snapshot.includes("?")
      ? snapshot.slice(snapshot.indexOf("?"))
      : typeof window === "undefined"
        ? ""
        : window.location.search;
  const section = new URLSearchParams(currentSearch).get("section");
  return section === accountTabKey ? accountTabKey : llmTabKey;
}

function buildSettingsHref(section: SettingsSection): string {
  return section === llmTabKey ? "/settings" : `/settings?section=${section}`;
}

function normalizeUserConfigDraft(config?: StudioUserLlmSettings): SettingsDraft {
  return {
    preferredLlmSelection: resolveSavedUserLlmSelection(config),
  };
}

function draftsEqual(left: SettingsDraft, right: SettingsDraft): boolean {
  return userLlmSelectionsEqual(
    left.preferredLlmSelection,
    right.preferredLlmSelection,
  );
}

function snapshotSettingsDraft(draft: SettingsDraft): SettingsDraft {
  const selection = draft.preferredLlmSelection;
  return {
    preferredLlmSelection: selection
      ? cloneUserLlmSelection(selection)
      : undefined,
  };
}

function selectedModelValue(
  selection: UserLlmSelectionDraft | undefined,
): string | undefined {
  if (!selection) {
    return undefined;
  }

  return selection.modelSelection.kind === "explicit_model"
    ? selection.modelSelection.modelId
    : platformDefaultModelValue;
}

function selectedModelLabel(
  selection: UserLlmSelectionDraft | undefined,
): string {
  if (!selection) {
    return "System default";
  }

  return selection.modelSelection.kind === "explicit_model"
    ? selection.modelSelection.modelId
    : "Provider default";
}

function formatProviderHealth(
  options: readonly StudioUserLlmRouteOption[],
): {
  readonly tone: "default" | "error" | "success" | "warning";
  readonly value: string;
} {
  const readyCount = options.filter((option) => option.ready && option.allowed).length;
  const unavailableCount = Math.max(0, options.length - readyCount);

  if (readyCount === 0) {
    return {
      tone: "error",
      value: options.length > 0 ? "No ready routes" : "No routes connected",
    };
  }

  if (unavailableCount > 0) {
    return {
      tone: "warning",
      value: t("pages.settings.index.routes.ready.unavailable", "{readyCount} ready / {unavailableCount} unavailable", {
        readyCount,
        unavailableCount,
      }),
    };
  }

  return {
    tone: "success",
    value: t("pages.settings.index.routes.ready.count", "{readyCount} routes ready", {
      readyCount,
    }),
  };
}

const ScopeChip: React.FC<ScopeChipProps> = ({ icon, label }) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        alignItems: "center",
        background: token.colorFillQuaternary,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 999,
        color: token.colorTextSecondary,
        display: "inline-flex",
        fontSize: 13,
        fontWeight: 600,
        gap: 8,
        padding: "8px 12px",
      }}
    >
      <span style={{ color: token.colorPrimary }}>{icon}</span>
      <span>{label}</span>
    </div>
  );
};

const FieldMetaPill: React.FC<FieldMetaPillProps> = ({
  label,
  tone = "default",
}) => {
  const { token } = theme.useToken();
  const visual =
    tone === "success"
      ? {
          background: token.colorSuccessBg,
          borderColor: token.colorSuccessBorder,
          color: token.colorSuccessText,
        }
      : tone === "warning"
        ? {
            background: token.colorWarningBg,
            borderColor: token.colorWarningBorder,
            color: token.colorWarningText,
          }
        : tone === "info"
          ? {
              background: token.colorInfoBg,
              borderColor: token.colorInfoBorder,
              color: token.colorInfoText,
            }
          : {
              background: token.colorBgContainer,
              borderColor: token.colorBorderSecondary,
              color: token.colorTextSecondary,
            };

  return (
    <span
      style={{
        alignItems: "center",
        background: visual.background,
        border: `1px solid ${visual.borderColor}`,
        borderRadius: 999,
        color: visual.color,
        display: "inline-flex",
        fontSize: 11,
        fontWeight: 700,
        letterSpacing: "0.03em",
        lineHeight: 1,
        padding: "5px 8px",
        textTransform: "uppercase",
        whiteSpace: "nowrap",
      }}
    >
      {label}
    </span>
  );
};

const ConnectedProviderChip: React.FC<{
  readonly option: StudioUserLlmRouteOption;
  readonly selected: boolean;
}> = ({ option, selected }) => {
  const { token } = theme.useToken();
  const ready = option.ready && option.allowed;
  const sourceLabel =
    option.source === "user_service"
      ? t("pages.settings.index.provider.source.user.service", "User service")
      : option.source === "gateway_provider"
        ? t("pages.settings.index.provider.source.gateway", "Gateway provider")
        : option.source === "provider_diagnostic"
          ? t(
              "pages.settings.index.provider.source.diagnostic",
              "Provider diagnostic",
            )
          : t("pages.settings.index.provider.source.status", "Provider status");
  const readinessLabel = ready
    ? t("pages.settings.index.provider.ready", "Ready")
    : t("pages.settings.index.provider.unavailable", "Unavailable");
  const label = option.label;
  const accessibleLabel = `${label} · ${readinessLabel} · ${sourceLabel}`;
  const background = selected
    ? ready
      ? token.colorSuccessBg
      : token.colorFillTertiary
    : token.colorBgContainer;
  const borderColor = selected
    ? ready
      ? token.colorSuccessBorder
      : token.colorBorder
    : token.colorBorderSecondary;
  const textColor = ready
    ? selected
      ? token.colorSuccessText
      : token.colorText
    : token.colorTextTertiary;
  const dotColor = ready ? token.colorSuccess : token.colorTextQuaternary;

  return (
    <AevatarTooltip
      mouseEnterDelay={0.15}
      placement="top"
      title={accessibleLabel}
    >
      <div
        aria-label={accessibleLabel}
        role="status"
        style={{
          alignItems: "center",
          background,
          border: `1px solid ${borderColor}`,
          borderRadius: 999,
          color: textColor,
          cursor: "default",
          display: "inline-flex",
          fontSize: 13,
          fontWeight: selected ? 700 : 500,
          gap: 8,
          lineHeight: 1,
          padding: "8px 12px",
        }}
      >
        <span
          style={{
            background: dotColor,
            borderRadius: 999,
            display: "inline-block",
            height: 6,
            width: 6,
          }}
        />
        <span>{label}</span>
      </div>
    </AevatarTooltip>
  );
};

const SettingsPage: React.FC = () => {
  const toast = useConsoleToast();
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => "",
  );
  const activeSection = React.useMemo(
    () => readSettingsSection(locationSnapshot),
    [locationSnapshot],
  );
  const screens = Grid.useBreakpoint();
  const { token } = theme.useToken();
  const queryClient = useQueryClient();
  const settingsPanelStyle = React.useMemo(
    () => buildSettingsPanelStyle(token),
    [token],
  );
  const insetCardStyle = React.useMemo(
    () => buildSettingsInsetCardStyle(token),
    [token],
  );

  const userLlmSettingsQuery = useQuery({
    queryKey: ["settings", "user-llm-settings"],
    queryFn: () => studioApi.getUserLlmSettings(),
  });
  const userRuntimeQuery = useQuery({
    queryKey: ["settings", "user-config-runtime"],
    queryFn: () => studioApi.getUserConfigRuntime(),
  });

  const loadedDraft = React.useMemo(
    () => normalizeUserConfigDraft(userLlmSettingsQuery.data),
    [userLlmSettingsQuery.data],
  );
  const [draftState, setDraftState] = React.useState<SettingsDraftState>(() => ({
    baseline: loadedDraft,
    pendingSave: null,
    draftRevision: 0,
    saveError: null,
    value: loadedDraft,
  }));
  const saveTokenRef = React.useRef(0);
  const draft = draftState.value;
  const pendingSave = draftState.pendingSave;
  const saveError = draftState.saveError;
  React.useEffect(() => {
    if (!saveError) return;
    toast.error(
      t(
        "pages.settings.index.save.failed.toast",
        "Settings could not be saved. Try again.",
      ),
    );
  }, [saveError, toast]);
  const draftDirty = React.useMemo(
    () => !draftsEqual(draft, draftState.baseline),
    [draft, draftState.baseline],
  );
  const routeCatalogOptions = userLlmSettingsQuery.data?.routeOptions ?? [];
  const liveSelectionOptions = React.useMemo(
    () => buildUserLlmSelectionOptions(routeCatalogOptions),
    [routeCatalogOptions],
  );

  React.useEffect(() => () => {
    saveTokenRef.current += 1;
  }, []);

  React.useEffect(() => {
    if (!userLlmSettingsQuery.isSuccess) {
      return;
    }

    setDraftState((current) => {
      const currentPending = current.pendingSave;
      if (
        currentPending &&
        saveTokenRef.current === currentPending.saveToken &&
        draftsEqual(currentPending.expectedCommittedDraft, loadedDraft)
      ) {
        const hasNewerEdit =
          current.draftRevision !== currentPending.submittedRevision ||
          !draftsEqual(current.value, currentPending.submittedDraft);
        return hasNewerEdit
          ? {
              ...current,
              baseline: loadedDraft,
              pendingSave: null,
            }
          : {
              ...current,
              baseline: loadedDraft,
              pendingSave: null,
              value: loadedDraft,
            };
      }

      if (draftsEqual(current.value, current.baseline)) {
        return draftsEqual(current.value, loadedDraft) &&
          draftsEqual(current.baseline, loadedDraft)
          ? current
          : {
              ...current,
              baseline: loadedDraft,
              value: loadedDraft,
            };
      }

      if (!current.pendingSave && draftsEqual(current.value, loadedDraft)) {
        return {
          ...current,
          baseline: loadedDraft,
          value: loadedDraft,
        };
      }

      return current;
    });
  }, [loadedDraft, pendingSave, userLlmSettingsQuery.isSuccess]);

  const startPendingObservation = React.useCallback(
    (target: PendingSettingsSave) => {
      let observedDraft: SettingsDraft | undefined;
      void observeUserLlmSave({
        saveToken: target.saveToken,
        isCurrent: (saveToken) => saveTokenRef.current === saveToken,
        read: (signal) => studioApi.getUserLlmSettings(signal),
        isObserved: (settings) => {
          const candidate = normalizeUserConfigDraft(settings);
          const observed = userLlmSelectionsEqual(
            candidate.preferredLlmSelection,
            target.submittedDraft.preferredLlmSelection,
          );
          if (observed) {
            observedDraft = candidate;
          }
          return observed;
        },
        onResponse: (settings) => {
          void queryClient.cancelQueries({
            exact: true,
            queryKey: ["settings", "user-llm-settings"],
          });
          queryClient.setQueryData(
            ["settings", "user-llm-settings"],
            settings,
          );
        },
      }).then((result) => {
        if (saveTokenRef.current !== target.saveToken) {
          return;
        }

        if (result.phase === "accepted_unobserved") {
          setDraftState((current) =>
            current.pendingSave?.saveToken === target.saveToken
              ? {
                  ...current,
                  pendingSave: {
                    ...current.pendingSave,
                    phase: "accepted_unobserved",
                  },
                }
              : current,
          );
          return;
        }

        if (result.phase !== "observed") {
          return;
        }

        const committedDraft = observedDraft ?? target.expectedCommittedDraft;
        setDraftState((current) => {
          if (current.pendingSave?.saveToken !== target.saveToken) {
            return current;
          }

          const hasNewerEdit =
            current.draftRevision !== target.submittedRevision ||
            !draftsEqual(current.value, target.submittedDraft);
          return {
            ...current,
            baseline: committedDraft,
            pendingSave: null,
            value: hasNewerEdit ? current.value : committedDraft,
          };
        });
        void queryClient.invalidateQueries({
          queryKey: ["studio-user-llm-settings"],
        });
        void queryClient.invalidateQueries({
          queryKey: ["chat", "user-llm-settings"],
        });
      });
    },
    [queryClient],
  );

  const saveMutation = useMutation({
    mutationFn: async ({ pendingSave: target }: SettingsSaveRequest) => {
      const selection = target.submittedDraft.preferredLlmSelection;
      const receipt = !selection
        ? studioApi.saveUserLlmSettings({ action: "reset" })
        : selection.routeKind === "gateway"
        ? studioApi.saveUserLlmSettings({
            action: "select_gateway",
            gateway: { model: selection.modelSelection },
          })
        : studioApi.saveUserLlmSettings({
            action: "select_user_service",
            userService: {
              userServiceId: selection.nyxIdUserServiceId,
              model: selection.modelSelection,
            },
          });
      const observedReceipt = await receipt;
      if (!observedReceipt.accepted) {
        throw new Error(
          t(
            "pages.settings.index.save.not.accepted",
            "The settings write was not accepted.",
          ),
        );
      }

      return observedReceipt;
    },
    onSuccess: (receipt, request) => {
      const target = request.pendingSave;
      if (saveTokenRef.current !== target.saveToken) {
        return;
      }

      const acceptedTarget: PendingSettingsSave = {
        ...target,
        commandId: receipt.commandId,
        phase: "accepted",
      };
      setDraftState((current) =>
        current.pendingSave?.saveToken === target.saveToken
          ? { ...current, pendingSave: acceptedTarget, saveError: null }
          : current,
      );
      startPendingObservation(acceptedTarget);
    },
    onError: (error, request) => {
      const target = request.pendingSave;
      if (saveTokenRef.current !== target.saveToken) {
        return;
      }

      setDraftState((current) => {
        if (current.pendingSave?.saveToken !== target.saveToken) {
          return current;
        }

        const submittedDraftIsVisible =
          current.draftRevision === target.submittedRevision &&
          draftsEqual(current.value, target.submittedDraft);
        return {
          ...current,
          pendingSave: null,
          saveError: submittedDraftIsVisible
            ? describeError(error, "Failed to save settings.")
            : null,
        };
      });
    },
  });
  const retainedSelectionOptions = React.useMemo(() => {
    const retained: UserLlmSelectionOption[] = [];
    const liveValues = new Set(
      liveSelectionOptions
        .map((option) => option.value),
    );
    const seenValues = new Set<string>();
    const addRetained = (
      selection: UserLlmSelectionDraft | undefined,
      label: string | undefined,
    ) => {
      if (!selection) {
        return;
      }

      const value = encodeUserLlmSelectionValue(selection);
      if (liveValues.has(value) || seenValues.has(value)) {
        return;
      }

      seenValues.add(value);
      retained.push({
        allowed: false,
        label:
          trimConversationValue(label) ??
          (selection.routeKind === "gateway"
            ? "Gateway"
            : selection.nyxIdUserServiceId),
        modelCatalog: {
          certainty: "unavailable",
          modelIds: [],
          defaultModelId: null,
          diagnostic:
            userLlmSettingsQuery.data?.catalogDiagnostic ??
            "observation_unavailable",
        },
        ready: false,
        selection: cloneUserLlmSelection(selection),
        value,
      });
    };

    addRetained(
      pendingSave?.submittedDraft.preferredLlmSelection,
      pendingSave?.selectionLabel,
    );
    addRetained(
      loadedDraft.preferredLlmSelection,
      userLlmSettingsQuery.data?.savedRouteLabel,
    );
    return retained;
  }, [
    liveSelectionOptions,
    loadedDraft.preferredLlmSelection,
    pendingSave,
    userLlmSettingsQuery.data?.catalogDiagnostic,
    userLlmSettingsQuery.data?.savedRouteLabel,
  ]);
  const selectionOptions = React.useMemo(
    () => [...liveSelectionOptions, ...retainedSelectionOptions],
    [liveSelectionOptions, retainedSelectionOptions],
  );
  const preferredSelectionValue = draft.preferredLlmSelection
    ? encodeUserLlmSelectionValue(draft.preferredLlmSelection)
    : undefined;
  const preferredSelectionOption = React.useMemo(
    () =>
      preferredSelectionValue
        ? selectionOptions.find((option) => option.value === preferredSelectionValue)
        : undefined,
    [preferredSelectionValue, selectionOptions],
  );
  const preferredSelectionAvailable = Boolean(
    preferredSelectionOption?.ready && preferredSelectionOption.allowed,
  );
  const selectedRoute =
    preferredSelectionOption?.selection.routeValue ??
    draft.preferredLlmSelection?.routeValue;
  const backendSavedRouteLabel = trimConversationValue(
    userLlmSettingsQuery.data?.savedRouteLabel,
  );
  const preferredServiceLabel =
    draftsEqual(draft, loadedDraft) && backendSavedRouteLabel
      ? backendSavedRouteLabel
      : preferredSelectionOption?.label ??
        (draft.preferredLlmSelection?.routeKind === "gateway"
          ? "Gateway"
          : draft.preferredLlmSelection?.nyxIdUserServiceId ?? "System default");
  const routeSummaryLabel = preferredServiceLabel;
  const selectedModelCatalog = preferredSelectionOption?.modelCatalog;
  const modelChoiceAvailable = Boolean(
    preferredSelectionAvailable &&
      selectedModelCatalog &&
      selectedModelCatalog.certainty !== "unavailable",
  );
  const modelOptions = React.useMemo<SelectProps["options"]>(
    () =>
      modelChoiceAvailable
        ? [
            {
              label: t("pages.settings.index.model.behavior", "Model behavior"),
              options: [
                {
                  label: t(
                    "pages.settings.index.provider.default",
                    "Provider default",
                  ),
                  value: platformDefaultModelValue,
                },
              ],
            },
            ...(selectedModelCatalog?.certainty === "enumerated"
              ? [
                  {
                    label: preferredServiceLabel,
                    options: selectedModelCatalog.modelIds.map((model) => ({
                      label: model,
                      value: model,
                    })),
                  },
                ]
              : []),
          ]
        : [],
    [modelChoiceAvailable, preferredServiceLabel, selectedModelCatalog],
  );
  const draftModelAllowed = Boolean(
    draft.preferredLlmSelection &&
      modelChoiceAvailable &&
      (draft.preferredLlmSelection.modelSelection.kind === "provider_default" ||
        (draft.preferredLlmSelection.modelSelection.kind === "explicit_model" &&
          selectedModelCatalog?.certainty === "enumerated" &&
          selectedModelCatalog.modelIds.includes(
            draft.preferredLlmSelection.modelSelection.modelId,
          ))),
  );
  const displayedRuntimeBaseUrl = React.useMemo(
    () => userRuntimeQuery.data?.activeRuntimeBaseUrl ?? "",
    [userRuntimeQuery.data?.activeRuntimeBaseUrl],
  );
  const persistedRuntimeMode = React.useMemo(
    () => normalizeStudioUserConfigRuntimeMode(userRuntimeQuery.data?.runtimeMode),
    [userRuntimeQuery.data?.runtimeMode],
  );
  const runtimeModeLabel = React.useMemo(
    () => formatStudioUserConfigRuntimeModeLabel(persistedRuntimeMode),
    [persistedRuntimeMode],
  );
  const providerHealth = React.useMemo(
    () => formatProviderHealth(routeCatalogOptions),
    [routeCatalogOptions],
  );
  const readyProviderCount = routeCatalogOptions.filter((option) => option.ready && option.allowed).length;
  const unavailableProviderCount = Math.max(0, routeCatalogOptions.length - readyProviderCount);
  const isCatalogOptionSelected = React.useCallback(
    (option: StudioUserLlmRouteOption) => {
      const selection = draft.preferredLlmSelection;
      if (!selection) {
        return false;
      }

      return selection.routeKind === "gateway"
        ? option.source === "gateway_provider"
        : option.userServiceId?.trim() === selection.nyxIdUserServiceId;
    },
    [draft.preferredLlmSelection],
  );
  const providerDisplayList = React.useMemo(
    () =>
      [...routeCatalogOptions].sort((left, right) => {
        const leftSelected = isCatalogOptionSelected(left);
        const rightSelected = isCatalogOptionSelected(right);
        if (leftSelected !== rightSelected) {
          return leftSelected ? -1 : 1;
        }

        const leftReady = left.ready && left.allowed;
        const rightReady = right.ready && right.allowed;
        if (leftReady !== rightReady) {
          return leftReady ? -1 : 1;
        }

        return left.label.localeCompare(right.label);
      }),
    [isCatalogOptionSelected, routeCatalogOptions],
  );
  const llmCapabilities = userLlmSettingsQuery.data?.capabilities;
  const canEditRoute = Boolean(llmCapabilities?.canEditRoute);
  const canEditModel = Boolean(llmCapabilities?.canEditModel);
  const canSaveLlmSettings = Boolean(llmCapabilities?.canSave);
  const selectionStatus = userLlmSettingsQuery.data?.selectionStatus;
  const summaryGridStyle = React.useMemo<React.CSSProperties>(
    () => ({
      display: "grid",
      gap: 12,
      gridTemplateColumns: screens.md
        ? "repeat(3, minmax(0, 1fr))"
        : "repeat(1, minmax(0, 1fr))",
    }),
    [screens.md],
  );
  const bodyGridStyle = React.useMemo<React.CSSProperties>(
    () => ({
      display: "grid",
      gap: 16,
      gridTemplateColumns: screens.lg
        ? "minmax(0, 1.9fr) minmax(280px, 1fr)"
        : "minmax(0, 1fr)",
      minHeight: 0,
    }),
    [screens.lg],
  );

  const selectionSelectOptions = React.useMemo<SelectProps["options"]>(() => {
    const liveOptions = liveSelectionOptions
      .map((option) => ({
        disabled: !option.ready || !option.allowed,
        label: option.label,
        value: option.value,
      }));
    return [
      {
        label: t("pages.settings.index.available.services", "LLM services"),
        options: liveOptions,
      },
      ...(retainedSelectionOptions.length > 0
        ? [
            {
              label: t(
                "pages.settings.index.retained.services",
                "Saved or accepted services",
              ),
              options: retainedSelectionOptions.map((option) => ({
                disabled: true,
                label: t(
                  "pages.settings.index.service.unavailable",
                  "{service} (unavailable)",
                  { service: option.label },
                ),
                value: option.value,
              })),
            },
          ]
        : []),
    ];
  }, [liveSelectionOptions, retainedSelectionOptions]);

  const advancedItems = React.useMemo<CollapseProps["items"]>(
    () => [
      {
        key: "advanced-runtime",
        label: t("pages.settings.index.advanced.runtime", "Advanced runtime"),
        children: (
          <div style={panelStackStyle}>
            <div style={formSectionStyle}>
              <div style={readOnlyFieldHeaderStyle}>
                <Typography.Text strong>{t("pages.settings.index.runtime.base.url", "Runtime base URL")}</Typography.Text>
                <FieldMetaPill label={runtimeModeLabel} tone="info" />
                <FieldMetaPill label={t("pages.settings.index.read.only", "Read only")} />
              </div>
              <Typography.Text type="secondary">
                {t("pages.settings.index.console.default.runtime.endpoint.resolved", "Console default runtime endpoint resolved from the active mode.")}</Typography.Text>
              <Input
                prefix={
                  <LockOutlined
                    style={{ color: token.colorTextTertiary, fontSize: 12 }}
                  />
                }
                readOnly
                style={{
                  background: token.colorFillQuaternary,
                  borderColor: token.colorBorderSecondary,
                  borderRadius: 10,
                  color: token.colorTextSecondary,
                  cursor: "default",
                  fontFamily: aevatarMonoFontFamily,
                }}
                value={displayedRuntimeBaseUrl}
              />
            </div>
          </div>
        ),
      },
    ],
    [
      displayedRuntimeBaseUrl,
      runtimeModeLabel,
      token.colorBorderSecondary,
      token.colorFillQuaternary,
      token.colorTextSecondary,
      token.colorTextTertiary,
    ],
  );

  const technicalPreviewRows = React.useMemo<TechnicalPreviewRow[]>(
    () => [
      {
        keyLabel: LLM_ROUTE_HEADER_KEY,
        value: selectedRoute || "system_default",
      },
      {
        keyLabel: LLM_MODEL_HEADER_KEY,
        value: selectedModelLabel(draft.preferredLlmSelection),
      },
      {
        keyLabel: "studio.runtime_base_url",
        value: displayedRuntimeBaseUrl,
      },
      {
        keyLabel: "aevatar.runtime_mode",
        value: persistedRuntimeMode,
      },
    ],
    [
      displayedRuntimeBaseUrl,
      draft.preferredLlmSelection,
      persistedRuntimeMode,
      selectedRoute,
    ],
  );

  const handleSave = React.useCallback(() => {
    const selection = draft.preferredLlmSelection;
    if (!selection) {
      return;
    }

    const selectionValue = encodeUserLlmSelectionValue(selection);
    const exactOption = liveSelectionOptions.find(
      (option) =>
        option.value === selectionValue && option.ready && option.allowed,
    );
    if (!exactOption) {
      return;
    }

    const saveToken = saveTokenRef.current + 1;
    saveTokenRef.current = saveToken;
    const submittedDraft = snapshotSettingsDraft(draft);
    const target: PendingSettingsSave = {
      saveToken,
      submittedRevision: draftState.draftRevision,
      submittedDraft,
      expectedCommittedDraft: submittedDraft,
      selectionLabel: exactOption.label,
      phase: "saving",
    };
    setDraftState((current) => ({
      ...current,
      pendingSave: target,
      saveError: null,
    }));
    saveMutation.mutate({ pendingSave: target });
  }, [draft, draftState.draftRevision, liveSelectionOptions, saveMutation]);

  const handleRetryObservation = React.useCallback(() => {
    if (
      !pendingSave ||
      pendingSave.phase !== "accepted_unobserved" ||
      saveTokenRef.current !== pendingSave.saveToken
    ) {
      return;
    }

    const acceptedTarget: PendingSettingsSave = {
      ...pendingSave,
      phase: "accepted",
    };
    setDraftState((current) =>
      current.pendingSave?.saveToken === acceptedTarget.saveToken
        ? { ...current, pendingSave: acceptedTarget }
        : current,
    );
    startPendingObservation(acceptedTarget);
  }, [pendingSave, startPendingObservation]);

  const handleReset = React.useCallback(() => {
    const saveToken = saveTokenRef.current + 1;
    saveTokenRef.current = saveToken;
    const submittedDraft: SettingsDraft = {
      preferredLlmSelection: undefined,
    };
    const target: PendingSettingsSave = {
      saveToken,
      submittedRevision: draftState.draftRevision,
      submittedDraft,
      expectedCommittedDraft: submittedDraft,
      selectionLabel: t(
        "pages.settings.index.system.default",
        "System default",
      ),
      phase: "saving",
    };
    setDraftState((current) => ({
      ...current,
      pendingSave: target,
      saveError: null,
      value: submittedDraft,
    }));
    saveMutation.mutate({ pendingSave: target });
  }, [draftState.draftRevision, saveMutation]);

  const handlePreferredServiceChange = React.useCallback(
    (nextValue: string) => {
      const nextSelection = decodeUserLlmSelectionValue(
        nextValue,
        selectionOptions,
      );
      if (!nextSelection) {
        return;
      }

      setDraftState((current) => ({
        ...current,
        draftRevision: current.draftRevision + 1,
        value: {
          preferredLlmSelection: cloneUserLlmSelection(nextSelection),
        },
      }));
    },
    [selectionOptions],
  );

  const handleDefaultModelChange = React.useCallback((nextValue: unknown) => {
    setDraftState((current) => ({
      ...current,
      draftRevision: current.draftRevision + 1,
      value: {
        preferredLlmSelection: current.value.preferredLlmSelection
          ? {
              ...current.value.preferredLlmSelection,
              modelSelection:
                nextValue === platformDefaultModelValue
                  ? { kind: "provider_default" }
                  : {
                      kind: "explicit_model",
                      modelId: String(nextValue || ""),
                    },
            }
          : undefined,
      },
    }));
  }, []);

  const handleSectionChange = React.useCallback((nextKey: string) => {
    const nextSection: SettingsSection =
      nextKey === accountTabKey ? accountTabKey : llmTabKey;
    history.replace(buildSettingsHref(nextSection));
  }, []);

  const llmLoadError =
    userLlmSettingsQuery.isError || userRuntimeQuery.isError
      ? describeError(
          userLlmSettingsQuery.error || userRuntimeQuery.error,
          "Failed to load LLM defaults.",
        )
      : null;

  const headerExtra =
    activeSection === llmTabKey ? (
      <Space>
        <Button
          disabled={
            !canSaveLlmSettings ||
            !loadedDraft.preferredLlmSelection ||
            saveMutation.isPending
          }
          icon={<ReloadOutlined />}
          onClick={handleReset}
        >
          {t("pages.settings.index.reset", "Reset")}</Button>
        <Button
          disabled={
            !draftDirty ||
            !canSaveLlmSettings ||
            !draft.preferredLlmSelection ||
            !preferredSelectionAvailable ||
            !draftModelAllowed
          }
          loading={saveMutation.isPending}
          onClick={handleSave}
          type="primary"
        >
          {t("pages.settings.index.save.config", "Save config")}</Button>
      </Space>
    ) : null;

  const tabDefinitions = React.useMemo(
    (): readonly { key: SettingsSection; label: string }[] => [
      { key: llmTabKey, label: "LLM" },
      { key: accountTabKey, label: "Account" },
    ],
    [],
  );
  const tabButtonRefs = React.useRef<Record<SettingsSection, HTMLButtonElement | null>>({
    [llmTabKey]: null,
    [accountTabKey]: null,
  });
  const activePanelId = `${activeSection}-panel`;
  const activeTabId = `${activeSection}-tab`;
  const handleSectionTabKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLButtonElement>, currentKey: SettingsSection) => {
      const currentIndex = tabDefinitions.findIndex((tab) => tab.key === currentKey);
      if (currentIndex < 0) {
        return;
      }

      let nextIndex = currentIndex;
      if (event.key === "ArrowRight") {
        nextIndex = (currentIndex + 1) % tabDefinitions.length;
      } else if (event.key === "ArrowLeft") {
        nextIndex = (currentIndex - 1 + tabDefinitions.length) % tabDefinitions.length;
      } else if (event.key === "Home") {
        nextIndex = 0;
      } else if (event.key === "End") {
        nextIndex = tabDefinitions.length - 1;
      } else {
        return;
      }

      event.preventDefault();
      const nextSection = tabDefinitions[nextIndex]?.key ?? currentKey;
      handleSectionChange(nextSection);
      window.requestAnimationFrame(() => {
        tabButtonRefs.current[nextSection]?.focus();
      });
    },
    [handleSectionChange, tabDefinitions],
  );

  const llmSection = React.useMemo(
    () => (
      <div style={tabBodyStyle}>
            <div style={summaryGridStyle}>
              <SummaryMetric
                label={t("pages.settings.index.saved.selection", "Saved selection")}
                tone={selectionStatus === "ready" ? "success" : "warning"}
                value={routeSummaryLabel}
              />
              <SummaryMetric
                label={t("pages.settings.index.default.model", "Default model")}
                tone={
                  draft.preferredLlmSelection?.modelSelection.kind ===
                  "explicit_model"
                    ? "info"
                    : "default"
                }
                value={selectedModelLabel(draft.preferredLlmSelection)}
              />
              <SummaryMetric
                label={t("pages.settings.index.provider.health", "Provider health")}
                tone={providerHealth.tone}
                value={providerHealth.value}
              />
            </div>

            {llmLoadError ? (
              <Alert
                message={t("pages.settings.index.failed.to.load.defaults", "Failed to load defaults")}
                description={llmLoadError}
                showIcon
                type="error"
              />
            ) : null}

            {pendingSave && pendingSave.phase !== "saving" ? (
              <Alert
                action={
                  pendingSave.phase === "accepted_unobserved" ? (
                    <Button
                      icon={<ReloadOutlined />}
                      onClick={handleRetryObservation}
                      size="small"
                    >
                      {t(
                        "pages.settings.index.retry.observation",
                        "Retry observation",
                      )}
                    </Button>
                  ) : undefined
                }
                message={
                  t(
                    "pages.settings.index.update.submitted",
                    "Update submitted · {commandId}",
                    { commandId: pendingSave.commandId ?? "pending" },
                  )
                }
                description={
                  pendingSave.phase === "accepted_unobserved"
                    ? t(
                        "pages.settings.index.update.not.observed",
                        "The exact selection has not been observed yet.",
                      )
                    : t(
                        "pages.settings.index.update.awaiting.observation",
                        "Waiting for the exact service and model selection to appear.",
                      )
                }
                showIcon
                type={
                  pendingSave.phase === "accepted_unobserved" ? "warning" : "info"
                }
              />
            ) : null}

            {selectionStatus === "verification_unavailable" ? (
              <Alert
                action={
                  userLlmSettingsQuery.data?.capabilities.canRetryCatalog ? (
                    <Button
                      icon={<ReloadOutlined />}
                      onClick={() => userLlmSettingsQuery.refetch()}
                      size="small"
                    >
                      {t("pages.settings.index.retry", "Retry")}</Button>
                  ) : undefined
                }
                message={t(
                  "pages.settings.index.verification.unavailable",
                  "Verification unavailable",
                )}
                description={t(
                  "pages.settings.index.verification.unavailable.description",
                  "The exact saved selection is retained. Retry verification before changing it.",
                )}
                showIcon
                type="warning"
              />
            ) : null}

            {selectionStatus === "needs_repair" ? (
              <Alert
                message={t(
                  "pages.settings.index.selection.needs.repair",
                  "Saved selection needs repair",
                )}
                description={t(
                  "pages.settings.index.selection.needs.repair.description",
                  "{service} · {model} is unavailable. New requests will not switch providers; choose a replacement or reset to System default.",
                  {
                    model: selectedModelLabel(loadedDraft.preferredLlmSelection),
                    service: preferredServiceLabel,
                  },
                )}
                showIcon
                type="warning"
              />
            ) : null}

            {selectionStatus === "legacy_repair_required" ? (
              <Alert
                message={t(
                  "pages.settings.index.selection.reselect",
                  "Reselect LLM service and model",
                )}
                description={t(
                  "pages.settings.index.selection.reselect.description",
                  "The saved legacy values are not a complete selection and cannot be used.",
                )}
                showIcon
                type="warning"
              />
            ) : null}

            <div style={bodyGridStyle}>
              <div style={panelStackStyle}>
                <AevatarPanel
                  description={t("pages.settings.index.choose.the.llm.service.and.model", "Choose the LLM service and model used for new chats, Studio sessions, and global tools that do not set their own overrides.")}
                  style={settingsPanelStyle}
                  title={t("pages.settings.index.edit.defaults", "Edit defaults")}
                >
                  {userLlmSettingsQuery.isLoading || userRuntimeQuery.isLoading ? (
                    <div style={{ padding: 20 }}>
                      <Typography.Text type="secondary">
                        {t("pages.settings.index.loading.your.current.defaults", "Loading your current defaults...")}</Typography.Text>
                    </div>
                  ) : (
                    <div style={{ ...panelStackStyle, padding: 20 }}>
                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>{t("pages.settings.index.preferred.route", "Preferred LLM service")}</Typography.Text>
                          <FieldMetaPill
                            label={
                              pendingSave
                                ? t(
                                    "pages.settings.index.save.pending",
                                    "Save pending",
                                  )
                                : selectionStatus === "ready" &&
                                    draftsEqual(draft, loadedDraft)
                                  ? t("pages.settings.index.active", "Active")
                                  : selectionStatus === "system_default"
                                    ? t(
                                        "pages.settings.index.system.default",
                                        "System default",
                                      )
                                    : t(
                                        "pages.settings.index.attention.required",
                                        "Attention required",
                                      )
                            }
                            tone={
                              pendingSave
                                ? "info"
                                : selectionStatus === "ready"
                                  ? "success"
                                  : "warning"
                            }
                          />
                        </div>
                        <Typography.Text type="secondary">
                          {t("pages.settings.index.choose.the.primary.route.used", "Choose the Gateway or an exact connected service used for requests.")}</Typography.Text>
                        <Select
                          aria-label={t("pages.settings.index.preferred.route.2", "Preferred LLM service")}
                          disabled={!canEditRoute}
                          onChange={handlePreferredServiceChange}
                          optionFilterProp="label"
                          options={selectionSelectOptions}
                          showSearch
                          value={preferredSelectionValue}
                          virtual={false}
                        />
                        {!preferredSelectionAvailable && draft.preferredLlmSelection ? (
                          <Typography.Text type="warning">
                            {t(
                              "pages.settings.index.saved.service.unavailable.no.fallback",
                              "Saved service unavailable. New requests will not switch providers.",
                            )}
                          </Typography.Text>
                        ) : (
                          <Typography.Text type="secondary">
                            {t("pages.settings.index.saved.selection.label", "Saved selection:")}{" "}
                            {routeSummaryLabel || preferredServiceLabel}
                          </Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>{t("pages.settings.index.connected.providers", "Connected providers")}</Typography.Text>
                          <Space size={6} wrap>
                            <FieldMetaPill
                              label={t(
                                "pages.settings.index.providers.ready.count",
                                "{count} ready",
                                { count: readyProviderCount },
                              )}
                              tone={readyProviderCount > 0 ? "success" : "default"}
                            />
                            {unavailableProviderCount > 0 ? (
                              <FieldMetaPill
                                label={t(
                                  "pages.settings.index.providers.unavailable.count",
                                  "{count} unavailable",
                                  { count: unavailableProviderCount },
                                )}
                                tone="warning"
                              />
                            ) : null}
                          </Space>
                        </div>
                        <Typography.Text type="secondary">
                          {t("pages.settings.index.saved.route.is", "Saved route is")}{" "}
                          {`${routeSummaryLabel}.`}
                        </Typography.Text>
                        {providerDisplayList.length > 0 ? (
                          <div style={providerRailStyle}>
                            {providerDisplayList.map((option) => (
                              <ConnectedProviderChip
                                key={`${option.source}-${option.userServiceId ?? `${option.routeValue}-${option.serviceSlug ?? option.label}`}`}
                                option={option}
                                selected={isCatalogOptionSelected(option)}
                              />
                            ))}
                          </div>
                        ) : (
                          <Typography.Text type="secondary">
                            {t("pages.settings.index.no.connected.providers.discovered.yet", "No connected providers discovered yet.")}</Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>{t("pages.settings.index.default.model.2", "Default model")}</Typography.Text>
                          <FieldMetaPill
                            label={
                              selectedModelCatalog?.certainty === "enumerated"
                                ? `${selectedModelCatalog.modelIds.length} live`
                                : selectedModelCatalog?.certainty === "not_verifiable"
                                  ? t(
                                      "pages.settings.index.provider.default.only",
                                      "Provider default only",
                                    )
                                  : t(
                                      "pages.settings.index.model.unavailable",
                                      "Unavailable",
                                    )
                            }
                            tone={modelChoiceAvailable ? "info" : "warning"}
                          />
                        </div>
                        <Select
                          aria-label={t("pages.settings.index.default.model.3", "Default model")}
                          disabled={!canEditModel || !modelChoiceAvailable}
                          onChange={handleDefaultModelChange}
                          optionFilterProp="label"
                          options={modelOptions}
                          placeholder={t(
                            "pages.settings.index.choose.verified.model",
                            "Choose a verified model",
                          )}
                          showSearch
                          value={selectedModelValue(draft.preferredLlmSelection)}
                          virtual={false}
                        />
                      </div>

                      <Collapse
                        bordered={false}
                        ghost
                        items={advancedItems}
                        style={{
                          background: token.colorFillQuaternary,
                          border: `1px solid ${token.colorBorderSecondary}`,
                          borderRadius: token.borderRadiusLG,
                          paddingInline: 12,
                        }}
                      />
                    </div>
                  )}
                </AevatarPanel>
              </div>

              <div style={panelStackStyle}>
                <AevatarPanel style={settingsPanelStyle} title={t("pages.settings.index.how.defaults.work", "How defaults work")}>
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(1, minmax(0, 1fr))",
                      }}
                    >
                      <SummaryField label={t("pages.settings.index.saved.service", "Saved service")} value={preferredServiceLabel} />
                      <SummaryField
                        label={t("pages.settings.index.saved.route", "Saved route")}
                        value={routeSummaryLabel}
                      />
                      <SummaryField label={t("pages.settings.index.runtime.mode", "Runtime mode")} value={runtimeModeLabel} />
                      <SummaryField
                        label={t("pages.settings.index.runtime.url", "Runtime URL")}
                        value={
                          <AevatarTooltip
                            mouseEnterDelay={0.15}
                            placement="topLeft"
                            title={displayedRuntimeBaseUrl}
                          >
                            <Typography.Text style={previewValueStyle}>
                              {truncateMiddle(displayedRuntimeBaseUrl, 18, 14)}
                            </Typography.Text>
                          </AevatarTooltip>
                        }
                      />
                    </div>
                    <p style={statusCopyStyle}>
                      {t("pages.settings.index.these.defaults.apply.when.creating", "These defaults apply when creating new chats, Studio sessions, and global tools that do not specify their own route or model.")}</p>
                    <p style={statusCopyStyle}>
                      {t(
                        "pages.settings.index.unavailable.selection.does.not.fallback",
                        "An unavailable saved selection is shown for repair; requests do not silently switch providers.",
                      )}</p>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title={t("pages.settings.index.apply.scope", "Apply scope")}>
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      {t("pages.settings.index.these.defaults.currently.apply.to", "These defaults currently apply to:")}</Typography.Text>
                    <Space size={[10, 10]} wrap>
                      <ScopeChip icon={<CommentOutlined />} label="Chat" />
                      <ScopeChip icon={<ExperimentOutlined />} label="Studio" />
                      <ScopeChip icon={<ToolOutlined />} label={t("pages.settings.index.global.tools", "Global tools")} />
                    </Space>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title={t("pages.settings.index.technical.preview", "Technical preview")}>
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      {t("pages.settings.index.these.values.reflect.saved.selection", "These values reflect the exact saved selection and stored runtime defaults.")}</Typography.Text>
                    <div style={codePreviewStyle}>
                      {technicalPreviewRows.map((row, index) => (
                        <div
                          key={row.keyLabel}
                          style={{
                            ...previewRowStyle,
                            borderBottom:
                              index === technicalPreviewRows.length - 1
                                ? "none"
                                : `1px solid ${token.colorBorderSecondary}`,
                          }}
                        >
                          <Typography.Text style={previewKeyStyle}>
                            {row.keyLabel}
                          </Typography.Text>
                          <AevatarTooltip
                            mouseEnterDelay={0.15}
                            placement="topLeft"
                            title={
                              <span
                                style={{
                                  fontFamily: aevatarMonoFontFamily,
                                  overflowWrap: "anywhere",
                                }}
                              >
                                {row.value}
                              </span>
                            }
                          >
                            <Typography.Text style={previewValueStyle}>
                              {truncateMiddle(row.value, 14, 12)}
                            </Typography.Text>
                          </AevatarTooltip>
                        </div>
                      ))}
                    </div>
                  </div>
                </AevatarPanel>
              </div>
            </div>
      </div>
    ),
    [
      advancedItems,
      bodyGridStyle,
      draft.preferredLlmSelection,
      displayedRuntimeBaseUrl,
      canEditModel,
      canEditRoute,
      llmLoadError,
      loadedDraft.preferredLlmSelection,
      modelChoiceAvailable,
      modelOptions,
      selectedModelCatalog,
      insetCardStyle,
      preferredSelectionAvailable,
      preferredSelectionValue,
      preferredServiceLabel,
      providerHealth.tone,
      providerHealth.value,
      providerDisplayList,
      readyProviderCount,
      pendingSave,
      handleDefaultModelChange,
      handlePreferredServiceChange,
      handleRetryObservation,
      isCatalogOptionSelected,
      selectionStatus,
      selectionSelectOptions,
      routeSummaryLabel,
      settingsPanelStyle,
      summaryGridStyle,
      technicalPreviewRows,
      token.colorBorderSecondary,
      token.colorFillQuaternary,
      token.borderRadiusLG,
      unavailableProviderCount,
      userLlmSettingsQuery.data?.capabilities.canRetryCatalog,
      userLlmSettingsQuery.isLoading,
      userLlmSettingsQuery.refetch,
      userRuntimeQuery.isLoading,
      runtimeModeLabel,
    ],
  );

  const accountSection = React.useMemo(
    () => (
      <div style={tabBodyStyle}>
        {draftDirty ? (
          <Alert
            message={t("pages.settings.index.llm.changes.are.still.pending", "LLM changes are still pending save.")}
            description={t("pages.settings.index.you.can.return.to.the", "You can return to the LLM tab and save whenever you are ready.")}
            showIcon
            type="info"
          />
        ) : null}
        <AccountSettingsContent />
      </div>
    ),
    [draftDirty],
  );

  return (
    <SettingsPageShell
      content={
        activeSection === llmTabKey
          ? t(
              "pages.settings.index.llmContent",
              "Personal defaults for Chat and Studio.",
            )
          : t(
              "pages.settings.index.accountContent",
              "Identity, session, and access details for this browser.",
            )
      }
      extra={headerExtra}
      title={t("pages.settings.index.title", "Settings")}
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div role="tablist" style={buildSettingsSwitchRailStyle(token)}>
          {tabDefinitions.map((option) => {
            const active = activeSection === option.key;
            return (
              <button
                key={option.key}
                aria-controls={active ? activePanelId : undefined}
                aria-selected={active}
                id={`${option.key}-tab`}
                onKeyDown={(event) =>
                  handleSectionTabKeyDown(event, option.key as SettingsSection)
                }
                onClick={() => handleSectionChange(option.key)}
                role="tab"
                ref={(node) => {
                  tabButtonRefs.current[option.key as SettingsSection] = node;
                }}
                style={buildSettingsSwitchButtonStyle(token, active)}
                tabIndex={active ? 0 : -1}
                type="button"
              >
                {option.label}
              </button>
            );
          })}
        </div>
        <div
          aria-labelledby={activeTabId}
          id={activePanelId}
          role="tabpanel"
        >
          {activeSection === llmTabKey ? llmSection : accountSection}
        </div>
      </div>
    </SettingsPageShell>
  );
};

export default SettingsPage;
