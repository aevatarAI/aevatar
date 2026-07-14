return function(M, h)
  local fact = h.fact
  local obligation = h.obligation
  local effect = h.effect
  local budget = h.budget
  local timeout = h.timeout
  local responsibility_signature = h.responsibility_signature
  return {
    from_state = "merged",
    terminal = true,
    to_states = {},
    responsibility_signature = responsibility_signature({
      receiver_kind = "none",
      driving_queue = "none",
      state_kind = "terminal_hold",
      liveness_class = "terminal",
      input_fact_family = "merged-fact",
      output_postcondition_family = "terminal-merged",
      phase_rank = M.stage_rank("merged"),
      lineage_keys = { "state.version", "merged.head_sha" },
      successors = {},
    }),
    marker_facts = "state:v1 merged plus merged:v1",
    replay = "Merged is a legal terminal state and has no output obligation.",
  }
end
