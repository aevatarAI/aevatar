local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local C = {}
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")
local no_revert_reopen = require("devloop.autonomy.no_revert_reopen")
local autonomy_projection = require("devloop.autonomy.projection")

local task_classes = {
  L0 = true,
  L1 = true,
  L2 = true,
  L3 = true,
  L4 = true,
  unknown = true,
}

local gate_states = {
  pass = true,
  fail = true,
  pending = true,
}

local audit_states = {
  ["true"] = true,
  ["false"] = true,
  pending = true,
  invalid_self_attested = true,
}

local required_gate_names = {
  "human_touch",
  "pre_merge_ci",
  "evidence_manifest",
  "post_merge_probe",
  "no_revert_reopen",
  "cost_budget",
}

local terminal_attempt_states = {
  blocked = true,
  ["impl-failed"] = true,
  merged = true,
}

local timeout_order_states = {
  "thinking",
  "ready",
  "implementing",
  "impl-failed",
  "pr-open",
  "reviewing",
  "review-meta",
  "merge-ready",
  "merging",
  "fixing",
  "blocked",
}

local function marker_attr(marker, name)
  return tostring(marker or ""):match(name .. '="([^"]*)"')
end

local function normalize_gate_state(value)
  local state = tostring(value or "")
  if gate_states[state] then
    return state
  end
  return "pending"
end

local function normalize_task_class(value)
  local class = tostring(value or "")
  if task_classes[class] then
    return class
  end
  return "unknown"
end

local function label_name(label)
  if type(label) == "table" then
    if label.name ~= nil then
      return tostring(label.name)
    end
    if label.label ~= nil then
      return tostring(label.label)
    end
  elseif label ~= nil then
    return tostring(label)
  end
  return ""
end

local function task_class_from_label(label)
  local text = label_name(label)
  local found = text:match("[Aa][Vv][Mm][%-%_: ]*([Ll][0-4])")
    or text:match("[Tt][Aa][Ss][Kk][%-%_ ]*[Cc][Ll][Aa][Ss][Ss][%-%_: ]*([Ll][0-4])")
    or text:match("[Cc][Oo][Mm][Pp][Ee][Tt][Ee][Nn][Cc][Ee][%-%_: ]*([Ll][0-4])")
  if found == nil then
    return nil
  end
  return found:upper()
end

local title_patterns = {
  { class = "L4", patterns = { "security", "auth", "credential", "secret", "cross%-repo", "api" } },
  { class = "L3", patterns = { "engine", "scheduler", "recovery", "conformance", "liveness", "saga", "watchdog" } },
  { class = "L2", patterns = { "refactor", "cross%-module", "architecture", "adapter", "ports" } },
  { class = "L1", patterns = { "fix", "bug", "test", "harness", "regression" } },
  { class = "L0", patterns = { "docs", "documentation", "readme", "comment", "chore" } },
}

function C.autonomy_task_class(M, issue)
  if type(issue) == "table" then
    for _, label in ipairs(issue.labels or {}) do
      local class = task_class_from_label(label)
      if class ~= nil then
        return normalize_task_class(class)
      end
    end
    local title = tostring(issue.title or ""):lower()
    for _, entry in ipairs(title_patterns) do
      for _, pattern in ipairs(entry.patterns) do
        if title:find(pattern) ~= nil then
          return entry.class
        end
      end
    end
  end
  return "unknown"
end

function C.autonomy_valid_autonomous_merge(M, gates)
  local has_pending = false
  for _, name in ipairs(required_gate_names) do
    local state = normalize_gate_state(type(gates) == "table" and gates[name] or nil)
    if state == "fail" then
      return "false"
    end
    if state == "pending" then
      has_pending = true
    end
  end
  if has_pending then
    return "pending"
  end
  return "true"
end

function C.autonomy_merge_rounds(M, version)
  return M.version_loop_round(version) + M.version_fix_round(version)
end

function C.autonomy_post_merge_probe_gate(M, pr, opts)
  local green, reason = M.evaluate_ci_status_gate(pr, opts)
  if green then
    return "pass", reason
  end
  return "fail", reason
end

local function autonomy_projection_proposal_id(repo, issue_number, opts)
  if type(opts) == "table" and opts.proposal_id ~= nil then
    return tostring(opts.proposal_id)
  end
  return "github-devloop/issue/" .. tostring(repo or "") .. "/" .. tostring(issue_number or "")
end

local function comment_evidence(M, comment)
  return {
    comment_id = type(comment) == "table" and comment.id or nil,
    comment_url = type(comment) == "table" and comment.url or nil,
    comment_created_at = parsers_misc._comment_created_at(M, comment),
  }
end

local function event_created_seconds(M, event)
  return contract_time.iso_timestamp_epoch_seconds(event.comment_created_at)
end

local function version_max_timeout_round(M, version)
  local max_n = 0
  for _, state_name in ipairs(timeout_order_states) do
    max_n = math.max(max_n, M.version_timeout_round(version, state_name))
  end
  return max_n
end

local function event_order_key(M, event)
  local version = tostring(event.version or event.claim_epoch or "")
  local primary = M.version_updated_at(version)
  if primary == "" then
    primary = M.version_order_key(version)
  end
  return {
    primary = primary,
    loop_n = M.version_loop_round(version),
    fix_n = M.version_fix_round(version),
    reimplement_n = M.version_reimplement_round(version),
    timeout_n = version_max_timeout_round(M, version),
    review_loop_n = M.version_review_loop_round(version),
    review_meta_action_n = M.version_review_meta_action_round(version),
    stage_rank = tonumber(event.stage_rank) or 0,
    kind_rank = event.kind == "claim" and 0 or 1,
    created_seconds = event_created_seconds(M, event),
    sequence = tonumber(event.sequence) or 0,
  }
end

local function event_before(M, left, right)
  local a = event_order_key(M, left)
  local b = event_order_key(M, right)
  for _, name in ipairs({
    "primary",
    "loop_n",
    "fix_n",
    "reimplement_n",
    "timeout_n",
    "review_meta_action_n",
    "review_loop_n",
    "stage_rank",
    "kind_rank",
  }) do
    if a[name] ~= b[name] then
      return a[name] < b[name]
    end
  end
  if a.created_seconds == nil then
    if b.created_seconds ~= nil then
      return false
    end
  elseif b.created_seconds == nil then
    return true
  elseif a.created_seconds ~= b.created_seconds then
    return a.created_seconds < b.created_seconds
  end
  return a.sequence < b.sequence
end

function C._autonomy_event_before(M, left, right)
  return event_before(M, left, right)
end

local function claim_epoch_key(dedup_key, attempt)
  return tostring(dedup_key or "") .. "#" .. tostring(attempt or "")
end

local function collect_autonomy_claim_events(M, comments, proposal_id, repo, issue_number, events, sequence)
  local seen = {}
  local marker_pattern = "<!%-%- fkst:github%-devloop:implement%-attempt:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    local body = parsers_misc._comment_body(M, comment)
    for marker in body:gmatch(marker_pattern) do
      sequence = sequence + 1
      local marker_proposal = marker_attr(marker, "proposal")
      local dedup_key = marker_attr(marker, "dedup")
      local attempt = tonumber(marker_attr(marker, "attempt"))
      local started_at = marker_attr(marker, "started_at")
      if marker_proposal == proposal_id
        and strings.is_bounded_string(dedup_key, M._max_dedup_len)
        and attempt ~= nil
        and attempt >= 1
        and attempt == math.floor(attempt) then
        local epoch = claim_epoch_key(dedup_key, attempt)
        if seen[epoch] == nil then
          seen[epoch] = true
          table.insert(events, {
            kind = "claim",
            proposal_id = marker_proposal,
            repo = tostring(repo or ""),
            issue_number = tostring(issue_number or ""),
            claim_epoch = epoch,
            claim_version = dedup_key,
            version = dedup_key,
            claim_attempt = attempt,
            started_at = started_at,
            comment_created_at = parsers_misc._comment_created_at(M, comment),
            evidence = comment_evidence(M, comment),
            sequence = sequence,
          })
        end
      end
    end
  end
  return sequence
end

local function put_terminal_event(events_by_key, key, event)
  local existing = events_by_key[key]
  if existing == nil or (event.marker_family == "merged" and existing.marker_family ~= "merged") then
    events_by_key[key] = event
  end
end

local function collect_autonomy_terminal_events(M, comments, proposal_id, events, sequence)
  local terminals = {}
  local state_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  local merged_pattern = "<!%-%- fkst:github%-devloop:merged:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    local body = parsers_misc._comment_body(M, comment)
    for marker in body:gmatch(state_pattern) do
      sequence = sequence + 1
      local marker_proposal = marker_attr(marker, "proposal")
      local state = marker_attr(marker, "state")
      local version = marker_attr(marker, "version")
      if marker_proposal == proposal_id
        and terminal_attempt_states[state] == true
        and strings.is_bounded_string(version, M._max_dedup_len) then
        put_terminal_event(terminals, tostring(state) .. ":" .. tostring(version), {
          kind = "terminal",
          marker_family = "state",
          proposal_id = marker_proposal,
          outcome = state,
          terminal_state = state,
          version = version,
          stage_rank = M.stage_rank(state),
          comment_created_at = parsers_misc._comment_created_at(M, comment),
          evidence = comment_evidence(M, comment),
          sequence = sequence,
        })
      end
    end
    for marker in body:gmatch(merged_pattern) do
      sequence = sequence + 1
      local marker_proposal = marker_attr(marker, "proposal")
      local version = marker_attr(marker, "version")
      local pr_number = marker_attr(marker, "pr")
      local head_sha = marker_attr(marker, "head_sha")
      if marker_proposal == proposal_id
        and strings.is_bounded_string(version, M._max_dedup_len)
        and forge_validators.is_positive_pr_number(pr_number)
        and forge_validators.is_git_sha(head_sha) then
        local autonomy_result = nil
        if marker:find('autonomy_result="v1"', 1, true) ~= nil then
          autonomy_result = C.autonomy_result_record_from_marker(M, marker, comment, marker_proposal, pr_number, version, head_sha)
        end
        put_terminal_event(terminals, "merged:" .. tostring(version), {
          kind = "terminal",
          marker_family = "merged",
          proposal_id = marker_proposal,
          outcome = "merged",
          terminal_state = "merged",
          version = version,
          pr_number = tonumber(pr_number),
          head_sha = head_sha,
          autonomy_result = autonomy_result,
          valid_autonomous_merge = autonomy_result and autonomy_result.valid_autonomous_merge or nil,
          stage_rank = M.stage_rank("merged"),
          comment_created_at = parsers_misc._comment_created_at(M, comment),
          evidence = comment_evidence(M, comment),
          sequence = sequence,
        })
      end
    end
  end
  for _, event in pairs(terminals) do
    table.insert(events, event)
  end
  return sequence
end

local function unresolved_attempt_outcome(M, row, opts)
  local options = opts or {}
  local now_seconds = tonumber(options.now_seconds)
  local claim_seconds = contract_time.iso_timestamp_epoch_seconds(row.claim_comment_created_at)
  if now_seconds ~= nil and claim_seconds ~= nil then
    local age = now_seconds - claim_seconds
    local timed_out_after = tonumber(options.timed_out_after_seconds)
    if timed_out_after ~= nil and age >= timed_out_after then
      return "timed_out"
    end
    local abandoned_after = tonumber(options.abandoned_after_seconds)
    if abandoned_after ~= nil and age >= abandoned_after then
      return "abandoned"
    end
  end
  return "in_flight"
end

local function close_attempt_with_terminal(row, event)
  row.outcome = event.outcome
  row.terminal_state = event.terminal_state
  row.terminal_version = event.version
  row.terminal_comment_created_at = event.comment_created_at
  row.terminal_evidence = event.evidence
  row.pr_number = event.pr_number
  row.head_sha = event.head_sha
  row.autonomy_result = event.autonomy_result
  row.valid_autonomous_merge = event.valid_autonomous_merge
end

local function close_attempt_without_terminal(M, row, opts)
  if row.outcome == nil then
    row.outcome = unresolved_attempt_outcome(M, row, opts)
  end
end

function C.autonomy_attempt_projection(M, comments, repo, issue_number, opts)
  local proposal_id = autonomy_projection_proposal_id(repo, issue_number, opts)
  local events = {}
  local sequence = collect_autonomy_claim_events(M, comments, proposal_id, repo, issue_number, events, 0)
  collect_autonomy_terminal_events(M, comments, proposal_id, events, sequence)
  table.sort(events, function(left, right)
    return event_before(M, left, right)
  end)

  local projection = {
    schema = "github-devloop.autonomy-attempt-projection.v1",
    repo = tostring(repo or ""),
    issue_number = tostring(issue_number or ""),
    proposal_id = proposal_id,
    total_attempts = 0,
    attempts = {},
    outcomes = {},
    valid_merges = 0,
  }
  local current = nil
  for _, event in ipairs(events) do
    if event.kind == "claim" then
      if current ~= nil then
        close_attempt_without_terminal(M, current, opts)
      end
      current = {
        attempt_id = tostring(repo or "") .. "#issue/" .. tostring(issue_number or "") .. "#" .. event.claim_epoch,
        repo = tostring(repo or ""),
        issue_number = tostring(issue_number or ""),
        proposal_id = event.proposal_id,
        claim_epoch = event.claim_epoch,
        claim_version = event.claim_version,
        claim_marker_id = event.evidence and event.evidence.comment_id or nil,
        attempt = event.claim_attempt,
        started_at = event.started_at,
        claim_comment_created_at = event.comment_created_at,
        claim_evidence = event.evidence,
      }
      table.insert(projection.attempts, current)
    elseif event.kind == "terminal" and current ~= nil and current.outcome == nil then
      close_attempt_with_terminal(current, event)
      current = nil
    end
  end
  if current ~= nil then
    close_attempt_without_terminal(M, current, opts)
  end

  projection.total_attempts = #projection.attempts
  for _, row in ipairs(projection.attempts) do
    local outcome = tostring(row.outcome or "unknown")
    projection.outcomes[outcome] = (projection.outcomes[outcome] or 0) + 1
    if row.valid_autonomous_merge == "true" then
      projection.valid_merges = projection.valid_merges + 1
    end
  end
  return projection
end

function C.autonomy_attempt_denominator(M, comments, repo, issue_number, opts)
  return C.autonomy_attempt_projection(M, comments, repo, issue_number, opts).total_attempts
end

function C.autonomy_result_record(M, repo, issue_number, merge_ready, issue, post_merge_pr)
  local human_touch_count = 0
  local post_merge_probe = "pending"
  if post_merge_pr ~= nil then
    post_merge_probe = C.autonomy_post_merge_probe_gate(M, post_merge_pr, {
      repo = repo,
      dept = "merge",
      proposal_id = tostring(merge_ready.proposal_id),
    })
  end
  local no_revert_reopen_gate = no_revert_reopen.gate({
    repo = repo,
    issue_number = issue_number,
    pr_number = merge_ready.pr_number,
    merged_at = post_merge_pr and post_merge_pr.merged_at or nil,
  }, {
    merged_pr = post_merge_pr,
    issue = issue,
  })
  local gates = {
    human_touch = human_touch_count == 0 and "pass" or "fail",
    pre_merge_ci = "pass",
    evidence_manifest = "pending",
    post_merge_probe = post_merge_probe,
    no_revert_reopen = no_revert_reopen_gate,
    cost_budget = "pending",
  }
  return {
    schema = "github-devloop.autonomy-result.v1",
    proposal_id = tostring(merge_ready.proposal_id),
    repo = tostring(repo or ""),
    issue_number = issue_number ~= nil and tostring(issue_number) or "",
    pr_number = tostring(merge_ready.pr_number),
    version = tostring(merge_ready.version),
    head_sha = tostring(merge_ready.reviewed_head_sha),
    merged_at = post_merge_pr and post_merge_pr.merged_at or nil,
    task_class = C.autonomy_task_class(M, issue),
    human_touch_count = human_touch_count,
    pre_merge_ci = gates.pre_merge_ci,
    rounds = C.autonomy_merge_rounds(M, merge_ready.version),
    retry_count = M.version_fix_round(merge_ready.version),
    codex_calls = nil,
    gates = gates,
    valid_autonomous_merge = C.autonomy_valid_autonomous_merge(M, gates),
  }
end

local function autonomy_result_parts(M, record)
  if type(record) ~= "table" then
    error("github-devloop: invalid autonomy result record")
  end
  local proposal_id = tostring(record.proposal_id or "")
  local repo = tostring(record.repo or "")
  local issue_number = tostring(record.issue_number or "")
  local pr_number = tostring(record.pr_number or "")
  local version = tostring(record.version or "")
  local head_sha = tostring(record.head_sha or "")
  local task_class = normalize_task_class(record.task_class)
  local human_touch_count = tonumber(record.human_touch_count)
  local rounds = tonumber(record.rounds)
  local retry_count = tonumber(record.retry_count)
  local codex_calls = record.codex_calls
  local gates = type(record.gates) == "table" and record.gates or {}
  local valid = C.autonomy_valid_autonomous_merge(M, gates)
  if valid ~= "true" and valid ~= "false" and valid ~= "pending" then
    error("github-devloop: invalid autonomy result predicate")
  end
  if not strings.is_path_safe_key(proposal_id, M._max_key_len)
    or not strings.is_path_safe_key(repo, M._max_key_len)
    or not forge_validators.is_positive_pr_number(issue_number)
    or not forge_validators.is_positive_pr_number(pr_number)
    or not strings.is_bounded_string(version, M._max_dedup_len)
    or not forge_validators.is_git_sha(head_sha)
    or human_touch_count == nil or human_touch_count < 0 or human_touch_count % 1 ~= 0
    or rounds == nil or rounds < 0 or rounds % 1 ~= 0
    or retry_count == nil or retry_count < 0 or retry_count % 1 ~= 0 then
    error("github-devloop: invalid autonomy result marker")
  end
  local codex_calls_value = "null"
  if codex_calls ~= nil then
    local parsed = tonumber(codex_calls)
    if parsed == nil or parsed < 0 or parsed % 1 ~= 0 then
      error("github-devloop: invalid autonomy result codex calls")
    end
    codex_calls_value = tostring(parsed)
  end
  return {
    proposal_id = proposal_id,
    repo = repo,
    issue_number = issue_number,
    pr_number = pr_number,
    version = version,
    head_sha = head_sha,
    task_class = task_class,
    human_touch_count = human_touch_count,
    rounds = rounds,
    retry_count = retry_count,
    codex_calls_value = codex_calls_value,
    gates = gates,
    valid = valid,
  }
end

function C.autonomy_result_marker_attrs(M, record)
  local parts = autonomy_result_parts(M, record)
  return ' repo="' .. parts.repo
    .. '" issue="' .. parts.issue_number
    .. '" task_class="' .. parts.task_class
    .. '" human_touch_count="' .. tostring(parts.human_touch_count)
    .. '" pre_merge_ci="' .. normalize_gate_state(parts.gates.pre_merge_ci)
    .. '" rounds="' .. tostring(parts.rounds)
    .. '" retry_count="' .. tostring(parts.retry_count)
    .. '" codex_calls="' .. parts.codex_calls_value
    .. '" gate_human_touch="' .. normalize_gate_state(parts.gates.human_touch)
    .. '" gate_evidence_manifest="' .. normalize_gate_state(parts.gates.evidence_manifest)
    .. '" gate_post_merge_probe="' .. normalize_gate_state(parts.gates.post_merge_probe)
    .. '" post_merge_probe_green="' .. normalize_gate_state(parts.gates.post_merge_probe)
    .. '" gate_no_revert_reopen="' .. normalize_gate_state(parts.gates.no_revert_reopen)
    .. '" gate_cost_budget="' .. normalize_gate_state(parts.gates.cost_budget)
    .. '" valid_autonomous_merge="' .. parts.valid .. '"'
end

function C.autonomy_result_marker(M, record)
  local parts = autonomy_result_parts(M, record)
  return '<!-- fkst:github-devloop:autonomy-result:v1 proposal="' .. parts.proposal_id
    .. '" repo="' .. parts.repo
    .. '" issue="' .. parts.issue_number
    .. '" pr="' .. parts.pr_number
    .. '" version="' .. parts.version
    .. '" head_sha="' .. parts.head_sha
    .. '" task_class="' .. parts.task_class
    .. '" human_touch_count="' .. tostring(parts.human_touch_count)
    .. '" pre_merge_ci="' .. normalize_gate_state(parts.gates.pre_merge_ci)
    .. '" rounds="' .. tostring(parts.rounds)
    .. '" retry_count="' .. tostring(parts.retry_count)
    .. '" codex_calls="' .. parts.codex_calls_value
    .. '" gate_human_touch="' .. normalize_gate_state(parts.gates.human_touch)
    .. '" gate_evidence_manifest="' .. normalize_gate_state(parts.gates.evidence_manifest)
    .. '" gate_post_merge_probe="' .. normalize_gate_state(parts.gates.post_merge_probe)
    .. '" post_merge_probe_green="' .. normalize_gate_state(parts.gates.post_merge_probe)
    .. '" gate_no_revert_reopen="' .. normalize_gate_state(parts.gates.no_revert_reopen)
    .. '" gate_cost_budget="' .. normalize_gate_state(parts.gates.cost_budget)
    .. '" valid_autonomous_merge="' .. parts.valid .. '"'
    .. ' -->'
end

function C.autonomy_result_record_from_marker(M, marker, comment, proposal_id, pr_number, version, head_sha)
  local marker_proposal = marker:match('proposal="([^"]+)"')
  local marker_pr = marker:match('pr="([^"]+)"')
  local marker_version = marker:match('version="([^"]*)"')
  local marker_head_sha = marker:match('head_sha="([^"]+)"')
  local task_class = normalize_task_class(marker:match('task_class="([^"]+)"'))
  local valid = marker:match('valid_autonomous_merge="([^"]+)"')
  local human_touch_count = tonumber(marker:match('human_touch_count="(%d+)"'))
  local rounds = tonumber(marker:match('rounds="(%d+)"'))
  local retry_count = tonumber(marker:match('retry_count="(%d+)"'))
  local codex_calls_raw = marker:match('codex_calls="([^"]+)"')
  local gates = {
    human_touch = normalize_gate_state(marker:match('gate_human_touch="([^"]+)"')),
    pre_merge_ci = normalize_gate_state(marker:match('pre_merge_ci="([^"]+)"')),
    evidence_manifest = normalize_gate_state(marker:match('gate_evidence_manifest="([^"]+)"')),
    post_merge_probe = normalize_gate_state(
      marker:match('post_merge_probe_green="([^"]+)"') or marker:match('gate_post_merge_probe="([^"]+)"')
    ),
    no_revert_reopen = normalize_gate_state(marker:match('gate_no_revert_reopen="([^"]+)"')),
    cost_budget = normalize_gate_state(marker:match('gate_cost_budget="([^"]+)"')),
  }
  if marker_proposal == nil then
    return nil, "missing_proposal"
  end
  if marker_proposal ~= tostring(proposal_id) then
    return nil, "mismatch_proposal"
  end
  if marker_pr == nil then
    return nil, "missing_pr"
  end
  if tostring(marker_pr) ~= tostring(pr_number) then
    return nil, "mismatch_pr"
  end
  if marker_version == nil then
    return nil, "missing_version"
  end
  if tostring(marker_version) ~= tostring(version) then
    return nil, "mismatch_version"
  end
  if marker_head_sha == nil then
    return nil, "missing_head_sha"
  end
  if tostring(marker_head_sha) ~= tostring(head_sha) then
    return nil, "mismatch_head_sha"
  end
  if not forge_validators.is_git_sha(marker_head_sha) then
    return nil, "invalid_head_sha"
  end
  if human_touch_count == nil then
    return nil, "missing_human_touch_count"
  end
  if rounds == nil then
    return nil, "missing_rounds"
  end
  if retry_count == nil then
    return nil, "missing_retry_count"
  end
  if valid ~= "true" and valid ~= "false" and valid ~= "pending" then
    return nil, "invalid_valid_autonomous_merge"
  end
  local codex_calls = nil
  if codex_calls_raw == nil then
    return nil, "missing_codex_calls"
  end
  if codex_calls_raw ~= "null" then
    codex_calls = tonumber(codex_calls_raw)
    if codex_calls == nil or codex_calls < 0 or codex_calls % 1 ~= 0 then
      return nil, "invalid_codex_calls"
    end
  end
  return {
    proposal_id = marker_proposal,
    repo = marker:match('repo="([^"]+)"'),
    issue_number = tonumber(marker:match('issue="(%d+)"')),
    pr_number = tonumber(marker_pr),
    version = marker_version,
    head_sha = marker_head_sha,
    task_class = task_class,
    human_touch_count = human_touch_count,
    pre_merge_ci = normalize_gate_state(marker:match('pre_merge_ci="([^"]+)"')),
    rounds = rounds,
    retry_count = retry_count,
    codex_calls = codex_calls,
    gates = gates,
    valid_autonomous_merge = C.autonomy_valid_autonomous_merge(M, gates),
    comment_created_at = parsers_misc._comment_created_at(M, comment),
  }
end

function C.autonomy_result_fact(M, comments, proposal_id, pr_number, version, head_sha)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:autonomy%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local fact = C.autonomy_result_record_from_marker(M, marker, comment, proposal_id, pr_number, version, head_sha)
      if fact ~= nil then
        return fact
      end
    end
  end
  return nil
end

function C.autonomy_audit_valid_autonomous_merge(M, fact, opts)
  if type(fact) ~= "table" then
    return nil
  end
  local no_revert_reopen_gate = no_revert_reopen.gate(fact, opts)
  local repo = tostring((type(opts) == "table" and opts.repo) or fact.repo or "")
  local head_sha = tostring((type(opts) == "table" and opts.merge_commit_sha) or fact.merge_commit_sha or fact.head_sha or "")
  if repo == "" or not forge_validators.is_git_sha(head_sha) then
    return {
      valid_autonomous_merge = "invalid_self_attested",
      reason = "missing-audit-source",
    }
  end
  local pr = {
    head_sha = head_sha,
    status_check_rollup = type(opts) == "table" and opts.status_check_rollup or {},
  }
  local green, reason = M.evaluate_ci_status_gate(pr, {
    repo = repo,
    dept = "autonomy-auditor",
    proposal_id = tostring(fact.proposal_id or ""),
  })
  local claimed_probe = normalize_gate_state(type(fact.gates) == "table" and fact.gates.post_merge_probe or nil)
  if green then
    return {
      valid_autonomous_merge = C.autonomy_valid_autonomous_merge(M, {
        human_touch = type(fact.gates) == "table" and fact.gates.human_touch or nil,
        pre_merge_ci = type(fact.gates) == "table" and fact.gates.pre_merge_ci or nil,
        evidence_manifest = type(fact.gates) == "table" and fact.gates.evidence_manifest or nil,
        post_merge_probe = "pass",
        no_revert_reopen = no_revert_reopen_gate,
        cost_budget = type(fact.gates) == "table" and fact.gates.cost_budget or nil,
      }),
      reason = "audited",
      gates = {
        post_merge_probe = "pass",
        no_revert_reopen = no_revert_reopen_gate,
      },
    }
  end
  if claimed_probe == "pass" then
    return {
      valid_autonomous_merge = "invalid_self_attested",
      reason = tostring(reason or "missing-post-merge-probe-run"),
      gates = {
        post_merge_probe = "fail",
        no_revert_reopen = no_revert_reopen_gate,
      },
    }
  end
  local state = "pending"
  if tostring(reason or "") == "rollup-red" then
    state = "invalid_self_attested"
  end
  return {
    valid_autonomous_merge = state,
    reason = tostring(reason or "post-merge-probe-not-green"),
    gates = {
      post_merge_probe = "fail",
      no_revert_reopen = no_revert_reopen_gate,
    },
  }
end

function C.autonomy_audited_result_fact(M, comments, proposal_id, pr_number, version, head_sha, opts)
  local fact = C.autonomy_result_fact(M, comments, proposal_id, pr_number, version, head_sha)
  if fact == nil then
    return nil
  end
  local audit = C.autonomy_audit_valid_autonomous_merge(M, fact, opts or {})
  if type(audit) == "table" and audit.valid_autonomous_merge ~= nil then
    local state = tostring(audit.valid_autonomous_merge)
    if not audit_states[state] then
      state = "invalid_self_attested"
    end
    fact.valid_autonomous_merge = state
    fact.audit_reason = audit.reason
    fact.audit_gates = audit.gates
    if type(audit.gates) == "table" and type(fact.gates) == "table" then
      for name, value in pairs(audit.gates) do
        fact.gates[name] = normalize_gate_state(value)
      end
    end
  end
  fact.attempt_projection = autonomy_projection.apply_audited_fact(C.autonomy_attempt_projection(M, comments, fact.repo, fact.issue_number, {
    proposal_id = proposal_id,
    now_seconds = type(opts) == "table" and opts.now_seconds or nil,
    timed_out_after_seconds = type(opts) == "table" and opts.timed_out_after_seconds or nil,
    abandoned_after_seconds = type(opts) == "table" and opts.abandoned_after_seconds or nil,
  }), fact)
  fact.attempts = fact.attempt_projection.attempts
  fact.attempt_outcomes = fact.attempt_projection.outcomes
  fact.avm_rate_numerator = fact.attempt_projection.valid_merges
  fact.avm_rate_denominator = fact.attempt_projection.total_attempts
  return fact
end

return C
