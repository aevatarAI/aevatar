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
  local responsibility_signature = h.responsibility_signature
  return {
    from_state = "dependency_wait",
    liveness_class_id = "dependency_held_blocker_bound",
    watchdog = watchdog("live-defer", 525600),
    actionable_epoch = {
      source = "live_defer_epoch:v1",
      generation_source = "same_as_actionable_epoch",
    },
    defer = {
      kind = "release_gate",
      live_marker = "dependency-wait:v1",
      freshness_ms = 525600 * 60 * 1000,
      clear_fact = "dependency-release:v1",
      observed_fact = "dependency-wait-observed:v1",
      clear_opens_generation = true,
    },
    terminal = false,
    to_states = { "dependency_wait", "ready", "blocked" },
    driving_queue = "devloop_observe_redrive",
    observe_surfaces = { issue = true, liveness_scan = true },
    timeout_surfaces = { issue = true, issue_liveness_scan = true, liveness_scan = true },
    output_obligation = obligation({ "dependency-wait:v1", "dependency-release:v1", "state:v1 ready", "timeout-reconcile:v1" }, { "dependency_wait", "ready", "blocked" }),
    budget = budget(525600, "Dependency wait is blocker-bound; long-lived open blockers are refreshed, release creates a fresh ready entry, and stale resolver facts fail closed."),
    liveness_contract = liveness({
      mode = "live-defer",
      signal = {
        family = "dependency-wait",
        resolver = "dependency-hold",
        producer = "dependency-wait",
        surface = "issue-comment-stream",
        version_form = "raw",
        max_age_minutes = 525600,
      },
    }),
    on_timeout = timeout("devloop_observe_redrive"),
    responsibility_signature = responsibility_signature({
      receiver_kind = "issue",
      driving_queue = "devloop_observe_redrive",
      state_kind = "gate",
      liveness_class = "dependency_held_blocker_bound",
      input_fact_family = "ready-base-preconditions-and-open-blockers",
      output_postcondition_family = "dependency-release-or-blocker-tracking",
      phase_rank = M.stage_rank("dependency_wait"),
      lineage_keys = { "state.version", "source_ref", "dependency_epoch", "blocker_fingerprint" },
      decision_type = "dependency_gate",
      successors = {
        {
          state = "dependency_wait",
          output_variant = "blockers_still_open",
          postcondition_family = "dependency-release-or-blocker-tracking",
          decision_type = "dependency_gate",
          monotonic = true,
        },
        {
          state = "ready",
          output_variant = "blockers_released",
          postcondition_family = "dependency-release-or-blocker-tracking",
          decision_type = "dependency_gate",
          bump = true,
        },
        {
          state = "blocked",
          output_variant = "dependency_resolver_stale",
          failure = true,
          terminal = true,
          decision_type = "dependency_gate",
          monotonic = true,
        },
      },
    }),
    payload_builder = payloads_builders.build_devloop_ready_payload,
    dedup_shape = "ready/<state.version> when blockers release",
    required_facts = { fact("state", "marker-read"), fact("dependency-wait", "marker-read"), fact("dependency-release", "marker-read") },
    advancing_facts = {
      advancing_fact("dependency-gate", "dependency_wait", { issue = true, liveness_scan = true }, "source_ref:issue"),
      advancing_fact("dependency-gate", "ready", { issue = true, liveness_scan = true }, "source_ref:issue"),
      advancing_fact("dependency-gate", "blocked", { issue = true, liveness_scan = true }, "source_ref:issue"),
    },
    payload_fields = {
      proposal_id = "marker:state.proposal",
      dedup_key = "marker:state.version",
      source_ref = "source_ref:issue",
    },
    version_identity = "strip_transition_version_suffixes(state.version)",
    effects = effect(
      { "dependency-wait-marker", "dependency-release-marker", "state:v1 ready", "devloop_ready" },
      "dependency_wait replay refreshes blocker facts while held and creates a fresh ready entry; comment handoff raises devloop_ready exactly when the dependency gate release ready marker write is acknowledged",
      "dependency_gate_rederive"
    ),
    marker_facts = "state:v1 dependency_wait plus dependency-wait:v1",
    kickoff = "devloop_observe_redrive",
    replay = "Observe/liveness re-derive blockedBy; held blockers refresh dependency_wait, released blockers enter fresh ready and route devloop_ready through comment handoff.",
  }
end
