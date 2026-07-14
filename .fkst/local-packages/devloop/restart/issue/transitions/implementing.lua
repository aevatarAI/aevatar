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
    from_state = "implementing",
    liveness_class_id = "implementing.active",
    watchdog = {
      mode = "live-defer",
      budget_ms = 120 * 60 * 1000,
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
    to_states = { "awaiting-pr", "impl-failed" },
    driving_queue = "devloop_ready",
    observe_surfaces = { issue = true, liveness_scan = true },
    output_obligation = obligation({ "state:v1 awaiting-pr", "state:v1 impl-failed", "timeout-reconcile:v1" }, { "awaiting-pr", "impl-failed" }),
    budget = budget(120, "A live implementation codex defers when fkst.codex_runs() positively reports a matching run with an unexpired run-derived deadline, or when codex run liveness is transiently indeterminate; a permanently indeterminate signal is bounded by this row budget."),
    liveness_contract = liveness({
      mode = "live-defer",
      real_execution = {
        primitive = "fkst.codex_runs",
        match = {
          role = "implement",
          proposal_id = "state.proposal_id",
          dedup_key = "state.version",
        },
        status = "running",
        on_error = "defer",
        indeterminate_timeout = "row-budget",
      },
    }),
    on_timeout = timeout("devloop_ready"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "code-producer",
      driving_queue = "devloop_ready",
      state_kind = "worker",
      liveness_class = "implementing.active",
      input_fact_family = "ready/devloop_ready",
      output_postcondition_family = "revision_published",
      phase_rank = M.stage_rank("implementing"),
      lineage_keys = { "state.version", "implementing.dedup", "source_ref" },
      successors = {
        {
          state = "awaiting-pr",
          output_variant = "revision_published",
          postcondition_family = "revision_published",
          monotonic = true,
        },
        {
          state = "impl-failed",
          output_variant = "revision_failed",
          failure = true,
          monotonic = true,
        },
      },
    }),
    payload_builder = payloads_builders.build_devloop_ready_payload,
    dedup_shape = "ready/<implementing_inner_version> with impl_retry_attempt=<implementation_retry_attempt(state.version)>",
    required_facts = {
      fact("state", "marker-read"),
      fact("implementing", "marker-read"),
      fact("implement-attempt", "marker-read"),
      fact("branch-head", "fetch-before-compare"),
    },
    advancing_facts = {
      advancing_fact("implementing", "implementing", { issue = true, liveness_scan = true }, "source_ref:issue"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      dedup_key = "marker:state.version",
      source_ref = "source_ref:issue",
    },
    version_identity = "ready_payload_inner_version(state.version) plus implementation_retry_attempt(state.version)",
    effects = effect({ "devloop_ready" }, "implementing replay is complete only when observe_issue can re-raise devloop_ready with the frozen implementing version for implement to re-derive PR link, remote branch, local branch, or bounded retry"),
    marker_facts = "active run uses state:v1 implementing plus fkst.codex_runs real execution; implement-attempt:v1 is audit-only and implementing:v1 exists only after codex completion",
    kickoff = "devloop_ready",
    replay = "Observe re-raises devloop_ready only when no matching codex run exists; implement then re-derives PR link, remote branch, local branch, or bounded retry.",
    span_contract = span_contract({
      department = "implement",
      durable_start_marker = "implement-attempt:v1",
      spawn_predecessor = "raise_implementing_state",
      spawn_function = "run_attempt",
    }),
  }
end
