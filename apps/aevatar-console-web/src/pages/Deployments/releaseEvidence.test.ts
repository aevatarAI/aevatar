import { buildDeploymentReleaseEvidenceSnapshot } from "./releaseEvidence";
import { buildDeploymentReleaseHandoff } from "./releaseHandoff";

describe("buildDeploymentReleaseEvidenceSnapshot", () => {
  it("marks candidate deploy evidence observed only when rollout, serving, and traffic snapshots show it", () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: "deploy-candidate",
      activeRevisionId: "rev-11",
      candidateRevisionId: "rev-12",
      receipt: {
        commandId: "cmd-1",
        correlationId: "corr-1",
      },
      rolloutId: "rollout-1",
      serviceId: "trade-agent",
    });

    const evidence = buildDeploymentReleaseEvidenceSnapshot({
      deployments: [],
      handoff,
      rollout: {
        baselineTargets: [],
        currentStageIndex: 0,
        displayName: "Canary",
        failureReason: "",
        rolloutId: "rollout-1",
        serviceKey: "scope-1:trade-agent",
        stages: [],
        startedAt: "2026-03-30T10:00:00Z",
        status: "canary",
        updatedAt: "2026-03-30T10:05:00Z",
      },
      serving: {
        activeRolloutId: "rollout-1",
        generation: 4,
        serviceKey: "scope-1:trade-agent",
        targets: [
          {
            allocationWeight: 10,
            deploymentId: "dep-2",
            enabledEndpointIds: ["chat"],
            primaryActorId: "actor-2",
            revisionId: "rev-12",
            servingState: "canary",
          },
        ],
        updatedAt: "2026-03-30T10:06:00Z",
      },
      traffic: {
        activeRolloutId: "rollout-1",
        endpoints: [
          {
            endpointId: "chat",
            targets: [
              {
                allocationWeight: 10,
                deploymentId: "dep-2",
                primaryActorId: "actor-2",
                revisionId: "rev-12",
                servingState: "canary",
              },
            ],
          },
        ],
        generation: 4,
        serviceKey: "scope-1:trade-agent",
        updatedAt: "2026-03-30T10:06:00Z",
      },
    });

    expect(evidence.observedCount).toBe(3);
    expect(evidence.summary).toBe("所有关键证据都已出现或需要人工核对。");
    expect(evidence.checks.map((check) => check.status)).toEqual([
      "observed",
      "observed",
      "observed",
    ]);
  });

  it("keeps candidate deploy evidence pending when serving and traffic do not show the candidate", () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: "deploy-candidate",
      activeRevisionId: "rev-11",
      candidateRevisionId: "rev-12",
      receipt: {
        commandId: "cmd-1",
        correlationId: "corr-1",
      },
      serviceId: "trade-agent",
    });

    const evidence = buildDeploymentReleaseEvidenceSnapshot({
      deployments: [],
      handoff,
      serving: {
        activeRolloutId: "rollout-1",
        generation: 3,
        serviceKey: "scope-1:trade-agent",
        targets: [
          {
            allocationWeight: 100,
            deploymentId: "dep-1",
            enabledEndpointIds: ["chat"],
            primaryActorId: "actor-1",
            revisionId: "rev-11",
            servingState: "active",
          },
        ],
        updatedAt: "2026-03-30T10:05:00Z",
      },
      traffic: {
        activeRolloutId: "rollout-1",
        endpoints: [
          {
            endpointId: "chat",
            targets: [
              {
                allocationWeight: 100,
                deploymentId: "dep-1",
                primaryActorId: "actor-1",
                revisionId: "rev-11",
                servingState: "active",
              },
            ],
          },
        ],
        generation: 3,
        serviceKey: "scope-1:trade-agent",
        updatedAt: "2026-03-30T10:05:00Z",
      },
    });

    expect(evidence.observedCount).toBe(0);
    expect(evidence.summary).toContain("3 项证据仍待观察");
    expect(evidence.checks.map((check) => check.status)).toEqual([
      "pending",
      "pending",
      "pending",
    ]);
  });

  it("marks deactivate evidence observed after catalog, serving, and traffic stop showing the deployment", () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: "deactivate-deployment",
      deploymentId: "dep-1",
      receipt: {
        commandId: "cmd-7",
        correlationId: "corr-7",
      },
      serviceId: "trade-agent",
    });

    const evidence = buildDeploymentReleaseEvidenceSnapshot({
      deployments: [
        {
          activatedAt: "2026-03-30T10:00:00Z",
          deploymentId: "dep-1",
          primaryActorId: "actor-1",
          revisionId: "rev-11",
          status: "inactive",
          updatedAt: "2026-03-30T10:10:00Z",
        },
      ],
      handoff,
      serving: {
        activeRolloutId: "",
        generation: 5,
        serviceKey: "scope-1:trade-agent",
        targets: [],
        updatedAt: "2026-03-30T10:10:00Z",
      },
      traffic: {
        activeRolloutId: "",
        endpoints: [],
        generation: 5,
        serviceKey: "scope-1:trade-agent",
        updatedAt: "2026-03-30T10:10:00Z",
      },
    });

    expect(evidence.checks.map((check) => check.status)).toEqual([
      "observed",
      "observed",
      "observed",
    ]);
  });
});
