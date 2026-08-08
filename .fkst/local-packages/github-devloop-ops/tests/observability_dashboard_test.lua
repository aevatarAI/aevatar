local h = require("tests.devloop_ops_helpers")
local t = h.t
local core = h.core
local dashboard_commands = require("devloop.commands.dashboard")
require("departments.observability.main")
local unpack_results = table.unpack or unpack

local function mock_env(write_mode)
  for _ = 1, 8 do
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = write_mode or "1",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 8 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
  end
  for _, name in ipairs({ "GH_TOKEN", "GITHUB_TOKEN" }) do
    t.mock_command('if [ -n "${' .. name .. ':-}" ]; then printf present; fi', {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function encode_body(value)
  return tostring(value or ""):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\n", "\\n")
end

local function dashboard_issue_list_stdout(body)
  if body == nil then
    return "[[]]\n"
  end
  return '[[{"number":99,"title":"fkst-dev board","user":{"login":"fkst-test-bot"},"body":"'
    .. encode_body(body)
    .. '"}]]\n'
end

local function dashboard_issue_list_stdout_many(bodies)
  local items = {}
  for index, body in ipairs(bodies or {}) do
    table.insert(items, '{"number":' .. tostring(98 + index) .. ',"title":"fkst-dev board","user":{"login":"fkst-test-bot"},"body":"'
      .. encode_body(body)
      .. '"}')
  end
  return "[[" .. table.concat(items, ",") .. "]]\n"
end

local function command_input_path(command)
  return tostring(command or ""):match("%-%-input '?([^'%s]+)'?")
end

local function dashboard_body_from_input(path)
  local raw = file.read(path)
  local body = raw:match('"body":"(.*)","labels"') or ""
  return body:gsub('\\"', '"'):gsub("\\n", "\n"):gsub("\\\\", "\\")
end

local function with_fake_dashboard_github(fake, callback)
  local old_label_get = dashboard_commands.gh_dashboard_label_get
  local old_issue_list = dashboard_commands.gh_dashboard_issue_list
  local old_issue_create = dashboard_commands.gh_dashboard_issue_create
  dashboard_commands.gh_dashboard_label_get = function(repo, label)
    table.insert(fake.commands, "label_get")
    if repo == "owner/repo" and label == core.dashboard_label() then
      return { stdout = '{"name":"fkst-dashboard"}\n', stderr = "", exit_code = 0 }
    end
    error("unexpected dashboard label get")
  end
  dashboard_commands.gh_dashboard_issue_list = function(repo, label)
    table.insert(fake.commands, "issue_list")
    if repo == "owner/repo" and label == core.dashboard_label() then
      fake.list_calls = fake.list_calls + 1
      return { stdout = dashboard_issue_list_stdout(fake.issue_body), stderr = "", exit_code = 0 }
    end
    error("unexpected dashboard issue list")
  end
  dashboard_commands.gh_dashboard_issue_create = function(repo, input_file)
    table.insert(fake.commands, "issue_create")
    if repo ~= "owner/repo" then
      error("unexpected dashboard issue create")
    end
    fake.create_calls = fake.create_calls + 1
    fake.issue_body = dashboard_body_from_input(input_file)
    return { stdout = '{"number":99}\n', stderr = "", exit_code = 0 }
  end
  local results = { pcall(callback) }
  dashboard_commands.gh_dashboard_label_get = old_label_get
  dashboard_commands.gh_dashboard_issue_list = old_issue_list
  dashboard_commands.gh_dashboard_issue_create = old_issue_create
  local ok = table.remove(results, 1)
  if not ok then
    error(results[1])
  end
  return unpack_results(results)
end

local function with_lock_capture(captured, callback)
  local old_with_lock = with_lock
  with_lock = function(key, fn)
    table.insert(captured, key)
    return fn()
  end
  local results = { pcall(callback) }
  with_lock = old_with_lock
  local ok = table.remove(results, 1)
  if not ok then
    error(results[1])
  end
  return unpack_results(results)
end

local function dashboard_fixture()
  return core.render_observability_dashboard({
    entities = {},
    counts = {},
    stalls = {},
    now_seconds = now(),
  })
end

return {
  test_dashboard_publish_defers_without_gh_calls_when_deadline_exhausted = function()
    mock_env("1")
    local gh_calls = 0
    local lock_calls = 0
    local old_gh_exec = core.gh_exec
    local old_with_lock = with_lock
    core.gh_exec = function()
      gh_calls = gh_calls + 1
      error("unexpected dashboard gh call")
    end
    with_lock = function(_, fn)
      lock_calls = lock_calls + 1
      return fn()
    end

    local ok, result = pcall(function()
      return core.publish_observability_dashboard("owner/repo", dashboard_fixture(), core.observability_limits(), now() - 1)
    end)
    core.gh_exec = old_gh_exec
    with_lock = old_with_lock

    t.eq(ok, true)
    t.eq(result, "deferred")
    t.eq(gh_calls, 0)
    t.eq(lock_calls, 0)
  end,

  test_dashboard_dry_run_logs_deferred_partial_board_when_deadline_exhausted = function()
    mock_env("")
    local captured = {}
    local old_log = log
    log = {
      info = function(message) table.insert(captured, tostring(message)) end,
      warn = function(message) table.insert(captured, tostring(message)) end,
      error = function(message) table.insert(captured, tostring(message)) end,
    }

    local result = core.publish_observability_dashboard("owner/repo", dashboard_fixture(), core.observability_limits(), now() - 1)
    log = old_log

    t.eq(result, "deferred")
    local body = table.concat(captured, "\n")
    t.is_true(body:find("tag=DASHBOARD_DEFERRED reason=deadline", 1, true) ~= nil)
    t.is_true(body:find("tag=DASHBOARD_DRY_RUN", 1, true) ~= nil)
    t.is_true(body:find("# fkst-dev board", 1, true) ~= nil)
  end,

  test_dashboard_publish_rereads_singleton_under_repo_lock = function()
    mock_env("1")
    local fake = { commands = {}, list_calls = 0, create_calls = 0, issue_body = nil }
    local lock_keys = {}

    local first, second = with_lock_capture(lock_keys, function()
      return with_fake_dashboard_github(fake, function()
        local dashboard = dashboard_fixture()
        return core.publish_observability_dashboard("owner/repo", dashboard, core.observability_limits(), now() + 90),
          core.publish_observability_dashboard("owner/repo", dashboard, core.observability_limits(), now() + 90)
      end)
    end)

    t.eq(first, "created")
    t.eq(second, "unchanged")
    t.eq(fake.create_calls, 1)
    t.eq(fake.list_calls, 2)
    t.eq(#lock_keys, 2)
    t.eq(lock_keys[1], "github-devloop/dashboard/owner/repo")
    t.eq(lock_keys[2], "github-devloop/dashboard/owner/repo")
  end,

  test_dashboard_locator_empty_stdout_fails_closed_without_create = function()
    mock_env("1")
    local old_label_get = dashboard_commands.gh_dashboard_label_get
    local old_issue_list = dashboard_commands.gh_dashboard_issue_list
    local old_issue_create = dashboard_commands.gh_dashboard_issue_create
    local create_calls = 0
    dashboard_commands.gh_dashboard_label_get = function(repo, label)
      if repo == "owner/repo" and label == core.dashboard_label() then
        return { stdout = '{"name":"fkst-dashboard"}\n', stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard label get")
    end
    dashboard_commands.gh_dashboard_issue_list = function(repo, label)
      if repo == "owner/repo" and label == core.dashboard_label() then
        return { stdout = "", stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard issue list")
    end
    dashboard_commands.gh_dashboard_issue_create = function()
      create_calls = create_calls + 1
      return { stdout = '{"number":99}\n', stderr = "", exit_code = 0 }
    end

    local ok, err = pcall(function()
      core.publish_observability_dashboard("owner/repo", dashboard_fixture(), core.observability_limits(), now() + 90)
    end)
    dashboard_commands.gh_dashboard_label_get = old_label_get
    dashboard_commands.gh_dashboard_issue_list = old_issue_list
    dashboard_commands.gh_dashboard_issue_create = old_issue_create

    t.eq(ok, false)
    t.is_true(tostring(err):find("dashboard-issue-list-empty: dashboard issue list failed: empty output", 1, true) ~= nil)
    t.eq(create_calls, 0)
  end,

  test_dashboard_publish_adopts_duplicate_marker_issue_without_create = function()
    mock_env("1")
    local old_body = "old\n" .. core.dashboard_marker("old", "2026-06-01T00:00:00Z")
    local newer_body = "newer\n" .. core.dashboard_marker("newer", "2026-06-01T00:01:00Z")
    local old_label_get = dashboard_commands.gh_dashboard_label_get
    local old_issue_list = dashboard_commands.gh_dashboard_issue_list
    local old_issue_get = dashboard_commands.gh_dashboard_issue_get
    local old_issue_update = dashboard_commands.gh_dashboard_issue_update
    local old_issue_create = dashboard_commands.gh_dashboard_issue_create
    local create_calls = 0
    local get_calls = 0
    dashboard_commands.gh_dashboard_label_get = function(repo, label)
      if repo == "owner/repo" and label == core.dashboard_label() then
        return { stdout = '{"name":"fkst-dashboard"}\n', stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard label get")
    end
    dashboard_commands.gh_dashboard_issue_list = function(repo, label)
      if repo == "owner/repo" and label == core.dashboard_label() then
        return { stdout = dashboard_issue_list_stdout_many({ old_body, newer_body }), stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard issue list")
    end
    dashboard_commands.gh_dashboard_issue_get = function(repo, issue_number)
      if repo == "owner/repo" and tonumber(issue_number) == 99 then
        get_calls = get_calls + 1
        return { stdout = 'HTTP/2.0 200 OK\netag: "dashboard-old-etag"\n\n{"number":99,"title":"fkst-dev board","author":{"login":"fkst-test-bot"},"body":"' .. encode_body(old_body) .. '"}\n', stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard issue get")
    end
    dashboard_commands.gh_dashboard_issue_update = function(repo, issue_number)
      if repo == "owner/repo" and tonumber(issue_number) == 99 then
        return { stdout = '{"number":99}\n', stderr = "", exit_code = 0 }
      end
      error("unexpected dashboard issue update")
    end
    dashboard_commands.gh_dashboard_issue_create = function()
      create_calls = create_calls + 1
      return { stdout = '{"number":101}\n', stderr = "", exit_code = 0 }
    end

    local result = core.publish_observability_dashboard("owner/repo", dashboard_fixture(), core.observability_limits(), now() + 90)
    dashboard_commands.gh_dashboard_label_get = old_label_get
    dashboard_commands.gh_dashboard_issue_list = old_issue_list
    dashboard_commands.gh_dashboard_issue_get = old_issue_get
    dashboard_commands.gh_dashboard_issue_update = old_issue_update
    dashboard_commands.gh_dashboard_issue_create = old_issue_create

    t.eq(result, "updated")
    t.eq(get_calls, 1)
    t.eq(create_calls, 0)
  end,
}
