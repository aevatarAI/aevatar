import { ChatTaskPlanProtocolError, decodeChatTaskPlan } from './chatTaskPlan';

function taskPlanWithObservation(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 4,
    actorId: 'conversation-alpha',
    taskId: 'task-alpha',
    turnId: 'turn-alpha',
    planId: 'plan-alpha',
    planRevision: 1,
    planRevisions: [],
    title: 'Update repository',
    status: 'active',
    steps: [
      {
        stepId: 'step-update',
        order: 1,
        kind: 'tool',
        status: 'waiting',
        required: true,
        description: 'Update repository',
        source: {
          tool: {
            toolName: 'repository_update',
            serviceSlug: 'github-api',
            serviceId: 'svc-alpha',
          },
        },
        mayChangeExternalState: true,
        externalEffect: 'not_started',
        availableActions: {},
        dependsOn: [],
        substeps: [],
        approvalObservation: {
          approvalRequestId: 'nyxid-approval-alpha',
          decisionMode: 'per_request',
          receiptStatus: 'approval_required',
          observedAt: '2026-08-08T00:10:00Z',
          terminalOutcome: 'rejected',
          subjectKind: 'nyxid.user-service',
          ...overrides,
        },
      },
    ],
  };
}

function taskPlanWithDomain(domain: unknown, artifact?: unknown) {
  return {
    schemaVersion: 6,
    actorId: 'conversation-domain',
    taskId: 'task-domain',
    turnId: 'turn-domain',
    planId: 'plan-domain',
    planRevision: 3,
    planRevisions: [],
    title: 'Complete the domain journey',
    status: 'succeeded',
    domain,
    ...(artifact === undefined ? {} : { artifact }),
    steps: [],
  };
}

function taskPlanWithCondition(
  conditionOverrides: Record<string, unknown> = {},
) {
  return {
    schemaVersion: 5,
    actorId: 'conversation-alpha',
    taskId: 'task-alpha',
    turnId: 'turn-alpha',
    planId: 'plan-alpha',
    planRevision: 2,
    planRevisions: [],
    title: 'Screen candidate',
    status: 'active',
    steps: [
      {
        stepId: 'step-condition',
        order: 1,
        kind: 'condition',
        status: 'done',
        required: true,
        description: 'Evaluate the committed threshold',
        source: {
          condition: {
            condition: {
              conditionId: 'condition-alpha',
              sourceInputRequestId: 'input-alpha',
              suggestedThreshold: 70,
              effectiveThreshold: 75,
              thresholdOrigin: 'user_override',
              observedValue: 80,
              comparison: 'gte',
              outcome: 'true',
              evaluatedAt: '2026-08-09T00:10:00Z',
              guardedToolName: 'bitable_record_create',
              ...conditionOverrides,
            },
          },
        },
        mayChangeExternalState: false,
        externalEffect: 'not_applied',
        availableActions: {},
        dependsOn: ['step-input'],
        substeps: [],
      },
      {
        stepId: 'step-write',
        order: 2,
        kind: 'tool',
        status: 'planned',
        required: true,
        description: 'Create the attestation row',
        source: { tool: { toolName: 'bitable_record_create' } },
        mayChangeExternalState: true,
        externalEffect: 'not_started',
        availableActions: {},
        dependsOn: ['step-condition'],
        substeps: [],
        guard: {
          conditionStepId: 'step-condition',
          requiredOutcome: 'true',
        },
      },
    ],
  };
}

describe('decodeChatTaskPlan', () => {
  it('preserves the complete typed Tier-B approval observation', () => {
    const decoded = decodeChatTaskPlan(taskPlanWithObservation());

    expect(decoded.steps[0]?.approvalObservation).toEqual({
      approvalRequestId: 'nyxid-approval-alpha',
      decisionMode: 'per_request',
      receiptStatus: 'approval_required',
      observedAt: '2026-08-08T00:10:00Z',
      terminalOutcome: 'rejected',
      subjectKind: 'nyxid.user-service',
    });
  });

  it.each([
    ['decisionMode', 'session'],
    ['receiptStatus', 'pending'],
    ['terminalOutcome', 'cancelled'],
  ])('rejects an unknown approval observation %s', (field, value) => {
    expect(() =>
      decodeChatTaskPlan(taskPlanWithObservation({ [field]: value })),
    ).toThrow(ChatTaskPlanProtocolError);
  });

  it('preserves typed condition and guard facts across reload', () => {
    const decoded = decodeChatTaskPlan(taskPlanWithCondition());

    expect(decoded.steps[0]?.source).toEqual({
      kind: 'condition',
      label: '80 >= 75',
      condition: {
        conditionId: 'condition-alpha',
        sourceInputRequestId: 'input-alpha',
        suggestedThreshold: 70,
        effectiveThreshold: 75,
        thresholdOrigin: 'user_override',
        observedValue: 80,
        comparison: 'gte',
        outcome: 'true',
        evaluatedAt: '2026-08-09T00:10:00Z',
        guardedToolName: 'bitable_record_create',
      },
    });
    expect(decoded.steps[1]?.guard).toEqual({
      conditionStepId: 'step-condition',
      requiredOutcome: 'true',
    });
  });

  it.each([
    ['thresholdOrigin', 'override'],
    ['comparison', 'gt'],
    ['outcome', 'passed'],
  ])('rejects an unknown condition %s', (field, value) => {
    expect(() =>
      decodeChatTaskPlan(taskPlanWithCondition({ [field]: value })),
    ).toThrow(ChatTaskPlanProtocolError);
  });

  it('preserves normalized reimbursement evidence and exact verified instance', () => {
    const decoded = decodeChatTaskPlan(
      taskPlanWithDomain(
        {
          reimbursement: {
            evidenceId: 'reimbursement-evidence-alpha',
            sourceInputRequestId: 'input-reimbursement-alpha',
            expenseCategory: 'travel',
            costCenter: 'cc-42',
            reimbursementCurrencyInstruction: 'Submit in SGD',
            guardedToolName: 'approval_instance_create',
            committedAt: '2026-08-11T12:00:00Z',
            sourceInvoices: [
              {
                sourceOrdinal: 1,
                vendor: 'Northwind Air',
                invoiceNumber: 'INV-001',
                invoiceDate: '2026-08-01',
                amount: {
                  currencyCode: 'SGD',
                  minorUnits: 12500,
                  fractionDigits: 2,
                },
              },
              {
                sourceOrdinal: 2,
                vendor: 'Contoso Hotel',
                invoiceNumber: 'INV-002',
                invoiceDate: '2026-08-02',
                amount: {
                  currencyCode: 'SGD',
                  minorUnits: 24000,
                  fractionDigits: 2,
                },
              },
              {
                sourceOrdinal: 3,
                vendor: 'Northwind Air',
                invoiceNumber: 'INV-001',
                invoiceDate: '2026-08-01',
                amount: {
                  currencyCode: 'SGD',
                  minorUnits: 12500,
                  fractionDigits: 2,
                },
              },
            ],
            retainedSourceOrdinals: [1, 2],
            duplicateInvoices: [
              { duplicateSourceOrdinal: 3, retainedSourceOrdinal: 1 },
            ],
          },
        },
        {
          checkName: 'approval.instance.exists',
          verifiedAt: '2026-08-11T12:01:00Z',
          reimbursement: {
            providerInstanceId: 'approval-instance-alpha',
            costCenter: 'cc-42',
            retainedItemCount: 2,
            duplicateItemCount: 1,
          },
        },
      ),
    );

    expect(decoded.domain).toMatchObject({
      kind: 'reimbursement',
      retainedSourceOrdinals: [1, 2],
      duplicateInvoices: [
        { duplicateSourceOrdinal: 3, retainedSourceOrdinal: 1 },
      ],
    });
    expect(decoded.artifact).toEqual({
      kind: 'reimbursement',
      checkName: 'approval.instance.exists',
      verifiedAt: '2026-08-11T12:01:00Z',
      providerInstanceId: 'approval-instance-alpha',
      costCenter: 'cc-42',
      retainedItemCount: 2,
      duplicateItemCount: 1,
    });
  });

  it('preserves candidate rubric, threshold branch evidence, and exact Bitable row', () => {
    const decoded = decodeChatTaskPlan(
      taskPlanWithDomain(
        {
          candidateScreening: {
            evidenceId: 'candidate-evidence-alpha',
            sourceInputRequestId: 'input-threshold-alpha',
            candidateName: 'Candidate Alpha',
            roleTitle: 'Platform Engineer',
            rubric: [
              { criterionId: 'systems', title: 'Systems', maximumPoints: 60 },
              { criterionId: 'delivery', title: 'Delivery', maximumPoints: 40 },
            ],
            scores: [
              {
                criterionId: 'systems',
                awardedPoints: 48,
                evidence: 'Designed actor protocols.',
              },
              {
                criterionId: 'delivery',
                awardedPoints: 32,
                evidence: 'Shipped production changes.',
              },
            ],
            totalScore: 80,
            trackerTable: 'Candidate Tracker',
            trackerTableId: 'tbl-candidates',
            stage: 'accepted',
            guardedToolName: 'bitable_record_create',
            committedAt: '2026-08-11T13:00:00Z',
          },
        },
        {
          checkName: 'bitable.record.exists',
          verifiedAt: '2026-08-11T13:01:00Z',
          candidateTracker: {
            providerRecordId: 'rec-candidate-alpha',
            candidateName: 'Candidate Alpha',
            score: 80,
            threshold: 75,
            trackerTable: 'Candidate Tracker',
            trackerTableId: 'tbl-candidates',
            stage: 'accepted',
          },
        },
      ),
    );

    expect(decoded.domain).toMatchObject({
      kind: 'candidateScreening',
      totalScore: 80,
      guardedToolName: 'bitable_record_create',
    });
    expect(decoded.artifact).toMatchObject({
      kind: 'candidateTracker',
      providerRecordId: 'rec-candidate-alpha',
      score: 80,
      threshold: 75,
    });
  });

  it('rejects ambiguous domain and artifact oneofs', () => {
    expect(() =>
      decodeChatTaskPlan(
        taskPlanWithDomain(
          { reimbursement: {}, candidateScreening: {} },
          { reimbursement: {}, candidateTracker: {} },
        ),
      ),
    ).toThrow(ChatTaskPlanProtocolError);
  });
});
