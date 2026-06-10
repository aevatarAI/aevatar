import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { studioApi } from "@/shared/studio/api";
import { deriveTeamRuntimeLens, selectCurrentTeamRun } from "./teamRuntimeLens";

const scopeServiceAppId = "default";
type UseTeamRuntimeLensOptions = {
  allowScopeServiceFallback?: boolean;
  enabled?: boolean;
  includeCatalogSignals?: boolean;
  preferredMemberId?: string;
  preferredRunId?: string;
  preferredServiceId?: string;
  teamMemberServiceIds?: readonly string[];
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
  const enabled = options?.enabled ?? true;
  const allowScopeServiceFallback = options?.allowScopeServiceFallback ?? true;
  const includeCatalogSignals = options?.includeCatalogSignals ?? true;
  const preferredMemberId = options?.preferredMemberId?.trim() ?? "";
  const preferredServiceId = options?.preferredServiceId?.trim() ?? "";
  const preferredRunId = options?.preferredRunId?.trim() ?? "";
  const teamMemberServiceIds = useMemo(
    () =>
      Array.from(
        new Set(
          (options?.teamMemberServiceIds ?? [])
            .map((serviceId) => serviceId.trim())
            .filter(Boolean),
        ),
      ),
    [options?.teamMemberServiceIds],
  );

  const workflowsQuery = useQuery({
    enabled: enabled && normalizedScopeId.length > 0 && includeCatalogSignals,
    queryKey: ["teams", "workflows", normalizedScopeId],
    queryFn: () => scopesApi.listWorkflows(normalizedScopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: enabled && normalizedScopeId.length > 0,
    queryKey: ["teams", "services", normalizedScopeId],
    queryFn: () =>
      scopeRuntimeApi.listServices(normalizedScopeId, {
        appId: scopeServiceAppId,
      }),
    retry: false,
  });
  const membersQuery = useQuery({
    enabled:
      enabled &&
      normalizedScopeId.length > 0 &&
      (preferredMemberId.length > 0 || preferredServiceId.length > 0),
    queryKey: ["teams", "members", normalizedScopeId],
    queryFn: () => studioApi.listMembers(normalizedScopeId),
    retry: false,
  });

  const services = useMemo(
    () => [...(servicesQuery.data ?? [])].sort(compareServices),
    [servicesQuery.data],
  );
  const preferredMemberSummary = useMemo(
    () => {
      const members = membersQuery.data?.members ?? [];
      if (preferredMemberId.length > 0) {
        const directMatch =
          members.find(
            (member) => trimOptional(member.memberId) === preferredMemberId,
          ) ?? null;
        if (directMatch) {
          return directMatch;
        }
      }

      if (preferredServiceId.length > 0) {
        return (
          members.find(
            (member) =>
              trimOptional(member.publishedServiceId) === preferredServiceId,
          ) ?? null
        );
      }

      return null;
    },
    [membersQuery.data?.members, preferredMemberId, preferredServiceId],
  );
  const preferredServiceHint =
    preferredServiceId ||
    trimOptional(preferredMemberSummary?.publishedServiceId) ||
    teamMemberServiceIds[0] ||
    "";
  const matchedPreferredServiceId = preferredServiceHint
    ? services.find((service) => service.serviceId === preferredServiceHint)?.serviceId ||
      ""
    : "";
  const serviceId =
    matchedPreferredServiceId ||
    (!preferredServiceHint && preferredMemberId.length === 0 && allowScopeServiceFallback
      ? services[0]?.serviceId || ""
      : "");
  const serviceRevisionsQuery = useQuery({
    enabled: enabled && normalizedScopeId.length > 0 && serviceId.length > 0,
    queryKey: ["teams", "service-revisions", normalizedScopeId, serviceId],
    queryFn: () => scopeRuntimeApi.getServiceRevisions(normalizedScopeId, serviceId),
    retry: false,
  });
  const runsQuery = useQuery({
    enabled:
      enabled &&
      normalizedScopeId.length > 0 &&
      serviceId.length > 0,
    queryKey: [
      "teams",
      "runs",
      normalizedScopeId,
      preferredMemberId || null,
      serviceId || null,
    ],
    queryFn: () =>
      scopeRuntimeApi.listServiceRuns(normalizedScopeId, serviceId, {
        take: 12,
      }),
    retry: false,
  });

  const currentRun = useMemo(
    () =>
      selectCurrentTeamRun(runsQuery.data?.runs ?? [], {
        preferredRunId,
      }),
    [preferredRunId, runsQuery.data?.runs],
  );

  const lens = useMemo(
    () =>
      deriveTeamRuntimeLens({
        scopeId: normalizedScopeId,
        focusedServiceId: serviceId || null,
        serviceRevisionCatalog: serviceRevisionsQuery.data ?? null,
        services,
        runs: runsQuery.data?.runs ?? [],
        currentRun,
        allowServiceFallback: allowScopeServiceFallback,
        workflowCount: workflowsQuery.data?.length ?? 0,
      }),
    [
      allowScopeServiceFallback,
      currentRun,
      normalizedScopeId,
      runsQuery.data?.runs,
      serviceId,
      serviceRevisionsQuery.data,
      services,
      workflowsQuery.data?.length,
    ],
  );

  return {
    lens,
    preferredMemberSummary,
    runsQuery,
    serviceRevisionsQuery,
    servicesQuery,
    workflowsQuery,
  };
}
