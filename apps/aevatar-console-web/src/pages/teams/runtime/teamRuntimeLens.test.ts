import { deriveTeamRuntimeLens, selectTeamCompareRuns } from "./teamRuntimeLens";

describe("teamRuntimeLens", () => {
  it("selects the latest run and a prior successful baseline", () => {
    const runs = [
      {
        runId: "run-2",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        completionStatus: "failed",
        lastSuccess: false,
      },
      {
        runId: "run-1",
        lastUpdatedAt: "2026-04-09T09:00:00Z",
        completionStatus: "completed",
        lastSuccess: true,
      },
    ] as any;

    expect(selectTeamCompareRuns(runs)).toEqual({
      currentRun: runs[0],
      baselineRun: runs[1],
    });
  });

  it("honors a preferred current run without promoting failed runs to baseline", () => {
    const runs = [
      {
        runId: "run-2",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        completionStatus: "failed",
        lastSuccess: false,
      },
      {
        runId: "run-1",
        lastUpdatedAt: "2026-04-09T09:00:00Z",
        completionStatus: "completed",
        lastSuccess: true,
      },
    ] as any;

    expect(
      selectTeamCompareRuns(runs, {
        preferredRunId: "run-1",
      }),
    ).toEqual({
      currentRun: runs[1],
      baselineRun: null,
    });
  });

  it("keeps baseline empty when no successful prior run exists", () => {
    const runs = [
      {
        runId: "run-2",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        completionStatus: "failed",
        lastSuccess: false,
      },
      {
        runId: "run-1",
        lastUpdatedAt: "2026-04-09T09:00:00Z",
        completionStatus: "waiting_approval",
        lastSuccess: false,
      },
    ] as any;

    expect(selectTeamCompareRuns(runs)).toEqual({
      currentRun: runs[0],
      baselineRun: null,
    });
  });

  it("derives a blocked health state and compare summary from runtime facts", () => {
    const lens = deriveTeamRuntimeLens({
      scopeId: "scope-team",
      binding: {
        available: true,
        scopeId: "scope-team",
        serviceId: "default",
        displayName: "Support Escalation Triage",
        serviceKey: "scope-team:default",
        defaultServingRevisionId: "rev-2",
        activeServingRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        primaryActorId: "actor-intake",
        updatedAt: "2026-04-09T09:00:00Z",
        revisions: [
          {
            revisionId: "rev-2",
            implementationKind: "workflow",
            status: "Published",
            artifactHash: "hash-2",
            failureReason: "",
            isDefaultServing: true,
            isActiveServing: true,
            isServingTarget: true,
            allocationWeight: 100,
            servingState: "Active",
            deploymentId: "dep-2",
            primaryActorId: "actor-intake",
            createdAt: "2026-04-09T08:00:00Z",
            preparedAt: "2026-04-09T08:01:00Z",
            publishedAt: "2026-04-09T08:02:00Z",
            retiredAt: null,
            workflowName: "support-triage",
            workflowDefinitionActorId: "definition://support-triage",
            inlineWorkflowCount: 1,
            scriptId: "",
            scriptRevision: "",
            scriptDefinitionActorId: "",
            scriptSourceHash: "",
            staticActorTypeName: "",
          },
          {
            revisionId: "rev-1",
            implementationKind: "workflow",
            status: "Published",
            artifactHash: "hash-1",
            failureReason: "",
            isDefaultServing: false,
            isActiveServing: false,
            isServingTarget: false,
            allocationWeight: 0,
            servingState: "",
            deploymentId: "",
            primaryActorId: "actor-intake-v1",
            createdAt: "2026-04-08T08:00:00Z",
            preparedAt: "2026-04-08T08:01:00Z",
            publishedAt: "2026-04-08T08:02:00Z",
            retiredAt: null,
            workflowName: "support-triage-v1",
            workflowDefinitionActorId: "definition://support-triage-v1",
            inlineWorkflowCount: 1,
            scriptId: "",
            scriptRevision: "",
            scriptDefinitionActorId: "",
            scriptSourceHash: "",
            staticActorTypeName: "",
          },
        ],
      },
      services: [
        {
          serviceKey: "scope-team:default",
          tenantId: "scope-team",
          appId: "default",
          namespace: "default",
          serviceId: "default",
          displayName: "Support Runtime",
          defaultServingRevisionId: "rev-2",
          activeServingRevisionId: "rev-2",
          deploymentId: "dep-2",
          primaryActorId: "actor-intake",
          deploymentStatus: "Active",
          endpoints: [],
          policyIds: [],
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ],
      actors: [
        {
          gAgentType: "IntakeAgent",
          actorIds: ["actor-intake"],
        },
      ],
      runs: [
        {
          scopeId: "scope-team",
          serviceId: "default",
          runId: "run-current",
          actorId: "actor-intake",
          definitionActorId: "definition://support-triage",
          revisionId: "rev-2",
          deploymentId: "dep-2",
          workflowName: "support-triage",
          completionStatus: "waiting_approval",
          stateVersion: 2,
          lastEventId: "evt-2",
          lastUpdatedAt: "2026-04-09T09:05:00Z",
          boundAt: "2026-04-09T09:00:00Z",
          bindingUpdatedAt: "2026-04-09T09:00:00Z",
          lastSuccess: false,
          totalSteps: 4,
          completedSteps: 2,
          roleReplyCount: 1,
          lastOutput: "",
          lastError: "Waiting on approval",
        },
        {
          scopeId: "scope-team",
          serviceId: "default",
          runId: "run-good",
          actorId: "actor-intake-v1",
          definitionActorId: "definition://support-triage-v1",
          revisionId: "rev-1",
          deploymentId: "dep-1",
          workflowName: "support-triage-v1",
          completionStatus: "completed",
          stateVersion: 1,
          lastEventId: "evt-1",
          lastUpdatedAt: "2026-04-09T08:55:00Z",
          boundAt: "2026-04-09T08:50:00Z",
          bindingUpdatedAt: "2026-04-09T08:50:00Z",
          lastSuccess: true,
          totalSteps: 3,
          completedSteps: 3,
          roleReplyCount: 1,
          lastOutput: "Resolved",
          lastError: "",
        },
      ],
      actorGraph: null,
      currentRunAudit: {
        summary: {
          scopeId: "scope-team",
          serviceId: "default",
          runId: "run-current",
          actorId: "actor-intake",
          definitionActorId: "definition://support-triage",
          revisionId: "rev-2",
          deploymentId: "dep-2",
          workflowName: "support-triage",
          completionStatus: "waiting_approval",
          stateVersion: 2,
          lastEventId: "evt-2",
          lastUpdatedAt: "2026-04-09T09:05:00Z",
          boundAt: "2026-04-09T09:00:00Z",
          bindingUpdatedAt: "2026-04-09T09:00:00Z",
          lastSuccess: false,
          totalSteps: 4,
          completedSteps: 2,
          roleReplyCount: 1,
          lastOutput: "",
          lastError: "Waiting on approval",
        },
        audit: {
          reportVersion: "1",
          projectionScope: "service",
          topologySource: "audit",
          completionStatus: "waiting_approval",
          workflowName: "support-triage",
          rootActorId: "actor-intake",
          commandId: "cmd-1",
          stateVersion: 2,
          lastEventId: "evt-2",
          createdAt: "2026-04-09T09:00:00Z",
          updatedAt: "2026-04-09T09:05:00Z",
          startedAt: "2026-04-09T09:00:00Z",
          endedAt: null,
          durationMs: 1000,
          success: false,
          input: "hello",
          finalOutput: "",
          finalError: "Waiting on approval",
          topology: [
            {
              parent: "actor-intake",
              child: "actor-risk",
            },
            {
              parent: "actor-risk",
              child: "actor-ops",
            },
          ],
          steps: [
            {
              stepId: "risk_review",
              stepType: "human_approval",
              targetRole: "operator",
              requestedAt: "2026-04-09T09:01:00Z",
              completedAt: null,
              success: null,
              workerId: "actor-intake",
              outputPreview: "",
              error: "",
              requestParameters: {},
              completionAnnotations: {},
              nextStepId: "",
              branchKey: "",
              assignedVariable: "",
              assignedValue: "",
              suspensionType: "human_approval",
              suspensionPrompt: "Approve escalation",
              suspensionTimeoutSeconds: null,
              requestedVariableName: "",
              durationMs: null,
            },
          ],
          roleReplies: [
            {
              timestamp: "2026-04-09T09:02:30Z",
              roleId: "operator",
              sessionId: "session-1",
              content: "Escalation needs approval from on-call.",
              contentLength: 39,
            },
          ],
          timeline: [
            {
              timestamp: "2026-04-09T09:01:30Z",
              stage: "human_gate",
              message: "Approval requested from operator",
              agentId: "actor-intake",
              stepId: "risk_review",
              stepType: "human_approval",
              eventType: "suspension_requested",
              data: {},
            },
          ],
          summary: {
            totalSteps: 4,
            requestedSteps: 2,
            completedSteps: 2,
            roleReplyCount: 1,
            stepTypeCounts: {},
          },
        },
      },
      baselineRunAudit: {
        summary: {
          scopeId: "scope-team",
          serviceId: "default",
          runId: "run-good",
          actorId: "actor-intake-v1",
          definitionActorId: "definition://support-triage-v1",
          revisionId: "rev-1",
          deploymentId: "dep-1",
          workflowName: "support-triage-v1",
          completionStatus: "completed",
          stateVersion: 1,
          lastEventId: "evt-1",
          lastUpdatedAt: "2026-04-09T08:55:00Z",
          boundAt: "2026-04-09T08:50:00Z",
          bindingUpdatedAt: "2026-04-09T08:50:00Z",
          lastSuccess: true,
          totalSteps: 3,
          completedSteps: 3,
          roleReplyCount: 1,
          lastOutput: "Resolved",
          lastError: "",
        },
        audit: {
          reportVersion: "1",
          projectionScope: "service",
          topologySource: "audit",
          completionStatus: "completed",
          workflowName: "support-triage-v1",
          rootActorId: "actor-intake-v1",
          commandId: "cmd-0",
          stateVersion: 1,
          lastEventId: "evt-1",
          createdAt: "2026-04-09T08:50:00Z",
          updatedAt: "2026-04-09T08:55:00Z",
          startedAt: "2026-04-09T08:50:00Z",
          endedAt: "2026-04-09T08:55:00Z",
          durationMs: 900,
          success: true,
          input: "hello",
          finalOutput: "Resolved",
          finalError: "",
          topology: [
            {
              parent: "actor-intake-v1",
              child: "actor-risk",
            },
          ],
          steps: [
            {
              stepId: "risk_review",
              stepType: "llm_call",
              targetRole: "triage",
              requestedAt: "2026-04-09T08:51:00Z",
              completedAt: "2026-04-09T08:52:00Z",
              success: true,
              workerId: "actor-intake-v1",
              outputPreview: "Escalation cleared",
              error: "",
              requestParameters: {},
              completionAnnotations: {},
              nextStepId: "notify_customer",
              branchKey: "",
              assignedVariable: "",
              assignedValue: "",
              suspensionType: "",
              suspensionPrompt: "",
              suspensionTimeoutSeconds: null,
              requestedVariableName: "",
              durationMs: 100,
            },
          ],
          roleReplies: [],
          timeline: [],
          summary: {
            totalSteps: 3,
            requestedSteps: 3,
            completedSteps: 3,
            roleReplyCount: 1,
            stepTypeCounts: {},
          },
        },
      },
      workflowCount: 2,
      scriptCount: 1,
    });

    expect(lens.healthStatus).toBe("blocked");
    expect(lens.humanInterventionDetected).toBe(true);
    expect(lens.compare.available).toBe(true);
    expect(lens.compare.details.some((item) => item.includes("Revision changed"))).toBe(true);
    expect(lens.compare.sections.some((section) => section.title === "Step deltas")).toBe(true);
    expect(lens.compare.sections.some((section) => section.title === "Handoff deltas")).toBe(true);
    expect(lens.playback.available).toBe(true);
    expect(lens.playback.currentRunId).toBe("run-current");
    expect(lens.playback.rootActorId).toBe("actor-intake");
    expect(lens.playback.commandId).toBe("cmd-1");
    expect(lens.playback.workflowName).toBe("support-triage");
    expect(lens.playback.prompt).toContain("Approve escalation");
    expect(lens.playback.steps[0]?.actorId).toBe("actor-intake");
    expect(lens.playback.steps[0]?.runId).toBe("run-current");
    expect(lens.playback.events[0]?.actorId).toBe("actor-intake");
    expect(lens.playback.events[0]?.runId).toBe("run-current");
    expect(lens.playback.roleReplies[0]).toContain("operator");
  });

  it("keeps health at attention when serving is active but no recent run exists", () => {
    const lens = deriveTeamRuntimeLens({
      scopeId: "scope-team",
      binding: {
        available: true,
        scopeId: "scope-team",
        serviceId: "default",
        displayName: "Support Escalation Triage",
        serviceKey: "scope-team:default",
        defaultServingRevisionId: "rev-2",
        activeServingRevisionId: "rev-2",
        deploymentId: "dep-2",
        deploymentStatus: "Active",
        primaryActorId: "actor-intake",
        updatedAt: "2026-04-09T09:00:00Z",
        revisions: [],
      } as any,
      services: [
        {
          serviceKey: "scope-team:default",
          tenantId: "scope-team",
          appId: "default",
          namespace: "default",
          serviceId: "default",
          displayName: "Support Runtime",
          deploymentStatus: "Active",
          endpoints: [],
          policyIds: [],
          updatedAt: "2026-04-09T09:00:00Z",
        },
      ] as any,
      actors: [],
      runs: [],
      actorGraph: null,
      currentRunAudit: null,
      baselineRunAudit: null,
      workflowCount: 0,
      scriptCount: 0,
    });

    expect(lens.healthStatus).toBe("attention");
    expect(lens.healthSummary).toBe(
      "The current team has an active serving deployment, but no recent run is available to prove runtime health.",
    );
    expect(lens.partialSignals).toContain("No recent runs");
  });
});
