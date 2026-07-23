export type TeamAutomationCreateDraft = {
  readonly scopeId: string;
  readonly teamId: string;
  readonly memberId: string;
  readonly publishedServiceId: string;
  readonly serviceRevisionId?: string;
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone?: string;
  readonly enabled: boolean;
};

export type TeamAutomationGrant = {
  readonly grantId: string;
  readonly targetId: string;
  readonly displayName: string;
  readonly permission: string;
};

export type TeamAutomationCredentialPlan = {
  readonly mode: "dedicated-per-schedule";
  readonly hostedBy: "Aevatar";
  readonly browserReceivesRawKey: false;
  readonly expiresAt: string;
};

export type TeamAutomationPermissionReview = {
  readonly status: "ready" | "plan-changed";
  readonly permissionDigest: string;
  readonly policyVersion: string;
  readonly credentialPlan: TeamAutomationCredentialPlan;
  readonly serviceGrants: readonly TeamAutomationGrant[];
  readonly nodeGrants: readonly TeamAutomationGrant[];
  readonly warning?: string;
};

export type TeamAutomationConsent = {
  readonly accepted: boolean;
  readonly acceptedAt: string;
  readonly browserLoginConsent: boolean;
  readonly automationAgentKeyConsent: boolean;
};

export type TeamAutomationCreateInput = {
  readonly draft: TeamAutomationCreateDraft;
  readonly permissionDigest: string;
  readonly policyVersion: string;
  readonly consent: TeamAutomationConsent;
};

export type TeamAutomationCreateReceipt = {
  readonly scheduleId: string;
  readonly scheduleActorId: string;
  readonly accepted: boolean;
  readonly commandId: string;
  readonly correlationId: string;
  readonly ackedAt: string;
  readonly ackStage: "accepted";
  readonly permissionDigest: string;
  readonly policyVersion: string;
};

export const teamAutomationMockFixtureIds = {
  memberId: "m-alpha",
  workflowId: "wf-alpha",
  publishedServiceId: "svc-alpha",
  scheduleId: "sch-alpha",
  credentialFixtureId: "ak-alpha",
} as const;

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

export function buildMockTeamAutomationPermissionReview(
  draft: TeamAutomationCreateDraft,
): TeamAutomationPermissionReview {
  const publishedServiceId =
    trimText(draft.publishedServiceId) ||
    teamAutomationMockFixtureIds.publishedServiceId;
  const serviceRevisionId = trimText(draft.serviceRevisionId);

  return {
    status: "ready",
    permissionDigest: "perm-digest-alpha-v1",
    policyVersion: "agent-key-policy-v1",
    credentialPlan: {
      mode: "dedicated-per-schedule",
      hostedBy: "Aevatar",
      browserReceivesRawKey: false,
      expiresAt: "2026-09-30T00:00:00Z",
    },
    serviceGrants: [
      {
        grantId: "service-chat-invoke",
        targetId: publishedServiceId,
        displayName: serviceRevisionId
          ? `Published service ${publishedServiceId} at ${serviceRevisionId}`
          : `Published service ${publishedServiceId}`,
        permission: "Invoke workflow chat endpoint",
      },
    ],
    nodeGrants: [
      {
        grantId: "workflow-runtime-start",
        targetId: "workflow-runtime",
        displayName: "Workflow runtime",
        permission: "Start scheduled workflow runs",
      },
    ],
  };
}

async function preflightCreateTeamAutomation(
  draft: TeamAutomationCreateDraft,
): Promise<TeamAutomationPermissionReview> {
  return buildMockTeamAutomationPermissionReview(draft);
}

async function createTeamAutomation(
  input: TeamAutomationCreateInput,
): Promise<TeamAutomationCreateReceipt> {
  if (!input.consent.accepted) {
    throw new Error("Team Automation Agent Key consent is required.");
  }

  return {
    scheduleId: teamAutomationMockFixtureIds.scheduleId,
    scheduleActorId: "schedule-actor-alpha",
    accepted: true,
    commandId: "cmd-team-automation-alpha",
    correlationId: "corr-team-automation-alpha",
    ackedAt: "2026-06-10T08:35:00Z",
    ackStage: "accepted",
    permissionDigest: input.permissionDigest,
    policyVersion: input.policyVersion,
  };
}

export const teamAutomationApi = {
  preflightCreate: preflightCreateTeamAutomation,
  create: createTeamAutomation,
};
