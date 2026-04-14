import { BuildOutlined, RocketOutlined } from "@ant-design/icons";
import { Button, Space, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildTeamsHref } from "@/shared/navigation/teamRoutes";
import { buildStudioRoute } from "@/shared/studio/navigation";
import { AevatarPageShell, AevatarPanel } from "@/shared/ui/aevatarPageShells";

const TeamCreatePage: React.FC = () => {
  return (
    <AevatarPageShell
      extra={
        <Button onClick={() => history.push(buildTeamsHref())}>Back to My Teams</Button>
      }
      layoutMode="document"
      onBack={() => history.push(buildTeamsHref())}
      title="Create Team"
      titleHelp="The current version reuses Studio as the team builder entry point before we introduce a templated team creation flow."
    >
      <AevatarPanel
        title="Start Building"
        titleHelp="Scope = Team, so this flow goes straight into the team builder to create behaviors, scripts, agent roles, and integrations."
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Typography.Paragraph style={{ marginBottom: 0 }}>
            This step does not introduce a new backend flow. It reuses the existing Studio workbench to assemble the team first, then moves into Team Details to inspect topology and the event stream.
          </Typography.Paragraph>
          <Space wrap size={[8, 8]}>
            <Button
              icon={<BuildOutlined />}
              onClick={() =>
                history.push(
                  buildStudioRoute({
                    draftMode: "new",
                    tab: "studio",
                  }),
                )
              }
              type="primary"
            >
              Open Team Builder
            </Button>
            <Button
              icon={<RocketOutlined />}
              onClick={() =>
                history.push(
                  buildStudioRoute({
                    tab: "workflows",
                  }),
                )
              }
            >
              View Behaviors
            </Button>
          </Space>
        </div>
      </AevatarPanel>
    </AevatarPageShell>
  );
};

export default TeamCreatePage;
