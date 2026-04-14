import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { studioApi } from "@/shared/studio/api";
import { deriveTeamRuntimeLens, selectTeamCompareRuns } from "./teamRuntimeLens";

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

type UseTeamRuntimeLensOptions = {
  includeCatalogSignals?: boolean;
  preferredRunId?: string;
  preferredServiceId?: string;
};

export function useTeamRuntimeLens(
  scopeId: string,
  options?: UseTeamRuntimeLensOptions,
) {
  const normalizedScopeId = scopeId.trim();
  const includeCatalogSignals = options?.includeCatalogSignals ?? true;
  const preferredServiceId = options?.preferredServiceId?.trim() ?? "";
  const preferredRunId = options?.preferredRunId?.trim() ?? "";

  const bindingQuery = useQuery({
    enabled: normalizedScopeId.length > 0,
    queryKey: ["teams", "binding", normalizedScopeId],
    queryFn: () => studioApi.getScopeBinding(normalizedScopeId),
    retry: false,
  });
  const workflowsQuery = useQuery({
    enabled: normalizedScopeId.length > 0 && includeCatalogSignals,
    queryKey: ["teams", "workflows", normalizedScopeId],
    queryFn: () => scopesApi.listWorkflows(normalizedScopeId),
    retry: false,
  });
  const scriptsQuery = useQuery({
    enabled: normalizedScopeId.length > 0 && includeCatalogSignals,
    queryKey: ["teams", "scripts", normalizedScopeId],
    queryFn: () => scopesApi.listScripts(normalizedScopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: normalizedScopeId.length > 0,
    queryKey: ["teams", "services", normalizedScopeId],
    queryFn: () =>
      servicesApi.listServices({
        tenantId: normalizedScopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
      }),
    retry: false,
  });
  const actorsQuery = useQuery({
    enabled: normalizedScopeId.length > 0,
    queryKey: ["teams", "actors", normalizedScopeId],
    queryFn: () => runtimeGAgentApi.listActors(normalizedScopeId),
    retry: false,
  });

  const serviceId =
    servicesQuery.data?.find((service) => service.serviceId === preferredServiceId)
      ?.serviceId ||
    servicesQuery.data?.find(
      (service) => service.serviceId === bindingQuery.data?.serviceId,
    )?.serviceId ||
    servicesQuery.data?.[0]?.serviceId ||
    "";
  const runsQuery = useQuery({
    enabled: normalizedScopeId.length > 0 && serviceId.length > 0,
    queryKey: ["teams", "runs", normalizedScopeId, serviceId],
    queryFn: () =>
      scopeRuntimeApi.listServiceRuns(normalizedScopeId, serviceId, {
        take: 12,
      }),
    retry: false,
  });

  const compareRuns = useMemo(
    () =>
      selectTeamCompareRuns(runsQuery.data?.runs ?? [], {
        preferredRunId,
      }),
    [preferredRunId, runsQuery.data?.runs],
  );
  const currentRunId = compareRuns.currentRun?.runId?.trim() || "";
  const baselineRunId = compareRuns.baselineRun?.runId?.trim() || "";

  const focusActorId =
    compareRuns.currentRun?.actorId?.trim() ||
    bindingQuery.data?.primaryActorId?.trim() ||
    actorsQuery.data?.flatMap((group) => group.actorIds)[0] ||
    "";

  const currentRunAuditQuery = useQuery({
    enabled:
      normalizedScopeId.length > 0 &&
      serviceId.length > 0 &&
      currentRunId.length > 0,
    queryKey: [
      "teams",
      "run-audit",
      normalizedScopeId,
      serviceId,
      currentRunId,
      compareRuns.currentRun?.actorId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRunAudit(
        normalizedScopeId,
        serviceId,
        currentRunId,
        {
          actorId: compareRuns.currentRun?.actorId || undefined,
        },
      ),
    retry: false,
  });
  const baselineRunAuditQuery = useQuery({
    enabled:
      normalizedScopeId.length > 0 &&
      serviceId.length > 0 &&
      baselineRunId.length > 0,
    queryKey: [
      "teams",
      "baseline-run-audit",
      normalizedScopeId,
      serviceId,
      baselineRunId,
      compareRuns.baselineRun?.actorId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRunAudit(
        normalizedScopeId,
        serviceId,
        baselineRunId,
        {
          actorId: compareRuns.baselineRun?.actorId || undefined,
        },
      ),
    retry: false,
  });
  const actorGraphQuery = useQuery({
    enabled: focusActorId.length > 0,
    queryKey: ["teams", "actor-graph", focusActorId],
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(focusActorId, {
        depth: 2,
        direction: "Both",
        take: 24,
      }),
    retry: false,
  });

  const lens = useMemo(
    () =>
      deriveTeamRuntimeLens({
        scopeId: normalizedScopeId,
        binding: bindingQuery.data ?? null,
        services: servicesQuery.data ?? [],
        actors: actorsQuery.data ?? [],
        runs: runsQuery.data?.runs ?? [],
        actorGraph: actorGraphQuery.data ?? null,
        currentRunAudit: currentRunAuditQuery.data ?? null,
        baselineRunAudit: baselineRunAuditQuery.data ?? null,
        workflowCount: workflowsQuery.data?.length ?? 0,
        scriptCount: scriptsQuery.data?.length ?? 0,
      }),
    [
      actorGraphQuery.data,
      actorsQuery.data,
      baselineRunAuditQuery.data,
      bindingQuery.data,
      baselineRunId,
      currentRunAuditQuery.data,
      currentRunId,
      normalizedScopeId,
      runsQuery.data?.runs,
      scriptsQuery.data?.length,
      servicesQuery.data,
      workflowsQuery.data?.length,
    ],
  );

  return {
    actorGraphQuery,
    actorsQuery,
    baselineRunAuditQuery,
    bindingQuery,
    currentRunAuditQuery,
    lens,
    runsQuery,
    scriptsQuery,
    servicesQuery,
    workflowsQuery,
  };
}
