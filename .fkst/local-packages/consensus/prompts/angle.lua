return {
  template = [[Judge this proposal from one consensus angle.
{{bias}}
State the reason that is specific to THIS angle; do not restate another angle's criterion.

Execution boundary:
- You are running in an empty runtime scratch directory, not a repository checkout.
- Do not clone, checkout, fetch with git, create branches, or modify any repository.
- Read required source content only from the context manifest below.

Respond with exactly the requested marker lines and no other text.
Line one: the marker ⟦FKST:VERDICT⟧ followed by one word - {{verdict_options}}.
Line two: the marker ⟦FKST:REPLY⟧ followed by one concise paragraph.
{{readiness_instruction}}

Proposal:
Angle: {{angle}}
Title: {{title}}
{{convergence_block}}
{{body_label}}
{{body}}
{{content_fetch_block}}
{{context_block}}]],

  bias = {
    minimal = "Bias: minimal. Judge only the smallest coherent path that fully satisfies the stated goal and acceptance bounds; treat unproven EXTRA scope beyond that path as a reason not to approve, and name in the reply what concrete evidence or scoping would change that.",
    structural = "Bias: structural. Judge whether the proposal preserves clean module boundaries, reliable data flow, clear source-of-truth, durable output/parse and injection trust contracts, and maintainability as the system grows.",
    delete = "Bias: delete. Judge whether the proposed surface should exist at all: prefer removing it, making it a no-op, reusing an existing deterministic mechanism, or collapsing an abstraction; approve adding or keeping surface ONLY when the proposal proves the new surface is necessary.",
    ["high-risk"] = "Bias: high-risk/security. CI/auth/dependency/scheduler/workflow/lockfile changes are prompt-injection and supply-chain vectors; the bot author may be prompt-injected. Approve ONLY if the high-risk surface is justified and safe under that threat model; abstain or reject if it is not adequately scrutinized.",
  },
}
