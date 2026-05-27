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
  buildConversationRouteOptions,
  decodeConversationRouteSelectValue,
  describeConversationRoute,
  encodeConversationRouteSelectValue,
  findConversationRouteOption,
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
  buildSettingsInsetCardStyle,
  buildSettingsPanelStyle,
  buildSettingsSwitchButtonStyle,
  buildSettingsSwitchRailStyle,
  SettingsPageShell,
  SummaryField,
  SummaryMetric,
} from "./shared";

type SettingsSection = "llm" | "account";

type SettingsDraft = {
  readonly defaultModel: string;
  readonly preferredLlmRoute: string;
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
    preferredLlmRoute: normalizeUserLlmRoute(config?.savedRoute),
  };
}

function draftsEqual(left: SettingsDraft, right: SettingsDraft): boolean {
  return (
    trimConversationValue(left.defaultModel) === trimConversationValue(right.defaultModel) &&
    normalizeUserLlmRoute(left.preferredLlmRoute) ===
      normalizeUserLlmRoute(right.preferredLlmRoute)
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
      value: options.length > 0 ? "没有可用路由" : "尚未连接路由",
    };
  }

  if (unavailableCount > 0) {
    return {
      tone: "warning",
      value: `${readyCount} 可用 / ${unavailableCount} 不可用`,
    };
  }

  return {
    tone: "success",
    value: `${readyCount} 条路由可用`,
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
  const sourceLabel = option.source === "user_service" ? "用户服务" : "网关 Provider";
  const label = option.label;
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
      title={`${label} · ${ready ? "可用" : "不可用"} · ${sourceLabel}`}
    >
      <div
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
  const [draft, setDraft] = React.useState<SettingsDraft>(loadedDraft);
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const hydratedDraftRef = React.useRef(false);
  const draftDirty = React.useMemo(
    () => !draftsEqual(draft, loadedDraft),
    [draft, loadedDraft],
  );

  React.useEffect(() => {
    if (!userLlmSettingsQuery.isSuccess) {
      return;
    }

    if (!hydratedDraftRef.current) {
      hydratedDraftRef.current = true;
      setDraft(loadedDraft);
      return;
    }

    if (!draftDirty) {
      setDraft(loadedDraft);
    }
  }, [draftDirty, loadedDraft, userLlmSettingsQuery.isSuccess]);

  const saveMutation = useMutation({
    mutationFn: async (nextDraft: SettingsDraft) =>
      studioApi.saveUserLlmSettings({
        routeValue: normalizeUserLlmRoute(nextDraft.preferredLlmRoute),
        model: trimConversationValue(nextDraft.defaultModel) ?? "",
      }),
    onSuccess: async () => {
      setSaveError(null);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["settings", "user-llm-settings"] }),
        queryClient.invalidateQueries({ queryKey: ["studio-user-llm-settings"] }),
        queryClient.invalidateQueries({ queryKey: ["chat", "user-llm-settings"] }),
      ]);
    },
    onError: (error) => {
      setSaveError(describeError(error, "保存设置失败。"));
    },
  });

  const routeCatalogOptions = userLlmSettingsQuery.data?.routeOptions ?? [];
  const routeOptions = React.useMemo(
    () =>
      buildConversationRouteOptions(
        userLlmSettingsQuery.data,
        loadedDraft.preferredLlmRoute,
        draft.preferredLlmRoute,
      ),
    [
      draft.preferredLlmRoute,
      loadedDraft.preferredLlmRoute,
      userLlmSettingsQuery.data,
    ],
  );
  const savedRouteOption = React.useMemo(
    () => findConversationRouteOption(userLlmSettingsQuery.data, draft.preferredLlmRoute),
    [draft.preferredLlmRoute, userLlmSettingsQuery.data],
  );
  const preferredRouteAvailable = Boolean(savedRouteOption?.ready && savedRouteOption.allowed);
  const effectiveRoute = React.useMemo(
    () =>
      draftsEqual(draft, loadedDraft)
        ? normalizeUserLlmRoute(userLlmSettingsQuery.data?.effectiveRoute)
        : draft.preferredLlmRoute,
    [draft, loadedDraft, userLlmSettingsQuery.data?.effectiveRoute],
  );
  const routeFallbackActive = draftsEqual(draft, loadedDraft)
    ? Boolean(userLlmSettingsQuery.data?.routeFallbackActive)
    : false;
  const routeSummaryLabel = describeConversationRoute(effectiveRoute, routeOptions);
  const preferredRouteLabel = describeConversationRoute(
    draft.preferredLlmRoute,
    routeOptions,
  );
  const modelGroups = React.useMemo(
    () =>
      buildConversationModelGroups({
        conversationModel: draft.defaultModel,
        effectiveRoute: draft.preferredLlmRoute,
        settings: userLlmSettingsQuery.data,
      }),
    [draft.defaultModel, draft.preferredLlmRoute, userLlmSettingsQuery.data],
  );
  const liveModelGroups = React.useMemo(
    () => modelGroups.filter((group) => group.id !== "__current__"),
    [modelGroups],
  );
  const modelOptions = React.useMemo<SelectProps["options"]>(
    () =>
      (liveModelGroups.length > 0 ? modelGroups : []).map((group) => ({
        label: group.label,
        options: group.models.map((model) => ({
          label: model,
          value: model,
        })),
      })),
    [liveModelGroups.length, modelGroups],
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
  const providerDisplayList = React.useMemo(
    () =>
      [...routeCatalogOptions].sort((left, right) => {
        const leftSelected = normalizeUserLlmRoute(left.routeValue) === effectiveRoute;
        const rightSelected = normalizeUserLlmRoute(right.routeValue) === effectiveRoute;
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
    [effectiveRoute, routeCatalogOptions],
  );
  const llmCapabilities = userLlmSettingsQuery.data?.capabilities;
  const canEditRoute = Boolean(llmCapabilities?.canEditRoute);
  const canEditModel = Boolean(llmCapabilities?.canEditModel);
  const canSaveLlmSettings = Boolean(llmCapabilities?.canSave);
  const catalogUnavailable = userLlmSettingsQuery.data?.catalogStatus === "unavailable";
  const defaultModelPlaceholder = React.useMemo(() => {
    if (modelOptions && modelOptions.length > 0) {
      return `搜索 ${preferredRouteLabel || "所选路由"} 的模型`;
    }

    return preferredRouteLabel
      ? `输入 ${preferredRouteLabel} 的模型 ID`
      : "输入模型 ID";
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

  const routeSelectOptions = React.useMemo<SelectProps["options"]>(() => {
    const readyOptions = routeCatalogOptions
      .filter((option) => option.ready && option.allowed)
      .map((option) => ({
        label: option.label,
        value: encodeConversationRouteSelectValue(normalizeUserLlmRoute(option.routeValue)),
      }));
    const hasDraftRoute = readyOptions.some(
      (option) =>
        decodeConversationRouteSelectValue(String(option.value)) === draft.preferredLlmRoute,
    );

    return [
      {
        label: "Routes",
        options: readyOptions,
      },
      ...(draft.preferredLlmRoute && !hasDraftRoute
        ? [
            {
              label: "Current saved route",
              options: [
                {
                  label: `${preferredRouteLabel}（不可用）`,
                  value: encodeConversationRouteSelectValue(draft.preferredLlmRoute),
                },
              ],
            },
          ]
        : []),
    ];
  }, [draft.preferredLlmRoute, preferredRouteLabel, routeCatalogOptions]);

  const advancedItems = React.useMemo<CollapseProps["items"]>(
    () => [
      {
        key: "advanced-runtime",
        label: "高级运行时",
        children: (
          <div style={panelStackStyle}>
            <div style={formSectionStyle}>
              <div style={readOnlyFieldHeaderStyle}>
                <Typography.Text strong>Runtime base URL</Typography.Text>
                <FieldMetaPill label={runtimeModeLabel} tone="info" />
                <FieldMetaPill label="只读" />
              </div>
              <Typography.Text type="secondary">
                控制台默认运行时端点由当前模式解析。
              </Typography.Text>
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
    saveMutation.mutate(draft);
  }, [draft, saveMutation]);

  const handleReset = React.useCallback(() => {
    setDraft(loadedDraft);
    setSaveError(null);
  }, [loadedDraft]);

  const handlePreferredRouteChange = React.useCallback(
    (nextValue: string) => {
      const nextRoute = normalizeUserLlmRoute(
        decodeConversationRouteSelectValue(nextValue),
      );
      const nextRouteGroups = buildConversationModelGroups({
        conversationModel: draft.defaultModel,
        effectiveRoute: nextRoute,
        settings: userLlmSettingsQuery.data,
      });
      const currentModel = trimConversationValue(draft.defaultModel);
      const nextLiveRouteGroups = nextRouteGroups.filter(
        (group) => group.id !== "__current__",
      );
      const shouldClearModel =
        Boolean(currentModel) &&
        nextLiveRouteGroups.length > 0 &&
        !nextLiveRouteGroups.some((group) => group.models.includes(currentModel!));

      setDraft((currentDraft) => ({
        ...currentDraft,
        defaultModel: shouldClearModel ? "" : currentDraft.defaultModel,
        preferredLlmRoute: nextRoute,
      }));
    },
    [
      draft.defaultModel,
      userLlmSettingsQuery.data,
    ],
  );

  const handleSectionChange = React.useCallback((nextKey: string) => {
    const nextSection: SettingsSection =
      nextKey === accountTabKey ? accountTabKey : llmTabKey;
    history.replace(buildSettingsHref(nextSection));
  }, []);

  const llmLoadError =
    userLlmSettingsQuery.isError || userRuntimeQuery.isError
      ? describeError(
          userLlmSettingsQuery.error || userRuntimeQuery.error,
          "加载 LLM 默认配置失败。",
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
          重置
        </Button>
        <Button
          disabled={!draftDirty || !canSaveLlmSettings}
          loading={saveMutation.isPending}
          onClick={handleSave}
          type="primary"
        >
          保存配置
        </Button>
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
                label="当前生效路由"
                tone={routeFallbackActive ? "warning" : "success"}
                value={routeSummaryLabel || "NyxID Gateway"}
              />
              <SummaryMetric
                label="默认模型"
                tone={trimConversationValue(draft.defaultModel) ? "info" : "default"}
                value={trimConversationValue(draft.defaultModel) || "未设置"}
              />
              <SummaryMetric
                label="Provider 状态"
                tone={providerHealth.tone}
                value={providerHealth.value}
              />
            </div>

            {llmLoadError ? (
              <Alert
                message="默认配置加载失败"
                description={llmLoadError}
                showIcon
                type="error"
              />
            ) : null}

            {saveError ? (
              <Alert
                message="保存失败"
                description={saveError}
                showIcon
                type="error"
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
                      重试
                    </Button>
                  ) : undefined
                }
                message="LLM 目录暂不可用"
                description="已保存的路由和模型会继续显示。目录恢复响应前，路由和模型编辑会暂时禁用。"
                showIcon
                type="warning"
              />
            ) : null}

            {routeFallbackActive ? (
              <Alert
                message={`当前生效路由为 ${routeSummaryLabel}。`}
                description={
                  preferredRouteAvailable
                    ? "所选路由可用，新请求会继续使用它。"
                    : `${preferredRouteLabel} 当前不可用，新请求会回退到 ${routeSummaryLabel}。`
                }
                showIcon
                type={preferredRouteAvailable ? "info" : "warning"}
              />
            ) : null}

            <div style={bodyGridStyle}>
              <div style={panelStackStyle}>
                <AevatarPanel
                  description="选择新建 Chat、Studio 会话和未指定覆盖项的全局工具默认使用的路由与模型。"
                  style={settingsPanelStyle}
                  title="编辑默认配置"
                >
                  {userLlmSettingsQuery.isLoading || userRuntimeQuery.isLoading ? (
                    <div style={{ padding: 20 }}>
                      <Typography.Text type="secondary">
                        正在加载当前默认配置...
                      </Typography.Text>
                    </div>
                  ) : (
                    <div style={{ ...panelStackStyle, padding: 20 }}>
                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>首选路由</Typography.Text>
                          <FieldMetaPill
                            label={
                              routeFallbackActive ? "正在回退" : "已同步"
                            }
                            tone={routeFallbackActive ? "warning" : "success"}
                          />
                        </div>
                        <Typography.Text type="secondary">
                          选择请求默认使用的主路由。
                        </Typography.Text>
                        <Select
                          aria-label="首选路由"
                          disabled={!canEditRoute}
                          onChange={handlePreferredRouteChange}
                          optionFilterProp="label"
                          options={routeSelectOptions}
                          showSearch
                          value={encodeConversationRouteSelectValue(
                            draft.preferredLlmRoute,
                          )}
                        />
                        {!preferredRouteAvailable && draft.preferredLlmRoute ? (
                          <Typography.Text type="warning">
                            已保存路由不可用。新请求将使用{" "}
                            {routeSummaryLabel}.
                          </Typography.Text>
                        ) : (
                          <Typography.Text type="secondary">
                            当前生效：{routeSummaryLabel || preferredRouteLabel}
                          </Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>已连接 Provider</Typography.Text>
                          <Space size={6} wrap>
                            <FieldMetaPill
                              label={`${readyProviderCount} 可用`}
                              tone={readyProviderCount > 0 ? "success" : "default"}
                            />
                            {unavailableProviderCount > 0 ? (
                              <FieldMetaPill
                                label={`${unavailableProviderCount} 不可用`}
                                tone="warning"
                              />
                            ) : null}
                          </Space>
                        </div>
                        <Typography.Text type="secondary">
                          当前路由解析到{" "}
                          {routeSummaryLabel || "NyxID Gateway"}.
                        </Typography.Text>
                        {providerDisplayList.length > 0 ? (
                          <div style={providerRailStyle}>
                            {providerDisplayList.map((option) => (
                              <ConnectedProviderChip
                                key={`${option.source}-${option.routeValue}`}
                                option={option}
                                selected={normalizeUserLlmRoute(option.routeValue) === effectiveRoute}
                              />
                            ))}
                          </div>
                        ) : (
                          <Typography.Text type="secondary">
                            尚未发现已连接 Provider。
                          </Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>默认模型</Typography.Text>
                          <FieldMetaPill
                            label={
                              modelOptions && modelOptions.length > 0
                                ? `${liveModelGroups.reduce(
                                    (count, group) => count + group.models.length,
                                    0,
                                  )} 个可用`
                                : "手动输入"
                            }
                            tone={modelOptions && modelOptions.length > 0 ? "info" : "default"}
                          />
                        </div>
                        {modelOptions && modelOptions.length > 0 ? (
                          <Select
                            aria-label="默认模型"
                            allowClear
                            disabled={!canEditModel}
                            onChange={(nextValue) =>
                              setDraft((currentDraft) => ({
                                ...currentDraft,
                                defaultModel: String(nextValue || ""),
                              }))
                            }
                            optionFilterProp="label"
                            options={modelOptions}
                            placeholder={defaultModelPlaceholder}
                            showSearch
                            value={trimConversationValue(draft.defaultModel)}
                          />
                        ) : (
                          <Input
                            aria-label="默认模型"
                            disabled={!canEditModel}
                            onChange={(event) =>
                              setDraft((currentDraft) => ({
                                ...currentDraft,
                                defaultModel: event.target.value,
                              }))
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
                <AevatarPanel style={settingsPanelStyle} title="默认配置如何生效">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(1, minmax(0, 1fr))",
                      }}
                    >
                      <SummaryField label="已保存路由" value={preferredRouteLabel} />
                      <SummaryField
                        label="当前生效路由"
                        value={routeSummaryLabel || "NyxID Gateway"}
                      />
                      <SummaryField label="运行模式" value={runtimeModeLabel} />
                      <SummaryField
                        label="运行时 URL"
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
                      这些默认配置会用于新建 Chat、Studio 会话，以及未指定
                      专属路由或模型的全局工具。
                    </p>
                    <p style={statusCopyStyle}>
                      如果已保存路由不可用，请求会自动使用上方显示的当前生效路由。
                    </p>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title="适用范围">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      这些默认配置当前适用于：
                    </Typography.Text>
                    <Space size={[10, 10]} wrap>
                      <ScopeChip icon={<CommentOutlined />} label="Chat" />
                      <ScopeChip icon={<ExperimentOutlined />} label="Studio" />
                      <ScopeChip icon={<ToolOutlined />} label="全局工具" />
                    </Space>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title="技术预览">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      这些值反映当前生效路由、当前模型草稿和已保存的运行时默认配置。
                    </Typography.Text>
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
      displayedRuntimeBaseUrl,
      canEditModel,
      canEditRoute,
      catalogUnavailable,
      llmLoadError,
      liveModelGroups,
      modelOptions,
      insetCardStyle,
      preferredRouteAvailable,
      preferredRouteLabel,
      providerHealth.tone,
      providerHealth.value,
      providerDisplayList,
      readyProviderCount,
      routeFallbackActive,
      handlePreferredRouteChange,
      routeSelectOptions,
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
            message="LLM 变更仍未保存"
            description="你可以回到 LLM 页签，在准备好后保存。"
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
          ? "Chat 与 Studio 的个人默认配置。"
          : "此浏览器的身份、会话与访问详情。"
      }
      extra={headerExtra}
      title="设置"
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
