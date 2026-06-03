import type {
  ServiceBindingCatalogSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServicePolicyCatalogSnapshot,
} from "@/shared/models/governance";
import { formatGovernanceTimestamp } from "./GovernanceResultPanels";
import { t } from "@/shared/i18n/messages";

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
      summary: t("pages.governance.governancecommandreceipt.copy", "{value1} 还没有返回更新时间；当前只知道命令已接收。", { value1: catalogLabel }),
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
      ? t("pages.governance.governancecommandreceipt.copy.2", "{value1} 已在 {value2} 之后刷新。", { value1: catalogLabel, value2: formatGovernanceTimestamp(updatedAt) })
      : t("pages.governance.governancecommandreceipt.copy.3", "{value1} 更新时间仍早于本次命令接收时间，暂不能当作已观察。", { value1: catalogLabel }),
    updatedAt,
  };
}
