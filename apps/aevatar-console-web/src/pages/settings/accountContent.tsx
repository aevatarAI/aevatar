import {
  LogoutOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Avatar, Button, Empty, Space, Typography, theme } from "antd";
import React, { useMemo } from "react";
import {
  clearStoredAuthSession,
  hasActiveAccessToken,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { AevatarCompactText } from "@/shared/ui/compactText";
import { describeError } from "@/shared/ui/errorText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from "@/shared/ui/proComponents";
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from "./shared";

type SessionTone = "default" | "error" | "info" | "success" | "warning";

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return "Unavailable";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value);
}

function formatOptionalText(value?: string | null): string {
  return value?.trim() || "Unavailable";
}

function formatEmailVerified(value?: boolean): string {
  if (value === undefined) {
    return "Unavailable";
  }

  return value ? "Verified" : "Needs review";
}

type AccountSettingsContentProps = {
  readonly showInlineSignOut?: boolean;
};

const AccountSettingsContent: React.FC<AccountSettingsContentProps> = ({
  showInlineSignOut = true,
}) => {
  const { token } = theme.useToken();
  const settingsPanelStyle = buildSettingsPanelStyle(token);
  const browserSession = useMemo(() => loadRestorableAuthSession(), []);
  const backendSessionQuery = useQuery({
    queryKey: ["settings", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });

  const backendSession = backendSessionQuery.data;
  const browserAccessTokenActive = hasActiveAccessToken(browserSession?.tokens);
  const browserSessionRestorable = Boolean(browserSession);
  const backendSessionStatus = (() => {
    if (backendSessionQuery.isPending) {
      return {
        tone: "info" as SessionTone,
        value: "Checking",
      };
    }

    if (backendSessionQuery.isError) {
      return {
        tone: "error" as SessionTone,
        value: "Check failed",
      };
    }

    if (!backendSession?.enabled) {
      return {
        tone: "default" as SessionTone,
        value: "Disabled",
      };
    }

    if (backendSession.authenticated) {
      return {
        tone: "success" as SessionTone,
        value: "Authenticated",
      };
    }

    return {
      tone: "warning" as SessionTone,
      value: "No backend session",
    };
  })();
  const browserSessionStatus = browserAccessTokenActive
    ? {
        tone: "success" as SessionTone,
        value: "Access token active",
      }
    : browserSessionRestorable
      ? {
          tone: "warning" as SessionTone,
          value: "Refresh available",
        }
      : {
          tone: "default" as SessionTone,
          value: "No local session",
        };
  const accountDisplayName =
    browserSession?.user.name ||
    browserSession?.user.email ||
    backendSession?.name ||
    backendSession?.email ||
    browserSession?.user.sub ||
    "Session diagnostics";
  const accountSecondaryText = browserSession
    ? browserSession.user.email || browserSession.user.sub
    : "No browser session is stored in this browser.";

  const rolesLabel = browserSession?.user.roles?.join(", ") || "No roles";
  const groupsLabel = browserSession?.user.groups?.join(", ") || "No groups";
  const backendErrorMessage = backendSessionQuery.isError
    ? describeError(
        backendSessionQuery.error,
        "Backend session could not be checked.",
      )
    : null;

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <>
      <AevatarPanel
        extra={
          browserSession && showInlineSignOut ? (
            <Button danger icon={<LogoutOutlined />} onClick={handleSignOut}>
              Sign out
            </Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="Session overview"
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
          <Space align="start" size={14}>
            <Avatar
              icon={<UserOutlined />}
              size={52}
              src={browserSession?.user.picture || backendSession?.picture}
            />
            <div style={{ minWidth: 0 }}>
              <Typography.Text
                strong
                style={{ display: "block", fontSize: 18 }}
              >
                {accountDisplayName}
              </Typography.Text>
              <Typography.Text type="secondary">
                {accountSecondaryText}
              </Typography.Text>
            </div>
          </Space>

          <div style={summaryMetricGridStyle}>
            <SummaryMetric
              label="Browser session"
              tone={browserSessionStatus.tone}
              value={browserSessionStatus.value}
            />
            <SummaryMetric
              label="Backend session"
              tone={backendSessionStatus.tone}
              value={backendSessionStatus.value}
            />
          </div>

          {browserSession ? (
            <div style={summaryFieldGridStyle}>
              <SummaryField
                label="User ID"
                value={
                  <AevatarCompactText
                    copyable
                    head={8}
                    maxWidth="100%"
                    monospace
                    tail={6}
                    value={browserSession.user.sub}
                  />
                }
              />
              <SummaryField
                label="Browser email"
                value={formatOptionalText(browserSession.user.email)}
              />
              <SummaryField
                label="Email check"
                value={formatEmailVerified(browserSession.user.email_verified)}
              />
              <SummaryField label="Roles" value={rolesLabel} />
              <SummaryField label="Groups" value={groupsLabel} />
            </div>
          ) : null}
        </div>
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="Browser session">
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Typography.Text type="secondary">
            Stored in this browser and used to attach bearer tokens to frontend
            API calls.
          </Typography.Text>
          {browserSession ? (
            <div style={summaryFieldGridStyle}>
              <SummaryField
                label="Access token"
                value={browserAccessTokenActive ? "Active" : "Expired"}
              />
              <SummaryField
                label="Access token expires"
                value={formatSessionExpiry(browserSession.tokens.expiresAt)}
              />
              <SummaryField
                label="Token type"
                value={formatOptionalText(browserSession.tokens.tokenType)}
              />
              <SummaryField
                label="OAuth scope"
                value={formatOptionalText(browserSession.tokens.scope)}
              />
              <SummaryField
                label="Refresh token"
                value={
                  browserSession.tokens.refreshToken ? "Available" : "Unavailable"
                }
              />
            </div>
          ) : (
            <Empty
              description="No restorable browser session is stored in this browser."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </div>
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="Backend session">
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Typography.Text type="secondary">
            Returned by the backend auth endpoint and used to confirm what the
            server currently recognizes.
          </Typography.Text>
          {backendSessionQuery.isPending ? (
            <Alert
              description="The browser session has been read. The backend session is still loading from /api/auth/me."
              showIcon
              title="Checking backend session"
              type="info"
            />
          ) : null}
          {backendErrorMessage ? (
            <Alert
              description={backendErrorMessage}
              showIcon
              title="Backend session check failed"
              type="error"
            />
          ) : null}
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label="Status"
              value={backendSessionStatus.value}
            />
            <SummaryField
              label="Provider"
              value={formatOptionalText(backendSession?.providerDisplayName)}
            />
            <SummaryField
              label="Backend identity"
              value={formatOptionalText(
                backendSession?.name || backendSession?.email,
              )}
            />
            <SummaryField
              label="Scope"
              value={formatOptionalText(backendSession?.scopeId)}
            />
            <SummaryField
              label="Scope source"
              value={formatOptionalText(backendSession?.scopeSource)}
            />
            <SummaryField
              label="Invoke auth mode"
              value={formatOptionalText(backendSession?.invokeAuthMode)}
            />
            <SummaryField
              label="Backend error"
              value={formatOptionalText(backendSession?.errorMessage)}
            />
          </div>
          {!backendSessionQuery.isPending &&
          !backendSessionQuery.isError &&
          backendSession?.enabled &&
          !backendSession.authenticated ? (
            <Empty
              description="The backend responded, but it does not recognize an authenticated session."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ) : null}
        </div>
      </AevatarPanel>
    </>
  );
};

export default AccountSettingsContent;
