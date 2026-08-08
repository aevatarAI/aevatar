-- Required-fact gathering for restart replay: validate / fetch / materialize the
-- durable facts a replay row declares, leaving replayer.lua to select and execute
-- replay strategies. Extracted from replayer.lua as a pure structural refactor
-- (Step 0.0 line-budget containment); behavior is unchanged.
local m_facts = require("devloop.markers.facts")
local parsers_pr = require("devloop.parsers.pr")
local conv_rounds = require("devloop.convergence.rounds")
local forge_validators = require("devloop.forge_validators")
local m_mgw = require("devloop.merge_gate_wait")
local decompose_lib = require("devloop.decompose")
local transition_version = require("contract.transition_version")
local replay_fields = require("devloop.replay_fields")

local F = {}

local function find_linked_pr(snapshot, pr_number)
  for _, item in ipairs(snapshot and snapshot.prs or {}) do
    if tostring(item.number or "") == tostring(pr_number or "") then
      return item.current
    end
  end
  return nil
end

local function snapshot_with_pr_comments(current_pr)
  local snapshot = { comments = {}, prs = {} }
  for _, comment in ipairs(current_pr and current_pr.comments or {}) do
    table.insert(snapshot.comments, comment)
  end
  return snapshot
end

local function snapshot_from_issue_comments(M, repo, proposal_id, comments)
  return M.linked_pr_surface_snapshot(repo, proposal_id, comments or {})
end

local function validate_required_fact(required)
  if type(required) ~= "table" or type(required.family) ~= "string" or required.family == "" then
    error("github-devloop: invalid replay required fact")
  end
  if required.freshness ~= "marker-read" and required.freshness ~= "fetch-before-compare" then
    error("github-devloop: invalid replay fact freshness")
  end
end

local function has_required_fact(row, family)
  for _, required in ipairs(row.required_facts or {}) do
    validate_required_fact(required)
    if required.family == family then
      return true
    end
  end
  return false
end

local function current_pr_fact(facts)
  local link = facts.link
  if link == nil then
    return nil
  end
  return find_linked_pr(facts.snapshot, link.pr_number)
end

local function child_pr_delegation_fact(M, facts)
  return facts.pr_delegation
    or facts["pr-delegation"]
    or m_facts.pr_delegation_fact(facts.snapshot.comments, facts.proposal_id, facts.state and facts.state.version)
end

local function fetch_child_state_fact(M, facts)
  if facts.child_state ~= nil then
    return facts.child_state
  end
  local delegation = child_pr_delegation_fact(M, facts)
  if delegation == nil then
    return nil
  end
  facts.pr_delegation = delegation
  facts["pr-delegation"] = delegation
  if facts.current_pr == nil then
    local view = M.fetch_pr_view_origin(facts.issue.repo, delegation.pr_number, nil, {
      force_fresh = true,
      consumer = "replay_child_state",
    })
    if view.exit_code ~= 0 then
      error("github-devloop: child-state PR view failed: " .. tostring(view.stderr))
    end
    facts.current_pr = parsers_pr.parse_pr_view_origin(view.stdout)
    facts.current_pr.number, facts.current_pr.force_fresh = delegation.pr_number, true
  end
  facts.child_state = require("devloop.entity").current_entity_state(facts.current_pr.comments, delegation.proposal_id)
  return facts.child_state
end

local function require_marker_fact(M, facts, family)
  if family == "state" then
    return facts.state
  end
  if family == "pr-link" then
    return m_facts.pr_link_fact(facts.snapshot.comments, facts.proposal_id) or (facts._synthetic_pr_link ~= true and facts.link or nil)
  end
  if family == "pr-delegation" then
    return child_pr_delegation_fact(M, facts)
  end
  if family == "child-state" then
    return fetch_child_state_fact(M, facts)
  end
  if family == "converge-round" then
    local base_version = M.version_loop_round(facts.state.version) > 0 and conv_rounds.converge_base_version(facts.state.version) or nil
    return M.latest_complete_converge_round(facts.snapshot.comments, facts.proposal_id, base_version, facts.issue.source_ref)
  end
  if family == "dependency-release" then
    return M.dependency_release_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "dependency-wait" then
    return M.dependency_hold_fact(facts.snapshot.comments, facts.proposal_id)
  end
  if family == "review-result" then
    return m_facts.review_reject_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "fix-feedback" then
    return M.fixing_replay_feedback_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "review-meta" then
    local current_pr = current_pr_fact(facts)
    if current_pr ~= nil and forge_validators.is_git_sha(current_pr.head_sha) then
      return M.review_meta_replay_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
    end
    return m_facts.review_meta_fix_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "fix-reflection" or family == "review-converge-round" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return M.review_meta_replay_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "merge-gate" then
    return m_facts.merge_gate_fix_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "merge-gate-wait" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_mgw.merge_gate_wait_fact(M, facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "decomposed" then
    local link = facts.link
    if link == nil then
      return nil
    end
    return decompose_lib.decomposed_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, link.pr_number)
  end
  if family == "implementing" then
    return m_facts.implementing_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "implement-attempt" then
    local attempt_version = facts.state.version
    if facts.state.state == "implementing" then
      attempt_version = transition_version.strip_timeout_suffixes(attempt_version)
    end
    return M.latest_implement_attempt_fact(facts.snapshot.comments, facts.proposal_id, attempt_version)
  end
  if family == "impl-failure" then
    return M.impl_failure_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version)
  end
  if family == "merge-ready" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_facts.merge_ready_fact(facts.snapshot.comments, facts.proposal_id, facts.state.version, facts.link.pr_number, current_pr.head_sha)
  end
  if family == "merging" then
    local current_pr = current_pr_fact(facts)
    if current_pr == nil or not forge_validators.is_git_sha(current_pr.head_sha) then
      return nil
    end
    return m_facts.merging_fact(facts.snapshot.comments, facts.proposal_id, facts.link.pr_number, facts.state.version, current_pr.head_sha)
  end
  if family == "review-carry-over" then
    return nil
  end
  if rawget(facts, family) ~= nil then
    return rawget(facts, family)
  end
  error("github-devloop: unsupported replay marker fact family: " .. tostring(family))
end

local function gather_fetch_before_compare_fact(M, facts, entity, family)
  if family == "pr-head" then
    if facts.link ~= nil and facts.current_pr ~= nil then
      facts.snapshot = snapshot_with_pr_comments(facts.current_pr)
      for _, comment in ipairs(facts.current and facts.current.comments or {}) do table.insert(facts.snapshot.comments, comment) end
      table.insert(facts.snapshot.prs, { number = facts.link.pr_number, current = facts.current_pr })
      facts.snapshot.state = facts.state
    else
      facts.snapshot = snapshot_from_issue_comments(M, entity.repo, facts.proposal_id, facts.current and facts.current.comments or {})
      facts.link = m_facts.pr_link_fact(facts.snapshot.comments, facts.proposal_id)
    end
    return true
  end
  if family == "base-head" or family == "ci-status" then
    return true
  end
  if family == "decompose-children" then
    local child_list = M.gh_issue_list_decompose_children(entity.repo, facts.proposal_id, 30)
    if child_list.exit_code ~= 0 then
      error("github-devloop: gh issue decompose child list failed: " .. tostring(child_list.stderr))
    end
    facts.decompose_children = decompose_lib.parse_decompose_child_issue_list(child_list.stdout)
    return facts.decompose_children
  end
  if family == "branch-head" then
    return true
  end
  error("github-devloop: unsupported replay fetch-before-compare fact family: " .. tostring(family))
end

local function store_gathered_marker_fact(facts, family, value)
  facts[family] = value
  if family == "pr-link" then
    facts.link = value
  elseif family == "review-result" then
    facts.feedback = facts.feedback or value
  elseif family == "review-meta" then
    facts.review_meta = facts.review_meta or value
    facts.feedback = facts.feedback or value
  elseif family == "fix-feedback" then
    facts.fix_feedback = value
    facts.feedback = facts.feedback or value
  elseif family == "fix-reflection" or family == "review-converge-round" then
    facts.review_meta = facts.review_meta or value
  elseif family == "merge-gate" then
    facts.feedback = facts.feedback or value
  elseif family == "merge-gate-wait" then
    facts.merge_gate_wait = value
  elseif family == "decomposed" then
    facts.decomposed = value
  elseif family == "impl-failure" then
    facts.impl_failure = value
  elseif family == "merge-ready" then
    facts["merge-ready"] = value
    facts.merge_ready = value
  elseif family == "merging" then
    facts.merging = value
  elseif family == "pr-delegation" then
    facts.pr_delegation = value
    facts["pr-delegation"] = value
  elseif family == "child-state" then
    facts.child_state = value
  end
end

local function gather_required_facts(M, row, entity, state, provided)
  local gathered = {}
  for key, value in pairs(provided or {}) do
    gathered[key] = value
  end
  gathered.issue = entity
  gathered.state = state
  gathered.proposal_id = gathered.proposal_id or replay_fields.marker_value({ state = state }, "state", "proposal")

  gathered.snapshot = gathered.snapshot or { comments = gathered.current and gathered.current.comments or {}, prs = {}, state = state }
  if gathered.current_pr ~= nil and gathered.link ~= nil then
    -- current_pr.comments may be the SAME table as snapshot.comments: callers
    -- (e.g. the PR liveness sweep) pass current_pr === current and a snapshot
    -- whose comments alias current.comments. Appending into a list while
    -- iterating that same list with ipairs never terminates and allocates
    -- unboundedly. When they alias, the PR comments are already present, so the
    -- append is a no-op; only copy across when they are genuinely distinct lists.
    if gathered.current_pr.comments ~= gathered.snapshot.comments then
      for _, comment in ipairs(gathered.current_pr.comments or {}) do table.insert(gathered.snapshot.comments, comment) end
    end
    table.insert(gathered.snapshot.prs, { number = gathered.link.pr_number, current = gathered.current_pr })
  end

  for _, required in ipairs(row.required_facts or {}) do
    validate_required_fact(required)
    if required.freshness == "fetch-before-compare" then
      gather_fetch_before_compare_fact(M, gathered, entity, required.family)
    end
  end

  gathered.link = gathered.link or m_facts.pr_link_fact(gathered.snapshot.comments, gathered.proposal_id)

  for _, required in ipairs(row.required_facts or {}) do
    if required.freshness == "marker-read" then
      store_gathered_marker_fact(gathered, required.family, require_marker_fact(M, gathered, required.family))
    end
  end

  return gathered
end

F.find_linked_pr = find_linked_pr
F.gather_required_facts = gather_required_facts
function F.gather_replay_required_facts(M, row, entity, state, facts)
  return gather_required_facts(M, row, entity, state, facts or {})
end

return F
