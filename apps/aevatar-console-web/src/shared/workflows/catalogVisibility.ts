import type { WorkflowCatalogItem } from "@/shared/models/runtime/catalog";
import { t } from "@/shared/i18n/messages";

export type WorkflowCatalogOption = {
  label: string;
  value: string;
};

function trimOptional(value?: string | null): string {
  return value?.trim() ?? "";
}

export function listVisibleWorkflowCatalogItems(
  items: readonly WorkflowCatalogItem[]
): WorkflowCatalogItem[] {
  return items.filter((item) => item.showInLibrary);
}

export function findWorkflowCatalogItem(
  items: readonly WorkflowCatalogItem[],
  workflowName?: string | null
): WorkflowCatalogItem | undefined {
  const normalized = trimOptional(workflowName);
  if (!normalized) {
    return undefined;
  }

  return items.find((item) => item.name === normalized);
}

export function resolveWorkflowCatalogSelection(
  items: readonly WorkflowCatalogItem[],
  currentWorkflowName?: string | null
): string {
  const normalized = trimOptional(currentWorkflowName);
  if (normalized && items.some((item) => item.name === normalized)) {
    return normalized;
  }

  return listVisibleWorkflowCatalogItems(items)[0]?.name ?? "";
}

export function buildWorkflowCatalogOptions(
  items: readonly WorkflowCatalogItem[],
  currentWorkflowName?: string | null
): WorkflowCatalogOption[] {
  const visibleItems = listVisibleWorkflowCatalogItems(items);
  const options = visibleItems.map((item) => ({
    label: t("shared.workflows.catalogvisibility.copy", "{value1} · {value2}", { value1: item.name, value2: item.groupLabel }),
    value: item.name,
  }));
  const normalizedCurrentWorkflowName = trimOptional(currentWorkflowName);
  const currentItem = findWorkflowCatalogItem(items, currentWorkflowName);

  if (!currentItem) {
    if (!normalizedCurrentWorkflowName) {
      return options;
    }

    return [
      {
        label: t("shared.workflows.catalogvisibility.unavailable.in.catalog", "{name} · Unavailable in catalog", {
          name: normalizedCurrentWorkflowName,
        }),
        value: normalizedCurrentWorkflowName,
      },
      ...options,
    ];
  }

  if (visibleItems.some((item) => item.name === currentItem.name)) {
    return options;
  }

  return [
    {
      label: t("shared.workflows.catalogvisibility.hidden.from.library", "{name} · {group} · Hidden from library", {
        group: currentItem.groupLabel,
        name: currentItem.name,
      }),
      value: currentItem.name,
    },
    ...options,
  ];
}
