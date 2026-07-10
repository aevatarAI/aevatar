import React, { useEffect } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildTeamWorkspaceRoute,
  readScopeQueryDraft,
} from "./components/scopeQuery";

function readLegacyOverviewExtras(
  search = typeof window === "undefined" ? "" : window.location.search,
): Record<string, string> {
  const params = new URLSearchParams(search);
  const extras: Record<string, string> = {};

  params.forEach((value, key) => {
    if (key === "scopeId") {
      return;
    }

    const normalizedValue = value.trim();
    if (normalizedValue) {
      extras[key] = normalizedValue;
    }
  });

  return extras;
}

const ScopeOverviewPage: React.FC = () => {
  useEffect(() => {
    const pathname = typeof window === "undefined" ? "" : window.location.pathname;
    const search = typeof window === "undefined" ? "" : window.location.search;

    const scopeDraft = readScopeQueryDraft(search, pathname);
    history.replace(buildTeamWorkspaceRoute(scopeDraft.scopeId, readLegacyOverviewExtras(search)));
  }, []);

  return null;
};

export default ScopeOverviewPage;
