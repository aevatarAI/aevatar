import type { StudioMemberSummary } from "./models";
import {
  findStudioMemberByMemberId,
  findStudioMemberByServiceId,
  findStudioMemberServiceIdInCatalog,
  resolveStudioMemberRuntimeServiceId,
} from "./memberRuntime";

function buildMember(
  overrides?: Partial<StudioMemberSummary>,
): StudioMemberSummary {
  return {
    memberId: "joker",
    scopeId: "scope-1",
    displayName: "Joker",
    description: "",
    implementationKind: "workflow",
    lifecycleStage: "bind_ready",
    publishedServiceId: "member-joker",
    lastBoundRevisionId: "rev-1",
    createdAt: "2026-04-27T08:00:00Z",
    updatedAt: "2026-04-27T08:05:00Z",
    ...overrides,
  };
}

describe("studio member runtime helpers", () => {
  it("matches a member to the backend service catalog by publishedServiceId", () => {
    const member = buildMember();

    expect(
      findStudioMemberServiceIdInCatalog(member, [
        { serviceId: "joker" },
        { serviceId: "member-joker" },
      ]),
    ).toBe("member-joker");
  });

  it("finds the roster member from its stable member id", () => {
    const member = buildMember();

    expect(findStudioMemberByMemberId([member], "joker")).toBe(member);
  });

  it("finds the roster member from its published service id", () => {
    const member = buildMember();

    expect(findStudioMemberByServiceId([member], "member-joker")).toBe(member);
  });

  it("falls back to the published service id when the catalog has not loaded yet", () => {
    const member = buildMember();

    expect(resolveStudioMemberRuntimeServiceId(member, [])).toBe("member-joker");
  });
});
