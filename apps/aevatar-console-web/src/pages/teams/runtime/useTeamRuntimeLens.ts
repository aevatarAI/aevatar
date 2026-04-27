import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { studioApi } from "@/shared/studio/api";
import { getScopeServiceCurrentRevision } from "@/shared/models/runtime/scopeServices";
import { deriveTeamRuntimeLens, selectTeamCompareRuns } from "./teamRuntimeLens";

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

type UseTeamRuntimeLensOptions = {
  graphDepth?: number;
  includeCatalogSignals?: boolean;
  preferredActorId?: string;
  preferredMemberId?: string;
  preferredRunId?: string;
  preferredServiceId?: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function compareServices(
  left: { displayName?: string | null; serviceId: string },
  right: { displayName?: string | null; serviceId: string },
): number {
  const leftDisplayName = trimOptional(left.displayName);
  const rightDisplayName = trimOptional(right.displayName);
  if (leftDisplayName && rightDisplayName && leftDisplayName !== rightDisplayName) {
    return leftDisplayName.localeCompare(rightDisplayName);
  }

  if (leftDisplayName && !rightDisplayName) {
    return -1;
  }

  if (!leftDisplayName && rightDisplayName) {
    return 1;
  }

  return trimOptional(left.serviceId).localeCompare(trimOptional(right.serviceId));
}

export function useTeamRuntimeLens(
  scopeId: string,
  options?: UseTeamRuntimeLensOptions,
) {
  const normalizedScopeId = scopeId.trim();
  const graphDepth = Math.max(1, Math.min(options?.graphDepth ?? 2, 4));
  const includeCatalogSignals = options?.includeCatalogSignals ?? true;
  const preferredActorId = options?.preferredActorId?.trim() ?? "";
  const preferredMemberId = options?.preferredMemberId?.trim() ?? "";
  const preferredServiceId = options?.preferredServiceId?.trim() ?? "";
  const preferredRunId = options?.preferredRunId?.trim() ?? "";

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
  const membersQuery = useQuery({
    enabled: normalizedScopeId.length > 0 && preferredMemberId.length > 0,
    queryKey: ["teams", "members", normalizedScopeId],
    queryFn: () => studioApi.listMembers(normalizedScopeId),
    retry: false,
  });

  const services = useMemo(
    () => [...(servicesQuery.data ?? [])].sort(compareServices),
    [servicesQuery.data],
  );
  const preferredMemberSummary = useMemo(
    () =>
      preferredMemberId.length > 0
        ? membersQuery.data?.members.find(
            (member) => trimOptional(member.memberId) === preferredMemberId,
          ) ?? null
        : null,
    [membersQuery.data?.members, preferredMemberId],
  );
  const preferredServiceHint =
    preferredServiceId || trimOptional(preferredMemberSummary?.publishedServiceId);
  const serviceId = preferredServiceHint
    ? services.find((service) => service.serviceId === preferredServiceHint)?.serviceId ||
      preferredServiceHint
    : preferredMemberId.length > 0
      ? ""
      : services[0]?.serviceId || "";
  const serviceRevisionsQuery = useQuery({
    enabled: normalizedScopeId.length > 0 && serviceId.length > 0,
    queryKey: ["teams", "service-revisions", normalizedScopeId, serviceId],
    queryFn: () => scopeRuntimeApi.getServiceRevisions(normalizedScopeId, serviceId),
    retry: false,
  });
  const runsQuery = useQuery({
    enabled:
      normalizedScopeId.length > 0 &&
      (serviceId.length > 0 || preferredMemberId.length > 0),
    queryKey: [
      "teams",
      "runs",
      normalizedScopeId,
      preferredMemberId || null,
      serviceId || null,
    ],
    queryFn: () =>
      preferredMemberId.length > 0
        ? scopeRuntimeApi.listMemberRuns(normalizedScopeId, preferredMemberId, {
            take: 12,
          })
        : scopeRuntimeApi.listServiceRuns(normalizedScopeId, serviceId, {
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
  const activeServiceRevision = useMemo(
    () => getScopeServiceCurrentRevision(serviceRevisionsQuery.data ?? null),
    [serviceRevisionsQuery.data],
  );

  const focusActorId =
    preferredActorId ||
    compareRuns.currentRun?.actorId?.trim() ||
    activeServiceRevision?.primaryActorId?.trim() ||
    serviceRevisionsQuery.data?.primaryActorId?.trim() ||
    services.find((service) => service.serviceId === serviceId)?.primaryActorId?.trim() ||
    (preferredMemberId.length > 0
      ? ""
      : actorsQuery.data?.flatMap((group) => group.actorIds)[0] || "") ||
    "";

  const currentRunAuditQuery = useQuery({
    enabled:
      normalizedScopeId.length > 0 &&
      (serviceId.length > 0 || preferredMemberId.length > 0) &&
      currentRunId.length > 0,
    queryKey: [
      "teams",
      "run-audit",
      normalizedScopeId,
      preferredMemberId || null,
      serviceId,
      currentRunId,
      compareRuns.currentRun?.actorId,
    ],
    queryFn: () =>
      preferredMemberId.length > 0
        ? scopeRuntimeApi.getMemberRunAudit(
            normalizedScopeId,
            preferredMemberId,
            currentRunId,
            {
              actorId: compareRuns.currentRun?.actorId || undefined,
            },
          )
        : scopeRuntimeApi.getServiceRunAudit(
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
      (serviceId.length > 0 || preferredMemberId.length > 0) &&
      baselineRunId.length > 0,
    queryKey: [
      "teams",
      "baseline-run-audit",
      normalizedScopeId,
      preferredMemberId || null,
      serviceId,
      baselineRunId,
      compareRuns.baselineRun?.actorId,
    ],
    queryFn: () =>
      preferredMemberId.length > 0
        ? scopeRuntimeApi.getMemberRunAudit(
            normalizedScopeId,
            preferredMemberId,
            baselineRunId,
            {
              actorId: compareRuns.baselineRun?.actorId || undefined,
            },
          )
        : scopeRuntimeApi.getServiceRunAudit(
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
    queryKey: ["teams", "actor-graph", focusActorId, graphDepth],
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(focusActorId, {
        depth: graphDepth,
        direction: "Both",
        take: 24,
      }),
    retry: false,
  });

  const lens = useMemo(
    () =>
      deriveTeamRuntimeLens({
        scopeId: normalizedScopeId,
        focusedServiceId: serviceId || null,
        serviceRevisionCatalog: serviceRevisionsQuery.data ?? null,
        services,
        actors: actorsQuery.data ?? [],
        runs: runsQuery.data?.runs ?? [],
        currentRun: compareRuns.currentRun,
        baselineRun: compareRuns.baselineRun,
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
      compareRuns.baselineRun,
      compareRuns.currentRun,
      currentRunAuditQuery.data,
      normalizedScopeId,
      runsQuery.data?.runs,
      serviceId,
      serviceRevisionsQuery.data,
      services,
      scriptsQuery.data?.length,
      workflowsQuery.data?.length,
    ],
  );

  return {
    actorGraphQuery,
    actorsQuery,
    baselineRunAuditQuery,
    currentRunAuditQuery,
    lens,
    membersQuery,
    preferredMemberSummary,
    runsQuery,
    serviceRevisionsQuery,
    scriptsQuery,
    servicesQuery,
    workflowsQuery,
  };
}
