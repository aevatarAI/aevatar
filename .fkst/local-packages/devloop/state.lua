local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local parsers_misc = require("devloop.parsers.misc")
local payloads_predicates = require("devloop.payloads.predicates")
local S = {}
local C = {}
local devloop_base = require("devloop.base")
local transition_version = require("contract.transition_version")
local m_builders = require("devloop.markers.builders")

local label_by_state = { thinking = "fkst-dev:thinking", dependency_wait = "fkst-dev:ready", ready = "fkst-dev:ready", implementing = "fkst-dev:implementing", ["awaiting-pr"] = "fkst-dev:awaiting-pr", ["pr-open"] = "fkst-dev:pr-open", reviewing = "fkst-dev:reviewing", ["merge-ready"] = "fkst-dev:merge-ready", merging = "fkst-dev:merging", merged = "fkst-dev:merged", ["closed-unmerged"] = "fkst-dev:blocked", fixing = "fkst-dev:fixing", ["review-meta"] = "fkst-dev:review-meta", ["impl-failed"] = "fkst-dev:impl-failed", declined = "fkst-dev:declined", blocked = "fkst-dev:blocked" }
local state_labels = {}
for _, label in pairs(label_by_state) do state_labels[label] = true end
local state_graph = { unmanaged = { "thinking" }, thinking = { "dependency_wait", "ready", "declined", "blocked" }, dependency_wait = { "dependency_wait", "ready", "blocked" }, ready = { "dependency_wait", "implementing", "blocked" }, implementing = { "awaiting-pr", "impl-failed" }, ["awaiting-pr"] = { "merged", "ready", "blocked" }, ["pr-open"] = { "reviewing", "blocked" }, reviewing = { "merge-ready", "fixing", "review-meta" }, ["merge-ready"] = { "merging", "blocked" }, merging = { "merged", "reviewing", "fixing", "blocked" }, merged = {}, ["closed-unmerged"] = {}, fixing = { "reviewing", "review-meta", "blocked" }, ["review-meta"] = { "fixing", "blocked" }, ["impl-failed"] = { "implementing" }, declined = {}, blocked = {} }
local issue_state_order = { "thinking", "dependency_wait", "ready", "implementing", "pr-open", "reviewing", "merge-ready", "fixing", "impl-failed", "declined", "blocked", "review-meta", "merging", "merged", "awaiting-pr" }
local state_order = { "thinking", "dependency_wait", "ready", "implementing", "pr-open", "reviewing", "merge-ready", "fixing", "impl-failed", "declined", "blocked", "review-meta", "merging", "merged", "closed-unmerged", "awaiting-pr" }
local state_stage_rank = { thinking = 100, dependency_wait = 500, ready = 500, implementing = 600, ["awaiting-pr"] = 625, ["pr-open"] = 650, reviewing = 675, ["merge-ready"] = 690, merging = 695, fixing = 700, ["review-meta"] = 710, ["impl-failed"] = 750, declined = 800, blocked = 800, ["closed-unmerged"] = 825, merged = 900 }
local function copy_array(values) local out = {}; for _, value in ipairs(values or {}) do table.insert(out, value) end; return out end

local function marker_attrs(marker)
  local attrs = {}
  for key, value in tostring(marker or ""):gmatch('([%w._-]+)="([^"]*)"') do
    attrs[key] = value
  end
  return attrs
end

function C.has_label(labels, expected)
  if type(labels) ~= "table" then
    return false
  end
  for _, label in ipairs(labels) do
    if tostring(label) == expected then
      return true
    end
  end
  return false
end

function C.is_state(state) return label_by_state[state] ~= nil end
function C.is_state_label(label) return state_labels[tostring(label)] == true end
function C.state_label(state) return label_by_state[state] end
function C.state_order() return copy_array(state_order) end
function C.issue_state_order() return copy_array(issue_state_order) end
function C.state_successors(state) return copy_array(state_graph[state]) end
function C.lifecycle_state_set()
  local out = {}
  for state, _ in pairs(label_by_state) do out[state] = true end
  for state, next_states in pairs(state_graph) do
    if state ~= "unmanaged" then out[state] = true end
    for _, next_state in ipairs(next_states or {}) do if next_state ~= "unmanaged" then out[next_state] = true end end
  end
  for _, state in ipairs(state_order) do out[state] = true end
  for state, _ in pairs(state_stage_rank) do out[state] = true end
  return out
end

function C.state_marker(proposal_id, state, version, effects)
  if not C.is_state(state) then
    error("github-devloop: invalid state")
  end
  local effects_field = ""
  if effects ~= nil and tostring(effects) ~= "" then
    effects_field = ' effects="' .. tostring(effects):gsub('"', "'") .. '"'
  end
  return '<!-- fkst:github-devloop:state:v1 proposal="' .. tostring(proposal_id)
    .. '" state="' .. tostring(state)
    .. '" version="' .. tostring(version)
    .. '" stage_rank="' .. tostring(C.stage_rank(state))
    .. '" marker_order_key="' .. C.marker_order_key(version, state)
    .. '"'
    .. effects_field
    .. ' -->'
end

function C.version_order_key(version)
  return transition_version.version_order_key(version)
end

function C.stage_rank(state)
  return state_stage_rank[state] or 0
end

function C.version_updated_at(version)
  return transition_version.updated_at(version)
end

function C.version_loop_round(version)
  return transition_version.loop_round(version)
end

function C.version_fix_round(version)
  return transition_version.fix_round(version)
end

function C.version_review_meta_action_round(version)
  return transition_version.review_meta_action_round(version)
end

function C.version_review_loop_round(version)
  return transition_version.review_loop_round(version)
end

function C.version_timeout_round(version, state_name)
  return transition_version.timeout_round(version, state_name)
end

function C.version_reimplement_round(version)
  return transition_version.reimplement_round(version)
end

function C.version_ready_split_round(version)
  return transition_version.ready_split_round(version)
end

function C.next_fix_version(version)
  return transition_version.next_fix(version)
end

function C.fix_version_from_review_version(version)
  return C.next_fix_version(version)
end

function C.next_review_meta_action_version(version)
  return transition_version.next_review_meta_action(version)
end

function C.next_review_loop_version(version)
  return transition_version.next_review_loop(version)
end

function C.marker_order_key(version, state_or_stage_rank)
  local stage_rank = tonumber(state_or_stage_rank)
  if stage_rank == nil then
    stage_rank = C.stage_rank(state_or_stage_rank)
  end
  return transition_version.marker_order_key(version, stage_rank)
end

local function marker_stage_rank(marker, state)
  local explicit_rank = tonumber(marker:match('stage_rank="(%d+)"'))
  return explicit_rank or C.stage_rank(state)
end

local function state_marker_fact(marker, comment)
  local attrs = marker_attrs(marker)
  local marker_proposal = attrs.proposal
  local marker_state = attrs.state
  local marker_version = attrs.version
  if marker_proposal == nil or not C.is_state(marker_state) then
    return nil
  end
  return {
    proposal_id = marker_proposal,
    state = marker_state,
    version = marker_version,
    stage_rank = marker_stage_rank(marker, marker_state),
    marker_created_at = parsers_misc._comment_created_at(comment),
  }
end

local function versions_equivalent(left, right)
  if left == nil or right == nil then
    return left == right
  end
  if tostring(left) == tostring(right) then
    return true
  end
  return transition_version.safe_version_segment(left) == transition_version.safe_version_segment(right)
end

local function strip_latest_fix_version_suffix(version)
  return transition_version.strip_trailing_fix(version)
end

local function compare_transition_versions(incoming_version, current_version)
  return transition_version.compare(incoming_version, current_version)
end

local function sign_order(value)
  if value > 0 then
    return 1
  end
  if value < 0 then
    return -1
  end
  return 0
end

function C.compare_state_marker_order(current, target_state, target_version)
  if current == nil or current.version == nil then
    return -1
  end
  local version_order = compare_transition_versions(current.version, target_version)
  if version_order ~= 0 then
    return sign_order(version_order)
  end
  return sign_order(C.stage_rank(current.state) - C.stage_rank(target_state))
end

function C.timeout_lineage_matches_current(scheduled, current)
  if type(scheduled) ~= "table" or type(current) ~= "table" then
    return true
  end
  if tostring(current.state or "") ~= tostring(scheduled.state or "") then
    return false, "state-advanced"
  end
  if transition_version.strip_suffixes(current.version) ~= transition_version.strip_suffixes(scheduled.version) then
    return false, "lineage-mismatch"
  end
  return true
end

local function compare_state_marker(a, b)
  if a == nil then
    return true
  end
  local version_order = compare_transition_versions(b.version, a.version)
  if version_order ~= 0 then
    return version_order > 0
  end
  local a_stage_rank = tonumber(a.stage_rank) or C.stage_rank(a.state)
  local b_stage_rank = tonumber(b.stage_rank) or C.stage_rank(b.state)
  if a_stage_rank ~= b_stage_rank then
    return b_stage_rank > a_stage_rank
  end
  local a_key = C.marker_order_key(a.version, a.stage_rank)
  local b_key = C.marker_order_key(b.version, b.stage_rank)
  return b_key > a_key
end

local milestone_domains = {
  ["github-devloop"] = nil,
  ["github-devloop-issue"] = {
    thinking = true,
    dependency_wait = true,
    ready = true,
    implementing = true,
    ["awaiting-pr"] = true,
    ["impl-failed"] = true,
    declined = true,
    blocked = true,
    merged = true,
  },
  ["github-devloop-pr"] = {
    ["pr-open"] = true,
    reviewing = true,
    ["review-meta"] = true,
    ["merge-ready"] = true,
    merging = true,
    fixing = true,
    blocked = true,
    ["closed-unmerged"] = true,
    merged = true,
  },
}

local function domain_allows_state(domain, state)
  if domain == nil or domain == "" then
    return true
  end
  local allowed = milestone_domains[domain]
  if allowed == nil then
    return domain == "github-devloop" and C.is_state(state)
  end
  return allowed[state] == true
end

local function validate_milestone_domain(domain, milestone)
  if domain == nil or domain == "" then
    return
  end
  if milestone_domains[domain] == nil and domain ~= "github-devloop" then
    error("github-devloop: unknown milestone domain")
  end
  if not domain_allows_state(domain, milestone) then
    error("github-devloop: milestone is outside milestone domain")
  end
end

local function lineage_matches(version, opts)
  local options = opts or {}
  if options.lineage_base == nil then
    return true
  end
  local actual = transition_version.strip_suffixes(version)
  local expected = transition_version.strip_suffixes(options.lineage_base)
  return versions_equivalent(actual, expected)
end

function C.comment_bodies(comments)
  local bodies = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(bodies, parsers_misc._comment_body(comment))
  end
  return bodies
end

local function derive_current_marker(comments, proposal_id)
  if type(comments) ~= "table" then
    return nil
  end

  local current = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      if candidate ~= nil and candidate.proposal_id == proposal_id then
        candidate = {
          state = candidate.state,
          version = candidate.version,
          stage_rank = candidate.stage_rank,
          marker_created_at = candidate.marker_created_at,
        }
        if compare_state_marker(current, candidate) then
          current = candidate
        end
      end
    end
  end
  return current or {
    state = nil,
    version = nil,
    stage_rank = 0,
  }
end

function C.current_state(comments, proposal_id)
  return derive_current_marker(comments, proposal_id)
end

function C.is_current_state(comments, proposal_id, state, version)
  local current = derive_current_marker(comments, proposal_id)
  return current.state == state and current.version == version
end

local function current_marker_state(comments, proposal_id)
  local current = derive_current_marker(comments, proposal_id)
  if current == nil or current.state == nil then
    return nil
  end
  return current
end

local function has_any_state_label(labels)
  for _, label in ipairs(labels or {}) do
    if C.is_state_label(label) then
      return true
    end
  end
  return false
end

function C.reintake_has_active_devloop_state(labels, comments, proposal_id)
  local current = current_marker_state(comments, proposal_id)
  if current ~= nil then
    return tostring(current.state or "") ~= "blocked"
  end
  return devloop_base.is_opted_in(labels) or has_any_state_label(labels)
end

local function later_timestamp(left, right)
  local l = tostring(left or "")
  local r = tostring(right or "")
  if r ~= "" and (l == "" or r > l) then
    return r
  end
  return l ~= "" and l or nil
end

function C.reintake_effect_updated_at(issue, command, comments, proposal_id)
  local updated_at = (command and command.created_at) or (issue and issue.updated_at)
  local current = current_marker_state(comments, proposal_id)
  if command ~= nil and current ~= nil and tostring(current.state or "") == "blocked" then
    updated_at = later_timestamp(updated_at, current.marker_created_at)
  end
  return updated_at or (issue and issue.updated_at)
end

function C.compare_phase(left, right, opts)
  local options = opts or {}
  local left_state = type(left) == "table" and left.state or left
  local right_state = type(right) == "table" and right.state or right
  local right_rank = C.stage_rank(right_state)
  if not C.is_state(right_state) then
    error("github-devloop: invalid milestone")
  end
  validate_milestone_domain(options.domain or options.milestone_domain, right_state)
  local left_rank = type(left) == "table" and tonumber(left.stage_rank) or nil
  if left_rank == nil then
    if not C.is_state(left_state) then
      return nil
    end
    left_rank = C.stage_rank(left_state)
  end
  return sign_order(left_rank - right_rank)
end

function C.is_at_or_after(state_or_marker, milestone, opts)
  return (C.compare_phase(state_or_marker, milestone, opts) or -1) >= 0
end

function C.reached(comments, proposal_id, milestone, opts)
  if type(comments) ~= "table" then
    return false
  end
  local options = opts or {}
  if not C.is_state(milestone) then
    error("github-devloop: invalid milestone")
  end
  local domain = options.domain or options.milestone_domain
  validate_milestone_domain(domain, milestone)

  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      if candidate ~= nil
        and candidate.proposal_id == proposal_id
        and domain_allows_state(domain, candidate.state)
        and lineage_matches(candidate.version, options)
        and C.is_at_or_after(candidate, milestone, options) then
        return true
      end
    end
  end
  return false
end

function C.has_state_marker(comments, proposal_id, state, version)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      if candidate ~= nil
        and candidate.proposal_id == proposal_id
        and candidate.state == state
        and candidate.version == version then
        return true
      end
    end
  end
  return false
end

function C.state_marker_comment_id(comments, proposal_id, state, version, effects)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      local attrs = marker_attrs(marker)
      if candidate ~= nil
        and candidate.proposal_id == proposal_id
        and candidate.state == state
        and candidate.version == version
        and tostring(attrs.effects or "") == tostring(effects or "")
        and payloads_predicates.is_safe_comment_id(comment.id) then
        return tostring(comment.id)
      end
    end
  end
  return nil
end

function C.ready_hand_off_comment_id(comments, proposal_id, marker_version)
  return C.state_marker_comment_id(
    comments,
    proposal_id,
    "ready",
    marker_version,
    "result-marker,ready-label,devloop-ready"
  )
end

local function normalize_state(state)
  if state == nil then
    return "unmanaged"
  end
  return state
end

local function can_reach(from_state, to_state, seen)
  local from = normalize_state(from_state)
  if from == to_state then
    return true
  end
  local next_states = state_graph[from]
  if next_states == nil then
    return false
  end
  local visited = seen or {}
  if visited[from] then
    return false
  end
  visited[from] = true
  for _, next_state in ipairs(next_states) do
    if can_reach(next_state, to_state, visited) then
      return true
    end
  end
  return false
end

function C.transition_status(current, from_states, to_state)
  local current_state = current
  if type(current) == "table" then
    current_state = current.state
  end
  if current_state == to_state then
    return "idempotent"
  end
  local normalized_current = normalize_state(current_state)
  for _, from_state in ipairs(from_states or {}) do
    if normalized_current == normalize_state(from_state) then
      return "apply"
    end
  end
  for _, from_state in ipairs(from_states or {}) do
    if can_reach(normalized_current, normalize_state(from_state)) then
      return "pending"
    end
  end
  return "stale"
end

function C.versioned_transition_status(current, from_states, to_state, incoming_version)
  if type(current) == "table"
    and current.version ~= nil
    and incoming_version ~= nil
    and compare_transition_versions(incoming_version, current.version) < 0 then
    return "stale"
  end
  local status = C.transition_status(current, from_states, to_state)
  return status
end

function C.cyclic_transition_status(current, from_states, to_state, incoming_version, target_version)
  local current_state = current
  local current_version = nil
  if type(current) == "table" then
    current_state = current.state
    current_version = current.version
  end
  if incoming_version == nil then
    return C.transition_status(current, from_states, to_state)
  end
  if target_version ~= nil and current_state == to_state and versions_equivalent(current_version, target_version) then
    return "idempotent"
  end

  local version_order = compare_transition_versions(incoming_version, current_version)
  if version_order > 0 then
    return "pending"
  end
  if version_order < 0 then
    return "stale"
  end

  if current_state == to_state then
    return "idempotent"
  end
  local normalized_current = normalize_state(current_state)
  for _, from_state in ipairs(from_states or {}) do
    if normalized_current == normalize_state(from_state) then
      return "apply"
    end
  end
  if C.stage_rank(to_state) > C.stage_rank(current_state) then
    return "apply"
  end
  return "stale"
end

function C.cas_outcome(current, transition, incoming_version)
  if transition == "apply" then
    return "applied"
  end
  if transition == "idempotent" then
    return "skip-idempotent(already at to_state)"
  end
  if transition == "pending" then
    return "retry-pending(from-state marker not yet visible)"
  end
  if transition == "stale" then
    if type(current) == "table"
      and current.version ~= nil
      and incoming_version ~= nil
      and compare_transition_versions(incoming_version, current.version) < 0 then
      return "skip-stale(incoming version < current marker version)"
    end
    return "skip-advanced-or-diverged"
  end
  return tostring(transition or "unknown")
end

function C.state_label_changes(to_state)
  local add_label = C.state_label(to_state)
  if add_label == nil then
    error("github-devloop: invalid state")
  end

  local remove_labels = {}
  local remove_seen = {}
  for _, state in ipairs(state_order) do
    local label = label_by_state[state]
    if state ~= to_state and label ~= add_label and remove_seen[label] ~= true then
      table.insert(remove_labels, label)
      remove_seen[label] = true
    end
  end
  return { add_label }, remove_labels
end

function C.state_label_reconcile_changes(labels, to_state)
  local expected_label = C.state_label(to_state)
  if expected_label == nil then
    error("github-devloop: invalid state")
  end

  local add_labels = {}
  local remove_labels = {}
  local has_expected = false
  for _, label in ipairs(labels or {}) do
    local label_text = tostring(label)
    if label_text == expected_label then
      has_expected = true
    elseif C.is_state_label(label_text) then
      table.insert(remove_labels, label_text)
    end
  end
  if not has_expected then
    table.insert(add_labels, expected_label)
  end
  return add_labels, remove_labels
end

function C.state_label_hint_matches(labels, state)
  local expected_label = C.state_label(state)
  if expected_label == nil then
    return false
  end

  local has_expected = false
  for _, label in ipairs(labels or {}) do
    local label_text = tostring(label)
    if label_text == expected_label then
      has_expected = true
    elseif C.is_state_label(label_text) then
      return false
    end
  end
  return has_expected
end

function C.build_reconcile_state_label_request(repo, issue_number, proposal_id, state, version, source_ref, current_labels)
  local add_labels, remove_labels
  if current_labels ~= nil then
    add_labels, remove_labels = C.state_label_reconcile_changes(current_labels, state)
  else
    add_labels, remove_labels = C.state_label_changes(state)
  end
  return requests_labels.build_label_request(repo,
    issue_number,
    add_labels,
    remove_labels,
    base_ids.dedup_key({
      "reconcile",
      "label",
      tostring(proposal_id),
      tostring(state),
      tostring(version or "unversioned"),
    }),
    source_ref
  )
end

function C.has_terminal_label(labels)
  return C.has_label(labels, devloop_base._ready_label)
    or C.has_label(labels, devloop_base._implementing_label)
    or C.has_label(labels, devloop_base._pr_open_label)
    or C.has_label(labels, devloop_base._reviewing_label)
    or C.has_label(labels, devloop_base._review_meta_label)
    or C.has_label(labels, devloop_base._merge_ready_label)
    or C.has_label(labels, devloop_base._merging_label)
    or C.has_label(labels, devloop_base._merged_label)
    or C.has_label(labels, devloop_base._fixing_label)
    or C.has_label(labels, devloop_base._impl_failed_label)
    or C.has_label(labels, devloop_base._declined_label)
    or C.has_label(labels, devloop_base._blocked_label)
end

function C.has_thinking_label(labels)
  return C.has_label(labels, devloop_base._thinking_label)
end

function C.has_blocked_label(labels)
  return C.has_label(labels, devloop_base._blocked_label)
end

function C.has_ready_label(labels)
  return C.has_label(labels, devloop_base._ready_label)
end

function C.has_implementing_label(labels)
  return C.has_label(labels, devloop_base._implementing_label)
end

function C.has_pr_open_label(labels)
  return C.has_label(labels, devloop_base._pr_open_label)
end

function C.has_reviewing_label(labels)
  return C.has_label(labels, devloop_base._reviewing_label)
end

function C.has_merge_ready_label(labels)
  return C.has_label(labels, devloop_base._merge_ready_label)
end

function C.has_merging_label(labels)
  return C.has_label(labels, devloop_base._merging_label)
end

function C.has_merged_label(labels)
  return C.has_label(labels, devloop_base._merged_label)
end

function C.has_fixing_label(labels)
  return C.has_label(labels, devloop_base._fixing_label)
end

function C.has_review_meta_label(labels)
  return C.has_label(labels, devloop_base._review_meta_label)
end

function C.has_impl_failed_label(labels)
  return C.has_label(labels, devloop_base._impl_failed_label)
end

function C.has_decision_terminal_label(labels)
  return C.has_label(labels, devloop_base._ready_label)
    or C.has_label(labels, devloop_base._implementing_label)
    or C.has_label(labels, devloop_base._pr_open_label)
    or C.has_label(labels, devloop_base._reviewing_label)
    or C.has_label(labels, devloop_base._review_meta_label)
    or C.has_label(labels, devloop_base._merge_ready_label)
    or C.has_label(labels, devloop_base._merging_label)
    or C.has_label(labels, devloop_base._merged_label)
    or C.has_label(labels, devloop_base._fixing_label)
    or C.has_label(labels, devloop_base._impl_failed_label)
    or C.has_label(labels, devloop_base._declined_label)
    or C.has_label(labels, devloop_base._blocked_label)
end

function C.is_loop_terminal(labels)
  return C.has_label(labels, devloop_base._ready_label)
    or C.has_label(labels, devloop_base._implementing_label)
    or C.has_label(labels, devloop_base._pr_open_label)
    or C.has_label(labels, devloop_base._reviewing_label)
    or C.has_label(labels, devloop_base._review_meta_label)
    or C.has_label(labels, devloop_base._merge_ready_label)
    or C.has_label(labels, devloop_base._merging_label)
    or C.has_label(labels, devloop_base._merged_label)
    or C.has_label(labels, devloop_base._fixing_label)
    or C.has_label(labels, devloop_base._impl_failed_label)
    or C.has_label(labels, devloop_base._declined_label)
    or C.has_label(labels, devloop_base._blocked_label)
end

function C.has_result_marker(comments, proposal_id, decision, dedup_key, decision_reason)
  if type(comments) ~= "table" then
    return false
  end
  -- Match the FULL marker (proposal + decision + dedup) so a stale opposite/older-version marker
  -- does not suppress writing the current decision's result marker.
  local needle = m_builders.result_marker(proposal_id, decision, dedup_key, decision_reason)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    if parsers_misc._comment_body(comment):find(needle, 1, true) ~= nil then
      return true
    end
  end
  return false
end


C._strip_latest_fix_version_suffix = strip_latest_fix_version_suffix
C._compare_transition_versions = compare_transition_versions

function S.install(M)
  for _, n in ipairs({"_compare_transition_versions", "_strip_latest_fix_version_suffix", "build_reconcile_state_label_request", "cas_outcome", "comment_bodies", "compare_phase", "compare_state_marker_order", "current_state", "cyclic_transition_status", "fix_version_from_review_version", "has_blocked_label", "has_decision_terminal_label", "has_fixing_label", "has_impl_failed_label", "has_implementing_label", "has_label", "has_merge_ready_label", "has_merged_label", "has_merging_label", "has_pr_open_label", "has_ready_label", "has_result_marker", "has_review_meta_label", "has_reviewing_label", "has_state_marker", "has_terminal_label", "has_thinking_label", "is_at_or_after", "is_loop_terminal", "is_state", "is_state_label", "issue_state_order", "lifecycle_state_set", "marker_order_key", "next_fix_version", "next_review_loop_version", "next_review_meta_action_version", "reached", "ready_hand_off_comment_id", "stage_rank", "state_label", "state_label_changes", "state_label_hint_matches", "state_label_reconcile_changes", "state_marker", "state_marker_comment_id", "state_order", "state_successors", "timeout_lineage_matches_current", "transition_status", "version_fix_round", "version_loop_round", "version_order_key", "version_ready_split_round", "version_reimplement_round", "version_review_loop_round", "version_review_meta_action_round", "version_timeout_round", "version_updated_at", "versioned_transition_status"}) do M[n] = C[n] end
end
C.install = S.install

return C
