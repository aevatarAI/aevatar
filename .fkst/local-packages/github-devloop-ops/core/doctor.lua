local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local m_facts = require("devloop.markers.facts")
local devloop_commands = require("devloop.commands")
local devloop_state = require("devloop.state")
local S = {}
local issue_observation_facts = require("devloop.restart.issue_observation_facts")
local decompose_lib = require("devloop.decompose")
local entity_list_cache = require("devloop.entity_list_cache")
local devloop_entity_view = require("devloop.github_proxy_entity_view")

function S.install(M)
local verdict_rank = {
  ORPHANED = 10,
  STUCK = 20,
  ["SEEN-WITHOUT-DECISION"] = 30,
  OK = 90,
}

local function sort_by_number(items)
  table.sort(items, function(left, right)
    if tostring(left.kind or "") ~= tostring(right.kind or "") then
      return tostring(left.kind or "") < tostring(right.kind or "")
    end
    return tonumber(left.number or 0) < tonumber(right.number or 0)
  end)
  return items
end

local function sort_diagnoses(items)
  table.sort(items, function(left, right)
    local left_rank = verdict_rank[left.verdict] or 80
    local right_rank = verdict_rank[right.verdict] or 80
    if left_rank ~= right_rank then
      return left_rank < right_rank
    end
    if tostring(left.kind or "") ~= tostring(right.kind or "") then
      return tostring(left.kind or "") < tostring(right.kind or "")
    end
    return tonumber(left.number or 0) < tonumber(right.number or 0)
  end)
  return items
end

local function has_state_label(labels)
  for _, label in ipairs(labels or {}) do
    if devloop_state.is_state_label(label) then
      return true
    end
  end
  return false
end

local function diagnosis(entity, state, row, verdict, reason, suggestion, details)
  return {
    kind = entity.kind,
    repo = entity.repo,
    number = entity.number,
    proposal_id = entity.proposal_id,
    state = state and state.state or nil,
    version = state and state.version or nil,
    row = row,
    verdict = verdict,
    reason = reason,
    suggested = suggestion,
    details = details or {},
  }
end

local function ok_reason(row, state, age)
  if row ~= nil and row.terminal == true then
    return "terminal state from trusted marker"
  end
  if age ~= nil then
    return "state age " .. tostring(age) .. "m is within liveness budget"
  end
  if state ~= nil and state.state ~= nil then
    return "trusted state marker is present; age is unavailable"
  end
  return "no anomaly detected"
end

local function pr_open_orphan(M, entity, _state, facts)
  local link = m_facts.pr_link_fact(entity.comments, entity.proposal_id)
  if link == nil then
    return true, "state pr-open has no trusted pr-link marker", "restore the pr-link fact or re-run observe/open-pr"
  end
  if type(facts) == "table" and type(facts.open_pr_numbers) == "table" then
    if facts.open_pr_numbers[tostring(link.pr_number)] ~= true then
      return true,
        "state pr-open links to PR #" .. tostring(link.pr_number) .. " but no matching open PR is visible",
        "re-run observe for the issue, or repair/replace the missing implementation PR"
    end
  end
  return false, nil, nil
end

local function blocked_orphan(M, entity, state, facts)
  local link = m_facts.pr_link_fact(entity.comments, entity.proposal_id)
  local decomposed = decompose_lib.decomposed_fact(entity.comments, entity.proposal_id, state and state.version, link and link.pr_number)
    or decompose_lib.decomposed_fact(entity.comments, entity.proposal_id)
  if decomposed == nil then
    return false, nil, nil
  end
  local child_issues = type(facts) == "table" and facts.decompose_children or {}
  local complete, completed_count = decompose_lib.decompose_children_complete(
    entity.comments,
    child_issues,
    entity.proposal_id,
    decomposed.version,
    decomposed.pr_number,
    decomposed.count
  )
  if not complete then
    return true,
      "blocked state declares " .. tostring(decomposed.count) .. " decompose child issue(s) but only "
        .. tostring(completed_count) .. " live child fact(s) are visible",
      "re-run decompose recovery or recreate the missing child issue facts"
  end
  return false, nil, nil
end

local orphan_rules = {
  ["pr-open"] = pr_open_orphan,
  blocked = blocked_orphan,
}

function M.saga_doctor_classify_entity(entity, opts)
  local options = opts or {}
  local now_seconds = tonumber(options.now_seconds) or now()
  local state = entity.current_state
  local row = issue_observation_facts.transition_row(state and state.state)

  -- NOTE: a label-vs-marker mismatch check (MIS-LABELED) is intentionally NOT done
  -- here. An issue's fkst-dev label legitimately mirrors its linked PR's downstream
  -- state (reviewing/fixing/merge-ready) while its own marker stays at pr-open, and
  -- PRs carry no fkst-dev label at all, so a naive marker-vs-label diff is almost all
  -- false positives. A correct label check needs the full label-sync model; defer it.

  if state == nil or state.state == nil then
    local enabled_or_candidate = devloop_base.is_opted_in(entity.labels) or has_state_label(entity.labels)
    if tostring(entity.open_state or entity.state or ""):upper() == "OPEN" and enabled_or_candidate then
      return diagnosis(entity, nil, nil, "SEEN-WITHOUT-DECISION", "open enabled/candidate entity has no trusted durable intake verdict or state marker", "run intake/observe; if still absent, inspect intake judge delivery")
    end
    return diagnosis(entity, nil, nil, "OK", "unmanaged entity has no enabled state", "none")
  end

  local orphan_rule = orphan_rules[state.state]
  if orphan_rule ~= nil then
    local is_orphan, reason, suggested = orphan_rule(M, entity, state, options.facts or {})
    if is_orphan then
      return diagnosis(entity, state, row, "ORPHANED", reason, suggested)
    end
  end

  if row == nil then
    return diagnosis(entity, state, row, "STUCK", "trusted marker state is not present in the lifecycle transition table", "update the package transition table or repair the marker")
  end
  if row.terminal == true then
    return diagnosis(entity, state, row, "OK", ok_reason(row, state), "none")
  end

  local due, age = M.liveness_timeout_due(row, state, now_seconds)
  if due then
    return diagnosis(entity, state, row, "STUCK",
      "state age " .. tostring(age or "unknown") .. "m exceeds " .. tostring(row.budget and row.budget.minutes or "unknown") .. "m liveness budget for " .. tostring(row.driving_queue or "unknown"),
      "inspect " .. tostring(row.driving_queue or "the driving queue") .. " delivery and re-run observe/liveness")
  end

  return diagnosis(entity, state, row, "OK", ok_reason(row, state, age), "none", {
    age_minutes = age,
    budget_minutes = row.budget and row.budget.minutes or nil,
  })
end

local function read_repo()
  local repo = devloop_base.read_env("FKST_GITHUB_REPO")
  if repo == nil or not base_ids.issue_ref_round_trips(repo, 1) then
    error("github-devloop: saga-doctor-invalid-repo: FKST_GITHUB_REPO is missing or invalid")
  end
  return repo
end

local function fetch_issue_entity(repo, issue)
  local view = require("devloop.github_proxy_entity_view").fetch_issue_view_state(repo, issue.number, issue.updated_at, {
    consumer = "saga_doctor",
  })
  if view.exit_code ~= 0 then
    error("github-devloop: saga-doctor-issue-view-failed: " .. tostring(view.stderr))
  end
  local current = parsers_issue.parse_issue_view_state(M, view.stdout)
  local proposal_id = base_ids.proposal_id(repo, issue.number)
  return {
    kind = "issue",
    repo = repo,
    number = tonumber(issue.number),
    proposal_id = proposal_id,
    labels = current.labels,
    comments = current.comments,
    open_state = current.state,
    current_state = require("devloop.entity").current_entity_state(current.comments, proposal_id),
  }
end

local function fetch_pr_entity(repo, pr)
  local view = devloop_entity_view.fetch_pr_view_origin(repo, pr.number, pr.updated_at, {
    consumer = "saga_doctor",
  })
  if view.exit_code ~= 0 then
    error("github-devloop: saga-doctor-pr-view-failed: " .. tostring(view.stderr))
  end
  local current = parsers_pr.parse_pr_view_origin(view.stdout)
  local origin = m_facts.pr_origin_fact(current.comments)
  local proposal_id = origin and origin.proposal_id or entity_lib.pr_proposal_id(repo, pr.number)
  return {
    kind = "pr",
    repo = repo,
    number = tonumber(pr.number),
    proposal_id = proposal_id,
    labels = current.labels or {},
    comments = current.comments,
    open_state = current.state,
    current_state = require("devloop.entity").current_entity_state(current.comments, proposal_id),
  }
end

local function list_open_issues(repo, poll_key)
  local result = entity_list_cache.fetch_shared_issue_observe_list(M, repo, {
    timeout = 60,
    poll_key = poll_key,
  })
  if result.exit_code ~= 0 then
    error("github-devloop: saga-doctor-issue-list-failed: " .. tostring(result.stderr))
  end
  return parsers_issue.parse_issue_list_observe(result.stdout)
end

local function list_open_prs(repo, poll_key)
  local result = entity_list_cache.fetch_shared_pr_observe_list(M, repo, {
    timeout = 60,
    poll_key = poll_key,
  })
  if result.exit_code ~= 0 then
    error("github-devloop: saga-doctor-pr-list-failed: " .. tostring(result.stderr))
  end
  return parsers_pr.parse_pr_list_observe(result.stdout)
end

local function open_pr_number_set(prs)
  local set = {}
  for _, pr in ipairs(prs or {}) do
    set[tostring(pr.number)] = true
  end
  return set
end

local function maybe_decompose_children(repo, entity)
  local state = entity.current_state
  if state == nil or state.state ~= "blocked" then
    return nil
  end
  if decompose_lib.decomposed_fact(entity.comments, entity.proposal_id) == nil then
    return nil
  end
  local result = devloop_commands.gh_issue_list_decompose_children(repo, entity.proposal_id, 30)
  if result.exit_code ~= 0 then
    error("github-devloop: saga-doctor-decompose-child-list-failed: " .. tostring(result.stderr))
  end
  return decompose_lib.parse_decompose_child_issue_list(result.stdout)
end

function M.saga_doctor_collect(opts)
  local options = opts or {}
  local repo = options.repo or read_repo()
  devloop_base.assert_trusted_bot_configured()
  local poll_key = options.poll_key

  local issues = options.issues or list_open_issues(repo, poll_key)
  local prs = options.prs or list_open_prs(repo, poll_key)
  local pr_numbers = open_pr_number_set(prs)
  local entities = {}
  for _, issue in ipairs(sort_by_number(issues)) do
    table.insert(entities, fetch_issue_entity(repo, issue))
  end
  for _, pr in ipairs(sort_by_number(prs)) do
    table.insert(entities, fetch_pr_entity(repo, pr))
  end

  local diagnoses = {}
  for _, entity in ipairs(entities) do
    local facts = {
      open_pr_numbers = pr_numbers,
      decompose_children = maybe_decompose_children(repo, entity),
    }
    table.insert(diagnoses, M.saga_doctor_classify_entity(entity, {
      now_seconds = options.now_seconds,
      facts = facts,
    }))
  end
  return {
    repo = repo,
    entities = entities,
    diagnoses = sort_diagnoses(diagnoses),
    cron = M.saga_doctor_cron_summary(options.raiser_dir),
    engine_facts = {
      ["cron exact next fire"] = "unavailable (needs fkst-framework doctor)",
      ["reliable queue depth"] = "unavailable (needs fkst-framework doctor)",
      ["DLQ depth"] = "unavailable (needs fkst-framework doctor)",
    },
  }
end

-- Stage 3 should let transition rows declare executable completion_fact and
-- progress_key fields. Keep the Stage 0 checks in table dispatch above so this
-- module can swap each hard-coded rule for row-provided facts without changing
-- report formatting or collection.
function M.saga_doctor_verdict_counts(diagnoses)
  local counts = {
    OK = 0,
    STUCK = 0,
    ["SEEN-WITHOUT-DECISION"] = 0,
    ORPHANED = 0,
  }
  for _, item in ipairs(diagnoses or {}) do
    counts[item.verdict] = (counts[item.verdict] or 0) + 1
  end
  return counts
end

local function read_raiser(path)
  local ok, spec = pcall(dofile, path)
  if not ok or type(spec) ~= "table" then
    return nil
  end
  return spec
end

function M.saga_doctor_cron_summary(raiser_dir)
  local dir = raiser_dir or "raisers"
  local files = {
    "branch_poll.lua",
    "ensure_repo_poll.lua",
    "liveness_poll.lua",
    "merge_queue_poll.lua",
    "observability_poll.lua",
  }
  local rows = {}
  for _, file_name in ipairs(files) do
    local spec = read_raiser(dir .. "/" .. file_name)
    if spec ~= nil and spec.type == "cron" then
      table.insert(rows, {
        raiser = file_name:gsub("%.lua$", ""),
        interval = tostring(spec.interval or "unknown"),
        produces = tostring(spec.produces or "unknown"),
        next_fire = "approx every " .. tostring(spec.interval or "unknown") .. "; exact next-fire unavailable (needs fkst-framework doctor)",
      })
    end
  end
  return rows
end

local function anomaly_count(counts)
  return (counts.STUCK or 0)
    + (counts["SEEN-WITHOUT-DECISION"] or 0)
    + (counts.ORPHANED or 0)
end

function M.saga_doctor_render(report)
  local diagnoses = report.diagnoses or {}
  local counts = M.saga_doctor_verdict_counts(diagnoses)
  local lines = {}
  table.insert(lines, "github-devloop saga doctor: " .. tostring(#diagnoses)
    .. " entities: " .. tostring(counts.OK or 0) .. " OK, "
    .. tostring(anomaly_count(counts)) .. " warnings")
  for _, item in ipairs(diagnoses) do
    if item.verdict ~= "OK" then
      table.insert(lines, "WARNING #" .. tostring(item.number) .. " "
        .. tostring(item.state or "unmanaged") .. " - " .. tostring(item.verdict)
        .. ": " .. tostring(item.reason or "")
        .. "; suggested: " .. tostring(item.suggested or "inspect entity"))
    end
  end
  table.insert(lines, "")
  table.insert(lines, "Cron schedule:")
  for _, row in ipairs(report.cron or {}) do
    table.insert(lines, "- " .. tostring(row.raiser) .. " -> " .. tostring(row.produces)
      .. " interval=" .. tostring(row.interval)
      .. "; next-fire=" .. tostring(row.next_fire))
  end
  table.insert(lines, "Engine-only facts:")
  for name, value in pairs(report.engine_facts or {}) do
    table.insert(lines, "- " .. tostring(name) .. ": " .. tostring(value))
  end
  return table.concat(lines, "\n")
end

function M.saga_doctor_run(opts)
  return M.saga_doctor_render(M.saga_doctor_collect(opts))
end

end

return S
