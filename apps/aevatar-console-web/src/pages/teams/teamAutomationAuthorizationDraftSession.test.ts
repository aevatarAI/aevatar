import {
  consumeTeamAutomationAuthorizationDraft,
  saveTeamAutomationAuthorizationDraft,
  type TeamAutomationAuthorizationDraftInput,
} from "./teamAutomationAuthorizationDraftSession";

const route = {
  scopeId: "scope-alpha",
  teamId: "team-alpha",
  memberId: "m-alpha",
};

const draft: TeamAutomationAuthorizationDraftInput = {
  ...route,
  mode: "reauthorize",
  scheduleId: "sch-alpha",
  displayName: "Daily review",
  prompt: "Summarize open work.",
  scheduleCron: "0 9 * * 1-5",
  scheduleTimezone: "Asia/Shanghai",
  enabled: true,
};

describe("teamAutomationAuthorizationDraftSession", () => {
  beforeEach(() => window.sessionStorage.clear());

  it("stores only allowlisted non-secret fields for exactly 15 minutes", () => {
    saveTeamAutomationAuthorizationDraft(window.sessionStorage, draft, 1_000);

    const values = Object.values(window.sessionStorage);
    expect(values).toHaveLength(1);
    const persisted = JSON.parse(values[0]);
    expect(persisted).toEqual({
      schemaVersion: 1,
      ...draft,
      savedAt: 1_000,
      expiresAt: 901_000,
    });
    expect(JSON.stringify(persisted)).not.toMatch(
      /plan|grant|permissionDigest|policyVersion|consent|operationId|idempotencyKey|accessToken|refreshToken|apiKeyId|secretReference|vaultReference|credential/i,
    );
  });

  it("consumes a matching draft once and never restores authorization state", () => {
    saveTeamAutomationAuthorizationDraft(window.sessionStorage, draft, 1_000);

    expect(
      consumeTeamAutomationAuthorizationDraft(window.sessionStorage, route, 2_000),
    ).toEqual(draft);
    expect(
      consumeTeamAutomationAuthorizationDraft(window.sessionStorage, route, 2_000),
    ).toBeNull();
  });

  it.each([
    ["expired", { now: 901_001, route }],
    ["wrong scope", { now: 2_000, route: { ...route, scopeId: "scope-other" } }],
    ["wrong team", { now: 2_000, route: { ...route, teamId: "team-other" } }],
    ["wrong member", { now: 2_000, route: { ...route, memberId: "m-other" } }],
  ])("rejects and removes a %s draft", (_label, input) => {
    saveTeamAutomationAuthorizationDraft(window.sessionStorage, draft, 1_000);

    expect(
      consumeTeamAutomationAuthorizationDraft(
        window.sessionStorage,
        input.route,
        input.now,
      ),
    ).toBeNull();
    expect(window.sessionStorage.length).toBe(0);
  });

  it("rejects corrupt or over-posted records and removes them", () => {
    saveTeamAutomationAuthorizationDraft(window.sessionStorage, draft, 1_000);
    const key = window.sessionStorage.key(0)!;
    window.sessionStorage.setItem(
      key,
      JSON.stringify({
        schemaVersion: 1,
        ...draft,
        savedAt: 1_000,
        expiresAt: 901_000,
        permissionDigest: "must-not-survive",
      }),
    );

    expect(
      consumeTeamAutomationAuthorizationDraft(window.sessionStorage, route, 2_000),
    ).toBeNull();
    expect(window.sessionStorage.length).toBe(0);
  });
});
