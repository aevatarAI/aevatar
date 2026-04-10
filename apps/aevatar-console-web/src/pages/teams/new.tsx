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
        <Button onClick={() => history.push(buildTeamsHref())}>返回我的团队</Button>
      }
      layoutMode="document"
      onBack={() => history.push(buildTeamsHref())}
      title="组建团队"
      titleHelp="当前版本先复用已有 Studio 能力作为团队构建器入口，后续再承接模板化的组建流程。"
    >
      <AevatarPanel
        title="开始构建"
        titleHelp="Scope = Team，因此这里直接进入团队构建器创建行为定义、脚本行为、Agent 角色和集成。"
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Typography.Paragraph style={{ marginBottom: 0 }}>
            这一步不会引入新的后端流程，先复用现有 Studio 工作台完成团队搭建，再从团队详情页观察事件拓扑和事件流。
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
              打开团队构建器
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
              查看行为定义
            </Button>
          </Space>
        </div>
      </AevatarPanel>
    </AevatarPageShell>
  );
};

export default TeamCreatePage;
