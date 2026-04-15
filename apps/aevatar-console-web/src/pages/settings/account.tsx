import { UserOutlined } from "@ant-design/icons";
import { ProCard } from "@ant-design/pro-components";
import { Avatar, Button, Space, Tag, Tooltip, Typography } from "antd";
import React, { useMemo } from "react";
import { history } from "@/shared/navigation/history";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { embeddedPanelStyle, summaryFieldGridStyle, summaryMetricGridStyle } from "@/shared/ui/proComponents";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import { SettingsPageShell, SummaryField, SummaryMetric } from "./shared";

const compactIdentityStyle: React.CSSProperties = {
  margin: 0,
  maxWidth: "100%",
};

const accountUsageNotes = [
  "This page reflects the active NyxID session restored from the browser before protected console requests are sent.",
  "Identity and access stay in the same shell as runtime and governance so operators never switch mental models.",
  "Sign out only clears the local browser session. Backend runtime state is unaffected.",
];

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
      "No active session",
    [authSession],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authSession) {
      return "No signed-in user information is available in this browser session.";
    }

    return authSession.user.email || authSession.user.sub;
  }, [authSession]);
  const compactUserId = useMemo(
    () =>
      authSession?.user.sub
        ? formatCompactIdentifier(authSession.user.sub)
        : "n/a",
    [authSession],
  );

  const rolesLabel = authSession?.user.roles?.join(", ") || "n/a";
  const groupsLabel = authSession?.user.groups?.join(", ") || "n/a";

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <SettingsPageShell content="Manage your signed-in identity, operator access posture, and browser-backed session.">
      <AevatarPanel
        title="Operator Account"
        titleHelp="Identity is presented like an operator surface, not a detached profile page."
      >
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
              <Tag color="processing">Signed in</Tag>
              <Tag color={authSession.user.email_verified ? "success" : "warning"}>
                {authSession.user.email_verified ? "Email verified" : "Email unverified"}
              </Tag>
              <Tag>{`${authSession.user.roles?.length ?? 0} roles`}</Tag>
              <Tag>{`${authSession.user.groups?.length ?? 0} groups`}</Tag>
            </Space>

            <div style={summaryFieldGridStyle}>
              <SummaryField label="Email" value={authSession.user.email || "n/a"} />
              <SummaryField
                label="User ID"
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
              <SummaryField label="Roles" value={rolesLabel} />
              <SummaryField label="Groups" value={groupsLabel} />
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
                Open runtime runs
              </Button>
              <Button danger onClick={handleSignOut}>
                Sign out
              </Button>
            </Space>
          </div>
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Typography.Text type="secondary">
              {accountSecondaryText}
            </Typography.Text>
            <Button type="primary" onClick={() => window.location.replace("/login")}>
              Sign in
            </Button>
          </div>
        )}
      </AevatarPanel>

      <div
        style={{
          display: "grid",
          gap: 16,
          gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
        }}
      >
        <AevatarPanel title="Session Summary">
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div style={summaryMetricGridStyle}>
              <SummaryMetric label="Roles" value={authSession?.user.roles?.length ?? 0} />
              <SummaryMetric label="Groups" value={authSession?.user.groups?.length ?? 0} />
            </div>
            <div style={summaryFieldGridStyle}>
              <SummaryField label="Display name" value={accountDisplayName} />
              <SummaryField
                label="Email status"
                value={
                  authSession
                    ? authSession.user.email_verified
                      ? "Verified"
                      : "Unverified"
                    : "No session"
                }
              />
            </div>
          </div>
        </AevatarPanel>

        <ProCard
          ghost
          style={{
            ...embeddedPanelStyle,
            borderRadius: 12,
            height: "100%",
          }}
        >
          <Space orientation="vertical" size={12} style={{ width: "100%" }}>
            <Typography.Text strong>Access Notes</Typography.Text>
            {accountUsageNotes.map((item) => (
              <Typography.Text key={item}>{item}</Typography.Text>
            ))}
          </Space>
        </ProCard>
      </div>
    </SettingsPageShell>
  );
};

export default AccountSettingsPage;
