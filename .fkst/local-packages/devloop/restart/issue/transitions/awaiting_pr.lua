return function(M, h)
  local fact = h.fact
  local obligation = h.obligation
  local effect = h.effect
  local budget = h.budget
  local timeout = h.timeout
  local liveness = h.liveness
  local advancing_fact = h.advancing_fact
  local responsibility_signature = h.responsibility_signature
  local contract = require("devloop.restart.issue.pr_partition_contract").awaiting_pr_contract()
  local terminal_states = contract.child_terminal_states
  return {
    from_state = "awaiting-pr",
    liveness_class_id = "child_workflow_wait",
    watchdog = {
      mode = "live-defer",
      budget_ms = 180 * 24 * 60 * 60 * 1000,
      on_stale = {
        op = "redrive_receiver",
        producer = "child-state",
      },
    },
    actionable_epoch = {
      source = "child_workflow_wait:v1",
      generation_source = "same_as_actionable_epoch",
      live_marker = "state:v1",
      producer = "child-state",
    },
    defer = {
      kind = "child_workflow_wait",
      live_marker = "state:v1",
      producer = "child-state",
      freshness_ms = 24 * 60 * 60 * 1000,
      redrive_opens_generation = true,
      delegation_marker = "pr-delegation:v1",
      terminal_states = terminal_states,
    },
    terminal = false,
    to_states = { "merged", "ready", "blocked" },
    driving_queue = "devloop_observe_redrive",
    observe_surfaces = { issue = true, liveness_scan = true },
    timeout_surfaces = { issue = true, issue_liveness_scan = true, liveness_scan = true },
    output_obligation = obligation({ "state:v1 merged", "state:v1 ready", "state:v1 blocked", "timeout-reconcile:v1" }, { "merged", "ready", "blocked" }),
    budget = budget(180 * 24 * 60, "The parent issue delegates PR work to a child workflow and waits on the PR child's state:v1 marker; PR review and merge time is deferred by child_workflow_wait rather than charged to the parent."),
    liveness_contract = liveness({
      mode = "live-defer",
      signal = {
        family = "state",
        resolver = "child-state",
        producer = "child-state",
        surface = "pr-comment-stream",
        version_form = "raw",
        max_age_minutes = 24 * 60,
      },
    }),
    on_timeout = timeout("devloop_observe_redrive"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "pr-child-workflow",
      driving_queue = "devloop_observe_redrive",
      state_kind = "gate",
      gate_kind = "decision",
      liveness_class = "child_workflow_wait",
      input_fact_family = "pr-delegation-and-child-state",
      output_postcondition_family = "parent_resume_from_child_state_terminal",
      phase_rank = M.stage_rank("awaiting-pr"),
      lineage_keys = { "state.version", "pr-delegation.pr_proposal", "pr-delegation.pr", "source_ref" },
      decision_type = "child_state_terminal_gate",
      successors = {
        {
          state = "merged",
          output_variant = "child_pr_merged",
          postcondition_family = "parent_resume_from_child_state_terminal",
          decision_type = "child_state_terminal_gate",
          monotonic = true,
        },
        {
          state = "ready",
          output_variant = "child_pr_closed_unmerged_replaced",
          postcondition_family = "parent_resume_from_child_state_terminal",
          decision_type = "child_state_terminal_gate",
          failure = true,
          replacement = true,
          bump = true,
        },
        {
          state = "blocked",
          output_variant = "child_pr_not_merged",
          postcondition_family = "parent_resume_from_child_state_terminal",
          decision_type = "child_state_terminal_gate",
          terminal = true,
          monotonic = true,
        },
      },
    }),
    dedup_shape = "child-state-terminal/<proposal>/<version>/<pr>",
    required_facts = {
      fact("state", "marker-read"),
      fact("pr-delegation", "marker-read"),
      fact("child-state", "marker-read"),
    },
    advancing_facts = {
      advancing_fact("child-state", "merged", { issue = true, liveness_scan = true }, "source_ref:pr"),
      advancing_fact("canonical-child-pr-merged", "merged", { issue = true, liveness_scan = true }, "source_ref:pr"),
      advancing_fact("child-state", "ready", { issue = true, liveness_scan = true }, "source_ref:pr"),
      advancing_fact("child-state", "blocked", { issue = true, liveness_scan = true }, "source_ref:pr"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      version = "marker:state.version",
      pr_number = "marker:pr-delegation.pr",
      pr_proposal_id = "marker:pr-delegation.pr_proposal",
      source_ref = "source_ref:pr",
    },
    version_identity = "strip_transition_version_suffixes(state.version)",
    effects = effect(
      { "github-proxy.github_issue_comment_request", "github-proxy.github_issue_label_request" },
      "awaiting-pr replay polls pr-delegation and child PR state; a child terminal writes one parent CAS, nonterminal child state defers",
      "replay_awaiting_pr_state"
    ),
    marker_facts = "state:v1 awaiting-pr plus pr-delegation:v1",
    kickoff = "devloop_observe_redrive",
    replay = "Observe/liveness read pr-delegation, fetch the child PR state marker, and CAS the parent only on a matching child terminal.",
  }
end
