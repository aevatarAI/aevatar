local base_ids = require("devloop.base_ids")
local devloop_base = require("devloop.base")
local common = require("departments.observability.common")
local contract_time = require("contract.time")
local m_facts = require("devloop.markers.facts")
local devloop_state = require("devloop.state")

local M = {}

function M.install_census(core)
local dept = common.dept
local stall_suspect_threshold_minutes = common.stall_suspect_threshold_minutes

local function state_or_nil(state)
  if type(state) ~= "table" or state.state == nil then
    return nil
  end
  return state
end

local function put_issue_entity(entities, repo, issue_number, issue)
  local proposal_id = base_ids.proposal_id(repo, issue_number)
  local issue_state = devloop_state.current_state(issue.comments, proposal_id)
  local link = m_facts.pr_link_fact(issue.comments, proposal_id)
  local dependency_wait = core.dependency_wait_fact(issue.comments, proposal_id)
  local entity = entities[proposal_id] or {
    proposal_id = proposal_id,
    issue_number = tonumber(issue_number),
    pr_number = nil,
    state = nil,
    marker_source = nil,
    dependency_wait = nil,
  }
  entity.issue_number = tonumber(issue_number)
  entity.title = issue.title
  entity.parent_issue = issue
  if state_or_nil(issue_state) ~= nil then
    entity.state = issue_state
    entity.marker_source = "issue"
  end
  if link ~= nil then
    entity.pr_number = link.pr_number
  end
  entity.dependency_wait = dependency_wait
  entities[proposal_id] = entity
  return entity, link
end

local function put_pr_entity(entities, repo, pr_number, pr)
  local origin = m_facts.pr_origin_fact(pr.comments)
  if origin == nil then
    return nil
  end
  local proposal_id = origin.proposal_id
  local pr_state = require("devloop.entity").current_entity_state(pr.comments, proposal_id)
  local entity = entities[proposal_id] or {
    proposal_id = proposal_id,
    issue_number = origin.issue_number,
    pr_number = tonumber(pr_number),
    state = nil,
    marker_source = nil,
  }
  entity.issue_number = origin.issue_number
  entity.pr_number = tonumber(pr_number)
  entity.pr_origin = origin
  entity.pr = pr
  if state_or_nil(pr_state) ~= nil then
    entity.state = pr_state
    entity.marker_source = "pr-comment"
  end
  entities[proposal_id] = entity
  return entity
end

local function observe_issue_candidate(repo, issue_number, entities, seen_prs, limits, deadline, budget)
  local issue_views = 0
  local pr_views = 0
  if (budget.remaining or 0) <= 0 or not core.observability_has_budget(deadline) then
    return issue_views, pr_views
  end
  local issue = common.fetch_issue(core, repo, issue_number, limits, deadline)
  if issue == nil then
    budget.deadline_deferred = true
    return issue_views, pr_views
  end
  budget.remaining = budget.remaining - 1
  issue_views = issue_views + 1
  local entity, link = put_issue_entity(entities, repo, issue_number, issue)
  if link ~= nil and seen_prs[link.pr_number] == nil then
    if (budget.remaining or 0) <= 0 or not core.observability_has_budget(deadline) then
      return issue_views, pr_views
    end
    seen_prs[link.pr_number] = true
    local pr = common.fetch_pr(core, repo, link.pr_number, limits, deadline)
    if pr == nil then
      budget.deadline_deferred = true
      return issue_views, pr_views
    end
    budget.remaining = budget.remaining - 1
    pr_views = pr_views + 1
    put_pr_entity(entities, repo, link.pr_number, pr)
  elseif entity ~= nil and entity.pr_number ~= nil then
    seen_prs[entity.pr_number] = true
  end
  return issue_views, pr_views
end

local function observe_pr_candidate(repo, pr_number, entities, seen_prs, limits, deadline, budget)
  local pr_views = 0
  if (budget.remaining or 0) <= 0 or not core.observability_has_budget(deadline) then
    return pr_views
  end
  if seen_prs[pr_number] == nil then
    seen_prs[pr_number] = true
    local pr = common.fetch_pr(core, repo, pr_number, limits, deadline)
    if pr == nil then
      budget.deadline_deferred = true
      return pr_views
    end
    budget.remaining = budget.remaining - 1
    pr_views = pr_views + 1
    put_pr_entity(entities, repo, pr_number, pr)
  end
  return pr_views
end

local function observe_candidates(repo, candidates, entities, seen_prs, limits, deadline)
  local budget = { remaining = limits.entity_cap }
  local processed_issues = 0
  local processed_prs = 0
  for _, candidate in ipairs(candidates or {}) do
    if budget.remaining <= 0 or not core.observability_has_budget(deadline) then
      break
    end
    if candidate.kind == "issue" then
      local issue_views, pr_views = observe_issue_candidate(repo, candidate.number, entities, seen_prs, limits, deadline, budget)
      processed_issues = processed_issues + issue_views
      processed_prs = processed_prs + pr_views
    elseif candidate.kind == "pr" then
      processed_prs = processed_prs + observe_pr_candidate(repo, candidate.number, entities, seen_prs, limits, deadline, budget)
    end
    if budget.deadline_deferred then
      break
    end
  end
  return processed_issues, processed_prs, budget.remaining
end

local function entity_sort_key(entity)
  return tostring(entity.proposal_id or "")
end

local function log_entity(entity)
  local state = entity.state or {}
  log.info(core.observe_entity_log_line(entity.proposal_id, {
    state = state.state,
    version = state.version,
    marker_source = entity.marker_source,
    pr_number = entity.pr_number,
    marker_created_at = state.marker_created_at,
  }))
end

function core.stall_suspect_age_minutes(version, now_seconds)
  local marker_updated_at = devloop_state.version_updated_at(version)
  if marker_updated_at == "" then
    return nil
  end
  local marker_seconds = contract_time.iso_timestamp_epoch_seconds(marker_updated_at)
  local current_seconds = tonumber(now_seconds)
  if marker_seconds == nil or current_seconds == nil then
    return nil
  end
  local age_seconds = current_seconds - marker_seconds
  if age_seconds < 0 then
    return nil
  end
  return math.floor(age_seconds / 60)
end

function core.stall_suspect_threshold_minutes(state)
  return stall_suspect_threshold_minutes[state]
end

function core.stall_suspect_log_line(proposal_id, state, age_minutes, threshold_minutes)
  return table.concat({
    "github-devloop",
    "dept=" .. dept,
    "tag=STALL_SUSPECT",
    "proposal=" .. tostring(proposal_id or "unknown"),
    "state=" .. tostring(state or "unknown"),
    "age_minutes=" .. tostring(age_minutes or 0),
    "threshold_minutes=" .. tostring(threshold_minutes or 0),
  }, " ")
end

local function log_stall_suspect(entity, now_seconds)
  local state = entity.state and entity.state.state or nil
  local threshold = core.stall_suspect_threshold_minutes(state)
  if threshold == nil then
    return
  end
  if state == "ready" and entity.dependency_wait ~= nil then
    return
  end
  local age = core.stall_suspect_age_minutes(entity.state.version, now_seconds)
  if age == nil or age <= threshold then
    return
  end
  log.info(core.stall_suspect_log_line(entity.proposal_id, state, age, threshold))
  return {
    entity = entity,
    state = state,
    age_minutes = age,
    threshold_minutes = threshold,
  }
end

local function log_summary(counts, total)
  local fields = {
    "github-devloop",
    "dept=" .. dept,
    "tag=OBSERVE_SUMMARY",
    "total=" .. tostring(total or 0),
  }
  for _, state in ipairs(devloop_state.issue_state_order()) do
    table.insert(fields, state .. "=" .. tostring(counts[state] or 0))
  end
  if counts.unmanaged ~= nil then
    table.insert(fields, "unmanaged=" .. tostring(counts.unmanaged))
  end
  log.info(table.concat(fields, " "))
end

function core.observe_entity_log_line(proposal_id, fields)
  return table.concat({
    "github-devloop",
    "dept=" .. dept,
    "tag=OBSERVE_ENTITY",
    "proposal_id=" .. tostring(proposal_id or "unknown"),
    "state=" .. tostring(fields and fields.state or "unmanaged"),
    "version=" .. tostring(fields and fields.version or ""),
    "marker_source=" .. tostring(fields and fields.marker_source or "none"),
    "pr=" .. tostring(fields and fields.pr_number or ""),
    "marker_created_at=" .. tostring(fields and fields.marker_created_at or ""),
  }, " ")
end

function core.collect_observability_entities(event, repo, limits, deadline)
  local labels = { devloop_base._enabled_label }
  for _, state in ipairs(devloop_state.issue_state_order()) do
    table.insert(labels, devloop_state.state_label(state))
  end
  local rotation_seed = core.observability_rotation_seed(event)
  local issue_items, deferred_issue_pages = core.observability_list_issue_candidates(repo, labels, limits, deadline, rotation_seed)
  local pr_items, deferred_pr_pages = core.observability_list_pr_candidates(repo, limits, deadline, rotation_seed)
  local issue_numbers = core.observability_sorted_numbers(issue_items)
  local pr_numbers = core.observability_sorted_numbers(pr_items)
  local candidates, deferred_candidates = core.observability_entity_candidates(issue_numbers, pr_numbers, rotation_seed, limits.entity_cap)
  local entities = {}
  local seen_prs = {}

  local processed_issues, processed_prs, remaining_budget = observe_candidates(repo, candidates, entities, seen_prs, limits, deadline)
  if deferred_issue_pages > 0 or deferred_pr_pages > 0 or deferred_candidates > 0 or remaining_budget == 0 or not core.observability_has_budget(deadline) then
    log.warn(core.observability_deferred_log_line({
      reason = core.observability_has_budget(deadline) and "batch-cap" or "deadline",
      listed_issues = #issue_numbers,
      listed_prs = #pr_numbers,
      processed_issues = processed_issues,
      processed_prs = processed_prs,
      deferred_issues = math.max(0, #issue_numbers - processed_issues),
      deferred_prs = math.max(0, #pr_numbers - processed_prs),
      entity_cap = limits.entity_cap,
    }))
  end

  local list = {}
  for _, entity in pairs(entities) do
    entity.observability_limits = limits
    entity.observability_deadline = deadline
    table.insert(list, entity)
  end
  table.sort(list, function(a, b)
    return entity_sort_key(a) < entity_sort_key(b)
  end)

  local counts = {}
  local now_seconds = now()
  local stalls = {}
  for _, entity in ipairs(list) do
    local state = entity.state and entity.state.state or "unmanaged"
    counts[state] = (counts[state] or 0) + 1
    log_entity(entity)
    local stall = log_stall_suspect(entity, now_seconds)
    if stall ~= nil then
      table.insert(stalls, stall)
    end
  end
  log_summary(counts, #list)
  local state_gap_report = core.state_gap_report(list)
  for _, edge in ipairs(state_gap_report.edges or {}) do
    log.info(core.state_gap_log_line(edge))
  end

  return {
    list = list,
    counts = counts,
    stalls = stalls,
    state_gap_report = state_gap_report,
    now_seconds = now_seconds,
  }
end
end

return M
