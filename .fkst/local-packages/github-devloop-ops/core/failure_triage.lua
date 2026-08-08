local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local error_facts = require("contract.error_facts")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local S = {}

function S.install(M)
local threshold = 3
local window_seconds = 24 * 60 * 60

local function triage_window_key(now_seconds)
  return "window-" .. tostring(math.floor((tonumber(now_seconds) or now()) / window_seconds))
end

local function normalized_fact(payload)
  if type(payload) ~= "table" then
    return nil, "payload-not-table"
  end

  local source = payload
  if type(payload.payload) == "table" then
    source = payload.payload
  end

  local queue = source.queue or payload.queue
  if not strings.is_bounded_string(queue, M._max_key_len) then
    return nil, "missing-queue"
  end

  local fingerprint = source.fingerprint or payload.fingerprint
  if not strings.is_bounded_string(fingerprint, M._max_key_len) then
    return nil, "missing-fingerprint"
  end

  local source_ref = source.source_ref or payload.source_ref
  if type(source_ref) ~= "table" then
    return nil, "missing-source-ref"
  end

  local attempt = tonumber(source.attempt or payload.attempt or 1)
  if attempt == nil or attempt < 1 or attempt % 1 ~= 0 then
    return nil, "invalid-attempt"
  end

  local normalized_source_ref
  if source_ref.kind == "cron" and tostring(source_ref.ref or "") == "" then
    normalized_source_ref = { kind = "cron", ref = "" }
  else
    normalized_source_ref = base_ids.normalize_source_ref(source_ref)
  end
  local repo, issue_number = devloop_base.parse_issue_source_ref(normalized_source_ref)
  local parent_target
  if repo ~= nil then
    parent_target = {
      repo = repo,
      issue_number = tostring(issue_number),
    }
  else
    local pr_repo, pr_number = devloop_base.parse_pr_source_ref(normalized_source_ref)
    if pr_repo == nil then
      return {
        schema = tostring(source.schema or payload.schema or ""),
        queue = tostring(queue),
        dept = tostring(source.dept or payload.dept or ""),
        error_class = M.error_fact_class({ error_class = source.error_class or payload.error_class }),
        fingerprint = tostring(fingerprint),
        source_ref = normalized_source_ref,
        attempt = attempt,
        terminal = (source.terminal or payload.terminal) == true,
        message = tostring(source.message or source.error or payload.error or ""),
        delivery_id = tostring(payload.delivery_id or source.delivery_id or ""),
        dead_queue = tostring(payload.queue or ""),
        no_issue_parent = true,
      }, nil
    end
    repo = pr_repo
    parent_target = {
      repo = pr_repo,
      pr_number = tostring(pr_number),
    }
  end

  return {
    schema = tostring(source.schema or payload.schema or ""),
    queue = tostring(queue),
    dept = tostring(source.dept or payload.dept or ""),
    error_class = M.error_fact_class({ error_class = source.error_class or payload.error_class }),
    fingerprint = tostring(fingerprint),
    source_ref = normalized_source_ref,
    source_repo = repo,
    parent_target = parent_target,
    attempt = attempt,
    terminal = (source.terminal or payload.terminal) == true,
    message = tostring(source.message or source.error or payload.error or ""),
    delivery_id = tostring(payload.delivery_id or source.delivery_id or ""),
    dead_queue = tostring(payload.queue or ""),
  }, nil
end

function M.failure_triage_dedup_key(repo, fingerprint)
  return base_ids.dedup_key({
    "failure-triage",
    base_ids.safe_repo(repo),
    tostring(fingerprint or "unknown"),
  })
end

local function fact_count_key(repo, fingerprint)
  return M.failure_triage_count_key(repo, fingerprint, triage_window_key())
end

function M.failure_triage_count_key(repo, fingerprint, window_key)
  return base_ids.dedup_key({
    "failure-triage-count",
    base_ids.safe_repo(repo),
    tostring(fingerprint or "unknown"),
    tostring(window_key or triage_window_key()),
  })
end

local function seen_key(repo, fingerprint)
  return base_ids.dedup_key({
    "failure-triage-seen",
    base_ids.safe_repo(repo),
    tostring(fingerprint or "unknown"),
  })
end

local function threshold_key(repo, fingerprint, window_key)
  return base_ids.dedup_key({
    "failure-triage-threshold",
    base_ids.safe_repo(repo),
    tostring(fingerprint or "unknown"),
    tostring(window_key or triage_window_key()),
  })
end

local function recorded_count(repo, fingerprint)
  local raw = cache_get(fact_count_key(repo, fingerprint))
  return tonumber(raw) or 0
end

local function record_count(repo, fingerprint, count)
  cache_set(fact_count_key(repo, fingerprint), tostring(count))
end

local function first_seen(repo, fingerprint)
  local key = seen_key(repo, fingerprint)
  if cache_get(key) == "1" then
    return false
  end
  cache_set(key, "1")
  return true
end

local function claim_threshold(repo, fingerprint, window_key)
  local key = threshold_key(repo, fingerprint, window_key)
  if cache_get(key) == "1" then
    return false
  end
  cache_set(key, "1")
  return true
end

local function display_text(value, limit)
  local text = devloop_base.neutralize_untrusted_comment_text(error_facts.one_line(value))
  text = text:gsub("`", "'"):gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then
    text = "unknown"
  end
  if limit ~= nil and #text > limit then
    text = base_ids.truncate_utf8(text, limit)
  end
  return text
end

local function display_source_ref(source_ref)
  local value = tostring(source_ref and source_ref.kind or "") .. ":" .. tostring(source_ref and source_ref.ref or "")
  return display_text(value, M._max_key_len * 2 + 1)
end

local function attr(marker, name)
  return tostring(marker or ""):match(tostring(name) .. '="([^"]*)"')
end

local function output_obligation_reason_class(reason_class)
  local value = tostring(reason_class or "")
  if value == "state-output-obligation-timeout" then
    return value
  end
  if value == "decompose-output-obligation-timeout" then
    return value
  end
  return nil
end

local function output_obligation_dedup_key(repo, _proposal_id, _terminal_version, reason_class)
  return base_ids.dedup_key({
    "output-obligation",
    "blocked",
    base_ids.safe_repo(repo),
    tostring(reason_class or "unknown"),
  })
end

local function marker_attr(value, limit)
  local text = tostring(value or "")
  if limit ~= nil and #text > limit then
    text = base_ids.truncate_utf8(text, limit)
  end
  return text:gsub("\r", " ")
    :gsub("\n", " ")
    :gsub("&", "&amp;")
    :gsub('"', "&quot;")
    :gsub("<", "&lt;")
    :gsub(">", "&gt;")
end

local function output_obligation_escalation_marker(fact)
  return '<!-- fkst:github-devloop-ops:output-obligation-escalation:v1 proposal="'
    .. marker_attr(fact.proposal_id, M._max_key_len)
    .. '" terminal_version="' .. marker_attr(fact.terminal_version, M._max_dedup_len)
    .. '" dedup="' .. marker_attr(fact.dedup_key, M._max_dedup_len)
    .. '" reason_class="' .. marker_attr(fact.reason_class, M._max_key_len)
    .. '" parent="' .. marker_attr(tostring(fact.source_repo or "") .. "#issue/" .. tostring(fact.issue_number or ""), M._max_key_len * 2 + 7)
    .. '" -->'
end

local function issue_created_drain_edge(comments, dedup_key)
  if type(comments) ~= "table" then
    return nil
  end
  local pattern = "<!%-%- fkst:github%-proxy:issue%-created:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(pattern) do
      if attr(marker, "dedup") == tostring(dedup_key) then
        local issue_number = attr(marker, "issue")
        if issue_number ~= nil and tostring(issue_number):match("^%d+$") then
          return {
            kind = "superseded-by-escalation-issue",
            issue_id = tostring(issue_number),
            dedup_key = tostring(dedup_key),
          }
        end
      end
    end
  end
  return nil
end

local function issue_body_semantic_drain_edge(issue, issue_number, dedup_key, _terminal_version, reason_class, source_repo)
  local marker_pattern = "<!%-%- fkst:github%-devloop%-ops:output%-obligation%-escalation:v1.-%-%->"
  for marker in tostring(issue and issue.body or ""):gmatch(marker_pattern) do
    local exact_problem = attr(marker, "dedup") == tostring(dedup_key)
    local legacy_problem = reason_class ~= nil
      and source_repo ~= nil
      and attr(marker, "reason_class") == tostring(reason_class)
      and tostring(attr(marker, "parent") or ""):find(tostring(source_repo) .. "#issue/", 1, true) == 1
    if exact_problem or legacy_problem then
      return {
        kind = exact_problem and "output-obligation-escalation-issue" or "legacy-output-obligation-escalation-issue",
        issue_id = tostring(math.floor(tonumber(issue_number))),
        dedup_key = tostring(dedup_key),
        terminal_version = tostring(attr(marker, "terminal_version") or ""),
        reason_class = tostring(attr(marker, "reason_class") or ""),
      }
    end
  end
  return nil
end

local function issue_body_proxy_drain_edge(issue, issue_number, dedup_key)
  local marker = "<!-- fkst:github-proxy:issue-create:" .. tostring(dedup_key) .. " -->"
  if tostring(issue and issue.body or ""):find(marker, 1, true) ~= nil then
    return {
      kind = "superseded-by-observed-escalation-issue",
      issue_id = tostring(math.floor(tonumber(issue_number))),
      dedup_key = tostring(dedup_key),
    }
  end
  return nil
end

local function issue_body_legacy_proxy_drain_edge(issue, issue_number, reason_class, source_repo)
  if reason_class == nil or source_repo == nil then
    return nil
  end
  local prefix = "output-obligation/blocked/" .. base_ids.safe_repo(source_repo) .. "/"
  local suffix = "/" .. tostring(reason_class)
  local pattern = "<!%-%- fkst:github%-proxy:issue%-create:([^%s]+) %-%->"
  for legacy_dedup_key in tostring(issue and issue.body or ""):gmatch(pattern) do
    if legacy_dedup_key:sub(1, #prefix) == prefix
      and legacy_dedup_key:sub(-#suffix) == suffix then
      return {
        kind = "legacy-output-obligation-escalation-issue",
        issue_id = tostring(math.floor(tonumber(issue_number))),
        dedup_key = tostring(legacy_dedup_key),
        reason_class = tostring(reason_class),
      }
    end
  end
  return nil
end

local function issue_body_drain_edge(issues, dedup_key, terminal_version, reason_class, source_repo)
  if type(issues) ~= "table" then
    return nil
  end
  for _, issue in ipairs(issues) do
    local observed_issue = type(issue) == "table" and (issue.parent_issue or issue.issue or issue) or nil
    if type(issue) == "table"
      and type(observed_issue) == "table"
      and parsers_misc._comment_author_login(observed_issue) == devloop_base.trusted_bot_login()
      and tonumber(issue.issue_number or issue.number) ~= nil then
      local issue_number = tonumber(issue.issue_number or issue.number)
      local edge = issue_body_semantic_drain_edge(observed_issue, issue_number, dedup_key, terminal_version, reason_class, source_repo)
        or issue_body_proxy_drain_edge(observed_issue, issue_number, dedup_key)
        or issue_body_legacy_proxy_drain_edge(observed_issue, issue_number, reason_class, source_repo)
      if edge ~= nil then
        return edge
      end
    end
  end
  return nil
end

local function output_obligation_drain_edge(comments, dedup_key, issues, recent_issues, terminal_version, reason_class, source_repo)
  return issue_created_drain_edge(comments, dedup_key)
    or issue_body_drain_edge(issues, dedup_key, terminal_version, reason_class, source_repo)
    or issue_body_drain_edge(recent_issues, dedup_key, terminal_version, reason_class, source_repo)
end

local function normalized_output_obligation_fact(source)
  if type(source) ~= "table" then
    return nil, "payload-not-table"
  end
  local reason_class = output_obligation_reason_class(source.reason_class)
  if reason_class == nil then
    return nil, "not-output-obligation"
  end
  if tostring(source.terminal_state or source.state or "") ~= "blocked" then
    return nil, "not-blocked-terminal"
  end
  local ok_ref, source_ref = pcall(function()
    return base_ids.normalize_source_ref(source.source_ref)
  end)
  if not ok_ref then
    return nil, "missing-source-ref"
  end
  local repo, issue_number = devloop_base.parse_issue_source_ref(source_ref)
  if repo == nil then
    return nil, "missing-issue-source-ref"
  end
  local terminal_version = tostring(source.terminal_version or source.version or "")
  if terminal_version == "" then
    return nil, "missing-terminal-version"
  end
  local proposal_id = tostring(source.proposal_id or base_ids.proposal_id(repo, issue_number))
  local dedup_key = output_obligation_dedup_key(repo, proposal_id, terminal_version, reason_class)
  return {
    failure_kind = "OutputObligationFailure",
    source_repo = repo,
    issue_number = tostring(issue_number),
    proposal_id = proposal_id,
    terminal_state = "blocked",
    terminal_version = terminal_version,
    reason_class = reason_class,
    source_ref = source_ref,
    from_state = tostring(source.from_state or ""),
    from_version = tostring(source.from_version or ""),
    attempt = tostring(source.attempt or ""),
    attempt_limit = tostring(source.attempt_limit or ""),
    driving_queue = tostring(source.driving_queue or ""),
    why_text = tostring(source.why_text or source.why or ""),
    dedup_key = dedup_key,
    drain_edge = output_obligation_drain_edge(source.comments, dedup_key, nil, nil, terminal_version, reason_class, repo),
  }, nil
end

local function timeout_reconcile_facts(comments, proposal_id)
  local facts = {}
  if type(comments) ~= "table" then
    return facts
  end
  local pattern = "<!%-%- fkst:github%-devloop:timeout%-reconcile:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    local body_text = parsers_misc._comment_body(comment)
    for marker in body_text:gmatch(pattern) do
      local reason_class = output_obligation_reason_class(attr(marker, "reason_class"))
      if attr(marker, "proposal") == tostring(proposal_id)
        and attr(marker, "action") == "drop"
        and reason_class ~= nil then
        table.insert(facts, {
          proposal_id = attr(marker, "proposal"),
          terminal_state = "blocked",
          terminal_version = attr(marker, "version"),
          reason_class = reason_class,
          from_state = attr(marker, "from_state"),
          from_version = attr(marker, "from_version"),
          attempt = attr(marker, "attempt"),
          attempt_limit = attr(marker, "attempt_limit"),
          driving_queue = attr(marker, "driving_queue"),
          source_ref = {
            kind = attr(marker, "source_ref_kind"),
            ref = attr(marker, "source_ref"),
          },
          why_text = body_text:match("Structured WHY:\n(.*)") or "",
        })
      end
    end
  end
  return facts
end

local function output_obligation_title(fact)
  local result = "Escalate blocked output obligation class: "
    .. display_text(fact.reason_class, M._max_key_len)
  if #result > M._max_title_len then
    result = base_ids.truncate_utf8(result, M._max_title_len)
  end
  return result
end

local function output_obligation_body(fact)
  local lines = {
    "Blocked-obligation patrol filed this class-level problem from a persisted terminal workflow fact.",
    "",
    "Classification:",
    "- `failure_kind`: `OutputObligationFailure`",
    "- `reason_class`: `" .. display_text(fact.reason_class, M._max_key_len) .. "`",
    "- `terminal_state`: `" .. display_text(fact.terminal_state, M._max_key_len) .. "`",
    "- `parent`: `" .. display_text(tostring(fact.source_repo or "") .. "#" .. tostring(fact.issue_number or ""), M._max_key_len * 2 + 1) .. "`",
    "- `proposal_id`: `" .. display_text(fact.proposal_id, M._max_key_len) .. "`",
    "- `terminal_version`: `" .. display_text(fact.terminal_version, M._max_dedup_len) .. "`",
    "- `source_ref`: `" .. display_source_ref(fact.source_ref) .. "`",
    "",
    output_obligation_escalation_marker(fact),
    "",
    "Evidence instance:",
    "- `from_state`: `" .. display_text(fact.from_state, M._max_key_len) .. "`",
    "- `from_version`: `" .. display_text(fact.from_version, M._max_dedup_len) .. "`",
    "- `attempt`: `" .. display_text(fact.attempt, M._max_key_len) .. "`",
    "- `attempt_limit`: `" .. display_text(fact.attempt_limit, M._max_key_len) .. "`",
    "- `driving_queue`: `" .. display_text(fact.driving_queue, M._max_key_len) .. "`",
    "",
    "Requested outcome:",
    "- Restore the producer-owned invariant for every terminal in this reason class.",
    "- Make reconciliation idempotent and deduplicate all matching terminal instances to this problem identity.",
    "- Cover timeout and retry boundaries with executable tests.",
    "- Treat each matching parent terminal as covered by this escalation issue; do not mutate runtime state directly.",
  }
  if fact.why_text ~= nil and fact.why_text ~= "" then
    table.insert(lines, "")
    table.insert(lines, "WHY text:")
    table.insert(lines, devloop_base.neutralize_untrusted_comment_text(fact.why_text))
  end
  local result = table.concat(lines, "\n")
  if #result > M._max_body_len then
    result = base_ids.truncate_utf8(result, M._max_body_len)
  end
  return result
end

local function output_obligation_issue_request(fact)
  return {
    schema = "github-proxy.issue-create.v1",
    repo = fact.source_repo,
    title = output_obligation_title(fact),
    body = output_obligation_body(fact),
    labels = json.decode("[]"),
    dedup_key = fact.dedup_key,
    parent_comment_target = {
      repo = fact.source_repo,
      issue_number = tonumber(fact.issue_number),
    },
    source_ref = fact.source_ref,
  }
end

local function title(fact)
  local result = "Investigate L2 failure: " .. display_text(fact.error_class, M._max_key_len)
    .. " in " .. display_text(fact.queue, M._max_key_len)
  if #result > M._max_title_len then
    result = base_ids.truncate_utf8(result, M._max_title_len)
  end
  return result
end

local function body(fact, count)
  local lines = {
    "L2 failure triage filed this issue from an existing structured dead-letter fact.",
    "",
    "Contract facts:",
    "- `error_class`: `" .. display_text(fact.error_class, M._max_key_len) .. "`",
    "- `fingerprint`: `" .. display_text(fact.fingerprint, M._max_key_len) .. "`",
    "- `source_ref`: `" .. display_source_ref(fact.source_ref) .. "`",
    "- `attempt`: `" .. display_text(fact.attempt, M._max_key_len) .. "`",
    "- `terminal`: `" .. display_text(fact.terminal, M._max_key_len) .. "`",
    "",
    "Delivery context:",
    "- `queue`: `" .. display_text(fact.queue, M._max_key_len) .. "`",
    "- `dead_queue`: `" .. display_text(fact.dead_queue, M._max_key_len) .. "`",
    "- `dept`: `" .. display_text(fact.dept, M._max_key_len) .. "`",
    "- `delivery_id`: `" .. display_text(fact.delivery_id, M._max_key_len) .. "`",
    "- `observed_count`: `" .. display_text(count, M._max_key_len) .. "`",
    "",
    "Requested outcome:",
    "- Diagnose the structural cause behind this failure fingerprint.",
    "- Implement any fix through the normal issue -> PR -> review -> merge pipeline.",
    "- Do not mutate runtime state directly from this triage path.",
  }
  if fact.message ~= "" then
    table.insert(lines, "")
    table.insert(lines, "Failure summary:")
    table.insert(lines, devloop_base.neutralize_untrusted_comment_text(fact.message))
  end
  local result = table.concat(lines, "\n")
  if #result > M._max_body_len then
    result = base_ids.truncate_utf8(result, M._max_body_len)
  end
  return result
end

function M.build_failure_triage_issue_create_request(fact, count)
  if type(fact) ~= "table" then
    error("github-devloop: failure-triage-fact-missing: failure triage fact is required")
  end
  return {
    schema = "github-proxy.issue-create.v1",
    repo = fact.source_repo,
    title = title(fact),
    body = body(fact, count or 1),
    labels = json.decode("[]"),
    dedup_key = M.failure_triage_dedup_key(fact.source_repo, fact.fingerprint),
    parent_comment_target = fact.parent_target,
    source_ref = fact.source_ref,
  }
end

function M.output_obligation_failure_dedup_key(repo, proposal_id, terminal_version, reason_class)
  return output_obligation_dedup_key(repo, proposal_id, terminal_version, reason_class)
end

function M.output_obligation_escalation_marker(fact)
  return output_obligation_escalation_marker(fact)
end

function M.output_obligation_failure_drain_edge(comments, dedup_key, issues, recent_issues, terminal_version, reason_class, source_repo)
  return output_obligation_drain_edge(comments, dedup_key, issues, recent_issues, terminal_version, reason_class, source_repo)
end

function M.classify_output_obligation_failure(payload)
  local source = type(payload) == "table" and type(payload.payload) == "table" and payload.payload or payload
  return normalized_output_obligation_fact(source)
end

function M.blocked_output_obligation_failures(entity, observed_entities, recent_merged_issues)
  if type(entity) ~= "table" then
    return {}
  end
  local current = entity.current_state or entity.state
  if type(current) ~= "table" or current.state ~= "blocked" then
    return {}
  end
  local repo = entity.repo
  local parsed_repo, parsed_issue = base_ids.parse_proposal_id(entity.proposal_id)
  if repo == nil then
    repo = parsed_repo
  end
  local issue_number = entity.number or entity.issue_number or parsed_issue
  local comments = entity.comments
  if comments == nil and type(entity.parent_issue) == "table" then
    comments = entity.parent_issue.comments
  end
  local source_ref = entity.source_ref
  if source_ref == nil and repo ~= nil and issue_number ~= nil then
    source_ref = base_ids.issue_source_ref(repo, issue_number)
  end
  local failures = {}
  for _, fact in ipairs(timeout_reconcile_facts(comments, entity.proposal_id)) do
    if fact.terminal_version == current.version then
      if fact.source_ref == nil or fact.source_ref.kind == nil or fact.source_ref.ref == nil or fact.source_ref.ref == "" then
        fact.source_ref = source_ref
      end
      local normalized = normalized_output_obligation_fact(fact)
      if normalized ~= nil then
        normalized.drain_edge = output_obligation_drain_edge(comments, normalized.dedup_key, observed_entities, recent_merged_issues, normalized.terminal_version, normalized.reason_class, normalized.source_repo)
        table.insert(failures, normalized)
      end
    end
  end
  return failures
end

function M.build_output_obligation_issue_create_request(fact)
  if type(fact) ~= "table" then
    error("github-devloop: output-obligation-fact-missing: output obligation fact is required")
  end
  return output_obligation_issue_request(fact)
end

function M.blocked_obligation_patrol_once(entity, observed_entities, recent_merged_issues)
  local raised = {}
  for _, fact in ipairs(M.blocked_output_obligation_failures(entity, observed_entities, recent_merged_issues)) do
    if fact.drain_edge == nil then
      table.insert(raised, {
        queue = "github-proxy.github_issue_create_request",
        payload = M.build_output_obligation_issue_create_request(fact),
        fact = fact,
      })
    end
  end
  return raised
end

function M.failure_triage_decision(payload)
  local fact, reason = normalized_fact(payload)
  if fact == nil then
    return { action = "skip", reason = reason }
  end
  if fact.no_issue_parent == true then
    return { action = "skip", reason = "no-issue-parent", fact = fact }
  end

  local count = recorded_count(fact.source_repo, fact.fingerprint) + 1
  record_count(fact.source_repo, fact.fingerprint, count)
  local window_key = triage_window_key()
  local is_new = first_seen(fact.source_repo, fact.fingerprint)
  local threshold_crossed = count >= threshold and claim_threshold(fact.source_repo, fact.fingerprint, window_key)
  if not is_new and not fact.terminal and not threshold_crossed then
    return {
      action = "suppress",
      reason = "below-threshold",
      fact = fact,
      count = count,
      threshold = threshold,
    }
  end

  return {
    action = "raise",
    fact = fact,
    count = count,
    threshold = threshold,
    reason = is_new and "new-fingerprint" or (fact.terminal and "terminal-fact" or "threshold-crossed"),
    request = M.build_failure_triage_issue_create_request(fact, count),
  }
end

end

return S
