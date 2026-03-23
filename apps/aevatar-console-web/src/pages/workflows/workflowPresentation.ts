import type {
  WorkflowCatalogItem,
  WorkflowCatalogStep,
} from "@/shared/models/runtime/catalog";

export type WorkflowLibraryFilter = {
  keyword: string;
  groups: string[];
  sources: string[];
  llmRequirement: "all" | "required" | "optional";
  primitives: string[];
};

export type WorkflowLibraryRow = WorkflowCatalogItem & {
  llmStatus: "processing" | "success";
  primitiveSummary: string;
  searchText: string;
};

export type WorkflowStepRow = WorkflowCatalogStep & {
  key: string;
  parameterCount: number;
  branchCount: number;
  childCount: number;
};

export const defaultWorkflowLibraryFilter: WorkflowLibraryFilter = {
  keyword: "",
  groups: [],
  sources: [],
  llmRequirement: "all",
  primitives: [],
};

export function buildWorkflowRows(
  items: WorkflowCatalogItem[]
): WorkflowLibraryRow[] {
  return items.map((item) => ({
    ...item,
    llmStatus: item.requiresLlmProvider ? "processing" : "success",
    primitiveSummary:
      item.primitives.length > 0 ? item.primitives.join(", ") : "n/a",
    searchText: [
      item.name,
      item.description,
      item.group,
      item.groupLabel,
      item.category,
      item.source,
      item.sourceLabel,
      item.primitives.join(" "),
    ]
      .join(" ")
      .toLowerCase(),
  }));
}

export function filterWorkflowRows(
  rows: WorkflowLibraryRow[],
  filters: WorkflowLibraryFilter
): WorkflowLibraryRow[] {
  const keyword = filters.keyword.trim().toLowerCase();

  return rows.filter((row) => {
    if (filters.groups.length > 0 && !filters.groups.includes(row.groupLabel)) {
      return false;
    }

    if (
      filters.sources.length > 0 &&
      !filters.sources.includes(row.sourceLabel)
    ) {
      return false;
    }

    if (filters.llmRequirement === "required" && !row.requiresLlmProvider) {
      return false;
    }

    if (filters.llmRequirement === "optional" && row.requiresLlmProvider) {
      return false;
    }

    if (
      filters.primitives.length > 0 &&
      !filters.primitives.every((primitive) =>
        row.primitives.includes(primitive)
      )
    ) {
      return false;
    }

    if (!keyword) {
      return true;
    }

    return row.searchText.includes(keyword);
  });
}

export function buildStepRows(steps: WorkflowCatalogStep[]): WorkflowStepRow[] {
  return steps.map((step) => ({
    ...step,
    key: step.id,
    parameterCount: Object.keys(step.parameters).length,
    branchCount: Object.keys(step.branches).length,
    childCount: step.children.length,
  }));
}

export function buildStringOptions(
  values: string[]
): Array<{ label: string; value: string }> {
  return Array.from(new Set(values.filter(Boolean)))
    .sort((left, right) => left.localeCompare(right))
    .map((value) => ({ label: value, value }));
}

export function findWorkflowStepTargetRole(
  rows: WorkflowStepRow[],
  stepId: string
): string {
  return rows.find((row) => row.id === stepId)?.targetRole ?? "";
}
