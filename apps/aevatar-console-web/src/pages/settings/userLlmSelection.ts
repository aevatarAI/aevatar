import { USER_LLM_ROUTE_GATEWAY } from "@/pages/chat/chatConversationConfig";
import { t } from "@/shared/i18n/messages";
import type {
  StudioLlmSelection,
  StudioSelectedLlmModelSelection,
  StudioUserLlmRouteOption,
  StudioUserLlmSettings,
} from "@/shared/studio/models";

const USER_SERVICE_SELECTION_PREFIX = "user-service:";
const GATEWAY_SELECTION_VALUE = "gateway";

export type UserLlmSelectionDraft = Exclude<
  StudioLlmSelection,
  { routeKind: "unspecified" }
>;

export type UserLlmSelectionOption = {
  readonly label: string;
  readonly value: string;
  readonly selection: UserLlmSelectionDraft;
  readonly ready: boolean;
  readonly allowed: boolean;
  readonly modelCatalog: StudioUserLlmRouteOption["modelCatalog"];
};

function trimOptional(value: unknown): string | undefined {
  const normalized = String(value ?? "").trim();
  return normalized || undefined;
}

function providerDefault(): StudioSelectedLlmModelSelection {
  return { kind: "provider_default" };
}

export function encodeUserLlmSelectionValue(
  selection: UserLlmSelectionDraft,
): string {
  return selection.routeKind === "gateway"
    ? GATEWAY_SELECTION_VALUE
    : `${USER_SERVICE_SELECTION_PREFIX}${encodeURIComponent(selection.nyxIdUserServiceId)}`;
}

export function buildUserLlmSelectionOptions(
  routeOptions: readonly StudioUserLlmRouteOption[],
): UserLlmSelectionOption[] {
  const options: UserLlmSelectionOption[] = [];
  let hasGateway = false;
  const seenUserServiceIds = new Set<string>();

  for (const option of routeOptions) {
    if (option.source === "gateway_provider") {
      if (hasGateway) {
        continue;
      }

      hasGateway = true;
      const selection: UserLlmSelectionDraft = {
        routeKind: "gateway",
        routeValue: USER_LLM_ROUTE_GATEWAY,
        modelSelection: providerDefault(),
      };
      options.push({
        label:
          option.label.trim() ||
          t("pages.settings.userllmselection.gateway", "Gateway"),
        value: encodeUserLlmSelectionValue(selection),
        selection,
        ready: option.ready,
        allowed: option.allowed,
        modelCatalog: option.modelCatalog,
      });
      continue;
    }

    if (option.source !== "user_service") {
      continue;
    }

    const userServiceId = trimOptional(option.userServiceId);
    const serviceSlug = trimOptional(option.serviceSlug);
    const routeValue = trimOptional(option.routeValue);
    if (
      !userServiceId ||
      !serviceSlug ||
      !routeValue ||
      seenUserServiceIds.has(userServiceId)
    ) {
      continue;
    }

    seenUserServiceIds.add(userServiceId);
    const selection: UserLlmSelectionDraft = {
      routeKind: "nyx_id_user_service",
      routeValue,
      nyxIdUserServiceId: userServiceId,
      serviceSlugSnapshot: serviceSlug,
      modelSelection: providerDefault(),
    };
    options.push({
      label: option.label.trim() || serviceSlug || userServiceId,
      value: encodeUserLlmSelectionValue(selection),
      selection,
      ready: option.ready,
      allowed: option.allowed,
      modelCatalog: option.modelCatalog,
    });
  }

  return options;
}

export function decodeUserLlmSelectionValue(
  value: string,
  options: readonly UserLlmSelectionOption[],
): UserLlmSelectionDraft | undefined {
  return options.find((option) => option.value === value)?.selection;
}

export function resolveSavedUserLlmSelection(
  settings: Pick<StudioUserLlmSettings, "savedSelection"> | undefined,
): UserLlmSelectionDraft | undefined {
  const selection = settings?.savedSelection;
  if (!selection || selection.routeKind === "unspecified") {
    return undefined;
  }

  return cloneUserLlmSelection(selection);
}

export function cloneUserLlmSelection(
  selection: UserLlmSelectionDraft,
): UserLlmSelectionDraft {
  return selection.routeKind === "gateway"
    ? {
        routeKind: selection.routeKind,
        routeValue: selection.routeValue,
        modelSelection: { ...selection.modelSelection },
      }
    : {
        routeKind: selection.routeKind,
        routeValue: selection.routeValue,
        nyxIdUserServiceId: selection.nyxIdUserServiceId,
        serviceSlugSnapshot: selection.serviceSlugSnapshot,
        modelSelection: { ...selection.modelSelection },
      };
}

export function userLlmSelectionsEqual(
  left: StudioLlmSelection | null | undefined,
  right: StudioLlmSelection | null | undefined,
): boolean {
  if (!left || !right) {
    return left === right;
  }

  if (left.routeKind !== right.routeKind) {
    return false;
  }
  if (left.modelSelection.kind !== right.modelSelection.kind) {
    return false;
  }
  if (
    left.modelSelection.kind === "explicit_model" &&
    (right.modelSelection.kind !== "explicit_model" ||
      left.modelSelection.modelId !== right.modelSelection.modelId)
  ) {
    return false;
  }

  if (left.routeKind === "unspecified" || right.routeKind === "unspecified") {
    return left.routeKind === right.routeKind;
  }
  if (left.routeValue !== right.routeValue) {
    return false;
  }
  if (left.routeKind === "gateway" || right.routeKind === "gateway") {
    return left.routeKind === right.routeKind;
  }

  return (
    left.nyxIdUserServiceId === right.nyxIdUserServiceId &&
    left.serviceSlugSnapshot === right.serviceSlugSnapshot
  );
}
