local decompose_lib = require("devloop.decompose")

return function(M, h)
  local fact = h.fact
  local obligation = h.obligation
  local effect = h.effect
  local budget = h.budget
  local timeout = h.timeout
  local liveness = h.liveness
  local watchdog = h.watchdog
  local actionable_epoch = h.actionable_epoch
  local responsibility_signature = h.responsibility_signature
  local decompose_queue = M.decompose_package_queue()
  return {
    from_state = "blocked",
    liveness_class_id = "blocked.operator_reentry",
    watchdog = watchdog("row-budget-bounds-receiver", 1440),
    actionable_epoch = actionable_epoch("state_entry:v1"),
    terminal = false,
    to_states = {},
    driving_queue = decompose_queue,
    observe_surfaces = { issue = true, pr = true, liveness_scan = true },
    output_obligation = obligation({ "decomposed:v1", "github-proxy.github_issue_create_request[*]", "operator reintake command" }, { "blocked", "thinking" }),
    reentry_commands = { "rereview", "reintake" },
    operator_reentry = {
      kind = "external_command",
      not_autonomous_successor = true,
      resets_budget = true,
      commands = { "rereview", "reintake" },
    },
    non_durable_advance = {
      category = "terminal-hold",
      reason = "blocked is a recovery hold: operator commands or the decompose escape can create follow-up work, but no single poll-derived durable fact is expected to advance this row to a normal successor.",
    },
    budget = budget(1440, "No receiver work is expected; the row waits up to 1410 minutes for operator reentry before the 30 minute watchdog margin."),
    liveness_contract = liveness({
      mode = "row-budget-bounds-receiver",
      receiver_bound_minutes = 0,
      external_wait_bound_minutes = 1410,
    }),
    on_timeout = timeout(decompose_queue),
    responsibility_signature = responsibility_signature({
      receiver_kind = "operator-reentry",
      driving_queue = decompose_queue,
      state_kind = "budget_bounded_recovery",
      liveness_class = "blocked.operator_reentry",
      input_fact_family = "blocked-recovery-hold",
      output_postcondition_family = "blocked-decompose-escape",
      phase_rank = M.stage_rank("blocked"),
      lineage_keys = { "state.version", "pr-link.pr", "source_ref" },
      successors = {},
      watchdog_escape = {
        kind = "watchdog_escape",
        queue = decompose_queue,
        output_variant = "budget_exhausted_decompose",
        postcondition_family = "blocked-decompose-escape",
        opens_generation = true,
      },
      operator_reentry = {
        kind = "external_command",
        not_autonomous_successor = true,
        resets_budget = true,
      },
    }),
    payload_builder = function(...)
      return decompose_lib.build_decompose_replay_payload(M, ...)
    end,
    dedup_shape = "forward:decompose/<proposal_id>/<version>; replay:decompose/replay/<proposal_id>/<version>/<pr>/<expected_child_count>/<completed_child_count>",
    required_facts = {
      fact("state", "marker-read"),
      fact("pr-link", "marker-read"),
      fact("decomposed", "marker-read"),
      fact("fix-feedback", "marker-read"),
      fact("decompose-children", "fetch-before-compare"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      version = "marker:state.version",
      pr_number = "marker:pr-link.pr",
      source_ref = "source_ref:pr",
    },
    version_identity = "strip_transition_version_suffixes(state.version)",
    effects = effect(
      { "decomposed-marker", "github-proxy.github_issue_create_request[*]" },
      "blocked decompose replay is complete only when the decomposed marker count and every declared child issue are derivable",
      "decompose_children_complete"
    ),
    marker_facts = "state:v1 blocked plus decomposed:v1 when class decomposition is incomplete",
    kickoff = decompose_queue,
    replay = "Observe can replay decomposed blocked issues when deterministic child completion facts are missing.",
  }
end
