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
  Tooltip,
  Typography,
  theme,
} from "antd";
import type { CollapseProps, SelectProps } from "antd";
import React from "react";
import {
  LLM_MODEL_HEADER_KEY,
  LLM_ROUTE_HEADER_KEY,
  buildConversationModelGroups,
  describeConversationRoute,
  normalizeUserLlmRoute,
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
import { codeBlockStyle } from "@/shared/ui/proComponents";
import AccountSettingsContent from "./accountContent";
import {
  buildUserLlmSelectionOptions,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
  userLlmSelectionsEqual,
} from "./userLlmSelection";
import type { UserLlmSelectionDraft } from "./userLlmSelection";
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
  readonly defaultModel: string;
  readonly preferredLlmSelection: UserLlmSelectionDraft | undefined;
};

type PendingSettingsSave = {
  readonly revision: number;
  readonly target: SettingsDraft;
};

type SettingsDraftState = {
  readonly baseline: SettingsDraft;
  readonly pendingSave: PendingSettingsSave | null;
  readonly revision: number;
  readonly value: SettingsDraft;
};

type SettingsSaveRequest = {
  readonly draft: SettingsDraft;
  readonly revision: number;
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
    defaultModel: trimConversationValue(config?.defaultModel) ?? "",
    preferredLlmSelection: resolveSavedUserLlmSelection(config),
  };
}

function draftsEqual(left: SettingsDraft, right: SettingsDraft): boolean {
  return (
    trimConversationValue(left.defaultModel) === trimConversationValue(right.defaultModel) &&
    userLlmSelectionsEqual(
      left.preferredLlmSelection,
      right.preferredLlmSelection,
    )
  );
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
    <Tooltip
      mouseEnterDelay={0.15}
      placement="top"
      title={accessibleLabel}
    >
      <div
        aria-label={accessibleLabel}
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
        tabIndex={0}
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
    </Tooltip>
  );
};

const SettingsPage: React.FC = () => {
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
    revision: 0,
    value: loadedDraft,
  }));
  const draft = draftState.value;
  const pendingSave = draftState.pendingSave;
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const draftDirty = React.useMemo(
    () => !draftsEqual(draft, draftState.baseline),
    [draft, draftState.baseline],
  );

  React.useEffect(() => {
    if (!userLlmSettingsQuery.isSuccess) {
      return;
    }

    setDraftState((current) => {
      const currentPending = current.pendingSave;
      if (currentPending && draftsEqual(currentPending.target, loadedDraft)) {
        const hasNewerEdit =
          current.revision !== currentPending.revision ||
          !draftsEqual(current.value, currentPending.target);
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

  const saveMutation = useMutation({
    mutationFn: async ({ draft: nextDraft }: SettingsSaveRequest) => {
      const model = trimConversationValue(nextDraft.defaultModel) ?? "";
      const selection = nextDraft.preferredLlmSelection;
      if (!selection) {
        throw new Error(
          t(
            "pages.settings.index.choose.exact.llm.service.before.saving",
            "Choose an exact LLM service before saving.",
          ),
        );
      }

      const receipt = selection.kind === "gateway"
        ? studioApi.saveUserLlmSettings({
            routeValue: selection.routeValue,
            model,
          })
        : studioApi.saveUserLlmSettings({
            userServiceId: selection.userServiceId,
            model,
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
    onSuccess: async (_receipt, request) => {
      setSaveError(null);
      setDraftState((current) => ({
        ...current,
        pendingSave: {
          revision: request.revision,
          target: request.draft,
        },
      }));
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["settings", "user-llm-settings"] }),
        queryClient.invalidateQueries({ queryKey: ["studio-user-llm-settings"] }),
        queryClient.invalidateQueries({ queryKey: ["chat", "user-llm-settings"] }),
      ]);
    },
    onError: (error) => {
      setSaveError(describeError(error, "Failed to save settings."));
    },
  });

  const routeCatalogOptions = userLlmSettingsQuery.data?.routeOptions ?? [];
  const selectionOptions = React.useMemo(
    () => buildUserLlmSelectionOptions(routeCatalogOptions),
    [routeCatalogOptions],
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
  const draftMatchesLoaded = draftsEqual(draft, loadedDraft);
  const selectedRoute = draft.preferredLlmSelection?.routeValue;
  const effectiveRoute = React.useMemo(
    () =>
      draftMatchesLoaded
        ? normalizeUserLlmRoute(userLlmSettingsQuery.data?.effectiveRoute)
        : normalizeUserLlmRoute(selectedRoute),
    [draftMatchesLoaded, selectedRoute, userLlmSettingsQuery.data?.effectiveRoute],
  );
  const routeFallbackActive = draftMatchesLoaded
    ? Boolean(userLlmSettingsQuery.data?.routeFallbackActive)
    : false;
  const backendEffectiveRouteLabel = trimConversationValue(
    userLlmSettingsQuery.data?.effectiveRouteLabel,
  );
  const backendSavedRouteLabel = trimConversationValue(
    userLlmSettingsQuery.data?.savedRouteLabel,
  );
  const routeDisplayOptions = React.useMemo(
    () =>
      routeCatalogOptions.map((option) => ({
        label: option.label,
        value: normalizeUserLlmRoute(option.routeValue),
      })),
    [routeCatalogOptions],
  );
  const routeSummaryLabel =
    draftMatchesLoaded && backendEffectiveRouteLabel
      ? backendEffectiveRouteLabel
      : describeConversationRoute(effectiveRoute, routeDisplayOptions);
  const preferredRouteLabel =
    draftMatchesLoaded && backendSavedRouteLabel
      ? backendSavedRouteLabel
      : preferredSelectionOption?.label ??
        describeConversationRoute(selectedRoute, routeDisplayOptions);
  const modelGroups = React.useMemo(
    () =>
      buildConversationModelGroups({
        effectiveRoute: selectedRoute ?? effectiveRoute,
        settings: userLlmSettingsQuery.data,
      }),
    [effectiveRoute, selectedRoute, userLlmSettingsQuery.data],
  );
  const modelOptions = React.useMemo<SelectProps["options"]>(
    () =>
      modelGroups.map((group) => ({
        label: group.label,
        options: group.models.map((model) => ({
          label: model,
          value: model,
        })),
      })),
    [modelGroups],
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

      return selection.kind === "gateway"
        ? option.source === "gateway_provider"
        : option.userServiceId?.trim() === selection.userServiceId;
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
  const catalogUnavailable = userLlmSettingsQuery.data?.catalogStatus === "unavailable";
  const savedServiceIdentityMissing =
    userLlmSettingsQuery.data?.savedRouteKind === "nyx_id_user_service" &&
    !trimConversationValue(userLlmSettingsQuery.data.savedUserServiceId);
  const defaultModelPlaceholder = React.useMemo(() => {
    const routeLabel =
      preferredRouteLabel ||
      t("pages.settings.index.selected.route", "the selected route");
    if (modelOptions && modelOptions.length > 0) {
      return t(
        "pages.settings.index.model.search.for",
        "Search models for {route}",
        { route: routeLabel },
      );
    }

    return preferredRouteLabel
      ? t(
          "pages.settings.index.model.type.for",
          "Type a model ID for {route}",
          { route: preferredRouteLabel },
        )
      : t("pages.settings.index.model.type", "Type a model ID");
  }, [modelOptions, preferredRouteLabel]);
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
    const readyOptions = selectionOptions
      .filter((option) => option.ready && option.allowed)
      .map((option) => ({
        label: option.label,
        value: option.value,
      }));
    const hasDraftSelection = Boolean(
      preferredSelectionValue &&
        readyOptions.some((option) => option.value === preferredSelectionValue),
    );
    return [
      {
        label: t("pages.settings.index.available.services", "Available services"),
        options: readyOptions,
      },
      ...(preferredSelectionValue && !hasDraftSelection
        ? [
            {
              label: t("pages.settings.index.current.saved.route", "Current saved service"),
              options: [
                {
                  label: t("pages.settings.index.route.unavailable", "{route} (unavailable)", {
                    route: preferredRouteLabel,
                  }),
                  value: preferredSelectionValue,
                },
              ],
            },
          ]
        : []),
    ];
  }, [preferredRouteLabel, preferredSelectionValue, selectionOptions]);

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
        value: effectiveRoute || "nyxid_gateway",
      },
      {
        keyLabel: LLM_MODEL_HEADER_KEY,
        value: trimConversationValue(draft.defaultModel) || "unset",
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
    [displayedRuntimeBaseUrl, draft.defaultModel, effectiveRoute, persistedRuntimeMode],
  );

  const handleSave = React.useCallback(() => {
    saveMutation.mutate({
      draft,
      revision: draftState.revision,
    });
  }, [draft, draftState.revision, saveMutation]);

  const handleReset = React.useCallback(() => {
    setDraftState((current) => ({
      ...current,
      baseline: loadedDraft,
      revision: current.revision + 1,
      value: loadedDraft,
    }));
    setSaveError(null);
  }, [loadedDraft]);

  const handlePreferredServiceChange = React.useCallback(
    (nextValue: string) => {
      const nextSelection = decodeUserLlmSelectionValue(
        nextValue,
        selectionOptions,
      );
      if (!nextSelection) {
        return;
      }

      const nextRouteGroups = buildConversationModelGroups({
        effectiveRoute: nextSelection.routeValue,
        settings: userLlmSettingsQuery.data,
      });
      setDraftState((current) => {
        const currentModel = trimConversationValue(current.value.defaultModel);
        const shouldClearModel =
          Boolean(currentModel) &&
          nextRouteGroups.length > 0 &&
          !nextRouteGroups.some((group) => group.models.includes(currentModel!));
        return {
          ...current,
          revision: current.revision + 1,
          value: {
            ...current.value,
            defaultModel: shouldClearModel ? "" : current.value.defaultModel,
            preferredLlmSelection: nextSelection,
          },
        };
      });
    },
    [selectionOptions, userLlmSettingsQuery.data],
  );

  const handleDefaultModelChange = React.useCallback((nextValue: unknown) => {
    setDraftState((current) => ({
      ...current,
      revision: current.revision + 1,
      value: {
        ...current.value,
        defaultModel: String(nextValue || ""),
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
          disabled={!draftDirty || saveMutation.isPending}
          icon={<ReloadOutlined />}
          onClick={handleReset}
        >
          {t("pages.settings.index.reset", "Reset")}</Button>
        <Button
          disabled={
            !draftDirty ||
            !canSaveLlmSettings ||
            !draft.preferredLlmSelection
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
                label={t("pages.settings.index.effective.route", "Effective route")}
                tone={routeFallbackActive ? "warning" : "success"}
                value={routeSummaryLabel}
              />
              <SummaryMetric
                label={t("pages.settings.index.default.model", "Default model")}
                tone={trimConversationValue(draft.defaultModel) ? "info" : "default"}
                value={trimConversationValue(draft.defaultModel) || "Not set"}
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

            {saveError ? (
              <Alert
                message={t("pages.settings.index.save.failed", "Save failed")}
                description={saveError}
                showIcon
                type="error"
              />
            ) : null}

            {pendingSave ? (
              <Alert
                message={t(
                  "pages.settings.index.save.accepted.awaiting.observation",
                  "Save accepted. Waiting for the exact service and model to be observed.",
                )}
                showIcon
                type="info"
              />
            ) : null}

            {catalogUnavailable ? (
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
                message={t("pages.settings.index.llm.catalog.is.unavailable", "LLM catalog is unavailable")}
                description={t("pages.settings.index.saved.route.and.model.are", "Saved route and model are shown from your stored settings. Route and model editing are temporarily disabled until the catalog responds.")}
                showIcon
                type="warning"
              />
            ) : null}

            {routeFallbackActive ? (
              <Alert
                message={t(
                  "pages.settings.index.effective.route.currently",
                  "Effective route is currently {route}.",
                  { route: routeSummaryLabel },
                )}
                description={
                  preferredSelectionAvailable
                    ? t(
                        "pages.settings.index.selected.service.available",
                        "The selected service is available and will be used for new requests.",
                      )
                    : t(
                        "pages.settings.index.selected.service.unavailable.fallback",
                        "{service} is unavailable right now, so new requests fall back to {route}.",
                        {
                          route: routeSummaryLabel,
                          service: preferredRouteLabel,
                        },
                      )
                }
                showIcon
                type={preferredSelectionAvailable ? "info" : "warning"}
              />
            ) : null}

            <div style={bodyGridStyle}>
              <div style={panelStackStyle}>
                <AevatarPanel
                  description={t("pages.settings.index.choose.the.route.and.model", "Choose the route and model used for new chats, Studio sessions, and global tools that do not set their own overrides.")}
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
                                : routeFallbackActive
                                  ? t(
                                      "pages.settings.index.fallback.active",
                                      "Fallback active",
                                    )
                                  : t(
                                      "pages.settings.index.in.sync",
                                      "In sync",
                                    )
                            }
                            tone={
                              pendingSave
                                ? "info"
                                : routeFallbackActive
                                  ? "warning"
                                  : "success"
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
                        {savedServiceIdentityMissing ? (
                          <Typography.Text type="warning">
                            {t(
                              "pages.settings.index.saved.service.identity.unavailable",
                              "Saved service identity unavailable. Choose an exact connected service before saving.",
                            )}
                          </Typography.Text>
                        ) : !preferredSelectionAvailable && draft.preferredLlmSelection ? (
                          <Typography.Text type="warning">
                            {t("pages.settings.index.saved.route.unavailable.new.requests", "Saved service unavailable. New requests will use")}{" "}
                            {routeSummaryLabel}.
                          </Typography.Text>
                        ) : (
                          <Typography.Text type="secondary">
                            {t("pages.settings.index.effective.now", "Effective now:")}{routeSummaryLabel || preferredRouteLabel}
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
                          {t("pages.settings.index.current.route.resolves.through", "Current route resolves through")}{" "}
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
                              modelOptions && modelOptions.length > 0
                                ? `${modelGroups.reduce(
                                    (count, group) => count + group.models.length,
                                    0,
                                  )} live`
                                : "Manual entry"
                            }
                            tone={modelOptions && modelOptions.length > 0 ? "info" : "default"}
                          />
                        </div>
                        {modelOptions && modelOptions.length > 0 ? (
                          <Select
                            aria-label={t("pages.settings.index.default.model.3", "Default model")}
                            allowClear
                            disabled={!canEditModel}
                            onChange={handleDefaultModelChange}
                            optionFilterProp="label"
                            options={modelOptions}
                            placeholder={defaultModelPlaceholder}
                            showSearch
                            value={trimConversationValue(draft.defaultModel)}
                          />
                        ) : (
                          <Input
                            aria-label={t("pages.settings.index.default.model.4", "Default model")}
                            disabled={!canEditModel}
                            onChange={(event) =>
                              handleDefaultModelChange(event.target.value)
                            }
                            placeholder={defaultModelPlaceholder}
                            value={draft.defaultModel}
                          />
                        )}
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
                      <SummaryField label={t("pages.settings.index.saved.route", "Saved route")} value={preferredRouteLabel} />
                      <SummaryField
                        label={t("pages.settings.index.effective.route.2", "Effective route")}
                        value={routeSummaryLabel}
                      />
                      <SummaryField label={t("pages.settings.index.runtime.mode", "Runtime mode")} value={runtimeModeLabel} />
                      <SummaryField
                        label={t("pages.settings.index.runtime.url", "Runtime URL")}
                        value={
                          <Tooltip
                            mouseEnterDelay={0.15}
                            placement="topLeft"
                            title={displayedRuntimeBaseUrl}
                          >
                            <Typography.Text style={previewValueStyle}>
                              {truncateMiddle(displayedRuntimeBaseUrl, 18, 14)}
                            </Typography.Text>
                          </Tooltip>
                        }
                      />
                    </div>
                    <p style={statusCopyStyle}>
                      {t("pages.settings.index.these.defaults.apply.when.creating", "These defaults apply when creating new chats, Studio sessions, and global tools that do not specify their own route or model.")}</p>
                    <p style={statusCopyStyle}>
                      {t("pages.settings.index.if.the.saved.route.becomes", "If the saved route becomes unavailable, requests automatically use the effective route shown above.")}</p>
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
                      {t("pages.settings.index.these.values.reflect.the.effective", "These values reflect the effective route, the current model draft, and the stored runtime defaults.")}</Typography.Text>
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
                          <Tooltip
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
                          </Tooltip>
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
      defaultModelPlaceholder,
      draft.defaultModel,
      draft.preferredLlmSelection,
      displayedRuntimeBaseUrl,
      canEditModel,
      canEditRoute,
      catalogUnavailable,
      llmLoadError,
      modelGroups,
      modelOptions,
      insetCardStyle,
      preferredSelectionAvailable,
      preferredSelectionValue,
      preferredRouteLabel,
      providerHealth.tone,
      providerHealth.value,
      providerDisplayList,
      readyProviderCount,
      routeFallbackActive,
      pendingSave,
      handleDefaultModelChange,
      handlePreferredServiceChange,
      isCatalogOptionSelected,
      savedServiceIdentityMissing,
      selectionSelectOptions,
      routeSummaryLabel,
      saveError,
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
