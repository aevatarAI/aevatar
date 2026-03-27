import { UserOutlined } from "@ant-design/icons";
import { history } from "@/shared/navigation/history";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { clearStoredAuthSession, loadRestorableAuthSession } from "@/shared/auth/session";
import {
  cardStackStyle,
  fillCardStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryMetricGridStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";
import { Avatar, Button, Col, Row, Space, Tag, Tooltip, Typography } from "antd";
import React, { useMemo } from "react";
import { ProCard } from "@ant-design/pro-components";
import { SettingsPageShell, SummaryField, SummaryMetric } from "./shared";

const accountUsageNotes = [
  {
    id: "account-session",
    text: "This page reflects the active NyxID session restored from the browser before authenticated console requests are sent.",
  },
  {
    id: "account-signout",
    text: "Signing out clears the stored browser session and returns the console to the login screen.",
  },
];

const compactIdentityStyle: React.CSSProperties = {
  margin: 0,
  maxWidth: "100%",
};

function formatCompactIdentifier(
  value: string,
  leading = 8,
  trailing = 6
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
    [authSession]
  );
  const accountSecondaryText = useMemo(() => {
    if (!authSession) {
      return "No signed-in user information is available in this browser session.";
    }

    return authSession.user.email || authSession.user.sub;
  }, [authSession]);
  const rolesLabel = useMemo(() => {
    const roles = authSession?.user.roles ?? [];
    return roles.length > 0 ? roles.join(", ") : "n/a";
  }, [authSession]);
  const groupsLabel = useMemo(() => {
    const groups = authSession?.user.groups ?? [];
    return groups.length > 0 ? groups.join(", ") : "n/a";
  }, [authSession]);
  const compactUserId = useMemo(
    () =>
      authSession?.user.sub
        ? formatCompactIdentifier(authSession.user.sub)
        : "n/a",
    [authSession]
  );

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };

  return (
    <SettingsPageShell
      content="Manage your signed-in identity and browser-backed session."
    >
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} xxl={15} style={stretchColumnStyle}>
          <ProCard
            title="Account profile"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <div style={cardStackStyle}>
              {authSession ? (
                <>
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
                    {authSession.user.email_verified ? (
                      <Tag color="success">Email verified</Tag>
                    ) : (
                      <Tag color="warning">Email unverified</Tag>
                    )}
                    {authSession.user.roles?.length ? (
                      <Tag>{`${authSession.user.roles.length} roles`}</Tag>
                    ) : null}
                    {authSession.user.groups?.length ? (
                      <Tag>{`${authSession.user.groups.length} groups`}</Tag>
                    ) : null}
                  </Space>

                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label="Email"
                      value={authSession.user.email || "n/a"}
                    />
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

                  <Space wrap size={[8, 8]}>
                    <Button
                      onClick={() =>
                        history.push(
                          buildRuntimeRunsHref({
                            workflow: "direct",
                          })
                        )
                      }
                    >
                      Open runtime runs
                    </Button>
                    <Button danger onClick={handleSignOut}>
                      Sign out
                    </Button>
                  </Space>
                </>
              ) : (
                <div style={cardStackStyle}>
                  <Typography.Text type="secondary">
                    {accountSecondaryText}
                  </Typography.Text>
                  <Space wrap size={[8, 8]}>
                    <Button
                      type="primary"
                      onClick={() => window.location.replace("/login")}
                    >
                      Sign in
                    </Button>
                  </Space>
                </div>
              )}
            </div>
          </ProCard>
        </Col>

        <Col xs={24} xxl={9} style={stretchColumnStyle}>
          <Space direction="vertical" style={{ width: "100%" }} size={16}>
            <ProCard
              title="Session summary"
              {...moduleCardProps}
              style={fillCardStyle}
            >
              <div style={cardStackStyle}>
                <div style={summaryMetricGridStyle}>
                  <SummaryMetric
                    label="Roles"
                    value={authSession?.user.roles?.length ?? 0}
                  />
                  <SummaryMetric
                    label="Groups"
                    value={authSession?.user.groups?.length ?? 0}
                  />
                </div>

                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Display name"
                    value={accountDisplayName}
                  />
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
            </ProCard>
            <ProCard
              title="Access notes"
              {...moduleCardProps}
              style={fillCardStyle}
            >
              <Space direction="vertical" style={{ width: "100%" }} size={12}>
                {accountUsageNotes.map((item) => (
                  <Typography.Text key={item.id}>{item.text}</Typography.Text>
                ))}
              </Space>
            </ProCard>
          </Space>
        </Col>
      </Row>
    </SettingsPageShell>
  );
};

export default AccountSettingsPage;
