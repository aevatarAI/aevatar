local git_mechanics = require("devloop.git_mechanics")
local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local parsers_misc = require("devloop.parsers.misc")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local payloads_builders = require("devloop.payloads.builders")
local m_facts = require("devloop.markers.facts")
local m_mgw = require("devloop.merge_gate_wait")
local C = {}
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")
local transition_version = require("contract.transition_version")
local support = require("devloop.commands.support")
local config = require("devloop.config")

local strings = require("contract.strings")

local active_wip_states = {
  implementing = true,
  ["pr-open"] = true,
  reviewing = true,
  fixing = true,
  ["review-meta"] = true,
  ["merge-ready"] = true,
  merging = true,
}

local merge_queue_lane_states = {
  ["merge-ready"] = true,
  merging = true,
}

C._merge_ready_starvation_threshold_minutes = 60

local function has_merge_ready_created_at(entry)
  local created = tostring(entry and entry.merge_ready_created_at or "")
  return created ~= ""
end

local function compare_merge_queue_entries(left, right)
  local left_has_created = has_merge_ready_created_at(left)
  local right_has_created = has_merge_ready_created_at(right)
  if left_has_created ~= right_has_created then
    return left_has_created
  end
  local left_created = tostring(left and left.merge_ready_created_at or "")
  local right_created = tostring(right and right.merge_ready_created_at or "")
  if left_has_created and left_created ~= right_created then
    return left_created < right_created
  end
  return tonumber(left.pr_number or 0) < tonumber(right.pr_number or 0)
end

local function entry_age_minutes(M, entry, now_seconds)
  local version = tostring(entry and entry.version or "")
  local updated_at = M.version_updated_at(version)
  if updated_at == "" then
    return nil
  end
  local marker_seconds = contract_time.iso_timestamp_epoch_seconds(updated_at)
  local current_seconds = tonumber(now_seconds)
  if marker_seconds == nil or current_seconds == nil or current_seconds < marker_seconds then
    return nil
  end
  return math.floor((current_seconds - marker_seconds) / 60)
end

local function compare_starvation_age(left, right)
  local left_age = tonumber(left and left.age_minutes)
  local right_age = tonumber(right and right.age_minutes)
  if left_age ~= right_age then
    return left_age > right_age
  end
  local left_created = tostring(left and left.entry and left.entry.merge_ready_created_at or "")
  local right_created = tostring(right and right.entry and right.entry.merge_ready_created_at or "")
  if left_created ~= "" and right_created ~= "" and left_created ~= right_created then
    return left_created < right_created
  end
  return tonumber(left and left.entry and left.entry.pr_number or 0) < tonumber(right and right.entry and right.entry.pr_number or 0)
end

local function predecessor_identity(entry)
  return "pr" .. tostring(entry.pr_number)
    .. "-" .. transition_version.safe_version_segment(entry.proposal_id)
    .. "-" .. transition_version.safe_version_segment(entry.version)
    .. "-" .. tostring(entry.head_sha)
end

local function path_set(paths)
  local set = {}
  for _, path in ipairs(paths or {}) do
    set[path] = true
  end
  return set
end

local function intersecting_path(left, right)
  for path in pairs(left or {}) do
    if right[path] then
      return path
    end
  end
  return nil
end

local function current_any_entity_state(M, entity_comments)
  local best = nil
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, entity_comments or {})) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      if marker_proposal ~= nil and entity_lib.parse_entity_proposal_id(marker_proposal) ~= nil then
        local candidate = M.current_state(entity_comments, marker_proposal)
        candidate.proposal_id = marker_proposal
        if best == nil or M.compare_state_marker_order(best, candidate.state, candidate.version) < 0 then
          best = candidate
        end
      end
    end
  end
  return best or {
    state = nil,
    version = nil,
    stage_rank = 0,
  }
end

local function merge_ready_version_for_lane_state(M, state)
  local version = tostring((state or {}).version or "")
  if state ~= nil and state.state == "fixing" and M._strip_latest_fix_version_suffix ~= nil then
    return M._strip_latest_fix_version_suffix(version)
  end
  return version
end

local function merge_queue_entry_from_pr(M, repo, pr_number, pr, expected_base)
  if type(pr) ~= "table" or tostring(pr.state or ""):upper() ~= "OPEN" then
    return nil
  end
  if tostring(pr.base_ref_name or "") ~= tostring(expected_base or "") then
    return nil
  end
  local state = current_any_entity_state(M, pr.comments)
  if not merge_queue_lane_states[state.state] then
    return nil
  end
  local current_head_sha = tostring(pr.head_sha or "")
  local fact = m_facts.merge_ready_fact(M, pr.comments, state.proposal_id or "", merge_ready_version_for_lane_state(M, state), pr_number, current_head_sha)
  if fact == nil then
    for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, pr.comments)) do
      for marker in parsers_misc._comment_body(M, comment):gmatch("<!%-%- fkst:github%-devloop:merge%-ready:v1.-%-%->") do
        local marker_issue = marker:match('proposal="([^"]+)"')
        if marker_issue ~= nil then
          local candidate_state = require("devloop.entity").current_entity_state(M, pr.comments, marker_issue)
          local merge_ready_version = merge_ready_version_for_lane_state(M, candidate_state)
          if merge_queue_lane_states[candidate_state.state]
            and tostring(merge_ready_version or "") == tostring(marker:match('version="([^"]*)"') or "") then
            fact = m_facts.merge_ready_fact(M, pr.comments, marker_issue, merge_ready_version, pr_number, current_head_sha)
            state = candidate_state
            break
          end
        end
      end
      if fact ~= nil then
        break
      end
    end
  end
  if fact == nil then
    return nil
  end
  return {
    pr_number = tonumber(pr_number),
    proposal_id = fact.proposal_id,
    version = fact.version,
    review_proposal_id = fact.review_proposal_id,
    review_dedup_key = fact.review_dedup_key,
    state = state.state,
    head_branch = pr.head_ref_name,
    head_sha = fact.head_sha,
    base_sha = pr.base_ref_oid,
    merge_ready_created_at = fact.comment_created_at or "",
  }
end

function C.merge_queue_head(M, repo, base_branch, current)
  local entries = {}
  local seen = {}
  if type(current) == "table" and current.pr_number ~= nil and type(current.pr) == "table" then
    local entry = merge_queue_entry_from_pr(M, repo, current.pr_number, current.pr, base_branch)
    if entry ~= nil then
      table.insert(entries, entry)
      seen[tostring(entry.pr_number)] = true
    end
  end

  local list = M.gh_pr_list_merge_queue(repo, base_branch, 30)
  if list.exit_code ~= 0 then
    error("github-devloop: merge queue PR list failed: " .. tostring(list.stderr))
  end
  for _, pr_item in ipairs(parsers_pr.parse_pr_list_merge_queue(M, list.stdout)) do
    local pr_number = tonumber(pr_item.number)
    if pr_number ~= nil and not seen[tostring(pr_number)] then
      local view = support.github().gh_pr_view_merge(repo, pr_number, 30)
      if view.exit_code ~= 0 then
        error("github-devloop: merge queue PR view failed: " .. tostring(view.stderr))
      end
      local pr = parsers_pr.parse_pr_view_merge(M, view.stdout)
      local entry = merge_queue_entry_from_pr(M, repo, pr_number, pr, base_branch)
      if entry ~= nil then
        table.insert(entries, entry)
        seen[tostring(entry.pr_number)] = true
      end
    end
  end
  table.sort(entries, compare_merge_queue_entries)
  return entries[1], entries
end

function C.merge_queue_starvation_candidate(M, entries, threshold_minutes, now_seconds)
  local threshold = tonumber(threshold_minutes)
  local current_seconds = tonumber(now_seconds) or now()
  if threshold == nil or threshold < 0 then
    return nil
  end
  local selected = nil
  for _, entry in ipairs(entries or {}) do
    local age = entry_age_minutes(M, entry, current_seconds)
    if entry.state == "merge-ready" and age ~= nil and age > threshold then
      local candidate = {
        entry = entry,
        age_minutes = age,
      }
      if selected == nil or compare_starvation_age(candidate, selected) then
        selected = candidate
      end
    end
  end
  return selected and selected.entry or nil, selected and selected.age_minutes or nil
end

function C.merge_queue_predecessors(M, repo, base_branch, current)
  local _, entries = C.merge_queue_head(M, repo, base_branch, current)
  local predecessors = {}
  local found = false
  local current_pr_number = tostring((current or {}).pr_number or "")
  for _, entry in ipairs(entries or {}) do
    if tostring(entry.pr_number or "") == current_pr_number then
      found = true
      break
    end
    table.insert(predecessors, entry)
  end
  if not found then
    return nil, "not-in-merge-queue"
  end
  return predecessors, "ok"
end

function C.merge_queue_position(M, repo, base_branch, current)
  local predecessors, reason = C.merge_queue_predecessors(M, repo, base_branch, current)
  if predecessors == nil then
    return nil, reason
  end
  return {
    is_head = #predecessors == 0,
    predecessors = predecessors,
    predecessor_set = C.merge_queue_predecessor_set(M, predecessors),
  }, "ok"
end

function C.merge_queue_predecessor_set(M, entries)
  local values = {}
  for _, entry in ipairs(entries or {}) do
    table.insert(values, predecessor_identity(entry))
  end
  if #values == 0 then
    return "none"
  end
  return table.concat(values, ".")
end

local function predecessor_set_entries(predecessor_set)
  if predecessor_set == nil or predecessor_set == "" or predecessor_set == "none" then
    return {}
  end
  local entries = {}
  for entry in tostring(predecessor_set):gmatch("[^.]+") do
    table.insert(entries, entry)
  end
  return entries
end

local function predecessor_head_sha(predecessor)
  local head_sha = tostring(predecessor or ""):match("([0-9a-fA-F]+)$")
  if head_sha == nil or not forge_validators.is_git_sha(head_sha) then
    return nil
  end
  return head_sha
end

function C.merge_queue_predecessor_set_matches_current_base(M, recorded_set, current_set, base_branch)
  local recorded = predecessor_set_entries(recorded_set)
  local current = predecessor_set_entries(current_set)
  if #current > #recorded then
    return false, "predecessor-set-mismatch"
  end
  local offset = #recorded - #current
  for index, entry in ipairs(current) do
    if entry ~= recorded[offset + index] then
      return false, "predecessor-set-mismatch"
    end
  end
  if offset == 0 then
    return true, "predecessor-set-current"
  end
  local base_head, base_reason = git_mechanics.current_base_head(M.git, base_branch)
  if base_head == nil then
    return false, base_reason
  end
  for index = 1, offset do
    local head_sha = predecessor_head_sha(recorded[index])
    if head_sha == nil then
      return false, "predecessor-set-mismatch"
    end
    local result = git_mechanics.git_is_ancestor(M.git, head_sha, base_head, 30)
    if result.exit_code ~= 0 then
      return false, "predecessor-not-landed"
    end
  end
  return true, "predecessor-set-landed-prefix"
end

function C.merge_queue_allows_event(M, repo, base_branch, merge_ready, current_pr)
  local head = C.merge_queue_head(M, repo, base_branch, {
    pr_number = merge_ready.pr_number,
    pr = current_pr,
  })
  if head == nil then
    return false, "merge-queue-empty"
  end
  if tostring(head.proposal_id or "") ~= tostring(merge_ready.proposal_id or "")
    or tostring(head.version or "") ~= tostring(merge_ready.version or "")
    or tostring(head.pr_number or "") ~= tostring(merge_ready.pr_number or "")
    or tostring(head.head_sha or "") ~= tostring(merge_ready.reviewed_head_sha or "") then
    return false, "merge-queue-head-pr-" .. tostring(head.pr_number or "unknown")
  end
  return true, "merge-queue-head"
end

function C.merge_queue_tick_dedup_key(M, repo, merged_pr_number, next_entry)
  if type(next_entry) ~= "table" then
    error("github-devloop: invalid merge queue next entry")
  end
  return base_ids.dedup_key({
    "merge-queue",
    "requeue",
    base_ids.safe_repo(repo),
    "merged-pr",
    base_ids.safe_issue(merged_pr_number),
    "next-pr",
    base_ids.safe_issue(next_entry.pr_number),
    devloop_base.safe_head_segment(next_entry.head_sha),
  })
end

function C.merge_queue_tick_payload(M, repo, merged_pr_number, next_entry)
  if type(next_entry) ~= "table" then
    return nil
  end
  return {
    schema = "github-devloop.merge-queue-tick.v1",
    dedup_key = C.merge_queue_tick_dedup_key(M, repo, merged_pr_number, next_entry),
    source_ref = entity_lib.pr_source_ref(repo, next_entry.pr_number),
    cause = {
      kind = "merge-progress",
      merged_pr_number = tonumber(merged_pr_number),
      next_pr_number = tonumber(next_entry.pr_number),
      next_head_sha = tostring(next_entry.head_sha or ""),
    },
  }
end

function C.merge_queue_starvation_tick_payload(M, repo, incident_identity, head_entry, attempt_key)
  if type(head_entry) ~= "table" then
    return nil
  end
  local bounded_attempt = strings.sanitize_key(attempt_key or "attempt", false)
  return {
    schema = "github-devloop.merge-queue-tick.v1",
    dedup_key = base_ids.dedup_key({
      "merge-queue",
      "queue-starvation",
      base_ids.safe_repo(repo),
      tostring(incident_identity or "merge-ready"),
      bounded_attempt,
    }),
    source_ref = entity_lib.pr_source_ref(repo, head_entry.pr_number),
    cause = {
      kind = "queue-starvation",
      incident_identity = tostring(incident_identity or "merge-ready"),
      attempt_key = bounded_attempt,
      head_pr_number = tonumber(head_entry.pr_number),
      head_sha = tostring(head_entry.head_sha or ""),
      proposal_id = tostring(head_entry.proposal_id or ""),
      version = tostring(head_entry.version or ""),
    },
  }
end

function C.queue_starvation_reconcile_marker(M, issue_proposal_id, pr_number, version, head_sha, incident_identity, attempt_key, outcome)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid queue-starvation reconcile marker")
  end
  local incident = strings.sanitize_key(tostring(incident_identity or "merge-ready"), false)
  local attempt = strings.sanitize_key(tostring(attempt_key or "attempt"), false)
  local proof = strings.sanitize_key(tostring(outcome or "head-redriven"), false):gsub("/", "-")
  if not strings.is_bounded_string(version, M._max_dedup_len)
    or not strings.is_path_safe_key(incident, M._max_dedup_len)
    or not strings.is_path_safe_key(attempt, M._max_dedup_len)
    or not strings.is_bounded_string(proof, M._max_key_len) then
    error("github-devloop: invalid queue-starvation reconcile marker")
  end
  return '<!-- fkst:github-devloop:queue-starvation-reconcile:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" incident="' .. incident
    .. '" attempt="' .. attempt
    .. '" outcome="' .. proof
    .. '" -->'
end

function C.merge_ready_payload_from_queue_entry(M, entry, source_ref)
  if type(entry) ~= "table" then
    return nil
  end
  return payloads_builders.build_devloop_merge_ready_payload(M,
    entry.proposal_id,
    entry.pr_number,
    entry.version,
    {
      review_proposal_id = entry.review_proposal_id,
      review_dedup_key = entry.review_dedup_key,
      reviewed_head_sha = entry.head_sha,
    },
    source_ref
  )
end

function C.merge_queue_changed_files(M, repo, entry)
  local result = M.gh_pr_diff_name_only(repo, entry.pr_number, 30)
  if result.exit_code ~= 0 then
    return nil, "diff-name-only-failed: " .. tostring(result.stderr)
  end
  local paths = devloop_base.parse_name_only_paths(result.stdout)
  return {
    pr_number = entry.pr_number,
    proposal_id = entry.proposal_id,
    version = entry.version,
    base_sha = entry.base_sha,
    head_sha = entry.head_sha,
    paths = paths,
    set = path_set(paths),
  }, "changed-files-ok"
end

function C.merge_queue_files_disjoint(M, left, right)
  local path = intersecting_path(left and left.set, right and right.set)
  if path ~= nil then
    return false, path
  end
  return true, "disjoint"
end

function C.wip_capacity_allows_start(M, repo, current_issue_number)
  local max_inflight = config.max_inflight(M)
  if max_inflight == nil then
    return true, "wip-cap-disabled", 0, nil
  end

  local integration_branch = config.branch_config(M).integration

  local list = M.gh_issue_list_wip(repo, 30)
  if list.exit_code ~= 0 then
    error("github-devloop: WIP issue list failed: " .. tostring(list.stderr))
  end

  local count = 0
  for _, issue in ipairs(parsers_issue.parse_issue_number_list(M, list.stdout)) do
    local issue_number = tonumber(issue.number)
    if issue_number ~= nil and tostring(issue_number) ~= tostring(current_issue_number) then
      local view = M.gh_issue_view_state(repo, issue_number, 30)
      if view.exit_code ~= 0 then
        error("github-devloop: WIP issue state view failed: " .. tostring(view.stderr))
      end
      local current = parsers_issue.parse_issue_view_state(M, view.stdout)
      local proposal_id = base_ids.proposal_id(repo, issue_number)
      local state = M.current_state(current.comments, proposal_id)
      local classification = C.wip_admission_classification(M, repo, proposal_id, current.comments, state, integration_branch)
      if classification.counts then
        count = count + 1
      elseif classification.reason ~= "state-not-active-wip" then
        C.log_wip_exclusion(M, proposal_id, classification)
      end
    end
  end
  if count >= max_inflight then
    return false, "wip-cap-reached", count, max_inflight
  end
  return true, "wip-cap-available", count, max_inflight
end

local function pr_merge_view_for_wip(M, repo, pr_number)
  local view = support.github().gh_pr_view_merge(repo, pr_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: WIP PR state view failed: " .. tostring(view.stderr))
  end
  return parsers_pr.parse_pr_view_merge(M, view.stdout)
end

local merge_gate_wait_wip_states = {
  ["merge-ready"] = true,
  merging = true,
}

function C.wip_admission_classification(M, repo, proposal_id, issue_comments, state, integration_branch)
  local state_name = tostring(state and state.state or "")
  if not active_wip_states[state_name] then
    return {
      counts = false,
      reason = "state-not-active-wip",
      state = state_name,
    }
  end

  local link = m_facts.pr_link_fact(M, issue_comments, proposal_id)
  if link ~= nil and tostring(link.base_branch or "") ~= tostring(integration_branch or "") then
    return {
      counts = false,
      reason = "base-unmanaged",
      state = state_name,
      pr_number = link.pr_number,
      pr_base = link.base_branch,
      integration = integration_branch,
    }
  end

  if link ~= nil and merge_gate_wait_wip_states[state_name] then
    local current_pr = pr_merge_view_for_wip(M, repo, link.pr_number)
    local wait = nil
    if type(current_pr) == "table" and forge_validators.is_git_sha(current_pr.head_sha) then
      wait = m_mgw.merge_gate_wait_fact(M, current_pr.comments, proposal_id, state.version, link.pr_number, current_pr.head_sha)
    end
    if wait ~= nil then
      return {
        counts = false,
        reason = "merge-gate-wait",
        state = state_name,
        pr_number = link.pr_number,
        pr_base = link.base_branch,
        wait_kind = wait.kind,
        wait_reason = wait.reason,
      }
    end
  end

  return {
    counts = true,
    reason = "active-wip",
    state = state_name,
    pr_number = link and link.pr_number or nil,
    pr_base = link and link.base_branch or nil,
  }
end

function C.log_wip_exclusion(M, proposal_id, classification)
  local fields = {
    "reason=" .. tostring(classification.reason),
    "state=" .. tostring(classification.state),
  }
  if classification.pr_number ~= nil then
    table.insert(fields, "pr=" .. tostring(classification.pr_number))
  end
  if classification.pr_base ~= nil then
    table.insert(fields, "pr_base=" .. tostring(classification.pr_base))
  end
  if classification.integration ~= nil then
    table.insert(fields, "integration=" .. tostring(classification.integration))
  end
  if classification.wait_kind ~= nil then
    table.insert(fields, "wait_kind=" .. tostring(classification.wait_kind))
  end
  if classification.wait_reason ~= nil then
    table.insert(fields, "wait_reason=" .. tostring(classification.wait_reason))
  end
  M.log_line("info", "wip", proposal_id, "WIP_EXCLUDE", fields)
end
return C
