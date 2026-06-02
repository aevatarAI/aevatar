import type {
  ServiceBindingCatalogSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServicePolicyCatalogSnapshot,
} from "@/shared/models/governance";
import { formatGovernanceTimestamp } from "./GovernanceResultPanels";

export type GovernanceCatalogKind = "bindings" | "endpoints" | "policies";

export type GovernanceCommandReceipt = {
  readonly acceptedAt: string;
  readonly catalogKind: GovernanceCatalogKind;
  readonly commandLabel: string;
  readonly targetId: string;
};

export type GovernanceReceiptObservation = {
  readonly catalogLabel: string;
  readonly observed: boolean;
  readonly summary: string;
  readonly updatedAt?: string;
};

type GovernanceCatalogSnapshot =
  | ServiceBindingCatalogSnapshot
  | ServiceEndpointCatalogSnapshot
  | ServicePolicyCatalogSnapshot
  | null
  | undefined;

const catalogLabels: Record<GovernanceCatalogKind, string> = {
  bindings: "Binding catalog",
  endpoints: "Endpoint catalog",
  policies: "Policy catalog",
};

export function buildGovernanceCommandReceipt(input: {
  readonly catalogKind: GovernanceCatalogKind;
  readonly commandLabel: string;
  readonly targetId: string;
}): GovernanceCommandReceipt {
  return {
    ...input,
    acceptedAt: new Date().toISOString(),
  };
}

export function observeGovernanceReceipt(
  receipt: GovernanceCommandReceipt,
  catalog: GovernanceCatalogSnapshot,
): GovernanceReceiptObservation {
  const catalogLabel = catalogLabels[receipt.catalogKind];
  const updatedAt = catalog?.updatedAt?.trim() || undefined;
  if (!updatedAt) {
    return {
      catalogLabel,
      observed: false,
      summary: `${catalogLabel} 还没有返回更新时间；当前只知道命令已接收。`,
    };
  }

  const acceptedTime = Date.parse(receipt.acceptedAt);
  const observedTime = Date.parse(updatedAt);
  const observed = Number.isFinite(acceptedTime) && Number.isFinite(observedTime)
    ? observedTime >= acceptedTime
    : false;

  return {
    catalogLabel,
    observed,
    summary: observed
      ? `${catalogLabel} 已在 ${formatGovernanceTimestamp(updatedAt)} 之后刷新。`
      : `${catalogLabel} 更新时间仍早于本次命令接收时间，暂不能当作已观察。`,
    updatedAt,
  };
}
