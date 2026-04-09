import { isTeamFirstEnabled } from "@/shared/config/consoleFeatures";

export const LEGACY_CONSOLE_HOME_ROUTE = "/scopes/overview";
export const TEAMS_HOME_ROUTE = "/teams";

export function getConsoleHomeRoute(): string {
  return isTeamFirstEnabled() ? TEAMS_HOME_ROUTE : LEGACY_CONSOLE_HOME_ROUTE;
}

export const CONSOLE_HOME_ROUTE = getConsoleHomeRoute();
