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
  local responsibility_signature = h.responsibility_signature; local span_contract = h.span_contract
  return {
    from_state = "thinking",
    liveness_class_id = "thinking.active",
    watchdog = {
      mode = "live-defer",
      budget_ms = 150 * 60 * 1000,
      on_stale = {
        op = "redrive_receiver",
      },
    },
    actionable_epoch = {
      source = "codex_run:v1",
      generation_source = "same_as_actionable_epoch",
    },
    defer = {
      kind = "codex_run",
      redrive_opens_generation = true,
    },
    terminal = false,
    to_states = { "ready", "blocked" },
    driving_queue = "consensus.proposal",
    observe_surfaces = { issue = true, liveness_scan = true },
    timeout_surfaces = { issue = true, issue_liveness_scan = true, liveness_scan = true },
    output_obligation = obligation({ "consensus.consensus_reached", "consensus.consensus_converge", "timeout-reconcile:v1" }, { "ready", "blocked", "thinking" }),
    budget = budget(150, "A live consensus receiver defers when fkst.codex_runs() positively reports a matching run with an unexpired run-derived deadline, or when codex run liveness is transiently indeterminate; a permanently indeterminate signal is bounded by this row budget."),
    liveness_contract = liveness({
      mode = "live-defer",
      real_execution = {
        primitive = "fkst.codex_runs",
        match = {
          role = "consensus",
          proposal_id = "state.proposal_id",
          dedup_key = "state.version",
        },
        status = "running",
        on_error = "defer",
        indeterminate_timeout = "row-budget",
      },
    }),
    on_timeout = timeout("consensus.proposal"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "consensus-worker",
      driving_queue = "consensus.proposal",
      state_kind = "worker",
      liveness_class = "thinking.active",
      input_fact_family = "issue-proposal",
      output_postcondition_family = "issue-consensus",
      phase_rank = M.stage_rank("thinking"),
      lineage_keys = { "state.version", "source_ref" },
      successors = {
        {
          state = "ready",
          output_variant = "consensus-reached",
          postcondition_family = "issue-consensus",
          monotonic = true,
        },
        {
          state = "blocked",
          output_variant = "consensus-stalled",
          failure = true,
          terminal = true,
          monotonic = true,
        },
      },
    }),
    payload_builder = payloads_builders.build_proposal,
    dedup_shape = "proposal:<proposal_id>/<updated_at> or consensus:<base_version>/loop/<n>",
    required_facts = { fact("state", "marker-read") },
    advancing_facts = {
      advancing_fact("converge-round", "blocked", { issue = true, liveness_scan = true }, "source_ref:issue"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      dedup_key = "marker:state.version",
      source_ref = "source_ref:issue",
    },
    version_identity = "strip_transition_version_suffixes(state.version)",
    effects = effect({ "consensus.proposal" }, "consensus proposal dedup is derived from state.version or next complete converge-round"),
    marker_facts = "active run uses state:v1 thinking plus fkst.codex_runs real execution; converge-round:v1 remains an audit/progress fact, not a heartbeat",
    kickoff = "consensus.proposal",
    replay = "Initial thinking reuses the state version as proposal dedup; convergence replays the next /loop/N from the latest complete converge-round marker.",
    span_contract = span_contract({
      department = "external:consensus",
      durable_start_marker = "state:v1 thinking",
      spawn_predecessor = "consensus.proposal",
      spawn_function = "consensus.decide",
    }),
  }
end
