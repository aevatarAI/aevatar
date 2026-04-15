import { UserOutlined } from "@ant-design/icons";
import { Avatar, Button, Space, Tag, Tooltip, Typography } from "antd";
import React, { useMemo } from "react";
import { history } from "@/shared/navigation/history";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { summaryFieldGridStyle, summaryMetricGridStyle } from "@/shared/ui/proComponents";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import { SettingsPageShell, SummaryField, SummaryMetric } from "./shared";

const compactIdentityStyle: React.CSSProperties = {
  margin: 0,
  maxWidth: "100%",
};

function formatCompactIdentifier(
  value: string,
  leading = 8,
  trailing = 6,
): string {
  if (value.length <= leading + trailing + 3) {
    return value;
  }

  return `${value.slice(0, leading)}...${value.slice(-trailing)}`;
}

const AccountSettingsPage: React.FC = () => {
  const authSession = useMemo(() => loadRestorableAuthSession(), []);

  const accountDisplayName = useMemo(
    () =>
      authSession?.user.name ||
      authSession?.user.email ||
      authSession?.user.sub ||
      "暂无会话",
    [authSession],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authSession) {
      return "当前浏览器没有可用的登录信息。";
    }

    return authSession.user.email || authSession.user.sub;
  }, [authSession]);
  const compactUserId = useMemo(
    () =>
      authSession?.user.sub
        ? formatCompactIdentifier(authSession.user.sub)
        : "暂无",
    [authSession],
  );

  const rolesLabel = authSession?.user.roles?.join(", ") || "暂无";
  const groupsLabel = authSession?.user.groups?.join(", ") || "暂无";

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <SettingsPageShell>
      <AevatarPanel title="账号信息">
        {authSession ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <Space align="start" size={16}>
              <Avatar
                icon={<UserOutlined />}
                size={56}
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

            <Space wrap size={[8, 8]}>
              <Tag color="processing">已登录</Tag>
              <Tag color={authSession.user.email_verified ? "success" : "warning"}>
                {authSession.user.email_verified ? "邮箱已验证" : "邮箱未验证"}
              </Tag>
              <Tag>{`${authSession.user.roles?.length ?? 0} 个角色`}</Tag>
              <Tag>{`${authSession.user.groups?.length ?? 0} 个分组`}</Tag>
            </Space>

            <div style={summaryFieldGridStyle}>
              <SummaryField label="邮箱" value={authSession.user.email || "暂无"} />
              <SummaryField
                label="用户 ID"
                value={
                  <Tooltip title={authSession.user.sub}>
                    <Typography.Text
                      copyable={{ text: authSession.user.sub }}
                      style={compactIdentityStyle}
                    >
                      {compactUserId}
                    </Typography.Text>
                  </Tooltip>
                }
              />
              <SummaryField label="角色" value={rolesLabel} />
              <SummaryField label="分组" value={groupsLabel} />
            </div>

            <Space wrap>
              <Button
                onClick={() =>
                  history.push(
                    buildRuntimeRunsHref({
                      workflow: "direct",
                    }),
                  )
                }
              >
                打开运行记录
              </Button>
              <Button danger onClick={handleSignOut}>
                退出登录
              </Button>
            </Space>
          </div>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Typography.Text type="secondary">
              {accountSecondaryText}
            </Typography.Text>
            <Button type="primary" onClick={() => window.location.replace("/login")}>
              去登录
            </Button>
          </div>
        )}
      </AevatarPanel>

      <AevatarPanel title="会话摘要">
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={summaryMetricGridStyle}>
            <SummaryMetric label="角色" value={authSession?.user.roles?.length ?? 0} />
            <SummaryMetric label="分组" value={authSession?.user.groups?.length ?? 0} />
          </div>
          <div style={summaryFieldGridStyle}>
            <SummaryField label="显示名称" value={accountDisplayName} />
            <SummaryField
              label="邮箱状态"
              value={
                authSession
                  ? authSession.user.email_verified
                    ? "已验证"
                    : "未验证"
                  : "暂无会话"
              }
            />
          </div>
        </div>
      </AevatarPanel>
    </SettingsPageShell>
  );
};

export default AccountSettingsPage;
