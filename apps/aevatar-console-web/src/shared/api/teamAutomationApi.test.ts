import {
  buildMockTeamAutomationPermissionReview,
  teamAutomationApi,
  type TeamAutomationCreateDraft,
} from "@/shared/api/teamAutomationApi";

const draft: TeamAutomationCreateDraft = {
  scopeId: "scope-alpha",
  teamId: "team-alpha",
  memberId: "m-alpha",
  publishedServiceId: "svc-alpha",
  serviceRevisionId: "rev-alpha",
  displayName: "Daily review",
  prompt: "Summarize open work.",
  cronExpression: "0 9 * * 1-5",
  timezone: "Asia/Singapore",
  enabled: true,
};

describe("teamAutomationApi", () => {
  it("fails closed until the scoped backend preflight contract is connected", async () => {
    await expect(teamAutomationApi.preflightCreate(draft)).rejects.toThrow(
      "Team Automation permission review is unavailable until the scoped backend contract is connected.",
    );
  });

  it("builds an explicit mock review without conflating member and service identities", () => {
    const review = buildMockTeamAutomationPermissionReview(draft);

    expect(review.status).toBe("ready");
    expect(review.serviceGrants[0]).toEqual(
      expect.objectContaining({
        targetId: "svc-alpha",
        displayName: "Published service svc-alpha at rev-alpha",
      }),
    );
    expect(JSON.stringify(review)).not.toContain("m-alpha");
    expect(review.credentialPlan.browserReceivesRawKey).toBe(false);
  });

  it("normalizes blank service identity only inside the mock boundary", () => {
    const review = buildMockTeamAutomationPermissionReview({
      ...draft,
      publishedServiceId: "   ",
      serviceRevisionId: "   ",
    });

    expect(review.serviceGrants[0]).toEqual(
      expect.objectContaining({
        targetId: "svc-alpha",
        displayName: "Published service svc-alpha",
      }),
    );
  });
});
