import {
  LogoutOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { Avatar, Button, Empty, Space, Typography, theme } from "antd";
import React, { useMemo } from "react";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { AevatarCompactText } from "@/shared/ui/compactText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from "@/shared/ui/proComponents";
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from "./shared";

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return "Unavailable";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value);
}

type AccountSettingsContentProps = {
  readonly showInlineSignOut?: boolean;
};

const AccountSettingsContent: React.FC<AccountSettingsContentProps> = ({
  showInlineSignOut = true,
}) => {
  const { token } = theme.useToken();
  const settingsPanelStyle = buildSettingsPanelStyle(token);
  const authSession = useMemo(() => loadRestorableAuthSession(), []);

  const accountDisplayName = useMemo(
    () =>
      authSession?.user.name ||
      authSession?.user.email ||
      authSession?.user.sub ||
      "No active session",
    [authSession],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authSession) {
      return "This browser does not have a restorable sign-in session.";
    }

    return authSession.user.email || authSession.user.sub;
  }, [authSession]);
  const rolesLabel = authSession?.user.roles?.join(", ") || "No roles";
  const groupsLabel = authSession?.user.groups?.join(", ") || "No groups";

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <>
      <AevatarPanel
        extra={
          authSession && showInlineSignOut ? (
            <Button danger icon={<LogoutOutlined />} onClick={handleSignOut}>
              Sign out
            </Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="Profile"
      >
        {authSession ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Space align="start" size={14}>
              <Avatar
                icon={<UserOutlined />}
                size={52}
                src={authSession.user.picture}
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
              <SummaryMetric label="Session" tone="success" value="Active" />
              <SummaryMetric
                label="Email"
                tone={authSession.user.email_verified ? "success" : "warning"}
                value={
                  authSession.user.email_verified ? "Verified" : "Needs review"
                }
              />
            </div>

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
                    value={authSession.user.sub}
                  />
                }
              />
              <SummaryField label="Roles" value={rolesLabel} />
              <SummaryField label="Groups" value={groupsLabel} />
            </div>
          </div>
        ) : (
          <Empty
            description="This browser does not have a restorable sign-in session."
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          >
            <Button
              type="primary"
              onClick={() => window.location.replace("/login")}
            >
              Sign in
            </Button>
          </Empty>
        )}
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="Authentication">
        <div style={summaryFieldGridStyle}>
          <SummaryField
            label="Access token expires"
            value={formatSessionExpiry(authSession?.tokens.expiresAt)}
          />
          <SummaryField
            label="Token type"
            value={authSession?.tokens.tokenType || "Unavailable"}
          />
          <SummaryField
            label="OAuth scope"
            value={authSession?.tokens.scope || "Unavailable"}
          />
          <SummaryField
            label="Refresh token"
            value={
              authSession?.tokens.refreshToken ? "Available" : "Unavailable"
            }
          />
        </div>
      </AevatarPanel>
    </>
  );
};

export default AccountSettingsContent;
