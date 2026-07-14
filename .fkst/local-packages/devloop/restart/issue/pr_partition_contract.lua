local P = {}

local ISSUE_STATES = {
  "thinking",
  "dependency_wait",
  "ready",
  "implementing",
  "awaiting-pr",
  "impl-failed",
  "merged",
  "blocked",
}

local PR_PHASE_STATES = {
  "pr-open",
  "reviewing",
  "fixing",
  "review-meta",
  "merge-ready",
  "merging",
}

local PR_TERMINAL_STATES = {
  "merged",
  "closed-unmerged",
  "blocked",
}

local AWAITING_PR_CONTRACT = {
  state = "awaiting-pr",
  responsibility = "parent issue polls one delegated PR child terminal state",
  liveness_class = "child_workflow_wait",
  marker_facts = {
    "state:v1 awaiting-pr",
    "pr-delegation:v1",
  },
  child_terminal_states = {
    "merged",
    "closed-unmerged",
    "blocked",
  },
}

local function set_from(list)
  local set = {}
  for _, value in ipairs(list) do
    set[value] = true
  end
  return set
end

local ISSUE_STATE_SET = set_from(ISSUE_STATES)
local PR_PHASE_STATE_SET = set_from(PR_PHASE_STATES)
local PR_TERMINAL_STATE_SET = set_from(PR_TERMINAL_STATES)

for state, _ in pairs(ISSUE_STATE_SET) do
  if PR_PHASE_STATE_SET[state] then
    error("github-devloop: PR partition contract states must be disjoint")
  end
end

local function copy_list(list)
  local copied = {}
  for index, value in ipairs(list) do
    copied[index] = value
  end
  return copied
end

local function copy_table(table_value)
  local copied = {}
  for key, value in pairs(table_value) do
    if type(value) == "table" then
      copied[key] = copy_table(value)
    else
      copied[key] = value
    end
  end
  return copied
end

function P.issue_states()
  return copy_list(ISSUE_STATES)
end

function P.pr_phase_states()
  return copy_list(PR_PHASE_STATES)
end

function P.pr_terminal_states()
  return copy_list(PR_TERMINAL_STATES)
end

function P.awaiting_pr_contract()
  return copy_table(AWAITING_PR_CONTRACT)
end

function P.state_allowed_for_saga(saga_kind, state)
  if saga_kind == "issue" then
    return ISSUE_STATE_SET[state] == true
  end
  if saga_kind == "pr" then
    return PR_PHASE_STATE_SET[state] == true or PR_TERMINAL_STATE_SET[state] == true
  end
  return false
end

-- Step 1 target: add a scoped current-state reader at the department boundary
-- using the production version-CAS comparator and entity-scoped comments.

function P.install(M)
  M.pr_partition_contract = P
end

return P
