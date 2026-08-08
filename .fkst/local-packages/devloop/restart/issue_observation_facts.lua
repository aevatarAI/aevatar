local F = {
  schema = "restart-owner-observation-facts.v1",
  owner = "github-devloop",
  source_rows_fingerprint = "71f027dc",
  states = {
    ["awaiting-pr"] = {
      from_state = "awaiting-pr",
      terminal = false,
      driving_queue = "devloop_observe_redrive",
      budget_minutes = 259200,
    },
    blocked = {
      from_state = "blocked",
      terminal = false,
      driving_queue = "github-devloop-decompose.devloop_decompose",
      budget_minutes = 1440,
    },
    dependency_wait = {
      from_state = "dependency_wait",
      terminal = false,
      driving_queue = "devloop_observe_redrive",
      budget_minutes = 525600,
    },
    declined = {
      from_state = "declined",
      terminal = true,
      driving_queue = "none",
      budget_minutes = nil,
    },
    ["impl-failed"] = {
      from_state = "impl-failed",
      terminal = false,
      driving_queue = "devloop_ready",
      budget_minutes = 1440,
    },
    implementing = {
      from_state = "implementing",
      terminal = false,
      driving_queue = "devloop_ready",
      budget_minutes = 120,
    },
    merged = {
      from_state = "merged",
      terminal = true,
      driving_queue = "none",
      budget_minutes = nil,
    },
    ready = {
      from_state = "ready",
      terminal = false,
      driving_queue = "devloop_ready",
      budget_minutes = 120,
    },
    thinking = {
      from_state = "thinking",
      terminal = false,
      driving_queue = "consensus.proposal",
      budget_minutes = 150,
    },
  },
}

function F.transition_row(state_name)
  local state = F.states[state_name]
  if state == nil then
    return nil
  end
  return {
    from_state = state.from_state,
    terminal = state.terminal,
    driving_queue = state.driving_queue,
    budget = state.budget_minutes and { minutes = state.budget_minutes } or nil,
  }
end

function F.budget_minutes(state_name)
  local state = F.states[state_name]
  return state and state.budget_minutes or nil
end

return F
