import type {
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import { formatAevatarStatusLabel } from "@/shared/ui/aevatarWorkbench";

export type GovernanceFactKind = "binding" | "endpoint" | "policy";

export type GovernanceEditMode = "readonly" | "writable";

export type GovernanceFactAffordance = {
  readonly editMode: GovernanceEditMode;
  readonly status: string;
  readonly statusLabel: string;
  readonly summary: string;
};

export type EndpointExposureAction = {
  readonly disabled: boolean;
  readonly label: string;
  readonly nextExposureKind: string;
  readonly reason: string;
};

export function normalizeGovernanceStatus(value: string | null | undefined): string {
  return value?.trim().toLowerCase() || "";
}

export function resolvePolicyAffordance(
  policy: ServicePolicySnapshot,
): GovernanceFactAffordance {
  const status = policy.retired ? "retired" : "active";
  return {
    editMode: policy.retired ? "readonly" : "writable",
    status,
    statusLabel: formatAevatarStatusLabel(status),
    summary: policy.retired
      ? "这条策略已经退役，是治理目录中的历史事实，不能继续保存或再次下线。"
      : "这条策略仍处于激活目录中，可以修改规则或提交下线请求。",
  };
}

export function resolveBindingAffordance(
  binding: ServiceBindingSnapshot,
): GovernanceFactAffordance {
  const status = binding.retired ? "retired" : "active";
  return {
    editMode: binding.retired ? "readonly" : "writable",
    status,
    statusLabel: formatAevatarStatusLabel(status),
    summary: binding.retired
      ? "这条绑定已经退役，是治理目录中的历史事实，不能继续保存或再次下线。"
      : "这条绑定仍处于激活目录中，可以修改目标、挂载策略或提交下线请求。",
  };
}

export function resolveEndpointAffordance(
  endpoint: ServiceEndpointExposureSnapshot,
  catalog: ServiceEndpointCatalogSnapshot | null,
): GovernanceFactAffordance {
  const status = normalizeGovernanceStatus(endpoint.exposureKind) || "internal";
  const hasCatalog = Boolean(catalog);
  return {
    editMode: hasCatalog ? "writable" : "readonly",
    status,
    statusLabel: formatAevatarStatusLabel(status),
    summary: hasCatalog
      ? "暴露状态来自当前 endpoint catalog；保存入口会提交整份目录更新。"
      : "当前无法读取 endpoint catalog，只能查看这条入口事实，不能提交修改。",
  };
}

export function resolveEndpointExposureAction(
  endpoint: ServiceEndpointExposureSnapshot,
  catalog: ServiceEndpointCatalogSnapshot | null,
): EndpointExposureAction | null {
  const currentExposure = normalizeGovernanceStatus(endpoint.exposureKind) || "internal";
  if (!catalog) {
    return {
      disabled: true,
      label: currentExposure === "public" ? "已公开" : "公开入口",
      nextExposureKind: "public",
      reason: "需要先读取 endpoint catalog，才能确认或提交暴露状态更新。",
    };
  }

  if (currentExposure === "public") {
    return {
      disabled: true,
      label: "已公开",
      nextExposureKind: "public",
      reason: "当前 endpoint catalog 已经观察到 public 状态，不再显示重复的公开切换。",
    };
  }

  return {
    disabled: false,
    label: "公开入口",
    nextExposureKind: "public",
    reason: "提交后需要等待 endpoint catalog 再次刷新，才表示 public 状态已被观察到。",
  };
}
