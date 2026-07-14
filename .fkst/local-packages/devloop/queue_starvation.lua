local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local parsers_misc = require("devloop.parsers.misc")
local parsers_issue = require("devloop.parsers.issue")
local C = {}
local forge_validators = require("devloop.forge_validators")
local contract_time = require("contract.time")
local transition_version = require("contract.transition_version")
local strings = require("contract.strings")
local config = require("devloop.config")
local m_mq = require("devloop.merge_queue")

local detector = "queue-starvation"
local merge_recent_threshold_minutes = 360
local recent_closed_limit = 30
local github_handle = nil

local function github()
  if github_handle ~= nil then
    return github_handle
  end
  if type(exec_argv) ~= "function" then
    error("github-devloop: GitHub adapter requires exec_argv")
  end
  github_handle = require("forge.github").new(exec_argv)
  return github_handle
end

local function format_timestamp(seconds)
  return os.date("!%Y-%m-%dT%H:%M:%SZ", tonumber(seconds) or now())
end

local function snapshot_path(M, repo, window_key)
  local safe_repo = base_ids.safe_repo(repo):gsub("/", "-"):gsub("%-+", "-")
  local safe_window = strings.sanitize_key(tostring(window_key or "unknown"), false):gsub("[/%s]+", "-")
  safe_window = safe_window:gsub("[^%w%._%-]", "-"):gsub("%-+", "-"):gsub("^%-+", ""):gsub("%-+$", "")
  if safe_window == "" then
    safe_window = "unknown"
  end
  if #safe_window > 120 then
    safe_window = safe_window:sub(1, 120):gsub("%-+$", "")
  end
  return "/tmp/fkst-github-devloop-queue-starvation-" .. safe_repo .. "-" .. safe_window .. ".json"
end

local function json_array(values)
  local parts = {}
  for _, value in ipairs(values or {}) do
    table.insert(parts, strings.json_string(value))
  end
  return "[" .. table.concat(parts, ",") .. "]"
end

local function issue_json(issue)
  return "{"
    .. '"number":' .. tostring(tonumber(issue.number) or 0)
    .. ',"title":' .. strings.json_string(issue.title)
    .. ',"closedAt":' .. strings.json_string(issue.closed_at)
    .. ',"labels":' .. json_array(issue.labels)
    .. "}"
end

local function entity_json(entity, age_minutes)
  return "{"
    .. '"proposal_id":' .. strings.json_string(entity and entity.proposal_id or "")
    .. ',"issue_number":' .. strings.json_string(entity and entity.issue_number or "")
    .. ',"pr_number":' .. strings.json_string(entity and entity.pr_number or "")
    .. ',"title":' .. strings.json_string(entity and entity.title or "")
    .. ',"state":' .. strings.json_string(entity and entity.state and entity.state.state or "")
    .. ',"version":' .. strings.json_string(entity and entity.state and entity.state.version or "")
    .. ',"head_sha":' .. strings.json_string(entity and entity.head_sha or "")
    .. ',"source":' .. strings.json_string(entity and entity.source or "")
    .. ',"age_minutes":' .. tostring(tonumber(age_minutes) or 0)
    .. "}"
end

local function write_snapshot(M, repo, window_key, evidence)
  local path = snapshot_path(M, repo, window_key)
  local closed = {}
  for _, issue in ipairs(evidence.recent_closed or {}) do
    table.insert(closed, issue_json(issue))
  end
  file.write(path, "{"
    .. '"detector":' .. strings.json_string(detector)
    .. ',"repo":' .. strings.json_string(repo)
    .. ',"window":' .. strings.json_string(window_key)
    .. ',"generated_at":' .. strings.json_string(format_timestamp(evidence.now_seconds))
    .. ',"queue_head":' .. entity_json(evidence.queue_head, evidence.queue_head_age_minutes)
    .. ',"threshold_minutes":' .. tostring(evidence.threshold_minutes)
    .. ',"last_merge_age_minutes":' .. strings.json_string(evidence.last_merge_age_minutes or "none")
    .. ',"recent_closed":[' .. table.concat(closed, ",") .. "]"
    .. "}\n")
  return path
end

local function marker_attr(marker, name)
  return marker:match(name .. '="([^"]*)"')
end

local function merged_fact_from_issue(M, issue)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, issue and issue.comments or {})) do
    for marker in parsers_misc._comment_body(M, comment):gmatch("<!%-%- fkst:github%-devloop:merged:v1.-%-%->") do
      local proposal_id = marker_attr(marker, "proposal")
      local pr_number = marker_attr(marker, "pr")
      local version = marker_attr(marker, "version")
      local head_sha = marker_attr(marker, "head_sha")
      if strings.is_bounded_string(proposal_id, M._max_key_len)
        and forge_validators.is_positive_pr_number(pr_number)
        and strings.is_bounded_string(version, M._max_dedup_len)
        and forge_validators.is_git_sha(head_sha) then
        return {
          proposal_id = proposal_id,
          pr_number = tonumber(pr_number),
          version = version,
          head_sha = head_sha,
          closed_at = issue.closed_at,
          issue_number = issue.number,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

local function has_label(issue, label)
  for _, item in ipairs(issue and issue.labels or {}) do
    if item == label then
      return true
    end
  end
  return false
end

local function run_observability_adapter(M, read_fn, limits, deadline, error_class)
  local label = error_class or "GitHub observability command"
  local timeout = M.observability_call_timeout(limits, deadline)
  if timeout <= 0 then
    local deferred = M.observability_deadline_deferred_result(label)
    deferred.stderr = "observability deadline exhausted"
    return deferred
  end
  local ok, result = pcall(read_fn, timeout)
  if not ok then
    error("github-devloop: " .. tostring(label) .. " failed: " .. tostring(result))
  end
  return result
end

function C.queue_starvation_recent_closed_merged_issues(M, repo, limits, deadline)
  local listed = run_observability_adapter(
    M,
    function(timeout)
      return github().issue_list_recent_closed(repo, recent_closed_limit, timeout)
    end,
    limits,
    deadline,
    "GitHub recent closed merged issue list"
  )
  if M.observability_result_deferred(listed) then
    return nil, nil, "deadline"
  end
  local issues = parsers_issue.parse_issue_list_recent_closed(M, listed.stdout)
  local merged = {}
  for _, issue in ipairs(issues) do
    local fact = nil
    if has_label(issue, M._merged_label) then
      local view = run_observability_adapter(
        M,
        function(timeout)
          return github().issue_view(repo, issue.number, "title,comments,state,stateReason,assignees,author", timeout)
        end,
        limits,
        deadline,
        "GitHub recent closed merged issue view"
      )
      if M.observability_result_deferred(view) then
        return nil, nil, "deadline"
      end
      local current = parsers_issue.parse_issue_view_observe(M, view.stdout)
      current.closed_at = issue.closed_at
      current.number = issue.number
      fact = merged_fact_from_issue(M, current)
    end
    if fact ~= nil then
      table.insert(merged, {
        number = issue.number,
        title = issue.title,
        closed_at = issue.closed_at,
        labels = issue.labels,
        merged = fact,
      })
    end
  end
  return issues, merged
end

local function age_minutes(timestamp, now_seconds)
  local seconds = contract_time.iso_timestamp_epoch_seconds(timestamp)
  if seconds == nil then
    return nil
  end
  local age = (tonumber(now_seconds) or now()) - seconds
  if age < 0 then
    return nil
  end
  return math.floor(age / 60)
end

local function newest_recent_merge(merged, now_seconds)
  local newest = nil
  for _, issue in ipairs(merged or {}) do
    local age = age_minutes(issue.closed_at, now_seconds)
    if age ~= nil and (newest == nil or age < newest.age_minutes) then
      newest = {
        issue = issue,
        age_minutes = age,
      }
    end
  end
  return newest
end

local function merge_ready_queue_head(M, entities, now_seconds)
  local selected = nil
  for _, entity in ipairs(entities or {}) do
    local state = entity.state and entity.state.state or nil
    local age = M.stall_suspect_age_minutes(entity.state and entity.state.version or nil, now_seconds)
    if state == "merge-ready"
      and tonumber(age) ~= nil
      and tonumber(age) > m_mq._merge_ready_starvation_threshold_minutes
      and (selected == nil
        or tonumber(age) > tonumber(selected.age_minutes)
        or (tonumber(age) == tonumber(selected.age_minutes)
          and tostring(entity.proposal_id or "") < tostring(selected.entity and selected.entity.proposal_id or ""))) then
      selected = {
        entity = entity,
        state = state,
        age_minutes = age,
        threshold_minutes = m_mq._merge_ready_starvation_threshold_minutes,
      }
    end
  end
  return selected
end

local function merge_queue_head_entity(M, repo, now_seconds)
  local branches = config.branch_config(M)
  local _, entries = m_mq.merge_queue_head(M, repo, branches.integration)
  local head, age = m_mq.merge_queue_starvation_candidate(M, entries, m_mq._merge_ready_starvation_threshold_minutes, now_seconds)
  if head == nil then
    return nil
  end
  local repo_from_proposal, issue_number = base_ids.parse_proposal_id(head.proposal_id)
  if repo_from_proposal == nil then
    return nil
  end
  return {
    entity = {
      proposal_id = head.proposal_id,
      issue_number = tonumber(issue_number) or issue_number,
      pr_number = head.pr_number,
      title = "PR #" .. tostring(head.pr_number),
      state = {
        state = "merge-ready",
        version = head.version,
      },
      head_sha = head.head_sha,
      source = "merge-queue",
    },
    state = "merge-ready",
    age_minutes = age,
    threshold_minutes = m_mq._merge_ready_starvation_threshold_minutes,
  }
end

local function observed_queue_head(M, entities, now_seconds)
  local selected = merge_ready_queue_head(M, entities, now_seconds)
  if selected ~= nil and selected.entity ~= nil then
    selected.entity.source = selected.entity.source or "observability-sample"
  end
  return selected
end

local function queue_head_for_starvation(M, repo, entities, now_seconds)
  local ok, head = pcall(function()
    return merge_queue_head_entity(M, repo, now_seconds)
  end)
  if ok and head ~= nil then
    return head
  end
  if not ok then
    log.warn("github-devloop dept=observability tag=QUEUE_STARVATION action=fallback reason=merge-queue-source-failed")
  end
  return observed_queue_head(M, entities, now_seconds)
end

function C.queue_starvation_window_key(now_seconds)
  local bucket_seconds = merge_recent_threshold_minutes * 60
  local bucket = math.floor((tonumber(now_seconds) or now()) / bucket_seconds)
  return "window-" .. tostring(bucket)
end

local function stable_incident_identity(M, queue_head)
  local entity = queue_head and queue_head.entity or queue_head
  if type(entity) ~= "table" then
    return "merge-ready"
  end
  local parts = { "merge-ready" }
  if entity.pr_number ~= nil then
    table.insert(parts, "pr")
    table.insert(parts, tostring(entity.pr_number))
  end
  if entity.proposal_id ~= nil then
    table.insert(parts, "proposal")
    table.insert(parts, tostring(entity.proposal_id))
  end
  local version = entity.state and entity.state.version or entity.version
  if version ~= nil then
    table.insert(parts, "version")
    table.insert(parts, transition_version.safe_version_segment(version))
  end
  if entity.head_sha ~= nil and forge_validators.is_git_sha(entity.head_sha) then
    table.insert(parts, "head")
    table.insert(parts, devloop_base.safe_head_segment(entity.head_sha))
  end
  return table.concat(parts, "/")
end

local function queue_head_entity(queue_head)
  if type(queue_head) ~= "table" then
    return nil
  end
  if type(queue_head.entity) == "table" then
    return queue_head.entity
  end
  return queue_head
end

function C.queue_starvation_redrive_payload(M, repo, evidence)
  local head = queue_head_entity(evidence and evidence.queue_head or nil)
  if type(head) ~= "table" or head.pr_number == nil then
    return nil
  end
  return m_mq.merge_queue_starvation_tick_payload(M, repo, evidence.incident_identity, {
    pr_number = head.pr_number,
    proposal_id = head.proposal_id,
    version = head.state and head.state.version or nil,
    head_sha = head.head_sha,
  }, evidence.window_key)
end

local function raise_redrive(M, redrive)
  if redrive == nil then
    return
  end
  if type(M.pr_package_queue) ~= "function" then
    log.info("github-devloop dept=observability tag=QUEUE_STARVATION action=no-op reason=pr-redrive-queue-unavailable")
    return
  end
  local ok, queue = pcall(M.pr_package_queue, "devloop_merge_queue_tick")
  if not ok or queue == nil or tostring(queue) == "" then
    log.info("github-devloop dept=observability tag=QUEUE_STARVATION action=no-op reason=pr-redrive-queue-unavailable")
    return
  end
  M.log_raise("observability", detector .. "/merge-ready", queue, redrive)
  raise(queue, redrive)
end

function C.queue_starvation_dedup_key(M, repo, identity)
  return base_ids.dedup_key({
    detector,
    tostring(repo or ""),
    tostring(identity or "queue"),
  })
end

local function alert_title(queue_head)
  local issue = queue_head and queue_head.issue_number or "unknown"
  return "Queue starvation: merge-ready head #" .. tostring(issue) .. " has no recent merge"
end

local function alert_body(M, evidence, snapshot)
  local head = evidence.queue_head or {}
  local lines = {
    "Queue starvation watchdog fired from deterministic observability signals.",
    "",
    "Detector: `" .. detector .. "`",
    "Queue head: #" .. tostring(head.issue_number or "unknown") .. " " .. devloop_base.neutralize_untrusted_comment_text(head.title or ""),
    "Queue head PR: #" .. tostring(head.pr_number or "unknown"),
    "Queue head age: " .. tostring(evidence.queue_head_age_minutes) .. " minutes",
    "Threshold: " .. tostring(evidence.threshold_minutes) .. " minutes",
    "Last merge age: " .. tostring(evidence.last_merge_age_minutes or "none"),
    "Head source: `" .. tostring(head.source or "unknown") .. "`",
    "Evidence snapshot: `" .. tostring(snapshot) .. "`",
    "",
    "Requested outcome:",
    "- Diagnose why merge-ready work is not making forward progress.",
    "- File any fix through the normal intake, consensus, implementation, and review pipeline.",
    "- This watchdog must not repair, merge, relabel, or mutate runtime state directly.",
  }
  local body = table.concat(lines, "\n")
  if #body > M._max_body_len then
    body = base_ids.truncate_utf8(body, M._max_body_len)
  end
  return body
end

function C.build_queue_starvation_issue_create_request(M, repo, evidence, snapshot)
  local identity = evidence.incident_identity or stable_incident_identity(M, evidence.queue_head)
  local window_key = evidence.window_key
  return {
    schema = "github-proxy.issue-create.v1",
    repo = repo,
    title = alert_title(evidence.queue_head),
    body = alert_body(M, evidence, snapshot),
    labels = json.decode("[]"),
    dedup_key = C.queue_starvation_dedup_key(M, repo, identity),
    parent_comment_target = {
      repo = repo,
      issue_number = tostring(evidence.queue_head.issue_number),
    },
    source_ref = {
      kind = "external",
      ref = tostring(repo or "") .. "#" .. detector .. "/" .. identity .. "/" .. tostring(window_key),
    },
  }
end

function C.observe_queue_starvation(M, repo, entities, limits, deadline, now_seconds)
  local queue_head = queue_head_for_starvation(M, repo, entities, now_seconds)
  if queue_head == nil then
    log.info("github-devloop dept=observability tag=QUEUE_STARVATION action=no-op reason=no-stale-merge-ready")
    return { action = "no-op", reason = "no-stale-merge-ready" }
  end

  local ok, recent_closed, merged, source_status = pcall(function()
    return C.queue_starvation_recent_closed_merged_issues(M, repo, limits, deadline)
  end)
  if not ok or recent_closed == nil then
    local reason = source_status == "deadline" and "recent-merge-source-deferred" or "recent-merge-source-failed"
    log.warn("github-devloop dept=observability tag=QUEUE_STARVATION action=no-op reason=" .. reason)
    return { action = "no-op", reason = reason }
  end

  local current_seconds = tonumber(now_seconds) or now()
  local newest = newest_recent_merge(merged, current_seconds)
  local evidence = {
    now_seconds = current_seconds,
    window_key = C.queue_starvation_window_key(current_seconds),
    queue_head = queue_head.entity,
    queue_head_age_minutes = queue_head.age_minutes,
    threshold_minutes = m_mq._merge_ready_starvation_threshold_minutes,
    last_merge_age_minutes = newest and newest.age_minutes or nil,
    recent_closed = recent_closed,
  }
  evidence.incident_identity = stable_incident_identity(M, queue_head)
  local redrive = C.queue_starvation_redrive_payload(M, repo, evidence)
  if newest ~= nil and newest.age_minutes <= merge_recent_threshold_minutes then
    raise_redrive(M, redrive)
    log.info("github-devloop dept=observability tag=QUEUE_STARVATION action=suppress"
      .. " reason=recent-merge"
      .. " last_merge_age_minutes=" .. tostring(newest.age_minutes)
      .. " threshold_minutes=" .. tostring(merge_recent_threshold_minutes)
      .. " redrive=" .. tostring(redrive ~= nil))
    return {
      action = "suppress",
      reason = "recent-merge",
      last_merge_age_minutes = newest.age_minutes,
      redrive = redrive,
    }
  end
  local snapshot = write_snapshot(M, repo, evidence.window_key, evidence)
  local request = C.build_queue_starvation_issue_create_request(M, repo, evidence, snapshot)
  M.log_raise("observability", detector .. "/merge-ready", "github-proxy.github_issue_create_request", request)
  raise_redrive(M, redrive)
  log.info("github-devloop dept=observability tag=QUEUE_STARVATION"
    .. " action=raise"
    .. " queue_head=" .. tostring(queue_head.entity and queue_head.entity.proposal_id or "")
    .. " age_minutes=" .. tostring(queue_head.age_minutes)
    .. " threshold_minutes=" .. tostring(m_mq._merge_ready_starvation_threshold_minutes)
    .. " last_merge_age_minutes=" .. tostring(evidence.last_merge_age_minutes or "none")
    .. " snapshot_path=" .. tostring(snapshot)
    .. " dedup_key=" .. tostring(request.dedup_key))
  return {
    action = "raise",
    request = request,
    redrive = redrive,
    snapshot_path = snapshot,
  }
end
return C
