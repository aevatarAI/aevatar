import type { ServiceCommandAcceptedReceipt } from "@/shared/models/services";

export type DeploymentReleaseHandoffAction =
  | "deploy-candidate"
  | "replace-serving-targets"
  | "advance-rollout"
  | "pause-rollout"
  | "resume-rollout"
  | "rollback-rollout"
  | "deactivate-deployment";

export type DeploymentReleaseEvidenceView =
  | "catalog"
  | "serving"
  | "rollout"
  | "traffic";

export type DeploymentReleaseHandoff = {
  action: DeploymentReleaseHandoffAction;
  actionLabel: string;
  actionSummary: string;
  commandId: string;
  correlationId: string;
  evidenceDescription: string;
  evidenceItems: string[];
  evidenceView: DeploymentReleaseEvidenceView;
  evidenceViewLabel: string;
  id: string;
  noticeMessage: string;
  noticeTone: "success" | "warning";
  pendingLabel: string;
  summaryItems: Array<{
    label: string;
    value: string;
  }>;
  title: string;
};

export type DeploymentReleaseHandoffInput = {
  action: DeploymentReleaseHandoffAction;
  activeRevisionId?: string;
  candidateRevisionId?: string;
  deploymentId?: string;
  endpointCount?: number;
  receipt?: Partial<ServiceCommandAcceptedReceipt>;
  rolloutId?: string;
  rolloutStageLabel?: string;
  serviceId: string;
  targetCount?: number;
};

const actionCopy: Record<
  DeploymentReleaseHandoffAction,
  Pick<
    DeploymentReleaseHandoff,
    | "actionLabel"
    | "actionSummary"
    | "evidenceDescription"
    | "evidenceItems"
    | "evidenceView"
    | "evidenceViewLabel"
    | "noticeMessage"
    | "noticeTone"
    | "title"
  >
> = {
  "advance-rollout": {
    actionLabel: "推进 rollout",
    actionSummary: "推进请求已进入发布控制面",
    evidenceDescription:
      "这只表示 rollout 推进命令已接收，仍要等待阶段和流量证据刷新后再判断是否完成。",
    evidenceItems: [
      "Rollout 当前 stage 或 updatedAt 发生变化",
      "Serving targets 与当前 stage 目标一致",
      "Traffic 分配反映新的 stage 权重",
    ],
    evidenceView: "rollout",
    evidenceViewLabel: "Rollout",
    noticeMessage: "Rollout 推进请求已提交，等待阶段证据刷新。",
    noticeTone: "success",
    title: "Rollout 推进已提交",
  },
  "deactivate-deployment": {
    actionLabel: "停用 deployment",
    actionSummary: "停用请求已进入发布控制面",
    evidenceDescription:
      "这只表示停用命令已接收，不代表该 deployment 已经从 serving 或 catalog 中消失。",
    evidenceItems: [
      "Deployment catalog 中目标 deployment 状态不再 active",
      "Serving targets 不再路由到被停用 deployment",
      "Traffic 入口不再把流量分配给该 revision/deployment",
    ],
    evidenceView: "catalog",
    evidenceViewLabel: "部署目录",
    noticeMessage: "Deployment 停用请求已提交，等待 catalog/serving 证据刷新。",
    noticeTone: "warning",
    title: "Deployment 停用已提交",
  },
  "deploy-candidate": {
    actionLabel: "部署候选版本",
    actionSummary: "候选版本请求已进入发布控制面",
    evidenceDescription:
      "这只表示候选版本部署命令已接收，尚未说明候选 revision 已经被 serving 观察到。",
    evidenceItems: [
      "Rollout 出现活动阶段或阶段目标变化",
      "Serving targets 中出现候选 revision",
      "Traffic 分配开始指向候选 revision 后再判断生效",
    ],
    evidenceView: "rollout",
    evidenceViewLabel: "Rollout",
    noticeMessage: "候选版本已提交，等待 rollout/serving 证据刷新。",
    noticeTone: "success",
    title: "候选版本部署已提交",
  },
  "pause-rollout": {
    actionLabel: "暂停 rollout",
    actionSummary: "暂停请求已进入发布控制面",
    evidenceDescription:
      "这只表示暂停命令已接收，仍要等待 rollout 状态显示暂停后再停止后续操作。",
    evidenceItems: [
      "Rollout 状态刷新为 paused 或等价暂停状态",
      "Serving targets 保持在暂停前的最后稳定分配",
      "Traffic 未继续推进到下一 stage",
    ],
    evidenceView: "rollout",
    evidenceViewLabel: "Rollout",
    noticeMessage: "Rollout 暂停请求已提交，等待状态证据刷新。",
    noticeTone: "success",
    title: "Rollout 暂停已提交",
  },
  "replace-serving-targets": {
    actionLabel: "应用权重",
    actionSummary: "Serving target 替换请求已进入发布控制面",
    evidenceDescription:
      "这只表示权重替换命令已接收，仍要等待 serving generation 和 traffic split 刷新。",
    evidenceItems: [
      "Serving generation 或 updatedAt 刷新",
      "Serving targets 显示新的 revision/weight 分配",
      "Traffic 入口 split 与新的 serving targets 对齐",
    ],
    evidenceView: "serving",
    evidenceViewLabel: "Serving",
    noticeMessage: "Serving targets 已提交，等待 serving/traffic 证据刷新。",
    noticeTone: "success",
    title: "Serving targets 替换已提交",
  },
  "resume-rollout": {
    actionLabel: "恢复 rollout",
    actionSummary: "恢复请求已进入发布控制面",
    evidenceDescription:
      "这只表示恢复命令已接收，仍要等待 rollout 状态重新进入活动推进状态。",
    evidenceItems: [
      "Rollout 状态不再停留在 paused",
      "Current stage 或 updatedAt 继续刷新",
      "Traffic 分配继续按 stage 计划推进",
    ],
    evidenceView: "rollout",
    evidenceViewLabel: "Rollout",
    noticeMessage: "Rollout 恢复请求已提交，等待状态证据刷新。",
    noticeTone: "success",
    title: "Rollout 恢复已提交",
  },
  "rollback-rollout": {
    actionLabel: "回滚 rollout",
    actionSummary: "回滚请求已进入发布控制面",
    evidenceDescription:
      "这只表示回滚命令已接收，不代表 serving 已经回到 baseline。",
    evidenceItems: [
      "Rollout 状态显示回滚或回到基线阶段",
      "Serving targets 与 baseline targets 对齐",
      "Traffic 分配不再指向被回滚的候选 revision",
    ],
    evidenceView: "rollout",
    evidenceViewLabel: "Rollout",
    noticeMessage: "Rollout 回滚请求已提交，等待 baseline 证据刷新。",
    noticeTone: "warning",
    title: "Rollout 回滚已提交",
  },
};

export function buildDeploymentReleaseHandoff(
  input: DeploymentReleaseHandoffInput,
): DeploymentReleaseHandoff {
  const copy = actionCopy[input.action];
  const commandId = input.receipt?.commandId?.trim() || "pending-command";
  const correlationId = input.receipt?.correlationId?.trim() || "pending-correlation";
  const summaryItems = [
    {
      label: "Service",
      value: input.serviceId || "未选择",
    },
    {
      label: "Command",
      value: commandId,
    },
    {
      label: "Correlation",
      value: correlationId,
    },
    {
      label: "当前 serving",
      value: input.activeRevisionId || "暂无",
    },
  ];

  if (input.candidateRevisionId) {
    summaryItems.push({
      label: "候选 revision",
      value: input.candidateRevisionId,
    });
  }

  if (input.deploymentId) {
    summaryItems.push({
      label: "Deployment",
      value: input.deploymentId,
    });
  }

  if (input.rolloutId) {
    summaryItems.push({
      label: "Rollout",
      value: input.rolloutId,
    });
  }

  if (input.rolloutStageLabel) {
    summaryItems.push({
      label: "当前 stage",
      value: input.rolloutStageLabel,
    });
  }

  if (typeof input.targetCount === "number") {
    summaryItems.push({
      label: "Serving targets",
      value: String(input.targetCount),
    });
  }

  if (typeof input.endpointCount === "number") {
    summaryItems.push({
      label: "Traffic endpoints",
      value: String(input.endpointCount),
    });
  }

  return {
    ...copy,
    action: input.action,
    commandId,
    correlationId,
    id: `${input.action}:${commandId}:${correlationId}`,
    pendingLabel: "已提交，不代表已完成",
    summaryItems,
  };
}
