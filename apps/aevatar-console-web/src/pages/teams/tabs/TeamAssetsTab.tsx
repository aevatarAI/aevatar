import {
  BranchesOutlined,
  CodeOutlined,
  DeploymentUnitOutlined,
} from "@ant-design/icons";
import { Button, Typography } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  CompactFactValue,
  DetailPill,
  FactLine,
  SignalCard,
} from "../components/TeamDetailPrimitives";

type AssetSummaryCard = {
  readonly caption?: string;
  readonly icon?: React.ReactNode;
  readonly label: string;
  readonly value: React.ReactNode;
};

type AssetRow = {
  readonly actionLabel: string;
  readonly badgeLabel: string;
  readonly badgeStyle: React.CSSProperties;
  readonly buttonStyle: React.CSSProperties;
  readonly key: string;
  readonly primaryMetaLabel: string;
  readonly primaryMetaValue: string;
  readonly secondaryMetaLabel: string;
  readonly secondaryMetaValue: string;
  readonly summary: string;
  readonly subtitle: string;
  readonly title: string;
};

type TeamAssetsTabProps = {
  readonly scriptRows: readonly AssetRow[];
  readonly summaryCards: readonly AssetSummaryCard[];
  readonly workflowRows: readonly AssetRow[];
  readonly onOpenScriptAsset: (scriptId: string) => void;
  readonly onOpenScriptsWorkspace: () => void;
  readonly onOpenWorkflowAsset: (workflowId: string) => void;
  readonly onOpenWorkflowWorkspace: () => void;
};

const assetWorkspaceButtonStyle: React.CSSProperties = {
  borderRadius: 14,
  height: 36,
  paddingInline: 16,
};

const TeamAssetsTab: React.FC<TeamAssetsTabProps> = ({
  scriptRows,
  summaryCards,
  workflowRows,
  onOpenScriptAsset,
  onOpenScriptsWorkspace,
  onOpenWorkflowAsset,
  onOpenWorkflowWorkspace,
}) => {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="当前 Team 资产"
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            scope workflows · scope scripts · Studio deep-link
          </Typography.Text>
        }
      >
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          {summaryCards.map((card) => (
            <SignalCard
              key={card.label}
              caption={card.caption}
              icon={card.icon}
              label={card.label}
              value={card.value}
            />
          ))}
        </div>
      </AevatarPanel>
      <div
        style={{
          display: "grid",
          gap: 16,
          gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
        }}
      >
        <AevatarPanel
          title="Workflow 资产"
          extra={
            <Button onClick={onOpenWorkflowWorkspace} style={assetWorkspaceButtonStyle}>
              打开 Workflow Studio
            </Button>
          }
        >
          {workflowRows.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
              {workflowRows.map((row) => (
                <button
                  aria-label={`打开 workflow ${row.title}`}
                  key={row.key}
                  onClick={() => onOpenWorkflowAsset(row.key)}
                  style={{
                    ...row.buttonStyle,
                    display: "flex",
                    flexDirection: "column",
                    gap: 12,
                    padding: 16,
                    textAlign: "left",
                  }}
                  type="button"
                >
                  <div
                    style={{
                      alignItems: "flex-start",
                      display: "flex",
                      gap: 12,
                      justifyContent: "space-between",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      <Typography.Text strong>{row.title}</Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.subtitle}
                      </Typography.Text>
                    </div>
                    <DetailPill compact style={row.badgeStyle} text={row.badgeLabel} />
                  </div>
                  <Typography.Text type="secondary">{row.summary}</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 8,
                      gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.primaryMetaLabel}
                      </Typography.Text>
                      <CompactFactValue value={row.primaryMetaValue} />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.secondaryMetaLabel}
                      </Typography.Text>
                      <CompactFactValue value={row.secondaryMetaValue} />
                    </div>
                  </div>
                  <Typography.Text strong style={{ color: "var(--ant-colorPrimary)" }}>
                    {row.actionLabel}
                  </Typography.Text>
                </button>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              compact
              title="当前还没有 workflow 资产"
              description="当前 scope 还没有可见 workflow 资产。"
            />
          )}
        </AevatarPanel>
        <AevatarPanel
          title="Script 资产"
          extra={
            <Button onClick={onOpenScriptsWorkspace} style={assetWorkspaceButtonStyle}>
              打开 Script Studio
            </Button>
          }
        >
          {scriptRows.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
              {scriptRows.map((row) => (
                <button
                  aria-label={`打开 script ${row.title}`}
                  key={row.key}
                  onClick={() => onOpenScriptAsset(row.key)}
                  style={{
                    ...row.buttonStyle,
                    display: "flex",
                    flexDirection: "column",
                    gap: 12,
                    padding: 16,
                    textAlign: "left",
                  }}
                  type="button"
                >
                  <div
                    style={{
                      alignItems: "flex-start",
                      display: "flex",
                      gap: 12,
                      justifyContent: "space-between",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      <Typography.Text strong>{row.title}</Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.subtitle}
                      </Typography.Text>
                    </div>
                    <DetailPill compact style={row.badgeStyle} text={row.badgeLabel} />
                  </div>
                  <Typography.Text type="secondary">{row.summary}</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 8,
                      gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.primaryMetaLabel}
                      </Typography.Text>
                      <CompactFactValue value={row.primaryMetaValue} />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {row.secondaryMetaLabel}
                      </Typography.Text>
                      <CompactFactValue value={row.secondaryMetaValue} />
                    </div>
                  </div>
                  <Typography.Text strong style={{ color: "var(--ant-colorPrimary)" }}>
                    {row.actionLabel}
                  </Typography.Text>
                </button>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              compact
              title="当前还没有 script 资产"
              description="当前 scope 还没有可见 script 资产。"
            />
          )}
        </AevatarPanel>
      </div>
    </div>
  );
};

export const teamAssetIcons = {
  deployment: <DeploymentUnitOutlined />,
  scripts: <CodeOutlined />,
  workflows: <BranchesOutlined />,
};

export default TeamAssetsTab;
