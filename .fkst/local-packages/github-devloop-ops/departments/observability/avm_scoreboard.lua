local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local parsers_misc = require("devloop.parsers.misc")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local M = {}
local contract_time = require("contract.time")
local no_revert_reopen = require("devloop.autonomy.no_revert_reopen")
local autonomy_projection = require("devloop.autonomy.projection")
local autonomy_ledger = require("devloop.autonomy_ledger")

function M.install_avm_scoreboard(core)
local task_levels = { "L0", "L1", "L2", "L3", "L4", "unclassified" }
local task_level_set = { L0 = true, L1 = true, L2 = true, L3 = true, L4 = true }

local function number_value(value)
  local parsed = tonumber(value)
  return parsed ~= nil and parsed >= 0 and parsed or nil
end

local function int_value(value)
  local parsed = number_value(value)
  return parsed ~= nil and math.floor(parsed) or 0
end

local function optional_int(value)
  local parsed = number_value(value)
  return parsed ~= nil and parsed == math.floor(parsed) and parsed or nil
end

local function normalize_task_level(value)
  local text = tostring(value or ""):upper()
  return task_level_set[text] and text or "unclassified"
end

local function gate_state(value)
  local text = tostring(value or ""):lower()
  if text == "pass" or text == "passed" or text == "true" or text == "green" or text == "success" then
    return "pass"
  end
  if text == "fail" or text == "failed" or text == "false" or text == "red"
    or text == "failure" or text == "invalid_self_attested" then
    return "fail"
  end
  if text == "pending" or text == "unknown" then
    return "pending"
  end
  return nil
end

local function safe_segment(value)
  local text = tostring(value or "unknown")
  text = text:gsub("[^%w%._%-/]", "-")
  if text == "" then return "unknown" end
  return #text > 120 and text:sub(1, 120) or text
end

local function marker_attr(marker, name)
  return tostring(marker or ""):match(tostring(name) .. '="([^"]*)"')
end

local function comments_from_entity(entity)
  local comments = {}
  local function append_list(values)
    if type(values) ~= "table" then return end
    for _, comment in ipairs(values) do
      table.insert(comments, comment)
    end
  end

  if type(entity) == "table" then
    append_list(entity.comments)
    if type(entity.parent_issue) == "table" then
      append_list(entity.parent_issue.comments)
    end
    if type(entity.issue) == "table" then
      append_list(entity.issue.comments)
    end
    if type(entity.pr) == "table" then
      append_list(entity.pr.comments)
    end
  end
  return comments
end

local function copy_fact(raw)
  local fact = {}
  if type(raw) ~= "table" then return fact end
  for key, value in pairs(raw) do
    fact[key] = value
  end
  return fact
end

local function pair_key(pair)
  return tostring(pair.reverted_pr or "")
    .. "->"
    .. tostring(pair.revert_pr or pair.issue_number or "")
    .. ":"
    .. tostring(pair.evidence or "")
end

local function append_pair(pairs, seen, pair)
  local key = pair_key(pair)
  if seen[key] then
    return
  end
  seen[key] = true
  table.insert(pairs, pair)
end

local function detect_false_consensus(fact, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  local pairs = no_revert_reopen.evidence(fact, {
    entities = entities,
    recent_merged_prs = recent_merged_prs,
    recent_merged_issues = recent_merged_issues,
    recent_revert_commits = recent_revert_commits,
  })
  return #pairs > 0, pairs
end

local function scanned_pr_contains(recent_merged_prs, pr_number)
  local number = tonumber(pr_number)
  if number == nil or type(recent_merged_prs) ~= "table" then return false end
  for _, pr in ipairs(recent_merged_prs) do
    if tonumber(pr and pr.number) == number then return true end
  end
  return false
end

local function decorate_with_attempt_projection(fact, comments, now_seconds)
  if type(fact) ~= "table" then
    return nil
  end
  if fact.avm_rate_numerator ~= nil and fact.avm_rate_denominator ~= nil then
    return fact
  end
  if fact.repo == nil or fact.issue_number == nil then
    return fact
  end
  local projection = autonomy_ledger.autonomy_attempt_projection(comments, fact.repo, fact.issue_number, {
    proposal_id = fact.proposal_id,
    now_seconds = now_seconds,
  })
  if projection.total_attempts > 0 then
    fact.attempt_projection = projection
    fact.attempts = projection.attempts
    fact.attempt_outcomes = projection.outcomes
    fact.avm_rate_numerator = projection.valid_merges
    fact.avm_rate_denominator = projection.total_attempts
  end
  return fact
end

local function decorate_with_false_consensus(fact, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  local detected, pairs = detect_false_consensus(fact, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  if detected then
    fact.false_consensus = true
    fact.false_consensus_pairs = pairs
    if type(fact.gates) ~= "table" then
      fact.gates = {}
    end
    fact.gates.no_revert_reopen = "fail"
  elseif fact.false_consensus == nil and scanned_pr_contains(recent_merged_prs, fact.pr_number) then
    fact.false_consensus = false
  end
  return fact
end

local function fact_issue_for_gate(fact, entities, recent_merged_issues)
  local issue_number = tonumber(fact and fact.issue_number)
  if issue_number == nil then
    return nil
  end
  for _, entity in ipairs(entities or {}) do
    local issue = type(entity) == "table" and (entity.parent_issue or entity.issue or entity) or nil
    local candidate = tonumber(issue and (issue.number or issue.issue_number)) or tonumber(entity and entity.issue_number)
    if candidate == issue_number then
      return issue
    end
  end
  for _, issue in ipairs(recent_merged_issues or {}) do
    local candidate = tonumber(issue and (issue.number or issue.issue_number))
    if candidate == issue_number then
      return issue
    end
  end
  return nil
end

local function fact_scan_for_gate(fact, recent_merged_prs, recent_merged_issues)
  if type(fact) ~= "table" then
    return nil
  end
  if type(fact.no_revert_reopen_scan) == "table" then
    return fact.no_revert_reopen_scan
  end
  local pr_number = tonumber(fact.pr_number)
  for _, pr in ipairs(recent_merged_prs or {}) do
    if pr_number ~= nil and tonumber(pr and (pr.number or pr.pr_number)) == pr_number
      and type(pr.no_revert_reopen_scan) == "table" then
      return pr.no_revert_reopen_scan
    end
  end
  local issue_number = tonumber(fact.issue_number)
  for _, issue in ipairs(recent_merged_issues or {}) do
    if issue_number ~= nil and tonumber(issue and (issue.number or issue.issue_number)) == issue_number
      and type(issue.no_revert_reopen_scan) == "table" then
      return issue.no_revert_reopen_scan
    end
  end
  return nil
end

local function decorate_with_no_revert_reopen(fact, now_seconds, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  if type(fact) ~= "table" then
    return fact
  end
  local gate = no_revert_reopen.gate(fact, {
    now_seconds = now_seconds,
    issue = fact_issue_for_gate(fact, entities, recent_merged_issues),
    entities = entities,
    recent_merged_prs = recent_merged_prs,
    recent_merged_issues = recent_merged_issues,
    recent_revert_commits = recent_revert_commits,
    no_revert_reopen_scan = fact_scan_for_gate(fact, recent_merged_prs, recent_merged_issues),
  })
  if type(fact.gates) ~= "table" then
    fact.gates = {}
  end
  fact.gates.no_revert_reopen = gate
  fact.valid_autonomous_merge = autonomy_ledger.autonomy_valid_autonomous_merge(fact.gates)
  if type(fact.attempt_projection) == "table" then
    autonomy_projection.apply_audited_fact(fact.attempt_projection, fact)
    fact.avm_rate_numerator = fact.attempt_projection.valid_merges
    fact.avm_rate_denominator = fact.attempt_projection.total_attempts
  end
  return fact
end

local function fact_from_marker(marker, comment)
  local proposal_id = marker_attr(marker, "proposal")
  local pr_number = marker_attr(marker, "pr")
  local version = marker_attr(marker, "version")
  local head_sha = marker_attr(marker, "head_sha")
  if proposal_id == nil or pr_number == nil or version == nil or head_sha == nil then
    return nil, "missing_identity"
  end
  local fact, reason = autonomy_ledger.autonomy_result_record_from_marker(marker, comment, proposal_id, pr_number, version, head_sha)
  if fact ~= nil and fact.issue_number == nil then
    local _, issue_number = base_ids.parse_proposal_id(proposal_id)
    fact.issue_number = tonumber(issue_number)
  end
  return fact, reason
end

local function log_marker_rejection(tag, reason, comment, marker)
  local marker_context = ""
  if marker ~= nil then
    marker_context = " proposal=" .. safe_segment(marker_attr(marker, "proposal"))
      .. " pr=" .. safe_segment(marker_attr(marker, "pr"))
      .. " version=" .. safe_segment(marker_attr(marker, "version"))
  end
  log.warn("github-devloop dept=observability tag=" .. tostring(tag)
    .. " reason=" .. safe_segment(reason)
    .. " author=" .. safe_segment(parsers_misc.comment_author_login(comment))
    .. marker_context)
end

local function append_comment_facts(facts, comments, now_seconds)
  local trust_set = m_claims.managed_bot_logins()
  if type(trust_set) == "table" and next(trust_set) == nil then
    trust_set = nil
  end
  for _, comment in ipairs(comments or {}) do
    local body = parsers_misc._comment_body(comment)
    local function append_marker(marker)
      local fact, reason = fact_from_marker(marker, comment)
      if fact ~= nil then
        table.insert(facts, decorate_with_attempt_projection(fact, comments, now_seconds))
      else
        log_marker_rejection("AVM_MARKER_REJECTED", reason or "parse_nil", comment, marker)
      end
    end
    if parsers_misc._is_trusted_comment(comment, trust_set) then
      for marker in body:gmatch("<!%-%- fkst:github%-devloop:autonomy%-result:v1.-%-%->") do
        append_marker(marker)
      end
      for marker in body:gmatch("<!%-%- fkst:github%-devloop:merged:v1.-%-%->") do
        if marker:find('autonomy_result="v1"', 1, true) ~= nil then
          append_marker(marker)
        end
      end
    elseif body:find("fkst:github-devloop:autonomy-result:v1", 1, true)
      or (body:find("fkst:github-devloop:merged:v1", 1, true)
        and body:find('autonomy_result="v1"', 1, true)) then
      log_marker_rejection("AVM_MARKER_COMMENT_REJECTED", "untrusted_author", comment)
    end
  end
end

local function append_direct_facts(facts, values)
  if type(values) ~= "table" then return end
  for _, value in ipairs(values) do
    if type(value) == "table" then
      table.insert(facts, copy_fact(value))
    end
  end
end

local function append_entity_direct_facts(facts, entity)
  if type(entity) ~= "table" then return end
  if type(entity.autonomy_result) == "table" then
    table.insert(facts, copy_fact(entity.autonomy_result))
  end
  append_direct_facts(facts, entity.avm_facts)
  append_direct_facts(facts, entity.autonomy_facts)
  append_direct_facts(facts, entity.autonomy_results)
end

local function append_recent_pr_facts(facts, recent_merged_prs, now_seconds)
  for _, pr in ipairs(recent_merged_prs or {}) do
    append_comment_facts(facts, comments_from_entity({ pr = pr }), now_seconds)
  end
end

local function append_recent_issue_facts(facts, recent_merged_issues, now_seconds)
  for _, issue in ipairs(recent_merged_issues or {}) do
    append_comment_facts(facts, comments_from_entity({ issue = issue }), now_seconds)
  end
end

local function repo_cache_segment(repo)
  return tostring(repo or ""):gsub("[^%w%._%-%/]", "-")
end

local function recent_merged_pr_cache_key(repo)
  return "github-devloop/avm/recent-merged-prs/" .. repo_cache_segment(repo)
end

local function recent_merged_issue_cache_key(repo)
  return "github-devloop/avm/recent-merged-issues/" .. repo_cache_segment(repo)
end

local function recent_merged_pr_view(pr, listed)
  pr.number = tonumber(pr.number) or tonumber(listed.number)
  if pr.title == nil or pr.title == "" then pr.title = tostring(listed.title or "") end
  pr.merged_at = pr.merged_at or listed.merged_at
  if pr.head_sha == nil or pr.head_sha == "" then
    pr.head_sha = listed.head_sha
  end
  return pr
end

local function recent_merged_issue_view(issue, listed)
  issue.number = tonumber(issue.number) or tonumber(listed.number)
  if issue.title == nil or issue.title == "" then issue.title = tostring(listed.title or "") end
  issue.closed_at = issue.closed_at or listed.closed_at
  issue.closedAt = issue.closedAt or listed.closedAt
  if type(issue.labels) ~= "table" then
    issue.labels = listed.labels
  end
  return issue
end

function core.collect_recent_merged_prs(repo, limits, deadline)
  local limit = math.max(1, math.floor(tonumber(limits and limits.entity_cap) or 25))
  local listed = core.observability_run_cmd({
    run = function(timeout)
      return core.gh_pr_list_recent_merged(repo, limit, timeout)
    end,
    read_coalesce = {
      key = recent_merged_pr_cache_key(repo),
      ttl_seconds = 60,
    },
  }, limits, deadline, "recent merged PR list")
  if core.observability_result_deferred(listed) then
    return nil
  end
  local prs = {}
  for _, item in ipairs(parsers_pr.parse_pr_list_recent_merged(listed.stdout)) do
    if not core.observability_has_budget(deadline) then
      log.warn("github-devloop dept=observability tag=AVM_FALSE_CONSENSUS_DEFERRED reason=deadline processed_prs=" .. tostring(#prs))
      break
    end
    local view = core.observability_run_cmd({
      run = function(timeout)
        return core.gh_pr_view_observe(repo, item.number, timeout)
      end,
    }, limits, deadline, "recent merged PR view")
    if core.observability_result_deferred(view) then
      log.warn("github-devloop dept=observability tag=AVM_FALSE_CONSENSUS_DEFERRED reason=deadline processed_prs=" .. tostring(#prs))
      break
    end
    table.insert(prs, recent_merged_pr_view(parsers_pr.parse_pr_view_origin(view.stdout), item))
  end
  return prs
end

function core.collect_recent_merged_issues(repo, limits, deadline)
  local limit = math.max(1, math.floor(tonumber(limits and limits.entity_cap) or 25))
  local listed = core.observability_run_cmd({
    run = function(timeout)
      return core.gh_issue_list_recent_closed(repo, limit, timeout)
    end,
    read_coalesce = {
      key = recent_merged_issue_cache_key(repo),
      ttl_seconds = 60,
    },
  }, limits, deadline, "recent merged issue list")
  if core.observability_result_deferred(listed) then
    return nil
  end
  local issues = {}
  for _, item in ipairs(parsers_issue.parse_issue_list_recent_closed(listed.stdout)) do
    if not core.observability_has_budget(deadline) then
      log.warn("github-devloop dept=observability tag=AVM_SCOREBOARD_DEFERRED reason=deadline processed_issues=" .. tostring(#issues))
      break
    end
    local view = core.observability_run_cmd({
      run = function(timeout)
        return core.gh_issue_view_observe(repo, item.number, timeout)
      end,
    }, limits, deadline, "recent merged issue view")
    if core.observability_result_deferred(view) then
      log.warn("github-devloop dept=observability tag=AVM_SCOREBOARD_DEFERRED reason=deadline processed_issues=" .. tostring(#issues))
      break
    end
    table.insert(issues, recent_merged_issue_view(parsers_issue.parse_issue_view_observe(core, view.stdout), item))
  end
  return issues
end

function core.collect_avm_scoreboard_facts(entities, now_seconds, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  local facts = {}
  for _, entity in ipairs(entities or {}) do
    append_entity_direct_facts(facts, entity)
    append_comment_facts(facts, comments_from_entity(entity), now_seconds)
  end
  append_recent_pr_facts(facts, recent_merged_prs, now_seconds)
  append_recent_issue_facts(facts, recent_merged_issues, now_seconds)
  for _, fact in ipairs(facts) do
    decorate_with_no_revert_reopen(fact, now_seconds, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
    decorate_with_false_consensus(fact, entities, recent_merged_prs, recent_merged_issues, recent_revert_commits)
  end
  return facts
end

function core.false_consensus_pairs(facts)
  local pairs = {}
  local seen = {}
  for _, fact in ipairs(facts or {}) do
    if type(fact) == "table" then
      for _, pair in ipairs(fact.false_consensus_pairs or {}) do
        append_pair(pairs, seen, pair)
      end
    end
  end
  table.sort(pairs, function(a, b)
    local left = tostring(a.reverted_pr or "") .. "/" .. tostring(a.revert_pr or a.issue_number or "")
    local right = tostring(b.reverted_pr or "") .. "/" .. tostring(b.revert_pr or b.issue_number or "")
    return left < right
  end)
  return pairs
end

local function fact_identity(fact)
  for _, key in ipairs({ "merge_id", "attempt_id", "id" }) do
    local value = fact[key]
    if value ~= nil and tostring(value) ~= "" then
      return key .. ":" .. tostring(value)
    end
  end
  local parts = {}
  for _, key in ipairs({ "proposal_id", "pr_number", "version", "head_sha" }) do
    local value = fact[key]
    if value ~= nil and tostring(value) ~= "" then
      table.insert(parts, tostring(value))
    end
  end
  if #parts >= 2 then
    return "merge:" .. table.concat(parts, "|")
  end
  return nil
end

local function empty_bucket(level)
  return {
    level = level,
    merges = 0,
    avm_numerator = 0,
    avm_denominator = 0,
    cost_total = 0,
    cost_missing = false,
    rounds = {},
    revert_numerator = 0,
    revert_denominator = 0,
    false_consensus_numerator = 0,
    false_consensus_denominator = 0,
  }
end

local function first_int(fact, keys)
  for _, key in ipairs(keys) do
    local parsed = optional_int(fact[key])
    if parsed ~= nil then
      return parsed
    end
  end
  return nil
end

local function first_number(fact, keys)
  for _, key in ipairs(keys) do
    local parsed = number_value(fact[key])
    if parsed ~= nil then
      return parsed
    end
  end
  return nil
end

local function avm_rate_parts(fact)
  local numerator = first_int(fact, { "avm_rate_numerator", "valid_merges" })
  local denominator = first_int(fact, { "avm_rate_denominator", "total_attempts" })
  if numerator ~= nil and denominator ~= nil then
    return numerator, denominator
  end
  if type(fact.attempt_projection) == "table" then
    numerator = first_int(fact.attempt_projection, { "valid_merges", "avm_rate_numerator" })
    denominator = first_int(fact.attempt_projection, { "total_attempts", "avm_rate_denominator" })
    if numerator ~= nil and denominator ~= nil then
      return numerator, denominator
    end
  end
  local valid = tostring(fact.valid_autonomous_merge or ""):lower()
  if valid == "true" or valid == "false" or valid == "pending" or valid == "invalid_self_attested" then
    return valid == "true" and 1 or 0, 1
  end
  return 0, 0
end

local function avm_cost(fact)
  return first_number(fact, { "cost", "total_cost", "cost_units", "codex_calls", "token_cost" })
end

local function nested_gate(fact, names)
  for _, name in ipairs(names) do
    local state = gate_state(fact[name])
    if state ~= nil then
      return state
    end
  end
  if type(fact.gates) == "table" then
    for _, name in ipairs(names) do
      local state = gate_state(fact.gates[name])
      if state ~= nil then
        return state
      end
    end
  end
  return nil
end

local function explicit_false_consensus_parts(fact)
  local numerator = first_int(fact, { "false_consensus_rate_numerator", "false_consensus_numerator" })
  local denominator = first_int(fact, { "false_consensus_rate_denominator", "false_consensus_denominator" })
  if numerator ~= nil and denominator ~= nil then
    return numerator, denominator
  end
  local value = fact.false_consensus
  if value == true then
    return 1, 1
  end
  if value == false then
    return 0, 1
  end
  local text = tostring(value or ""):lower()
  if text == "true" or text == "false" then
    return text == "true" and 1 or 0, 1
  end
  return nil, nil
end

function core.aggregate_avm_scoreboard(facts)
  local buckets = {}
  local seen = {}
  for _, level in ipairs(task_levels) do
    buckets[level] = empty_bucket(level)
  end

  for _, raw in ipairs(facts or {}) do
    if type(raw) == "table" then
      local identity = fact_identity(raw)
      if identity == nil or seen[identity] ~= true then
        if identity ~= nil then
          seen[identity] = true
        end
        local bucket = buckets[normalize_task_level(raw.task_level or raw.task_class or raw.risk_tier)]
        bucket.merges = bucket.merges + 1

        local avm_numerator, avm_denominator = avm_rate_parts(raw)
        bucket.avm_numerator = bucket.avm_numerator + avm_numerator
        bucket.avm_denominator = bucket.avm_denominator + avm_denominator

        local cost = avm_cost(raw)
        if cost == nil then
          bucket.cost_missing = true
        else
          bucket.cost_total = bucket.cost_total + cost
        end

        local rounds = first_int(raw, { "rounds", "median_rounds", "merge_rounds" })
        if rounds ~= nil then
          table.insert(bucket.rounds, rounds)
        end

        local revert_state = nested_gate(raw, { "no_revert_reopen", "gate_no_revert_reopen", "revert", "reopened" })
        if revert_state == "pass" or revert_state == "fail" then
          bucket.revert_denominator = bucket.revert_denominator + 1
          if revert_state == "fail" then
            bucket.revert_numerator = bucket.revert_numerator + 1
          end
        end

        local false_numerator, false_denominator = explicit_false_consensus_parts(raw)
        if false_numerator ~= nil and false_denominator ~= nil then
          bucket.false_consensus_numerator = bucket.false_consensus_numerator + false_numerator
          bucket.false_consensus_denominator = bucket.false_consensus_denominator + false_denominator
        end
      end
    end
  end

  local rows = {}
  for _, level in ipairs(task_levels) do
    table.insert(rows, buckets[level])
  end
  return rows
end

local function format_decimal(value)
  local text = string.format("%.2f", tonumber(value) or 0)
  text = text:gsub("0+$", ""):gsub("%.$", "")
  if text == "" then
    return "0"
  end
  return text
end

local function format_rate(numerator, denominator)
  if tonumber(denominator) == nil or tonumber(denominator) <= 0 then
    return "n/a"
  end
  local pct = (tonumber(numerator) or 0) / tonumber(denominator) * 100
  return tostring(int_value(numerator)) .. "/" .. tostring(int_value(denominator)) .. " (" .. format_decimal(pct) .. "%)"
end

local function format_median(values)
  if type(values) ~= "table" or #values == 0 then
    return "n/a"
  end
  local ordered = {}
  for _, value in ipairs(values) do
    table.insert(ordered, tonumber(value) or 0)
  end
  table.sort(ordered)
  local mid = math.floor(#ordered / 2) + 1
  if #ordered % 2 == 1 then
    return format_decimal(ordered[mid])
  end
  return format_decimal((ordered[mid - 1] + ordered[mid]) / 2)
end

local function format_cost_per_avm(bucket)
  if bucket.merges == 0 then
    return "n/a"
  end
  if bucket.cost_missing then
    return "unknown"
  end
  if bucket.avm_numerator <= 0 then
    return "n/a"
  end
  return format_decimal(bucket.cost_total / bucket.avm_numerator)
end

function core.render_avm_scoreboard_bucket(bucket)
  return "- " .. tostring(bucket.level)
    .. " merges=" .. tostring(bucket.merges)
    .. " AVM-rate=" .. format_rate(bucket.avm_numerator, bucket.avm_denominator)
    .. " cost-per-AVM=" .. format_cost_per_avm(bucket)
    .. " revert-rate=" .. format_rate(bucket.revert_numerator, bucket.revert_denominator)
    .. " median-rounds=" .. format_median(bucket.rounds)
    .. " false-consensus-rate=" .. format_rate(bucket.false_consensus_numerator, bucket.false_consensus_denominator)
end
end

return M
