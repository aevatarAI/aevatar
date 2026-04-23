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
  USER_CONFIG_PROVIDER_SOURCE_GATEWAY,
  USER_CONFIG_PROVIDER_SOURCE_SERVICE,
  buildConversationRouteOptions,
  decodeConversationRouteSelectValue,
  describeConversationRoute,
  encodeConversationRouteSelectValue,
  formatConversationProviderLabel,
  normalizeUserLlmRoute,
  resolveReadyConversationRoute,
  routePathFromProviderSlug,
  trimConversationValue,
} from "@/pages/chat/chatConversationConfig";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { studioApi } from "@/shared/studio/api";
import type {
  StudioUserConfig,
  StudioUserConfigProviderStatus,
} from "@/shared/studio/models";
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
  readonly runtimeMode: "local" | "remote";
  readonly localRuntimeBaseUrl: string;
  readonly remoteRuntimeBaseUrl: string;
  readonly maxToolRounds: number | null;
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

type RouteScopedModelGroup = {
  readonly id: string;
  readonly label: string;
  readonly models: string[];
};

const llmTabKey = "llm";
const accountTabKey = "account";
const defaultRuntimeBaseUrl = "https://aevatar-console-backend-api.aevatar.ai";
const defaultRuntimeMode = "local";

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

function normalizeUserConfigDraft(config?: StudioUserConfig): SettingsDraft {
  const runtimeMode =
    trimConversationValue(config?.runtimeMode)?.toLowerCase() === "remote"
      ? "remote"
      : defaultRuntimeMode;

  return {
    defaultModel: trimConversationValue(config?.defaultModel) ?? "",
    preferredLlmRoute: normalizeUserLlmRoute(config?.preferredLlmRoute),
    runtimeMode,
    localRuntimeBaseUrl:
      trimConversationValue(config?.localRuntimeBaseUrl) ?? "",
    remoteRuntimeBaseUrl:
      trimConversationValue(config?.remoteRuntimeBaseUrl) ?? "",
    maxToolRounds: config?.maxToolRounds ?? null,
  };
}

function draftsEqual(left: SettingsDraft, right: SettingsDraft): boolean {
  return (
    trimConversationValue(left.defaultModel) === trimConversationValue(right.defaultModel) &&
    normalizeUserLlmRoute(left.preferredLlmRoute) ===
      normalizeUserLlmRoute(right.preferredLlmRoute) &&
    left.runtimeMode === right.runtimeMode &&
    trimConversationValue(left.localRuntimeBaseUrl) ===
      trimConversationValue(right.localRuntimeBaseUrl) &&
    trimConversationValue(left.remoteRuntimeBaseUrl) ===
      trimConversationValue(right.remoteRuntimeBaseUrl) &&
    left.maxToolRounds === right.maxToolRounds
  );
}

function resolveActiveRuntimeBaseUrl(draft: SettingsDraft): string {
  const configuredValue =
    draft.runtimeMode === "remote"
      ? trimConversationValue(draft.remoteRuntimeBaseUrl)
      : trimConversationValue(draft.localRuntimeBaseUrl);

  return configuredValue || defaultRuntimeBaseUrl;
}

function formatRuntimeModeLabel(runtimeMode: SettingsDraft["runtimeMode"]): string {
  return runtimeMode === "remote" ? "Remote" : "Local";
}

function isProviderReady(provider: StudioUserConfigProviderStatus): boolean {
  return provider.status.trim().toLowerCase() === "ready";
}

function resolveRouteScopedProviders(
  route: string,
  readyGatewayProvider: StudioUserConfigProviderStatus | null,
  readyServiceProviders: readonly StudioUserConfigProviderStatus[],
): StudioUserConfigProviderStatus[] {
  if (route === "") {
    return readyGatewayProvider ? [readyGatewayProvider] : [];
  }

  return readyServiceProviders.filter(
    (provider) => routePathFromProviderSlug(provider.providerSlug) === route,
  );
}

function buildRouteScopedModelGroups(input: {
  readonly modelsByProvider?: Record<string, string[]>;
  readonly routeProviders: readonly StudioUserConfigProviderStatus[];
  readonly supportedModels: readonly string[];
}): RouteScopedModelGroup[] {
  const explicitGroups = input.routeProviders
    .map((provider) => {
      const models = Array.from(
        new Set(
          (input.modelsByProvider?.[provider.providerSlug] ?? []).filter(Boolean),
        ),
      );

      return {
        id: provider.providerSlug || formatConversationProviderLabel(provider),
        label: formatConversationProviderLabel(provider),
        models,
      };
    })
    .filter((group) => group.models.length > 0);

  if (explicitGroups.length > 0) {
    return explicitGroups;
  }

  const fallbackModels = Array.from(new Set(input.supportedModels.filter(Boolean)));
  if (input.routeProviders.length === 0 || fallbackModels.length === 0) {
    return [];
  }

  return [
    {
      id: "__supported__",
      label:
        input.routeProviders.length === 1
          ? formatConversationProviderLabel(input.routeProviders[0])
          : "Supported models",
      models: fallbackModels,
    },
  ];
}

function isRouteAvailable(
  route: string,
  readyGatewayProvider: StudioUserConfigProviderStatus | null,
  readyServiceProviders: readonly StudioUserConfigProviderStatus[],
): boolean {
  if (route === "") {
    return Boolean(readyGatewayProvider);
  }

  return readyServiceProviders.some(
    (provider) => routePathFromProviderSlug(provider.providerSlug) === route,
  );
}

function formatProviderHealth(
  providers: readonly StudioUserConfigProviderStatus[],
): {
  readonly tone: "default" | "error" | "success" | "warning";
  readonly value: string;
} {
  const readyCount = providers.filter(isProviderReady).length;
  const unavailableCount = Math.max(0, providers.length - readyCount);

  if (readyCount === 0) {
    return {
      tone: "error",
      value: providers.length > 0 ? "No ready providers" : "No providers connected",
    };
  }

  if (unavailableCount > 0) {
    return {
      tone: "warning",
      value: `${readyCount} ready / ${unavailableCount} unavailable`,
    };
  }

  return {
    tone: "success",
    value: `${readyCount} providers ready`,
  };
}

function buildProviderSlugCountMap(
  providers: readonly StudioUserConfigProviderStatus[],
): Record<string, number> {
  const counts: Record<string, number> = {};

  for (const provider of providers) {
    const key = provider.providerSlug.trim();
    if (!key) {
      continue;
    }

    counts[key] = (counts[key] ?? 0) + 1;
  }

  return counts;
}

function formatProviderSourceLabel(
  source: string | undefined,
): string {
  return source === USER_CONFIG_PROVIDER_SOURCE_SERVICE
    ? "User service"
    : "Gateway provider";
}

function formatConnectedProviderLabel(
  provider: StudioUserConfigProviderStatus,
  duplicateSlugCount: number,
): string {
  const baseLabel = formatConversationProviderLabel(provider);
  if (duplicateSlugCount <= 1) {
    return baseLabel;
  }

  return provider.source === USER_CONFIG_PROVIDER_SOURCE_SERVICE
    ? `${baseLabel} Service`
    : `${baseLabel} Gateway`;
}

function isProviderActiveForRoute(
  provider: StudioUserConfigProviderStatus,
  route: string,
): boolean {
  if (route === "") {
    return (
      (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) !==
      USER_CONFIG_PROVIDER_SOURCE_SERVICE
    );
  }

  return routePathFromProviderSlug(provider.providerSlug) === route;
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
  readonly duplicateSlugCount: number;
  readonly provider: StudioUserConfigProviderStatus;
  readonly selected: boolean;
}> = ({ duplicateSlugCount, provider, selected }) => {
  const { token } = theme.useToken();
  const ready = isProviderReady(provider);
  const sourceLabel = formatProviderSourceLabel(provider.source);
  const label = formatConnectedProviderLabel(provider, duplicateSlugCount);
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
      title={`${label} · ${ready ? "Ready" : "Unavailable"} · ${sourceLabel}`}
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

  const userConfigQuery = useQuery({
    queryKey: ["settings", "user-config"],
    queryFn: () => studioApi.getUserConfig(),
  });
  const userConfigModelsQuery = useQuery({
    queryKey: ["settings", "user-config-models"],
    queryFn: () => studioApi.getUserConfigModels(),
  });

  const loadedDraft = React.useMemo(
    () => normalizeUserConfigDraft(userConfigQuery.data),
    [userConfigQuery.data],
  );
  const [draft, setDraft] = React.useState<SettingsDraft>(loadedDraft);
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const hydratedDraftRef = React.useRef(false);
  const draftDirty = React.useMemo(
    () => !draftsEqual(draft, loadedDraft),
    [draft, loadedDraft],
  );

  React.useEffect(() => {
    if (!userConfigQuery.isSuccess) {
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
  }, [draftDirty, loadedDraft, userConfigQuery.isSuccess]);

  const saveMutation = useMutation({
    mutationFn: async (nextDraft: SettingsDraft) =>
      studioApi.saveUserConfig({
        defaultModel: trimConversationValue(nextDraft.defaultModel) ?? "",
        preferredLlmRoute: normalizeUserLlmRoute(nextDraft.preferredLlmRoute),
        runtimeMode: nextDraft.runtimeMode,
        localRuntimeBaseUrl:
          trimConversationValue(nextDraft.localRuntimeBaseUrl) ?? "",
        remoteRuntimeBaseUrl:
          trimConversationValue(nextDraft.remoteRuntimeBaseUrl) ?? "",
        maxToolRounds: nextDraft.maxToolRounds,
      }),
    onSuccess: (savedConfig) => {
      const normalized = normalizeUserConfigDraft(savedConfig);
      queryClient.setQueryData(["settings", "user-config"], savedConfig);
      queryClient.setQueryData(["studio-user-config"], savedConfig);
      queryClient.setQueryData(["chat", "user-config"], savedConfig);
      setDraft(normalized);
      setSaveError(null);
    },
    onError: (error) => {
      setSaveError(describeError(error, "Failed to save settings."));
    },
  });

  const providers = userConfigModelsQuery.data?.providers ?? [];
  const readyProviders = React.useMemo(
    () => providers.filter(isProviderReady),
    [providers],
  );
  const readyGatewayProvider = React.useMemo(
    () =>
      readyProviders.find(
        (provider) =>
          (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) !==
          USER_CONFIG_PROVIDER_SOURCE_SERVICE,
      ) ?? null,
    [readyProviders],
  );
  const readyServiceProviders = React.useMemo(
    () =>
      readyProviders.filter(
        (provider) =>
          (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) ===
          USER_CONFIG_PROVIDER_SOURCE_SERVICE,
      ),
    [readyProviders],
  );
  const routeOptions = React.useMemo(
    () =>
      buildConversationRouteOptions(
        userConfigModelsQuery.data,
        loadedDraft.preferredLlmRoute,
        draft.preferredLlmRoute,
      ),
    [
      draft.preferredLlmRoute,
      loadedDraft.preferredLlmRoute,
      userConfigModelsQuery.data,
    ],
  );
  const preferredRouteAvailable = React.useMemo(
    () =>
      isRouteAvailable(
        draft.preferredLlmRoute,
        readyGatewayProvider,
        readyServiceProviders,
      ),
    [draft.preferredLlmRoute, readyGatewayProvider, readyServiceProviders],
  );
  const effectiveRoute = React.useMemo(
    () =>
      resolveReadyConversationRoute(
        draft.preferredLlmRoute,
        readyGatewayProvider,
        readyServiceProviders,
      ),
    [draft.preferredLlmRoute, readyGatewayProvider, readyServiceProviders],
  );
  const routeFallbackActive = effectiveRoute !== draft.preferredLlmRoute;
  const routeSummaryLabel = describeConversationRoute(effectiveRoute, routeOptions);
  const preferredRouteLabel = describeConversationRoute(
    draft.preferredLlmRoute,
    routeOptions,
  );
  const routeScopedProviders = React.useMemo(
    () =>
      resolveRouteScopedProviders(
        draft.preferredLlmRoute,
        readyGatewayProvider,
        readyServiceProviders,
      ),
    [draft.preferredLlmRoute, readyGatewayProvider, readyServiceProviders],
  );
  const modelGroups = React.useMemo(
    () =>
      buildRouteScopedModelGroups({
        modelsByProvider: userConfigModelsQuery.data?.modelsByProvider,
        routeProviders: routeScopedProviders,
        supportedModels: userConfigModelsQuery.data?.supportedModels ?? [],
      }),
    [routeScopedProviders, userConfigModelsQuery.data],
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
    () => resolveActiveRuntimeBaseUrl(draft),
    [draft],
  );
  const runtimeModeLabel = React.useMemo(
    () => formatRuntimeModeLabel(draft.runtimeMode),
    [draft.runtimeMode],
  );
  const providerHealth = React.useMemo(
    () => formatProviderHealth(providers),
    [providers],
  );
  const readyProviderCount = readyProviders.length;
  const unavailableProviderCount = Math.max(0, providers.length - readyProviderCount);
  const providerSlugCountMap = React.useMemo(
    () => buildProviderSlugCountMap(providers),
    [providers],
  );
  const providerDisplayList = React.useMemo(
    () =>
      [...providers].sort((left, right) => {
        const leftSelected = isProviderActiveForRoute(
          left,
          effectiveRoute,
        );
        const rightSelected = isProviderActiveForRoute(
          right,
          effectiveRoute,
        );
        if (leftSelected !== rightSelected) {
          return leftSelected ? -1 : 1;
        }

        const leftReady = isProviderReady(left);
        const rightReady = isProviderReady(right);
        if (leftReady !== rightReady) {
          return leftReady ? -1 : 1;
        }

        return formatConversationProviderLabel(left).localeCompare(
          formatConversationProviderLabel(right),
        );
      }),
    [effectiveRoute, providers],
  );
  const defaultModelPlaceholder = React.useMemo(() => {
    if (modelOptions && modelOptions.length > 0) {
      return `Search models for ${preferredRouteLabel || "the selected route"}`;
    }

    return preferredRouteLabel
      ? `Type a model ID for ${preferredRouteLabel}`
      : "Type a model ID";
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
    const gatewayLabel = readyGatewayProvider
      ? "NyxID Gateway"
      : "NyxID Gateway (fallback unavailable)";
    const serviceOptions = readyServiceProviders.map((provider) => ({
      label: formatConversationProviderLabel(provider),
      value: encodeConversationRouteSelectValue(
        routePathFromProviderSlug(provider.providerSlug),
      ),
    }));
    const staleGroup =
      draft.preferredLlmRoute && !preferredRouteAvailable
        ? [
            {
              label: "Current saved route",
              options: [
                {
                  label: `${preferredRouteLabel} (unavailable)`,
                  value: encodeConversationRouteSelectValue(
                    draft.preferredLlmRoute,
                  ),
                },
              ],
            },
          ]
        : [];

    return [
      {
        label: "Gateway",
        options: [
          {
            label: gatewayLabel,
            value: encodeConversationRouteSelectValue(""),
          },
        ],
      },
      ...(serviceOptions.length > 0
        ? [
            {
              label: "User services",
              options: serviceOptions,
            },
          ]
        : []),
      ...staleGroup,
    ];
  }, [
    draft.preferredLlmRoute,
    preferredRouteAvailable,
    preferredRouteLabel,
    readyGatewayProvider,
    readyServiceProviders,
  ]);

  const advancedItems = React.useMemo<CollapseProps["items"]>(
    () => [
      {
        key: "advanced-runtime",
        label: "Advanced runtime",
        children: (
          <div style={panelStackStyle}>
            <div style={formSectionStyle}>
              <div style={readOnlyFieldHeaderStyle}>
                <Typography.Text strong>Runtime base URL</Typography.Text>
                <FieldMetaPill label={runtimeModeLabel} tone="info" />
                <FieldMetaPill label="Read only" />
              </div>
              <Typography.Text type="secondary">
                Console default runtime endpoint resolved from the active mode.
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
                  fontFamily:
                    '"SFMono-Regular", "SF Mono", "JetBrains Mono", Consolas, monospace',
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
        value: draft.runtimeMode,
      },
    ],
    [displayedRuntimeBaseUrl, draft.defaultModel, draft.runtimeMode, effectiveRoute],
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
      const nextRouteProviders = resolveRouteScopedProviders(
        nextRoute,
        readyGatewayProvider,
        readyServiceProviders,
      );
      const nextRouteGroups = buildRouteScopedModelGroups({
        modelsByProvider: userConfigModelsQuery.data?.modelsByProvider,
        routeProviders: nextRouteProviders,
        supportedModels: userConfigModelsQuery.data?.supportedModels ?? [],
      });
      const currentModel = trimConversationValue(draft.defaultModel);
      const shouldClearModel =
        Boolean(currentModel) &&
        nextRouteGroups.length > 0 &&
        !nextRouteGroups.some((group) => group.models.includes(currentModel!));

      setDraft((currentDraft) => ({
        ...currentDraft,
        defaultModel: shouldClearModel ? "" : currentDraft.defaultModel,
        preferredLlmRoute: nextRoute,
      }));
    },
    [
      draft.defaultModel,
      readyGatewayProvider,
      readyServiceProviders,
      userConfigModelsQuery.data?.modelsByProvider,
      userConfigModelsQuery.data?.supportedModels,
    ],
  );

  const handleSectionChange = React.useCallback((nextKey: string) => {
    const nextSection: SettingsSection =
      nextKey === accountTabKey ? accountTabKey : llmTabKey;
    history.replace(buildSettingsHref(nextSection));
  }, []);

  const llmLoadError =
    userConfigQuery.isError || userConfigModelsQuery.isError
      ? describeError(
          userConfigQuery.error || userConfigModelsQuery.error,
          "Failed to load LLM defaults.",
        )
      : null;

  const headerExtra =
    activeSection === llmTabKey || draftDirty ? (
      <Space>
        <Button
          disabled={!draftDirty || saveMutation.isPending}
          icon={<ReloadOutlined />}
          onClick={handleReset}
        >
          Reset
        </Button>
        <Button
          disabled={!draftDirty}
          loading={saveMutation.isPending}
          onClick={handleSave}
          type="primary"
        >
          Save config
        </Button>
      </Space>
    ) : null;

  const llmSection = React.useMemo(
    () => (
      <div style={tabBodyStyle}>
            <div style={summaryGridStyle}>
              <SummaryMetric
                label="Effective route"
                tone={routeFallbackActive ? "warning" : "success"}
                value={routeSummaryLabel || "NyxID Gateway"}
              />
              <SummaryMetric
                label="Default model"
                tone={trimConversationValue(draft.defaultModel) ? "info" : "default"}
                value={trimConversationValue(draft.defaultModel) || "Not set"}
              />
              <SummaryMetric
                label="Provider health"
                tone={providerHealth.tone}
                value={providerHealth.value}
              />
            </div>

            {llmLoadError ? (
              <Alert
                message="Failed to load defaults"
                description={llmLoadError}
                showIcon
                type="error"
              />
            ) : null}

            {saveError ? (
              <Alert
                message="Save failed"
                description={saveError}
                showIcon
                type="error"
              />
            ) : null}

            {routeFallbackActive ? (
              <Alert
                message={`Effective route is currently ${routeSummaryLabel}.`}
                description={
                  preferredRouteAvailable
                    ? "The selected route is available and will be used for new requests."
                    : `${preferredRouteLabel} is unavailable right now, so new requests fall back to ${routeSummaryLabel}.`
                }
                showIcon
                type={preferredRouteAvailable ? "info" : "warning"}
              />
            ) : null}

            <div style={bodyGridStyle}>
              <div style={panelStackStyle}>
                <AevatarPanel
                  description="Choose the route and model used for new chats, Studio sessions, and global tools that do not set their own overrides."
                  style={settingsPanelStyle}
                  title="Edit defaults"
                >
                  {userConfigQuery.isLoading || userConfigModelsQuery.isLoading ? (
                    <div style={{ padding: 20 }}>
                      <Typography.Text type="secondary">
                        Loading your current defaults...
                      </Typography.Text>
                    </div>
                  ) : (
                    <div style={{ ...panelStackStyle, padding: 20 }}>
                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>Preferred route</Typography.Text>
                          <FieldMetaPill
                            label={
                              routeFallbackActive ? "Fallback active" : "In sync"
                            }
                            tone={routeFallbackActive ? "warning" : "success"}
                          />
                        </div>
                        <Typography.Text type="secondary">
                          Choose the primary route used for requests.
                        </Typography.Text>
                        <Select
                          aria-label="Preferred route"
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
                            Saved route unavailable. New requests will use{" "}
                            {routeSummaryLabel}.
                          </Typography.Text>
                        ) : (
                          <Typography.Text type="secondary">
                            Effective now: {routeSummaryLabel || preferredRouteLabel}
                          </Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>Connected providers</Typography.Text>
                          <Space size={6} wrap>
                            <FieldMetaPill
                              label={`${readyProviderCount} ready`}
                              tone={readyProviderCount > 0 ? "success" : "default"}
                            />
                            {unavailableProviderCount > 0 ? (
                              <FieldMetaPill
                                label={`${unavailableProviderCount} unavailable`}
                                tone="warning"
                              />
                            ) : null}
                          </Space>
                        </div>
                        <Typography.Text type="secondary">
                          Current route resolves through{" "}
                          {routeSummaryLabel || "NyxID Gateway"}.
                        </Typography.Text>
                        {providerDisplayList.length > 0 ? (
                          <div style={providerRailStyle}>
                            {providerDisplayList.map((provider) => (
                              <ConnectedProviderChip
                                duplicateSlugCount={
                                  providerSlugCountMap[provider.providerSlug] ?? 1
                                }
                                key={`${
                                  provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY
                                }-${provider.providerSlug}`}
                                provider={provider}
                                selected={isProviderActiveForRoute(
                                  provider,
                                  effectiveRoute,
                                )}
                              />
                            ))}
                          </div>
                        ) : (
                          <Typography.Text type="secondary">
                            No connected providers discovered yet.
                          </Typography.Text>
                        )}
                      </div>

                      <div style={{ ...insetCardStyle, ...fieldCardStyle }}>
                        <div style={fieldHeaderRowStyle}>
                          <Typography.Text strong>Default model</Typography.Text>
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
                            aria-label="Default model"
                            allowClear
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
                            aria-label="Default model"
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
                <AevatarPanel style={settingsPanelStyle} title="How defaults work">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(1, minmax(0, 1fr))",
                      }}
                    >
                      <SummaryField label="Saved route" value={preferredRouteLabel} />
                      <SummaryField
                        label="Effective route"
                        value={routeSummaryLabel || "NyxID Gateway"}
                      />
                      <SummaryField label="Runtime mode" value={runtimeModeLabel} />
                      <SummaryField
                        label="Runtime URL"
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
                      These defaults apply when creating new chats, Studio
                      sessions, and global tools that do not specify their own
                      route or model.
                    </p>
                    <p style={statusCopyStyle}>
                      If the saved route becomes unavailable, requests
                      automatically use the effective route shown above.
                    </p>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title="Apply scope">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      These defaults currently apply to:
                    </Typography.Text>
                    <Space size={[10, 10]} wrap>
                      <ScopeChip icon={<CommentOutlined />} label="Chat" />
                      <ScopeChip icon={<ExperimentOutlined />} label="Studio" />
                      <ScopeChip icon={<ToolOutlined />} label="Global tools" />
                    </Space>
                  </div>
                </AevatarPanel>

                <AevatarPanel style={settingsPanelStyle} title="Technical preview">
                  <div style={{ ...panelStackStyle, padding: 20 }}>
                    <Typography.Text type="secondary">
                      These values reflect the effective route and current draft.
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
      draft.maxToolRounds,
      draft.localRuntimeBaseUrl,
      draft.preferredLlmRoute,
      draft.remoteRuntimeBaseUrl,
      draft.runtimeMode,
      draftDirty,
      displayedRuntimeBaseUrl,
      llmLoadError,
      modelGroups,
      modelOptions,
      insetCardStyle,
      preferredRouteAvailable,
      preferredRouteLabel,
      providerHealth.tone,
      providerHealth.value,
      providerDisplayList,
      providerSlugCountMap,
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
      userConfigModelsQuery.isLoading,
      userConfigQuery.isLoading,
      runtimeModeLabel,
    ],
  );

  const accountSection = React.useMemo(
    () => (
      <div style={tabBodyStyle}>
        {draftDirty ? (
          <Alert
            message="LLM changes are still pending save."
            description="You can return to the LLM tab and save whenever you are ready."
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
          ? "Personal defaults for Chat and Studio."
          : "Identity, session, and access details for this browser."
      }
      extra={headerExtra}
      title="Settings"
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div role="tablist" style={buildSettingsSwitchRailStyle(token)}>
          {[
            { key: llmTabKey, label: "LLM" },
            { key: accountTabKey, label: "Account" },
          ].map((option) => {
            const active = activeSection === option.key;
            return (
              <button
                key={option.key}
                aria-selected={active}
                onClick={() => handleSectionChange(option.key)}
                role="tab"
                style={buildSettingsSwitchButtonStyle(token, active)}
                type="button"
              >
                {option.label}
              </button>
            );
          })}
        </div>
        {activeSection === llmTabKey ? llmSection : accountSection}
      </div>
    </SettingsPageShell>
  );
};

export default SettingsPage;
