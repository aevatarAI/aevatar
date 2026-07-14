local payloads_builders = require("devloop.payloads.builders")
return function(M, h)
  local fact = h.fact
  local obligation = h.obligation
  local effect = h.effect
  local budget = h.budget
  local timeout = h.timeout
  local liveness = h.liveness
  local watchdog = h.watchdog
  local advancing_fact = h.advancing_fact
  local actionable_epoch = h.actionable_epoch
  local responsibility_signature = h.responsibility_signature
  return {
    from_state = "ready",
    liveness_class_id = "actionable_kickoff",
    watchdog = watchdog("row-budget-bounds-receiver", 120),
    actionable_epoch = actionable_epoch("state_entry:v1"),
    terminal = false,
    to_states = { "dependency_wait", "implementing", "blocked" },
    driving_queue = "devloop_ready",
    observe_surfaces = { issue = true, liveness_scan = true },
    output_obligation = obligation({ "state:v1 implementing", "timeout-reconcile:v1" }, { "implementing", "dependency_wait", "blocked" }),
    budget = budget(120, "Actionable ready governs the implement codex run until the implementing marker is written at completion; 120 bounds a 60-minute codex attempt plus margin (matching fixing)."),
    liveness_contract = liveness({
      mode = "row-budget-bounds-receiver",
      receiver_bound_minutes = 15,
      external_wait_bound_minutes = 0,
    }),
    on_timeout = timeout("devloop_ready"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "issue",
      driving_queue = "devloop_ready",
      state_kind = "queue_wait",
      liveness_class = "actionable_kickoff",
      input_fact_family = "ready-base-preconditions-and-no-open-blockers",
      output_postcondition_family = "implementation_kickoff",
      phase_rank = M.stage_rank("ready"),
      lineage_keys = { "state.version", "source_ref", "actionable_epoch" },
      successors = {
        {
          state = "implementing",
          output_variant = "implementation_kicked_off",
          postcondition_family = "implementation_kickoff",
          monotonic = true,
        },
        {
          state = "dependency_wait",
          output_variant = "blocker_reappeared",
          regression = "blocker_reappeared",
          failure = true,
          bump = true,
        },
        {
          state = "blocked",
          output_variant = "actionable_kickoff_timeout",
          terminal = true,
          monotonic = true,
        },
      },
    }),
    payload_builder = payloads_builders.build_devloop_ready_payload,
    dedup_shape = "ready/<state.version>",
    required_facts = { fact("state", "marker-read") },
    advancing_facts = {
      advancing_fact("dependency-gate", "dependency_wait", { issue = true, liveness_scan = true }, "source_ref:issue"),
      advancing_fact("dependency-gate", "implementing", { issue = true, liveness_scan = true }, "source_ref:issue"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      dedup_key = "marker:state.version",
      source_ref = "source_ref:issue",
    },
    version_identity = "strip_transition_version_suffixes(state.version)",
    effects = effect(
      { "devloop_ready" },
      "ready replay is complete only when observe_issue can re-raise devloop_ready for an actionable blocker-free issue",
      "result_effects_complete"
    ),
    marker_facts = "state:v1 ready",
    kickoff = "devloop_ready",
    replay = "Raise ready/<version> only after dependency gate re-derives no effective open blockers.",
  }
end
