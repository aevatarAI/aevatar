import {
  LogoutOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Avatar, Button, Empty, Space, Typography, theme } from "antd";
import React, { useMemo } from "react";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { AevatarCompactText } from "@/shared/ui/compactText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from "@/shared/ui/proComponents";
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from "./shared";

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return "不可用";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value);
}

function formatIsoSessionExpiry(value?: string | null): string {
  if (!value) {
    return "不可用";
  }

  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) {
    return "不可用";
  }

  return formatSessionExpiry(parsed);
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
  const authMeQuery = useQuery({
    queryKey: ["settings", "auth-me"],
    queryFn: () => studioApi.getAuthSession(),
  });
  const backendProfile = authMeQuery.data?.profile;
  const backendSession = authMeQuery.data?.session;
  const authenticated = Boolean(authMeQuery.data?.authenticated || authSession);

  const accountDisplayName = useMemo(
    () =>
      backendProfile?.name ||
      backendProfile?.email ||
      backendProfile?.subject ||
      authSession?.user.name ||
      authSession?.user.email ||
      authSession?.user.sub ||
      "没有活动会话",
    [authSession, backendProfile],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authenticated) {
      return "此浏览器没有可恢复的登录会话。";
    }

    if (backendProfile?.email || backendProfile?.subject) {
      return backendProfile.email || backendProfile.subject;
    }

    return authSession?.user.email || authSession?.user.sub || "不可用";
  }, [authSession, authenticated, backendProfile]);
  const rolesLabel =
    (backendProfile?.roles && backendProfile.roles.length > 0
      ? backendProfile.roles.join(", ")
      : authSession?.user.roles?.join(", ")) || "无角色";
  const groupsLabel =
    (backendProfile?.groups && backendProfile.groups.length > 0
      ? backendProfile.groups.join(", ")
      : authSession?.user.groups?.join(", ")) || "无分组";
  const userId = backendProfile?.subject || authSession?.user.sub || "";
  const picture = backendProfile?.picture || authSession?.user.picture;
  const emailVerified =
    backendProfile?.emailVerified ?? authSession?.user.email_verified ?? null;

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <>
      <AevatarPanel
        extra={
          authenticated && showInlineSignOut ? (
            <Button danger icon={<LogoutOutlined />} onClick={handleSignOut}>
              退出登录
            </Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="个人资料"
      >
        {authenticated ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Space align="start" size={14}>
              <Avatar
                icon={<UserOutlined />}
                size={52}
                src={picture}
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
                label="会话"
                tone={backendSession?.authenticated || authSession ? "success" : "warning"}
                value={backendSession?.authenticated || authSession ? "已激活" : "仅浏览器"}
              />
              <SummaryMetric
                label="邮箱"
                tone={emailVerified ? "success" : "warning"}
                value={
                  emailVerified ? "已验证" : "待确认"
                }
              />
            </div>

            <div style={summaryFieldGridStyle}>
              <SummaryField
                label="用户 ID"
                value={
                  <AevatarCompactText
                    copyable
                    head={8}
                    maxWidth="100%"
                    monospace
                    tail={6}
                    value={userId}
                  />
                }
              />
              <SummaryField label="角色" value={rolesLabel} />
              <SummaryField label="分组" value={groupsLabel} />
            </div>
          </div>
        ) : (
          <Empty
            description="此浏览器没有可恢复的登录会话。"
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          >
            <Button
              type="primary"
              onClick={() => window.location.replace("/login")}
            >
              登录
            </Button>
          </Empty>
        )}
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="认证信息">
        <div style={summaryFieldGridStyle}>
          <SummaryField
            label="会话过期时间"
            value={
              backendSession?.expiresAtUtc
                ? formatIsoSessionExpiry(backendSession.expiresAtUtc)
                : formatSessionExpiry(authSession?.tokens.expiresAt)
            }
          />
          <SummaryField
            label="Provider"
            value={authMeQuery.data?.providerDisplayName || "不可用"}
          />
          <SummaryField
            label="Scope"
            value={authMeQuery.data?.scopeId || authSession?.tokens.scope || "不可用"}
          />
          <SummaryField
            label="本地 refresh token"
            value={authSession?.tokens.refreshToken ? "可用" : "不可用"}
          />
        </div>
      </AevatarPanel>
    </>
  );
};

export default AccountSettingsContent;
