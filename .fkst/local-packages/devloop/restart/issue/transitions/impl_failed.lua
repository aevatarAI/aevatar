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
    from_state = "impl-failed",
    liveness_class_id = "impl_failed.operator_reentry",
    watchdog = watchdog("row-budget-bounds-receiver", 1440),
    actionable_epoch = actionable_epoch("state_entry:v1"),
    terminal = false,
    to_states = { "implementing" },
    driving_queue = "devloop_ready",
    observe_surfaces = { issue = true, liveness_scan = true },
    output_obligation = obligation({ "impl-failure:v1 retryable fact", "operator reready/reimplement command", "state:v1 implementing", "timeout-reconcile:v1" }, { "implementing", "impl-failed" }),
    reentry_commands = { "reready", "reimplement" },
    budget = budget(1440, "No receiver work is expected; the row waits up to 1410 minutes for operator reentry before the 30 minute watchdog margin."),
    liveness_contract = liveness({
      mode = "row-budget-bounds-receiver",
      receiver_bound_minutes = 0,
      external_wait_bound_minutes = 1410,
    }),
    on_timeout = timeout("devloop_ready"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "operator-reentry",
      driving_queue = "devloop_ready",
      state_kind = "queue_wait",
      liveness_class = "impl_failed.operator_reentry",
      input_fact_family = "retryable-implementation-failure",
      output_postcondition_family = "implementation-retry",
      phase_rank = M.stage_rank("impl-failed"),
      lineage_keys = { "state.version", "impl-failure.dedup", "source_ref" },
      successors = {
        {
          state = "implementing",
          output_variant = "retry-implementation",
          postcondition_family = "implementation-retry",
          bump = true,
        },
      },
    }),
    payload_builder = payloads_builders.build_devloop_ready_payload,
    dedup_shape = "ready/<impl-failure inner dedup> with impl_retry_attempt=<impl-failure.attempt+1>",
    required_facts = { fact("state", "marker-read"), fact("impl-failure", "marker-read"), fact("dependency-release", "marker-read") },
    advancing_facts = {
      advancing_fact("impl-failure", "implementing", { issue = true, liveness_scan = true }, "source_ref:issue"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      dedup_key = "marker:impl-failure.dedup",
      source_ref = "source_ref:issue",
    },
    version_identity = "ready_payload_inner_version(impl-failure.dedup) plus next_impl_retry_attempt(impl-failure)",
    effects = effect({ "devloop_ready" }, "impl-failed replay is complete when trusted retryable impl-failure attempt is below the retry ceiling"),
    marker_facts = "state:v1 impl-failed plus impl-failure:v1 retryable reason attempt<N",
    kickoff = "devloop_ready",
    replay = "Observe re-raises ready/<version> after one observe tick for bounded retryable implementation failures.",
  }
end
