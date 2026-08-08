local t = fkst.test
local core = require("core")
local gh_argv = require("testkit.gh_argv_mock")
gh_argv.install(t, core)

local function opts(name, extra)
  local env = {
    FKST_RUNTIME_ROOT = "/tmp/fkst-packages-test/github-devloop/" .. tostring(now()) .. "/" .. tostring(name),
    FKST_GITHUB_REPO = "owner/repo",
    FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
    FKST_GITHUB_WRITE = "",
    FKST_DEVLOOP_UPSTREAM_BRANCH = "dev",
    FKST_DEVLOOP_INTEGRATION_BRANCH = "integration/dev",
  }
  for key, value in pairs(extra or {}) do
    env[key] = value
  end
  return { env = env }
end

local function run_ensure(run_opts)
  return t.run_department("departments/ensure_repo/main.lua", {
    queue = "devloop_ensure_repo_tick",
    payload = { schema = "github-devloop.ensure-repo-tick.v1" },
  }, run_opts or opts("ensure-repo"))
end

local function encode_json_string(value)
  return tostring(value or ""):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\n", "\\n")
end

local function label_json(label)
  return string.format(
    '{"name":"%s","color":"%s","description":"%s"}',
    encode_json_string(label.name),
    encode_json_string(label.color or ""),
    encode_json_string(label.description or "")
  )
end

local function mock_env(write_mode, integration)
  t.mock_command('printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"', {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"', {
    stdout = integration == nil and "integration/dev" or integration,
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"', {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_GITHUB_REPO"', {
    stdout = "owner/repo",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 2 do
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = write_mode or "",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 2 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function labels_list_command()
  return "gh api --paginate --slurp 'repos/owner/repo/labels?per_page=100'"
end

local function dashboard_issue_list_command()
  return "gh api --paginate --slurp 'repos/owner/repo/issues?state=open&per_page=100'"
end

local function dashboard_issue_add_label_command(issue_number)
  return "gh api --method POST 'repos/owner/repo/issues/" .. tostring(issue_number) .. "/labels' -f 'labels[]=fkst-dashboard'"
end

local function dashboard_anchor_input_path()
  return "/tmp/fkst-github-devloop-dashboard-anchor-owner-repo.json"
end

local function mock_labels(labels)
  local rendered = {}
  for _, label in ipairs(labels or {}) do
    table.insert(rendered, label_json(label))
  end
  t.mock_command(labels_list_command(), {
    stdout = "[[" .. table.concat(rendered, ",") .. "]]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_dashboard_anchor(present, has_label)
  local stdout = "[[]]\n"
  if present then
    local labels = ""
    if has_label ~= false then
      labels = ',"labels":[{"name":"fkst-dashboard"}]'
    end
    stdout = '[[{"number":268,"title":"fkst-dev board","user":{"login":"fkst-test-bot"},"body":"'
      .. core.dashboard_marker("anchor", "1970-01-01T00:00:00Z"):gsub('"', '\\"')
      .. '"'
      .. labels
      .. "}]]\n"
  end
  t.mock_command(dashboard_issue_list_command(), {
    stdout = stdout,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_forged_dashboard_anchor()
  t.mock_command(dashboard_issue_list_command(), {
    stdout = '[[{"number":269,"title":"fkst-dev board","user":{"login":"someone-else"},"body":"'
      .. core.dashboard_marker("anchor", "1970-01-01T00:00:00Z"):gsub('"', '\\"')
      .. '","labels":[{"name":"fkst-dashboard"}]}]]\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_topology(exit_code)
  t.mock_command("git fetch 'origin' 'integration/dev'", {
    stdout = "",
    stderr = exit_code == 0 and "" or "fatal: couldn't find remote ref integration/dev\n",
    exit_code = exit_code,
  })
  if exit_code == 0 then
    t.mock_command("git rev-parse --verify refs/remotes/'origin'/'integration/dev'^{commit}", {
      stdout = "abcdef1234567890\n",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function count_calls(needle)
  local count = 0
  for _, call in ipairs(t.command_calls()) do
    if gh_argv.call_contains(call, needle) then
      count = count + 1
    end
  end
  return count
end

local function first_call(needle)
  for _, call in ipairs(t.command_calls()) do
    if gh_argv.call_contains(call, needle) then
      return gh_argv.call_rendered(call)
    end
  end
  return nil
end

local function canonical_labels()
  return core.ensure_repo_label_specs()
end

return {
  test_dry_run_empty_repo_renders_management_plane_diff_without_writes = function()
    mock_env("")
    mock_labels({})
    mock_dashboard_anchor(false)
    mock_topology(0)

    local result = run_ensure(opts("ensure-empty-dry-run"))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method POST"), 0)
    t.eq(count_calls("git push"), 0)
  end,

  test_forged_dashboard_anchor_is_not_trusted = function()
    mock_env("")
    mock_labels(canonical_labels())
    mock_forged_dashboard_anchor()
    mock_topology(0)

    local result = run_ensure(opts("ensure-forged-anchor-dry-run"))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method POST 'repos/owner/repo/issues'"), 0)
    t.eq(count_calls(dashboard_issue_list_command()), 1)
  end,

  test_real_mode_reuses_unlabeled_dashboard_anchor_and_adds_label = function()
    local labels = canonical_labels()
    table.insert(labels, {
      name = core.dashboard_label(),
      color = "ededed",
      description = "fkst observability dashboard singleton",
    })
    mock_env("1")
    mock_labels(labels)
    mock_dashboard_anchor(true, false)
    mock_topology(0)
    t.mock_command(dashboard_issue_add_label_command(268), {
      stdout = '{"labels":[{"name":"fkst-dashboard"}]}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = run_ensure(opts("ensure-unlabeled-anchor-real", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method POST 'repos/owner/repo/issues'"), 0)
    t.eq(count_calls(dashboard_issue_add_label_command(268)), 1)
    t.eq(count_calls(dashboard_issue_list_command()), 1)
  end,

  test_real_mode_creates_missing_labels_and_dashboard_anchor = function()
    local labels = canonical_labels()
    mock_env("1")
    mock_labels({ labels[1], labels[2] })
    mock_dashboard_anchor(false)
    mock_topology(0)
    for _, label in ipairs(canonical_labels()) do
      if label.name ~= labels[1].name and label.name ~= labels[2].name then
        t.mock_command(core.gh_repo_label_create_cmd("owner/repo", label.name, label.color, label.description), {
          stdout = '{"name":"' .. encode_json_string(label.name) .. '"}\n',
          stderr = "",
          exit_code = 0,
        })
      end
    end
    t.mock_command(core.gh_repo_label_create_cmd("owner/repo", core.dashboard_label(), "ededed", "fkst observability dashboard singleton"), {
      stdout = '{"name":"fkst-dashboard"}\n',
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh api --method POST 'repos/owner/repo/issues' --input '/tmp/fkst-github-devloop-dashboard-anchor-owner-repo.json'", {
      stdout = '{"number":268}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = run_ensure(opts("ensure-partial-real", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method POST 'repos/owner/repo/labels'"), #canonical_labels() - 2 + 1)
    t.eq(count_calls("gh api --method POST 'repos/owner/repo/issues'"), 1)
    local written = file.read(dashboard_anchor_input_path())
    t.is_true(written:find('"title":"fkst-dev board"', 1, true) ~= nil)
    t.is_true(written:find('"labels":["fkst-dashboard"]', 1, true) ~= nil)
    t.is_true(written:find('<!-- fkst:dashboard:v1 version=\\"1970-01-01T00:00:00Z\\" hash=\\"anchor\\"', 1, true) ~= nil)
  end,

  test_real_mode_updates_canonical_label_drift = function()
    local labels = canonical_labels()
    labels[1] = {
      name = labels[1].name,
      color = "000000",
      description = "operator drift",
    }
    table.insert(labels, {
      name = "operator-owned",
      color = "111111",
      description = "left alone",
    })
    table.insert(labels, {
      name = core.dashboard_label(),
      color = "ededed",
      description = "fkst observability dashboard singleton",
    })
    mock_env("1")
    mock_labels(labels)
    mock_dashboard_anchor(true)
    mock_topology(0)
    local desired = canonical_labels()[1]
    t.mock_command(core.gh_repo_label_update_cmd("owner/repo", desired.name, desired.color, desired.description), {
      stdout = '{"name":"' .. encode_json_string(desired.name) .. '"}\n',
      stderr = "",
      exit_code = 0,
    })

    local result = run_ensure(opts("ensure-drift-real", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method PATCH 'repos/owner/repo/labels/fkst-dev%3Aenabled'"), 1)
    t.eq(count_calls("gh api --method POST"), 0)
    t.is_nil(first_call("operator-owned"))
  end,

  test_fully_converged_repo_performs_zero_writes = function()
    local labels = canonical_labels()
    table.insert(labels, {
      name = core.dashboard_label(),
      color = "ededed",
      description = "fkst observability dashboard singleton",
    })
    mock_env("1")
    mock_labels(labels)
    mock_dashboard_anchor(true)
    mock_topology(0)

    local result = run_ensure(opts("ensure-converged-real", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --method POST"), 0)
    t.eq(count_calls("gh api --method PATCH"), 0)
    t.eq(count_calls(labels_list_command()), 1)
    t.eq(count_calls(dashboard_issue_list_command()), 1)
  end,

  test_missing_integration_branch_holds_without_creating_branch = function()
    mock_env("1")
    mock_labels(canonical_labels())
    mock_dashboard_anchor(true)
    mock_topology(1)

    local result = run_ensure(opts("ensure-missing-branch", { FKST_GITHUB_WRITE = "1" }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("git fetch 'origin' 'integration/dev'"), 1)
    t.eq(count_calls("git push"), 0)
    t.eq(count_calls("gh api --method POST"), 0)
    t.eq(count_calls("gh api --method PATCH"), 0)
  end,
}
