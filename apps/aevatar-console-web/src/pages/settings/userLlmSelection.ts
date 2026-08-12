import { USER_LLM_ROUTE_GATEWAY } from "@/pages/chat/chatConversationConfig";
import { t } from "@/shared/i18n/messages";
import type {
  StudioUserLlmRouteOption,
  StudioUserLlmSettings,
} from "@/shared/studio/models";

const USER_SERVICE_SELECTION_PREFIX = "user-service:";
const GATEWAY_SELECTION_VALUE = "gateway";

export type UserLlmSelectionDraft =
  | { readonly kind: "gateway"; readonly routeValue: string }
  | {
      readonly kind: "nyx_id_user_service";
      readonly userServiceId: string;
      readonly routeValue: string;
    };

export type UserLlmSelectionOption = {
  readonly label: string;
  readonly value: string;
  readonly selection: UserLlmSelectionDraft;
  readonly ready: boolean;
  readonly allowed: boolean;
  readonly defaultModel: string | null;
};

function trimOptional(value: unknown): string | undefined {
  const normalized = String(value ?? "").trim();
  return normalized || undefined;
}

function routeOptionDefaultModelId(
  option: StudioUserLlmRouteOption,
): string | null {
  return (
    trimOptional(option.modelCatalog?.defaultModelId) ??
    trimOptional(option.defaultModel) ??
    null
  );
}

export function encodeUserLlmSelectionValue(
  selection: UserLlmSelectionDraft,
): string {
  return selection.kind === "gateway"
    ? GATEWAY_SELECTION_VALUE
    : `${USER_SERVICE_SELECTION_PREFIX}${encodeURIComponent(selection.userServiceId)}`;
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
        kind: "gateway",
        routeValue: USER_LLM_ROUTE_GATEWAY,
      };
      options.push({
        label:
          option.label.trim() ||
          t("pages.settings.userllmselection.gateway", "Gateway"),
        value: encodeUserLlmSelectionValue(selection),
        selection,
        ready: option.ready,
        allowed: option.allowed,
        defaultModel: null,
      });
      continue;
    }

    if (option.source !== "user_service") {
      continue;
    }

    const userServiceId = trimOptional(option.userServiceId);
    if (!userServiceId || seenUserServiceIds.has(userServiceId)) {
      continue;
    }

    seenUserServiceIds.add(userServiceId);
    const selection: UserLlmSelectionDraft = {
      kind: "nyx_id_user_service",
      userServiceId,
      routeValue: option.routeValue.trim(),
    };
    options.push({
      label: option.label.trim() || option.serviceSlug?.trim() || userServiceId,
      value: encodeUserLlmSelectionValue(selection),
      selection,
      ready: option.ready,
      allowed: option.allowed,
      defaultModel: routeOptionDefaultModelId(option),
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
  settings:
    | (Pick<
        StudioUserLlmSettings,
        "savedRoute" | "savedRouteKind" | "savedUserServiceId"
      > &
        Partial<Pick<StudioUserLlmSettings, "routeOptions">>)
    | undefined,
): UserLlmSelectionDraft | undefined {
  if (!settings) {
    return undefined;
  }

  if (settings.savedRouteKind === "gateway") {
    return {
      kind: "gateway",
      routeValue: USER_LLM_ROUTE_GATEWAY,
    };
  }

  if (settings.savedRouteKind !== "nyx_id_user_service") {
    return undefined;
  }

  const userServiceId = trimOptional(settings.savedUserServiceId);
  if (!userServiceId) {
    return undefined;
  }

  const currentOption = settings.routeOptions?.find(
    (option) =>
      option.source === "user_service" &&
      trimOptional(option.userServiceId) === userServiceId,
  );

  return {
    kind: "nyx_id_user_service",
    userServiceId,
    routeValue:
      trimOptional(currentOption?.routeValue) ?? settings.savedRoute.trim(),
  };
}

export function userLlmSelectionsEqual(
  left: UserLlmSelectionDraft | undefined,
  right: UserLlmSelectionDraft | undefined,
): boolean {
  if (!left || !right) {
    return left === right;
  }

  if (left.kind !== right.kind) {
    return false;
  }

  return left.kind === "gateway" ||
    (right.kind === "nyx_id_user_service" &&
      left.userServiceId === right.userServiceId);
}
