import type {
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import { formatAevatarStatusLabel } from "@/shared/ui/aevatarWorkbench";
import { t } from "@/shared/i18n/messages";

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
      ? t("pages.governance.governanceaffordance.copy", "This policy has been retired and is a historical fact in the governance directory. It cannot be saved or taken offline again.")
      : t("pages.governance.governanceaffordance.copy.2", "This policy is still in the activation directory, and you can modify the rules or submit an offline request."),
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
      ? t("pages.governance.governanceaffordance.copy.3", "This binding has been retired and is a historical fact in the governance directory. It cannot be saved or taken offline again.")
      : t("pages.governance.governanceaffordance.copy.4", "This binding is still in the activation directory, and you can modify the target, mount strategy, or submit an offline request."),
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
      ? t("pages.governance.governanceaffordance.endpoint.catalog", "The exposed state comes from the current endpoint catalog; saving the entry commits the entire catalog update.")
      : t("pages.governance.governanceaffordance.endpoint.catalog.2", "Currently, the endpoint catalog cannot be read. You can only view the entry facts and cannot submit modifications."),
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
      label: currentExposure === "public" ? t("pages.governance.governanceaffordance.copy.5", "Published") : t("pages.governance.governanceaffordance.copy.6", "Public endpoints"),
      nextExposureKind: "public",
      reason: t("pages.governance.governanceaffordance.endpoint.catalog.3", "The endpoint catalog needs to be read before exposure status updates can be confirmed or submitted."),
    };
  }

  if (currentExposure === "public") {
    return {
      disabled: true,
      label: t("pages.governance.governanceaffordance.copy.7", "Published"),
      nextExposureKind: "public",
      reason: t("pages.governance.governanceaffordance.endpoint.catalog.public", "The current endpoint catalog has observed the public status and no longer displays repeated public switching."),
    };
  }

  return {
    disabled: false,
    label: t("pages.governance.governanceaffordance.copy.8", "Public endpoints"),
    nextExposureKind: "public",
    reason: t("pages.governance.governanceaffordance.endpoint.catalog.public.2", "After submission, you need to wait for the endpoint catalog to be refreshed again to indicate that the public status has been observed."),
  };
}
