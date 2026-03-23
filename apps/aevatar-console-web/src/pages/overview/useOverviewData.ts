import { useQuery } from "@tanstack/react-query";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { buildObservabilityTargets } from "@/shared/observability/observabilityLinks";
import { loadConsolePreferences } from "@/shared/preferences/consolePreferences";
import { listVisibleWorkflowCatalogItems } from "@/shared/workflows/catalogVisibility";

export type ConsoleProfileItem = {
  preferredWorkflow: string;
  observability: string;
};

export type ObservabilityOverviewItem = {
  id: string;
  label: string;
  description: string;
  status: "configured" | "missing";
  homeUrl: string;
};

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/, "");
}

export function useOverviewData() {
  const preferences = useMemo(() => loadConsolePreferences(), []);
  const [deferredDetailsEnabled, setDeferredDetailsEnabled] = useState(false);

  useEffect(() => {
    const requestIdle = window.requestIdleCallback?.bind(window);
    if (requestIdle) {
      const handle = requestIdle(() => setDeferredDetailsEnabled(true));
      return () => window.cancelIdleCallback?.(handle);
    }

    const handle = window.setTimeout(() => setDeferredDetailsEnabled(true), 0);
    return () => window.clearTimeout(handle);
  }, []);

  const workflowsQuery = useQuery({
    queryKey: ["overview-workflows"],
    queryFn: () => runtimeCatalogApi.listWorkflowNames(),
    staleTime: 60_000,
  });
  const catalogQuery = useQuery({
    queryKey: ["overview-catalog"],
    queryFn: () => runtimeCatalogApi.listWorkflowCatalog(),
    staleTime: 60_000,
  });
  const agentsQuery = useQuery({
    queryKey: ["overview-agents"],
    queryFn: () => runtimeQueryApi.listAgents(),
    enabled: deferredDetailsEnabled,
    staleTime: 30_000,
  });
  const capabilitiesQuery = useQuery({
    queryKey: ["overview-capabilities"],
    queryFn: () => runtimeQueryApi.getCapabilities(),
    enabled: deferredDetailsEnabled,
    staleTime: 30_000,
  });

  const visibleCatalogItems = useMemo(
    () => listVisibleWorkflowCatalogItems(catalogQuery.data ?? []),
    [catalogQuery.data]
  );

  const humanFocusedWorkflows = useMemo(
    () =>
      visibleCatalogItems
        .filter((item) =>
          item.primitives.some((primitive) =>
            ["human_input", "human_approval", "wait_signal"].includes(primitive)
          )
        )
        .slice(0, 6),
    [visibleCatalogItems]
  );

  const capabilityPrimitiveCategorySummary = useMemo(() => {
    const categoryCounts = new Map<string, number>();

    for (const primitive of capabilitiesQuery.data?.primitives ?? []) {
      categoryCounts.set(
        primitive.category,
        (categoryCounts.get(primitive.category) ?? 0) + 1
      );
    }

    return Array.from(categoryCounts.entries())
      .sort((left, right) => right[1] - left[1])
      .slice(0, 3)
      .map(([category, count]) => `${count} ${category}`);
  }, [capabilitiesQuery.data]);

  const capabilityConnectorEnabledCount = useMemo(
    () =>
      (capabilitiesQuery.data?.connectors ?? []).filter(
        (connector) => connector.enabled
      ).length,
    [capabilitiesQuery.data]
  );

  const capabilityConnectorSummary = useMemo(() => {
    const connectors = capabilitiesQuery.data?.connectors ?? [];

    if (connectors.length === 0) {
      return "No connectors exposed";
    }

    return `${capabilityConnectorEnabledCount}/${connectors.length} enabled`;
  }, [capabilitiesQuery.data, capabilityConnectorEnabledCount]);

  const capabilityWorkflowSourceSummary = useMemo(() => {
    const workflows = capabilitiesQuery.data?.workflows ?? [];
    const sourceCounts = new Map<string, number>();

    for (const workflow of workflows) {
      const source = workflow.source || "runtime";
      sourceCounts.set(source, (sourceCounts.get(source) ?? 0) + 1);
    }

    return Array.from(sourceCounts.entries())
      .sort((left, right) => right[1] - left[1])
      .slice(0, 3)
      .map(([source, count]) => `${count} ${source}`);
  }, [capabilitiesQuery.data]);

  const liveActors = useMemo(
    () => (agentsQuery.data ?? []).slice(0, 6),
    [agentsQuery.data]
  );

  const grafanaBaseUrl = normalizeBaseUrl(preferences.grafanaBaseUrl);

  const profileData = useMemo<ConsoleProfileItem>(
    () => ({
      preferredWorkflow: preferences.preferredWorkflow,
      observability: grafanaBaseUrl ? "Configured" : "Not configured",
    }),
    [grafanaBaseUrl, preferences.preferredWorkflow]
  );

  const observabilityTargets = useMemo<ObservabilityOverviewItem[]>(
    () =>
      buildObservabilityTargets(preferences, {
        workflow: preferences.preferredWorkflow,
        actorId: "",
        commandId: "",
        runId: "",
        stepId: "",
      }).map((target) => ({
        id: target.id,
        label: target.label,
        description: target.description,
        status: target.status,
        homeUrl: target.homeUrl,
      })),
    [preferences]
  );

  const configuredObservabilityCount = useMemo(
    () =>
      observabilityTargets.filter((target) => target.status === "configured")
        .length,
    [observabilityTargets]
  );

  return {
    agentsQuery,
    capabilitiesQuery,
    catalogQuery,
    configuredObservabilityCount,
    grafanaBaseUrl,
    humanFocusedWorkflows,
    liveActors,
    observabilityTargets,
    preferences,
    profileData,
    visibleCatalogItems,
    workflowsQuery,
    capabilityConnectorSummary,
    capabilityPrimitiveCategorySummary,
    capabilityWorkflowSourceSummary,
  };
}
