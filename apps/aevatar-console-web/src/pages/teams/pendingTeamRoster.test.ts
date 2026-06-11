import type { StudioTeamSummary } from "@/shared/studio/models";
import {
  clearSyncedPendingTeamRosterSummaries,
  mergePendingTeamRosterSummaries,
  rememberPendingTeamRosterSummary,
} from "./pendingTeamRoster";

const storageKey = "aevatar:teams:pending-roster:v1";

function createTeam(overrides: Partial<StudioTeamSummary> = {}): StudioTeamSummary {
  return {
    teamId: "team-1",
    scopeId: "scope-a",
    displayName: "Support Team",
    description: "Handles support requests",
    lifecycleStage: "active",
    memberCount: 0,
    createdAt: "2026-05-20T09:00:00Z",
    updatedAt: "2026-05-20T09:00:00Z",
    ...overrides,
  };
}

describe("pendingTeamRoster", () => {
  beforeEach(() => {
    window.sessionStorage.clear();
    jest.useRealTimers();
  });

  it("remembers and merges a just-created team while roster projection catches up", () => {
    const team = createTeam();

    rememberPendingTeamRosterSummary(team);

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([team]);
  });

  it("does not duplicate teams already returned by the roster read model", () => {
    const team = createTeam();

    rememberPendingTeamRosterSummary(team);

    expect(mergePendingTeamRosterSummaries("scope-a", [team])).toEqual([team]);
  });

  it("replaces a pending team entry for the same scope and team", () => {
    const original = createTeam({ displayName: "Original Team" });
    const updated = createTeam({ displayName: "Updated Team" });

    rememberPendingTeamRosterSummary(original);
    rememberPendingTeamRosterSummary(updated);

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([updated]);
  });

  it("filters pending teams by scope", () => {
    const scopeATeam = createTeam({ teamId: "team-a", scopeId: "scope-a" });
    const scopeBTeam = createTeam({ teamId: "team-b", scopeId: "scope-b" });

    rememberPendingTeamRosterSummary(scopeATeam);
    rememberPendingTeamRosterSummary(scopeBTeam);

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([
      scopeATeam,
    ]);
  });

  it("clears pending entries once the roster read model returns them", () => {
    const team = createTeam();

    rememberPendingTeamRosterSummary(team);
    clearSyncedPendingTeamRosterSummaries("scope-a", [team]);

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([]);
    expect(window.sessionStorage.getItem(storageKey)).toBeNull();
  });

  it("drops corrupt storage payloads instead of surfacing invalid pending teams", () => {
    window.sessionStorage.setItem(storageKey, "{not-json");

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([]);
    expect(window.sessionStorage.getItem(storageKey)).toBeNull();
  });

  it("drops expired pending teams", () => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date("2026-05-20T09:00:00Z"));
    const team = createTeam();

    rememberPendingTeamRosterSummary(team);
    jest.setSystemTime(new Date("2026-05-20T09:11:00Z"));

    expect(mergePendingTeamRosterSummaries("scope-a", [])).toEqual([]);
    expect(window.sessionStorage.getItem(storageKey)).toBeNull();
  });
});
