import {
  ApiOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  CodeOutlined,
  GlobalOutlined,
  SafetyCertificateOutlined,
  WarningOutlined,
} from "@ant-design/icons";
import { Alert, Grid, Space, Tag, Tooltip, Typography, theme } from "antd";
import React from "react";
import packageJson from "../../../package.json";
import { getNyxIDRuntimeConfig } from "@/shared/auth/config";
import {
  hasActiveAccessToken,
  readStoredAuthSession,
} from "@/shared/auth/session";
import { getOrnnRuntimeConfig } from "@/shared/studio/ornnConfig";
import {
  aevatarMonoFontFamily,
  AevatarCompactText,
  truncateMiddle,
} from "@/shared/ui/compactText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from "@/shared/ui/proComponents";
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from "./shared";

type DiagnosticsSettingsContentProps = {
  readonly runtimeBaseUrl: string;
  readonly runtimeConfigError?: string | null;
  readonly runtimeConfigLoading?: boolean;
  readonly runtimeModeLabel: string;
};

type DiagnosticsTone = "default" | "error" | "info" | "success" | "warning";

type SessionStatus = {
  readonly detail: string;
  readonly label: string;
  readonly tone: DiagnosticsTone;
};

const diagnosticsVersion = packageJson.version || "unknown";

const diagnosticsStackStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 16,
  minHeight: 0,
};

const diagnosticsPanelBodyStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 14,
  padding: 20,
};

const runtimeValueStyle: React.CSSProperties = {
  display: "inline-block",
  fontFamily: aevatarMonoFontFamily,
  maxWidth: "100%",
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

function trimRuntimeValue(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized || undefined;
}

export function resolveConsoleApiBaseUrl(): string {
  if (typeof window === "undefined") {
    return "/api";
  }

  try {
    return new URL("/api", window.location.origin).toString().replace(/\/+$/, "");
  } catch {
    return "/api";
  }
}

export function resolveConsoleEnvironment(): string {
  const processEnv =
    typeof process === "undefined" ? undefined : process.env;
  return (
    trimRuntimeValue(processEnv?.UMI_ENV) ||
    trimRuntimeValue(processEnv?.NODE_ENV) ||
    "unknown"
  );
}

function resolvePublicPath(): string {
  return trimRuntimeValue(
    typeof process === "undefined"
      ? undefined
      : process.env.AEVATAR_CONSOLE_PUBLIC_PATH,
  ) || "/";
}

function resolveBrowserOrigin(): string {
  if (typeof window === "undefined") {
    return "Unavailable";
  }

  return window.location.origin || "Unavailable";
}

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return "Unavailable";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value);
}

function resolveSessionStatus(): SessionStatus {
  const session = readStoredAuthSession();

  if (!session) {
    return {
      detail: "No browser session is stored for this console.",
      label: "Missing",
      tone: "warning",
    };
  }

  if (hasActiveAccessToken(session.tokens)) {
    return {
      detail: "An active access token is available in this browser.",
      label: "Active",
      tone: "success",
    };
  }

  if (session.tokens.refreshToken) {
    return {
      detail: "The access token expired, but the session can be refreshed.",
      label: "Refreshable",
      tone: "info",
    };
  }

  return {
    detail: "Stored session tokens are expired and cannot be refreshed.",
    label: "Expired",
    tone: "error",
  };
}

function renderCompactRuntimeValue(value: string): React.ReactNode {
  return (
    <Tooltip
      mouseEnterDelay={0.15}
      placement="topLeft"
      title={
        <span style={{ fontFamily: aevatarMonoFontFamily, overflowWrap: "anywhere" }}>
          {value}
        </span>
      }
    >
      <Typography.Text style={runtimeValueStyle}>
        {truncateMiddle(value, 20, 16)}
      </Typography.Text>
    </Tooltip>
  );
}

const DiagnosticsSettingsContent: React.FC<DiagnosticsSettingsContentProps> = ({
  runtimeBaseUrl,
  runtimeConfigError,
  runtimeConfigLoading = false,
  runtimeModeLabel,
}) => {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const settingsPanelStyle = React.useMemo(
    () => buildSettingsPanelStyle(token),
    [token],
  );
  const apiBaseUrl = React.useMemo(() => resolveConsoleApiBaseUrl(), []);
  const browserOrigin = React.useMemo(() => resolveBrowserOrigin(), []);
  const consoleEnvironment = React.useMemo(
    () => resolveConsoleEnvironment(),
    [],
  );
  const publicPath = React.useMemo(() => resolvePublicPath(), []);
  const nyxIdConfig = React.useMemo(() => getNyxIDRuntimeConfig(), []);
  const ornnConfig = React.useMemo(() => getOrnnRuntimeConfig(), []);
  const sessionStatus = React.useMemo(() => resolveSessionStatus(), []);
  const storedSession = React.useMemo(() => readStoredAuthSession(), []);
  const diagnosticsGridStyle = React.useMemo<React.CSSProperties>(
    () => ({
      ...summaryMetricGridStyle,
      gridTemplateColumns: screens.lg
        ? "repeat(4, minmax(0, 1fr))"
        : screens.md
          ? "repeat(2, minmax(0, 1fr))"
          : "repeat(1, minmax(0, 1fr))",
    }),
    [screens.lg, screens.md],
  );
  const authConfigTone: DiagnosticsTone = nyxIdConfig.configurationError
    ? "error"
    : nyxIdConfig.enabled
      ? "success"
      : "warning";

  return (
    <div style={diagnosticsStackStyle}>
      {nyxIdConfig.configurationError ? (
        <Alert
          description={nyxIdConfig.configurationError}
          message="Authentication runtime configuration needs attention"
          showIcon
          type="error"
        />
      ) : null}

      {runtimeConfigError ? (
        <Alert
          description={runtimeConfigError}
          message="Runtime configuration could not be loaded"
          showIcon
          type="warning"
        />
      ) : null}

      <div style={diagnosticsGridStyle}>
        <SummaryMetric
          label="API base URL"
          tone="info"
          value={renderCompactRuntimeValue(apiBaseUrl)}
        />
        <SummaryMetric
          label="Environment"
          tone={consoleEnvironment === "production" ? "success" : "info"}
          value={consoleEnvironment}
        />
        <SummaryMetric
          label="Frontend version"
          tone="default"
          value={diagnosticsVersion}
        />
        <SummaryMetric
          label="Session"
          tone={sessionStatus.tone}
          value={sessionStatus.label}
        />
      </div>

      <AevatarPanel
        description="Browser-facing endpoints and build values used by the console shell."
        style={settingsPanelStyle}
        title="Runtime checks"
      >
        <div style={diagnosticsPanelBodyStyle}>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label="API base URL"
              value={
                <AevatarCompactText
                  copyable
                  head={24}
                  maxWidth="100%"
                  monospace
                  tail={20}
                  value={apiBaseUrl}
                />
              }
            />
            <SummaryField label="Current environment" value={consoleEnvironment} />
            <SummaryField label="Frontend version" value={diagnosticsVersion} />
            <SummaryField label="Public path" value={publicPath} />
            <SummaryField
              label="Browser origin"
              value={
                <AevatarCompactText
                  copyable
                  head={24}
                  maxWidth="100%"
                  monospace
                  tail={18}
                  value={browserOrigin}
                />
              }
            />
            <SummaryField
              label="Runtime mode"
              value={runtimeConfigLoading ? "Loading" : runtimeModeLabel}
            />
            <SummaryField
              label="Runtime API URL"
              value={
                <AevatarCompactText
                  copyable
                  head={24}
                  maxWidth="100%"
                  monospace
                  tail={20}
                  value={runtimeConfigLoading ? "Loading" : runtimeBaseUrl}
                />
              }
            />
            <SummaryField
              label="Ornn base URL"
              value={
                <AevatarCompactText
                  copyable
                  head={24}
                  maxWidth="100%"
                  monospace
                  tail={20}
                  value={ornnConfig.baseUrl || "Unavailable"}
                />
              }
            />
          </div>

          {ornnConfig.configurationError ? (
            <Alert
              description={ornnConfig.configurationError}
              message="Ornn runtime configuration needs attention"
              showIcon
              type="warning"
            />
          ) : null}
        </div>
      </AevatarPanel>

      <AevatarPanel
        description="Authentication provider configuration and the session currently stored in this browser."
        style={settingsPanelStyle}
        title="Auth and session"
      >
        <div style={diagnosticsPanelBodyStyle}>
          <Space size={[8, 8]} wrap>
            <Tag
              color={
                authConfigTone === "success"
                  ? "success"
                  : authConfigTone === "error"
                    ? "error"
                    : "warning"
              }
              icon={
                authConfigTone === "success" ? (
                  <SafetyCertificateOutlined />
                ) : (
                  <WarningOutlined />
                )
              }
            >
              Auth {nyxIdConfig.enabled ? "enabled" : "disabled"}
            </Tag>
            <Tag
              color={
                sessionStatus.tone === "success"
                  ? "success"
                  : sessionStatus.tone === "error"
                    ? "error"
                    : sessionStatus.tone === "info"
                      ? "processing"
                      : "warning"
              }
              icon={
                sessionStatus.tone === "success" ? (
                  <CheckCircleOutlined />
                ) : sessionStatus.tone === "info" ? (
                  <ClockCircleOutlined />
                ) : (
                  <WarningOutlined />
                )
              }
            >
              Session {sessionStatus.label.toLowerCase()}
            </Tag>
          </Space>

          <Typography.Paragraph
            style={{
              color: token.colorTextSecondary,
              margin: 0,
            }}
          >
            {sessionStatus.detail}
          </Typography.Paragraph>

          <div style={summaryFieldGridStyle}>
            <SummaryField
              label="NyxID base URL"
              value={
                <AevatarCompactText
                  copyable
                  head={24}
                  maxWidth="100%"
                  monospace
                  tail={20}
                  value={nyxIdConfig.baseUrl || "Unavailable"}
                />
              }
            />
            <SummaryField
              label="Client ID"
              value={
                <AevatarCompactText
                  copyable
                  head={12}
                  maxWidth="100%"
                  monospace
                  tail={8}
                  value={nyxIdConfig.clientId || "Unavailable"}
                />
              }
            />
            <SummaryField
              label="Token type"
              value={storedSession?.tokens.tokenType || "Unavailable"}
            />
            <SummaryField
              label="Access token expires"
              value={formatSessionExpiry(storedSession?.tokens.expiresAt)}
            />
            <SummaryField
              label="User"
              value={
                storedSession ? (
                  storedSession.user.name ||
                  storedSession.user.email ||
                  storedSession.user.sub
                ) : (
                  "Unavailable"
                )
              }
            />
            <SummaryField
              label="Refresh token"
              value={storedSession?.tokens.refreshToken ? "Available" : "Unavailable"}
            />
          </div>
        </div>
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="Quick signals">
        <div style={diagnosticsPanelBodyStyle}>
          <Space size={[10, 10]} wrap>
            <Tag icon={<ApiOutlined />} color="blue">
              Same-origin API
            </Tag>
            <Tag icon={<GlobalOutlined />} color="geekblue">
              {runtimeModeLabel} runtime
            </Tag>
            <Tag icon={<CodeOutlined />}>Build {diagnosticsVersion}</Tag>
          </Space>
        </div>
      </AevatarPanel>
    </div>
  );
};

export default DiagnosticsSettingsContent;
