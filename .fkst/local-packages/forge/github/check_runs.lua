local gitref = require("forge.gitref")
local strings = require("contract.strings")

local C = {}

function C.is_open_pr(pr)
  return tostring(pr.state or ""):upper() == "OPEN"
end

local function check_run_entries(value)
  if type(value) ~= "table" then
    return {}
  end
  if type(value.check_runs) == "table" then
    return value.check_runs
  end
  return value
end

function C.parse_commit_check_runs(stdout)
  local decoded = json.decode(stdout or "{}")
  local runs = {}
  for _, run in ipairs(check_run_entries(decoded)) do
    if type(run) == "table" then
      table.insert(runs, {
        id = run.id,
        databaseId = run.databaseId,
        database_id = run.database_id,
        name = run.name,
        status = run.status,
        conclusion = run.conclusion,
        head_sha = run.head_sha,
        headSha = run.headSha,
        check_suite = run.check_suite,
        checkSuite = run.checkSuite,
        app = run.app,
        workflowName = run.workflowName,
        workflow_name = run.workflow_name,
        workflow = run.workflow,
      })
    end
  end
  return runs
end

local function upper_text(value)
  return tostring(value or ""):upper()
end

local function check_entry_state(entry)
  if type(entry) ~= "table" then
    return nil, nil
  end
  return upper_text(entry.state or entry.status), upper_text(entry.conclusion)
end

local green_check_conclusions = {
  SUCCESS = true,
  -- NEUTRAL is excluded for irreversible-merge safety.
  SKIPPED = true,
}

local green_status_states = {
  SUCCESS = true,
}

local green_check_run_conclusions = {
  SUCCESS = true,
  NEUTRAL = true,
  SKIPPED = true,
}

local red_status_states = {
  ERROR = true,
  FAILURE = true,
}

local required_check_run_names = {
  "test",
}

local required_check_run_name_set = {}
for _, name in ipairs(required_check_run_names) do
  required_check_run_name_set[name] = true
end

local function check_name(entry)
  if type(entry) ~= "table" then
    return ""
  end
  return tostring(entry.name or entry.context or entry.workflowName or entry.workflow_name or "")
end

function C.pr_rollup_green(pr)
  local entries = type(pr) == "table" and pr.status_check_rollup or nil
  if type(entries) ~= "table" or #entries == 0 then
    return false, "missing-status-rollup"
  end
  for _, entry in ipairs(entries) do
    local state, conclusion = check_entry_state(entry)
    if state == "COMPLETED" then
      if not green_check_conclusions[conclusion] then
        return false, "rollup-red"
      end
    elseif conclusion == "" and green_status_states[state] then
      -- Legacy StatusContext entries report state=SUCCESS without a conclusion.
    elseif conclusion == "" and red_status_states[state] then
      return false, "rollup-red"
    else
      return false, "rollup-pending"
    end
  end
  return true, "rollup-green"
end

function C.commit_check_runs_green(runs)
  if type(runs) ~= "table" or #runs == 0 then
    return false, "missing-status-rollup"
  end
  local seen_required = {}
  local pending_required = {}
  for _, run in ipairs(runs) do
    local name = check_name(run)
    local state, conclusion = check_entry_state(run)
    if state == "COMPLETED" then
      if not green_check_run_conclusions[conclusion] then
        return false, "rollup-red"
      end
    elseif required_check_run_name_set[name] then
      pending_required[name] = true
    end
    if required_check_run_name_set[name] then
      seen_required[name] = true
    end
  end
  for _, name in ipairs(required_check_run_names) do
    if pending_required[name] then
      return false, "rollup-pending"
    end
    if not seen_required[name] then
      return false, "missing-status-rollup"
    end
  end
  return true, "rollup-green"
end

function C.pr_mergeable(pr)
  if type(pr) ~= "table" then
    return false, "missing-pr"
  end
  local mergeable = upper_text(pr.mergeable)
  local merge_state = upper_text(pr.merge_state_status)
  if mergeable == "UNKNOWN" then
    return false, "mergeable-unknown"
  end
  if mergeable ~= "MERGEABLE" then
    if mergeable == "" then
      return false, "missing-mergeability"
    end
    return false, "mergeable-" .. mergeable:lower()
  end
  if merge_state ~= "CLEAN" then
    if merge_state == "" then
      return false, "missing-mergeability"
    end
    if merge_state == "UNSTABLE" then
      local rollup_green, rollup_reason = C.pr_rollup_green(pr)
      if not rollup_green and (rollup_reason == "rollup-red" or rollup_reason == "rollup-pending") then
        return true, "mergeable"
      end
    end
    return false, "merge-state-" .. merge_state:lower()
  end
  return true, "mergeable"
end

function C.is_not_mergeable_reason(reason)
  local text = tostring(reason or "")
  return text == "mergeable-conflicting"
    or text == "mergeable-false"
    or text == "merge-state-dirty"
    or text == "merge-state-conflicting"
end

function C.check_run_id(run)
  local id = type(run) == "table" and (run.id or run.databaseId or run.database_id) or nil
  local text = tostring(id or "")
  if text ~= "" and text:find("[^0-9]") == nil then
    return text
  end
  return nil
end

function C.check_run_head_sha(run)
  if type(run) ~= "table" then
    return nil
  end
  for _, value in ipairs({
    run.head_sha,
    run.headSha,
    run.headSHA,
  }) do
    if gitref.is_git_sha(value) then
      return tostring(value):lower()
    end
  end
  if type(run.check_suite) == "table" then
    for _, value in ipairs({
      run.check_suite.head_sha,
      run.check_suite.headSha,
    }) do
      if gitref.is_git_sha(value) then
        return tostring(value):lower()
      end
    end
  end
  if type(run.checkSuite) == "table" then
    for _, value in ipairs({
      run.checkSuite.head_sha,
      run.checkSuite.headSha,
    }) do
      if gitref.is_git_sha(value) then
        return tostring(value):lower()
      end
    end
  end
  return nil
end

function C.check_run_name(run)
  if type(run) ~= "table" then
    return ""
  end
  return tostring(run.name or run.context or run.workflowName or run.workflow_name or "")
end

function C.check_run_state(run)
  if type(run) ~= "table" then
    return "", ""
  end
  return tostring(run.state or run.status or ""):upper(), tostring(run.conclusion or ""):upper()
end

local green_required_check_conclusions = {
  SUCCESS = true,
  NEUTRAL = true,
  SKIPPED = true,
}

local function required_name_set(required_names)
  local required = {}
  for _, name in ipairs(required_names or {}) do
    required[tostring(name)] = true
  end
  return required
end

local function run_matches_head(run, expected)
  local run_head = C.check_run_head_sha(run)
  return run_head == nil or run_head == expected
end

local function stable_run_workflow(run)
  if type(run) ~= "table" then
    return ""
  end
  for _, value in ipairs({
    run.workflowName,
    run.workflow_name,
    type(run.workflow) == "table" and run.workflow.name or nil,
    type(run.check_suite) == "table" and type(run.check_suite.workflow_run) == "table" and run.check_suite.workflow_run.name or nil,
    type(run.checkSuite) == "table" and type(run.checkSuite.workflowRun) == "table" and run.checkSuite.workflowRun.name or nil,
  }) do
    local text = tostring(value or "")
    if text ~= "" then
      return text
    end
  end
  return ""
end

local function stable_run_app_slug(run)
  if type(run) ~= "table" then
    return ""
  end
  for _, value in ipairs({
    type(run.app) == "table" and run.app.slug or nil,
    type(run.app) == "table" and run.app.name or nil,
  }) do
    local text = tostring(value or "")
    if text ~= "" then
      return text
    end
  end
  return ""
end

local function failing_head_runs(runs, head_sha, required_names)
  if type(runs) ~= "table" or not gitref.is_git_sha(head_sha) then
    return nil
  end
  local required = required_names ~= nil and required_name_set(required_names) or nil
  local expected = tostring(head_sha):lower()
  local failing = {}
  for _, run in ipairs(runs) do
    local name = C.check_run_name(run)
    if (required == nil or required[name]) and run_matches_head(run, expected) then
      local state, conclusion = C.check_run_state(run)
      if state == "COMPLETED" and not green_required_check_conclusions[conclusion] then
        table.insert(failing, {
          id = C.check_run_id(run),
          name = name,
          workflow = stable_run_workflow(run),
          app_slug = stable_run_app_slug(run),
          conclusion = conclusion,
        })
      end
    end
  end
  return failing
end

function C.required_head_check_run_status(runs, head_sha, required_names)
  if type(runs) ~= "table" or not gitref.is_git_sha(head_sha) then
    return "unknown"
  end
  required_names = required_names or {}
  local required = {}
  local pending_required = {}
  for _, name in ipairs(required_names) do
    required[tostring(name)] = false
  end
  local expected = tostring(head_sha):lower()
  for _, run in ipairs(runs) do
    if run_matches_head(run, expected) then
      local name = C.check_run_name(run)
      local state, conclusion = C.check_run_state(run)
      if required[name] ~= nil and state == "COMPLETED" then
        if not green_required_check_conclusions[conclusion] then
          return "red"
        end
      elseif required[name] ~= nil then
        pending_required[name] = true
      end

      if required[name] ~= nil then
        required[name] = true
      end
    end
  end
  for _, name in ipairs(required_names) do
    if pending_required[tostring(name)] then
      return "pending"
    end
    if required[tostring(name)] ~= true then
      return "unknown"
    end
  end
  return "green"
end

function C.required_head_ci_failure_key(runs, head_sha, required_names)
  local failing = failing_head_runs(runs, head_sha, required_names)
  if failing == nil or #failing == 0 then
    return nil
  end
  local parts = {}
  for _, run in ipairs(failing) do
    table.insert(parts, table.concat({
      "name=" .. tostring(run.name or ""),
      "workflow=" .. tostring(run.workflow or ""),
      "app=" .. tostring(run.app_slug or ""),
      "conclusion=" .. tostring(run.conclusion or ""),
    }, "\t"))
  end
  table.sort(parts)
  local head = tostring(head_sha):lower()
  return "head:" .. head .. "/checks:digest-" .. strings.decimal_checksum(head .. "\n" .. table.concat(parts, "\n"))
end

C.required_check_run_names = required_check_run_names

return C
