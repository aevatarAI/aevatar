local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local parsers_misc = require("devloop.parsers.misc")
local payloads_predicates = require("devloop.payloads.predicates")
local S = {}
local source_ref = require("contract.source_ref")
local transition_version = require("contract.transition_version")
local m_builders = require("devloop.markers.builders")
local order_number_width = 12

local label_by_state = { thinking = "fkst-dev:thinking", dependency_wait = "fkst-dev:ready", ready = "fkst-dev:ready", implementing = "fkst-dev:implementing", ["awaiting-pr"] = "fkst-dev:awaiting-pr", ["pr-open"] = "fkst-dev:pr-open", reviewing = "fkst-dev:reviewing", ["merge-ready"] = "fkst-dev:merge-ready", merging = "fkst-dev:merging", merged = "fkst-dev:merged", ["closed-unmerged"] = "fkst-dev:blocked", fixing = "fkst-dev:fixing", ["review-meta"] = "fkst-dev:review-meta", ["impl-failed"] = "fkst-dev:impl-failed", blocked = "fkst-dev:blocked" }
local state_labels = {}
for _, label in pairs(label_by_state) do state_labels[label] = true end
local state_graph = { unmanaged = { "thinking" }, thinking = { "dependency_wait", "ready", "blocked" }, dependency_wait = { "dependency_wait", "ready", "blocked" }, ready = { "dependency_wait", "implementing", "blocked" }, implementing = { "awaiting-pr", "impl-failed" }, ["awaiting-pr"] = { "merged", "ready", "blocked" }, ["pr-open"] = { "reviewing", "blocked" }, reviewing = { "merge-ready", "fixing", "review-meta" }, ["merge-ready"] = { "merging", "blocked" }, merging = { "merged", "reviewing", "fixing", "blocked" }, merged = {}, ["closed-unmerged"] = {}, fixing = { "reviewing", "review-meta" }, ["review-meta"] = { "fixing", "blocked" }, ["impl-failed"] = { "implementing" }, blocked = {} }
local issue_state_order = { "thinking", "dependency_wait", "ready", "implementing", "pr-open", "reviewing", "merge-ready", "fixing", "impl-failed", "blocked", "review-meta", "merging", "merged", "awaiting-pr" }
local state_order = { "thinking", "dependency_wait", "ready", "implementing", "pr-open", "reviewing", "merge-ready", "fixing", "impl-failed", "blocked", "review-meta", "merging", "merged", "closed-unmerged", "awaiting-pr" }
local state_stage_rank = { thinking = 100, dependency_wait = 500, ready = 500, implementing = 600, ["awaiting-pr"] = 625, ["pr-open"] = 650, reviewing = 675, ["merge-ready"] = 690, merging = 695, fixing = 700, ["review-meta"] = 710, ["impl-failed"] = 750, blocked = 800, ["closed-unmerged"] = 825, merged = 900 }
local function copy_array(values) local out = {}; for _, value in ipairs(values or {}) do table.insert(out, value) end; return out end

local function marker_attrs(marker)
  local attrs = {}
  for key, value in tostring(marker or ""):gmatch('([%w._-]+)="([^"]*)"') do
    attrs[key] = value
  end
  return attrs
end

local function padded_order_number(value)
  return string.format("%0" .. tostring(order_number_width) .. "d", tonumber(value) or 0)
end

function S.install(M)
function M.has_label(labels, expected)
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

function M.is_state(state) return label_by_state[state] ~= nil end
function M.is_state_label(label) return state_labels[tostring(label)] == true end
function M.state_label(state) return label_by_state[state] end
function M.state_order() return copy_array(state_order) end
function M.issue_state_order() return copy_array(issue_state_order) end
function M.state_successors(state) return copy_array(state_graph[state]) end
function M.lifecycle_state_set()
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

function M.state_marker(proposal_id, state, version, effects)
  if not M.is_state(state) then
    error("github-devloop: invalid state")
  end
  local effects_field = ""
  if effects ~= nil and tostring(effects) ~= "" then
    effects_field = ' effects="' .. tostring(effects):gsub('"', "'") .. '"'
  end
  return '<!-- fkst:github-devloop:state:v1 proposal="' .. tostring(proposal_id)
    .. '" state="' .. tostring(state)
    .. '" version="' .. tostring(version)
    .. '" stage_rank="' .. tostring(M.stage_rank(state))
    .. '" marker_order_key="' .. M.marker_order_key(version, state)
    .. '"'
    .. effects_field
    .. ' -->'
end

function M.version_order_key(version)
  return source_ref.version_order_key(version)
end

function M.stage_rank(state)
  return state_stage_rank[state] or 0
end

function M.version_updated_at(version)
  local text = tostring(version or "")
  local updated_at = ""
  for found in text:gmatch("(%d%d%d%d%-%d%d%-%d%dT%d%d[%-:]%d%d[%-:]%d%dZ)") do
    updated_at = found:gsub(":", "-")
  end
  return updated_at
end

function M.version_loop_round(version)
  -- Extract the no-consensus loop round wherever it appears, not only at the
  -- end of the version string. A reviewing version like ".../loop/2" is later
  -- extended to a fixing version ".../loop/2/fix/1"; an end-anchored match
  -- returned 0 for the fixing version, so version ordering wrongly ranked the
  -- (loop_n=2) reviewing marker above the (loop_n=0) fixing marker and the fix
  -- loop stalled. Match the gmatch/max shape of the sibling round extractors.
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]loop[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_fix_round(version)
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]fix[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_review_meta_action_round(version)
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]review%-meta%-action[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_review_loop_round(version)
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]review%-loop[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_timeout_round(version, state_name)
  local max_n = 0
  local state = tostring(state_name or "")
  if state == "" then
    return 0
  end
  local escaped = state:gsub("%-", "%%-")
  for n in tostring(version or ""):gmatch("/timeout/" .. escaped .. "/(%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  for n in tostring(version or ""):gmatch("%-timeout%-" .. escaped .. "%-(%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_reimplement_round(version)
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]reimplement[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

function M.version_ready_split_round(version)
  local max_n = 0
  for n in tostring(version or ""):gmatch("[/-]ready%-split[/-](%d+)") do
    local parsed = tonumber(n) or 0
    if parsed > max_n then
      max_n = parsed
    end
  end
  return max_n
end

local timeout_order_states = {
  "thinking",
  "ready",
  "implementing",
  "awaiting-pr",
  "impl-failed",
  "pr-open",
  "reviewing",
  "review-meta",
  "merge-ready",
  "merging",
  "fixing",
  "blocked",
}

local function version_max_timeout_round(version)
  local max_n = 0
  for _, state_name in ipairs(timeout_order_states) do
    max_n = math.max(max_n, M.version_timeout_round(version, state_name))
  end
  return max_n
end

function M.next_fix_version(version)
  local base = tostring(version or "")
  local next_n = M.version_fix_round(base) + 1
  return base .. "/fix/" .. tostring(next_n)
end

function M.fix_version_from_review_version(version)
  return M.next_fix_version(version)
end

function M.next_review_meta_action_version(version)
  local base = tostring(version or "")
  local next_n = M.version_review_meta_action_round(base) + 1
  return base .. "/review-meta-action/" .. tostring(next_n)
end

function M.next_review_loop_version(version)
  local base = tostring(version or "")
  local next_n = M.version_review_loop_round(base) + 1
  return base .. "/review-loop/" .. tostring(next_n)
end

local comparable_transition_base

local function version_primary_key(version)
  if version == nil then
    return 0, ""
  end
  local base = comparable_transition_base(version)
  local updated_at = M.version_updated_at(base)
  if updated_at ~= "" then
    return 1, updated_at
  end
  return 0, source_ref.version_order_key(transition_version.safe_version_segment(base))
end

local function version_sort_key(version, stage_rank)
  local primary_rank, primary = version_primary_key(version)
  return {
    primary_rank = primary_rank,
    primary = primary,
    loop_n = M.version_loop_round(version),
    fix_n = M.version_fix_round(version),
    reimplement_n = M.version_reimplement_round(version),
    timeout_n = version_max_timeout_round(version),
    review_loop_n = M.version_review_loop_round(version),
    review_meta_action_n = M.version_review_meta_action_round(version),
    ready_split_n = M.version_ready_split_round(version),
    stage_rank = tonumber(stage_rank) or 0,
  }
end

function M.marker_order_key(version, state_or_stage_rank)
  local stage_rank = tonumber(state_or_stage_rank)
  if stage_rank == nil then
    stage_rank = M.stage_rank(state_or_stage_rank)
  end
  local key = version_sort_key(version, stage_rank)
  return table.concat({
    tostring(key.primary or ""),
    padded_order_number(key.loop_n),
    padded_order_number(key.fix_n),
    padded_order_number(key.reimplement_n),
    padded_order_number(key.timeout_n),
    padded_order_number(key.review_meta_action_n),
    padded_order_number(key.review_loop_n),
    padded_order_number(key.ready_split_n),
    padded_order_number(key.stage_rank),
  }, "/")
end

local function marker_stage_rank(marker, state)
  local explicit_rank = tonumber(marker:match('stage_rank="(%d+)"'))
  return explicit_rank or M.stage_rank(state)
end

local function state_marker_fact(marker, comment)
  local attrs = marker_attrs(marker)
  local marker_proposal = attrs.proposal
  local marker_state = attrs.state
  local marker_version = attrs.version
  if marker_proposal == nil or not M.is_state(marker_state) then
    return nil
  end
  return {
    proposal_id = marker_proposal,
    state = marker_state,
    version = marker_version,
    stage_rank = marker_stage_rank(marker, marker_state),
    marker_created_at = parsers_misc._comment_created_at(M, comment),
  }
end

local function compare_version_keys(left, right)
  if left.primary_rank ~= right.primary_rank then
    return left.primary_rank > right.primary_rank and 1 or -1
  end
  if left.primary ~= right.primary then
    return left.primary > right.primary and 1 or -1
  end
  if left.loop_n ~= right.loop_n then
    return left.loop_n > right.loop_n and 1 or -1
  end
  if left.fix_n ~= right.fix_n then
    return left.fix_n > right.fix_n and 1 or -1
  end
  if left.reimplement_n ~= right.reimplement_n then
    return left.reimplement_n > right.reimplement_n and 1 or -1
  end
  if left.timeout_n ~= right.timeout_n then
    return left.timeout_n > right.timeout_n and 1 or -1
  end
  if left.review_meta_action_n ~= right.review_meta_action_n then
    return left.review_meta_action_n > right.review_meta_action_n and 1 or -1
  end
  if left.review_loop_n ~= right.review_loop_n then
    return left.review_loop_n > right.review_loop_n and 1 or -1
  end
  if left.ready_split_n ~= right.ready_split_n then
    return left.ready_split_n > right.ready_split_n and 1 or -1
  end
  if left.stage_rank ~= right.stage_rank then
    return left.stage_rank > right.stage_rank and 1 or -1
  end
  return 0
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

comparable_transition_base = function(version)
  local text = transition_version.strip_suffixes(version)
  return text:match("^consensus:(.+)$") or text
end

local function strip_latest_fix_version_suffix(version)
  return tostring(version or "")
    :gsub("/fix/%d+$", "")
    :gsub("%-fix%-%d+$", "")
end

local function compare_same_base_transition_versions(incoming_version, current_version)
  local incoming_key = version_sort_key(incoming_version, 0)
  local current_key = version_sort_key(current_version, 0)
  if incoming_key.loop_n ~= current_key.loop_n then
    return incoming_key.loop_n > current_key.loop_n and 1 or -1
  end
  if incoming_key.fix_n ~= current_key.fix_n then
    return incoming_key.fix_n > current_key.fix_n and 1 or -1
  end
  if incoming_key.reimplement_n ~= current_key.reimplement_n then
    return incoming_key.reimplement_n > current_key.reimplement_n and 1 or -1
  end
  if incoming_key.timeout_n ~= current_key.timeout_n then
    return incoming_key.timeout_n > current_key.timeout_n and 1 or -1
  end
  if incoming_key.review_meta_action_n ~= current_key.review_meta_action_n then
    return incoming_key.review_meta_action_n > current_key.review_meta_action_n and 1 or -1
  end
  if incoming_key.review_loop_n ~= current_key.review_loop_n then
    return incoming_key.review_loop_n > current_key.review_loop_n and 1 or -1
  end
  if incoming_key.ready_split_n ~= current_key.ready_split_n then
    return incoming_key.ready_split_n > current_key.ready_split_n and 1 or -1
  end
  return 0
end

local function compare_transition_versions(incoming_version, current_version)
  if incoming_version == current_version then
    return 0
  end
  if incoming_version == nil then
    return current_version == nil and 0 or -1
  end
  if current_version == nil then
    return 1
  end
  local incoming_base = comparable_transition_base(incoming_version)
  local current_base = comparable_transition_base(current_version)
  if versions_equivalent(incoming_base, current_base) then
    return compare_same_base_transition_versions(incoming_version, current_version)
  end
  return compare_version_keys(version_sort_key(incoming_version, 0), version_sort_key(current_version, 0))
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

function M.compare_state_marker_order(current, target_state, target_version)
  if current == nil or current.version == nil then
    return -1
  end
  local version_order = compare_transition_versions(current.version, target_version)
  if version_order ~= 0 then
    return sign_order(version_order)
  end
  return sign_order(M.stage_rank(current.state) - M.stage_rank(target_state))
end

function M.timeout_lineage_matches_current(scheduled, current)
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
  local a_stage_rank = tonumber(a.stage_rank) or M.stage_rank(a.state)
  local b_stage_rank = tonumber(b.stage_rank) or M.stage_rank(b.state)
  if a_stage_rank ~= b_stage_rank then
    return b_stage_rank > a_stage_rank
  end
  local a_key = version_sort_key(a.version, a.stage_rank)
  local b_key = version_sort_key(b.version, b.stage_rank)
  return compare_version_keys(b_key, a_key) > 0
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
    return domain == "github-devloop" and M.is_state(state)
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

function M.comment_bodies(comments)
  local bodies = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(bodies, parsers_misc._comment_body(M, comment))
  end
  return bodies
end

function M.current_state(comments, proposal_id)
  if type(comments) ~= "table" then
    return nil
  end

  local current = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
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

function M.compare_phase(left, right, opts)
  local options = opts or {}
  local left_state = type(left) == "table" and left.state or left
  local right_state = type(right) == "table" and right.state or right
  local right_rank = M.stage_rank(right_state)
  if not M.is_state(right_state) then
    error("github-devloop: invalid milestone")
  end
  validate_milestone_domain(options.domain or options.milestone_domain, right_state)
  local left_rank = type(left) == "table" and tonumber(left.stage_rank) or nil
  if left_rank == nil then
    if not M.is_state(left_state) then
      return nil
    end
    left_rank = M.stage_rank(left_state)
  end
  return sign_order(left_rank - right_rank)
end

function M.is_at_or_after(state_or_marker, milestone, opts)
  return (M.compare_phase(state_or_marker, milestone, opts) or -1) >= 0
end

function M.reached(comments, proposal_id, milestone, opts)
  if type(comments) ~= "table" then
    return false
  end
  local options = opts or {}
  if not M.is_state(milestone) then
    error("github-devloop: invalid milestone")
  end
  local domain = options.domain or options.milestone_domain
  validate_milestone_domain(domain, milestone)

  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      if candidate ~= nil
        and candidate.proposal_id == proposal_id
        and domain_allows_state(domain, candidate.state)
        and lineage_matches(candidate.version, options)
        and M.is_at_or_after(candidate, milestone, options) then
        return true
      end
    end
  end
  return false
end

function M.has_state_marker(comments, proposal_id, state, version)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
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

function M.state_marker_comment_id(comments, proposal_id, state, version, effects)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local candidate = state_marker_fact(marker, comment)
      local attrs = marker_attrs(marker)
      if candidate ~= nil
        and candidate.proposal_id == proposal_id
        and candidate.state == state
        and candidate.version == version
        and tostring(attrs.effects or "") == tostring(effects or "")
        and payloads_predicates.is_safe_comment_id(M, comment.id) then
        return tostring(comment.id)
      end
    end
  end
  return nil
end

function M.ready_hand_off_comment_id(comments, proposal_id, marker_version)
  return M.state_marker_comment_id(
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

function M.transition_status(current, from_states, to_state)
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

function M.versioned_transition_status(current, from_states, to_state, incoming_version)
  if type(current) == "table"
    and current.version ~= nil
    and incoming_version ~= nil
    and compare_transition_versions(incoming_version, current.version) < 0 then
    return "stale"
  end
  local status = M.transition_status(current, from_states, to_state)
  return status
end

function M.cyclic_transition_status(current, from_states, to_state, incoming_version, target_version)
  local current_state = current
  local current_version = nil
  if type(current) == "table" then
    current_state = current.state
    current_version = current.version
  end
  if incoming_version == nil then
    return M.transition_status(current, from_states, to_state)
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
  if M.stage_rank(to_state) > M.stage_rank(current_state) then
    return "apply"
  end
  return "stale"
end

function M.cas_outcome(current, transition, incoming_version)
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

function M.state_label_changes(to_state)
  local add_label = M.state_label(to_state)
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

function M.state_label_reconcile_changes(labels, to_state)
  local expected_label = M.state_label(to_state)
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
    elseif M.is_state_label(label_text) then
      table.insert(remove_labels, label_text)
    end
  end
  if not has_expected then
    table.insert(add_labels, expected_label)
  end
  return add_labels, remove_labels
end

function M.state_label_hint_matches(labels, state)
  local expected_label = M.state_label(state)
  if expected_label == nil then
    return false
  end

  local has_expected = false
  for _, label in ipairs(labels or {}) do
    local label_text = tostring(label)
    if label_text == expected_label then
      has_expected = true
    elseif M.is_state_label(label_text) then
      return false
    end
  end
  return has_expected
end

function M.build_reconcile_state_label_request(repo, issue_number, proposal_id, state, version, source_ref, current_labels)
  local add_labels, remove_labels
  if current_labels ~= nil then
    add_labels, remove_labels = M.state_label_reconcile_changes(current_labels, state)
  else
    add_labels, remove_labels = M.state_label_changes(state)
  end
  return requests_labels.build_label_request(M,
    repo,
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

function M.has_terminal_label(labels)
  return M.has_label(labels, M._ready_label)
    or M.has_label(labels, M._implementing_label)
    or M.has_label(labels, M._pr_open_label)
    or M.has_label(labels, M._reviewing_label)
    or M.has_label(labels, M._review_meta_label)
    or M.has_label(labels, M._merge_ready_label)
    or M.has_label(labels, M._merging_label)
    or M.has_label(labels, M._merged_label)
    or M.has_label(labels, M._fixing_label)
    or M.has_label(labels, M._impl_failed_label)
    or M.has_label(labels, M._blocked_label)
end

function M.has_thinking_label(labels)
  return M.has_label(labels, M._thinking_label)
end

function M.has_blocked_label(labels)
  return M.has_label(labels, M._blocked_label)
end

function M.has_ready_label(labels)
  return M.has_label(labels, M._ready_label)
end

function M.has_implementing_label(labels)
  return M.has_label(labels, M._implementing_label)
end

function M.has_pr_open_label(labels)
  return M.has_label(labels, M._pr_open_label)
end

function M.has_reviewing_label(labels)
  return M.has_label(labels, M._reviewing_label)
end

function M.has_merge_ready_label(labels)
  return M.has_label(labels, M._merge_ready_label)
end

function M.has_merging_label(labels)
  return M.has_label(labels, M._merging_label)
end

function M.has_merged_label(labels)
  return M.has_label(labels, M._merged_label)
end

function M.has_fixing_label(labels)
  return M.has_label(labels, M._fixing_label)
end

function M.has_review_meta_label(labels)
  return M.has_label(labels, M._review_meta_label)
end

function M.has_impl_failed_label(labels)
  return M.has_label(labels, M._impl_failed_label)
end

function M.has_decision_terminal_label(labels)
  return M.has_label(labels, M._ready_label)
    or M.has_label(labels, M._implementing_label)
    or M.has_label(labels, M._pr_open_label)
    or M.has_label(labels, M._reviewing_label)
    or M.has_label(labels, M._review_meta_label)
    or M.has_label(labels, M._merge_ready_label)
    or M.has_label(labels, M._merging_label)
    or M.has_label(labels, M._merged_label)
    or M.has_label(labels, M._fixing_label)
    or M.has_label(labels, M._impl_failed_label)
    or M.has_label(labels, M._blocked_label)
end

function M.is_loop_terminal(labels)
  return M.has_label(labels, M._ready_label)
    or M.has_label(labels, M._implementing_label)
    or M.has_label(labels, M._pr_open_label)
    or M.has_label(labels, M._reviewing_label)
    or M.has_label(labels, M._review_meta_label)
    or M.has_label(labels, M._merge_ready_label)
    or M.has_label(labels, M._merging_label)
    or M.has_label(labels, M._merged_label)
    or M.has_label(labels, M._fixing_label)
    or M.has_label(labels, M._impl_failed_label)
    or M.has_label(labels, M._blocked_label)
end

function M.has_result_marker(comments, proposal_id, decision, dedup_key)
  if type(comments) ~= "table" then
    return false
  end
  -- Match the FULL marker (proposal + decision + dedup) so a stale opposite/older-version marker
  -- does not suppress writing the current decision's result marker.
  local needle = m_builders.result_marker(M, proposal_id, decision, dedup_key)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    if parsers_misc._comment_body(M, comment):find(needle, 1, true) ~= nil then
      return true
    end
  end
  return false
end


M._strip_latest_fix_version_suffix = strip_latest_fix_version_suffix
M._compare_transition_versions = compare_transition_versions
end

return S
