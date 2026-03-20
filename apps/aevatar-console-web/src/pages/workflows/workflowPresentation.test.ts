import {
  buildStepRows,
  buildWorkflowRows,
  defaultWorkflowLibraryFilter,
  filterWorkflowRows,
  findWorkflowStepTargetRole,
} from './workflowPresentation';

describe('workflowPresentation', () => {
  it('filters workflow rows by keyword, source, llm, and primitives', () => {
    const rows = buildWorkflowRows([
      {
        name: 'human_approval_release_gate',
        description: 'Approval workflow',
        category: 'approval',
        group: 'human',
        groupLabel: 'Human',
        sortOrder: 1,
        source: 'Advanced',
        sourceLabel: 'Advanced',
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: true,
        primitives: ['human_approval', 'wait_signal'],
      },
      {
        name: 'demo_template',
        description: 'Template workflow',
        category: 'templates',
        group: 'advanced',
        groupLabel: 'Advanced Patterns',
        sortOrder: 2,
        source: 'BuiltIn',
        sourceLabel: 'Built-in',
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: false,
        primitives: ['render_template'],
      },
    ]);

    const filtered = filterWorkflowRows(rows, {
      ...defaultWorkflowLibraryFilter,
      keyword: 'approval',
      sources: ['Advanced'],
      llmRequirement: 'required',
      primitives: ['human_approval'],
    });

    expect(filtered).toHaveLength(1);
    expect(filtered[0].name).toBe('human_approval_release_gate');
  });

  it('builds step rows and resolves target role for a selected step', () => {
    const steps = buildStepRows([
      {
        id: 'review',
        type: 'human_approval',
        targetRole: 'reviewer',
        parameters: { severity: 'high' },
        next: 'notify',
        branches: { approved: 'release' },
        children: [],
      },
    ]);

    expect(steps[0].parameterCount).toBe(1);
    expect(steps[0].branchCount).toBe(1);
    expect(findWorkflowStepTargetRole(steps, 'review')).toBe('reviewer');
  });
});
