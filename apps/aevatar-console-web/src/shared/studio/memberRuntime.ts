import type { StudioMemberSummary } from "./models";

type StudioMemberIdentity = Pick<StudioMemberSummary, "memberId" | "publishedServiceId">;

type ServiceIdCarrier = {
  readonly serviceId?: string | null;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

export function findStudioMemberByMemberId(
  members: readonly StudioMemberSummary[],
  memberId: string | null | undefined,
): StudioMemberSummary | null {
  const normalizedMemberId = trimOptional(memberId);
  if (!normalizedMemberId) {
    return null;
  }

  return (
    members.find(
      (member) => trimOptional(member.memberId) === normalizedMemberId,
    ) ?? null
  );
}

export function collectStudioMemberServiceIds(
  member: StudioMemberIdentity | null | undefined,
): readonly string[] {
  const candidates = [trimOptional(member?.publishedServiceId)];
  const seen = new Set<string>();

  return candidates.filter((candidate) => {
    if (!candidate || seen.has(candidate)) {
      return false;
    }

    seen.add(candidate);
    return true;
  });
}

export function matchesStudioMemberServiceId(
  member: StudioMemberIdentity | null | undefined,
  serviceId: string | null | undefined,
): boolean {
  const normalizedServiceId = trimOptional(serviceId);
  if (!normalizedServiceId) {
    return false;
  }

  return collectStudioMemberServiceIds(member).includes(normalizedServiceId);
}

export function findStudioMemberByServiceId(
  members: readonly StudioMemberSummary[],
  serviceId: string | null | undefined,
): StudioMemberSummary | null {
  return (
    members.find((member) => matchesStudioMemberServiceId(member, serviceId)) ?? null
  );
}

export function findStudioMemberServiceIdInCatalog(
  member: StudioMemberIdentity | null | undefined,
  services: readonly ServiceIdCarrier[],
): string {
  const serviceIds = collectStudioMemberServiceIds(member);
  for (const serviceId of serviceIds) {
    const matchedService = services.find(
      (service) => trimOptional(service.serviceId) === serviceId,
    );
    if (matchedService) {
      return trimOptional(matchedService.serviceId);
    }
  }

  return "";
}

export function resolveStudioMemberRuntimeServiceId(
  member: StudioMemberIdentity | null | undefined,
  services: readonly ServiceIdCarrier[],
): string {
  return (
    findStudioMemberServiceIdInCatalog(member, services) ||
    trimOptional(member?.publishedServiceId) ||
    ""
  );
}
