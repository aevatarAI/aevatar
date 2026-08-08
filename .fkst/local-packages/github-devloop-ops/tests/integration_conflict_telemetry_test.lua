local h = require("tests.devloop_ops_helpers")
local t = h.t
local core = h.core
local gh_argv = require("testkit.gh_argv_mock")
local conflict_telemetry = require("devloop.conflict_telemetry")

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

local function run_observability(run_opts)
  return t.run_department("departments/observability/main.lua", {
    queue = "devloop_observe_tick",
    payload = { schema = "github-devloop.observe-tick.v1" },
  }, run_opts or opts("conflict-telemetry"))
end

local function mock_env(extra)
  local env = extra or {}
  for _ = 1, 12 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = env.FKST_GITHUB_BOT_LOGIN == nil and "fkst-test-bot" or env.FKST_GITHUB_BOT_LOGIN,
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command('printf %s "$FKST_GITHUB_REPO"', {
    stdout = "owner/repo",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 8 do
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = env.FKST_GITHUB_WRITE or "",
      stderr = "",
      exit_code = 0,
    })
  end
  for _ = 1, 4 do
    t.mock_command('printf %s "$FKST_DEVLOOP_CONFLICT_LOG_CMD"', {
      stdout = env.FKST_DEVLOOP_CONFLICT_LOG_CMD or "",
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

local function mock_empty_observe_lists()
  t.mock_command(core.gh_issue_list_observe_cmd("owner/repo", core._enabled_label, 1, true), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
  for _, state in ipairs(core.state_order()) do
    t.mock_command(core.gh_issue_list_observe_cmd("owner/repo", core.state_label(state), 1, true), {
      stdout = "[]\n",
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command(core.gh_pr_list_observe_cmd("owner/repo", 1, true), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(core.gh_pr_list_recent_merged_cmd("owner/repo", core.observability_limits().entity_cap), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(core.gh_issue_list_recent_closed_cmd("owner/repo", core.observability_limits().entity_cap), {
    stdout = "[]\n",
    stderr = "",
    exit_code = 0,
  })
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

local function find_raise(raises, queue)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue then
      return raised
    end
  end
  return nil
end

local function conflict_log_line(issue_number, pr_number, file, timestamp)
  local proposal_id = "github-devloop/issue/owner/repo/" .. tostring(issue_number)
  return "github-devloop dept=fix proposal_id=" .. proposal_id
    .. " tag=CONFLICT_FILE"
    .. " ts=" .. tostring(timestamp)
    .. " conflict_file=" .. tostring(file)
    .. " pr=" .. tostring(pr_number)
    .. " proposal_id=" .. proposal_id
end

return {
  test_unmerged_paths_are_deduped_into_safe_conflict_files = function()
    local paths = conflict_telemetry.conflict_file_paths_from_unmerged(table.concat({
      "100644 abc123 1\tpackages/github-devloop/core.lua",
      "100644 def456 2\tpackages/github-devloop/core.lua",
      "100644 bad 1\tunsafe path.lua",
      "100644 ccc 1\tpackages/github-devloop/core/payloads.lua",
    }, "\n"))

    t.eq(#paths, 2)
    t.eq(paths[1], "packages/github-devloop/core.lua")
    t.eq(paths[2], "packages/github-devloop/core/payloads.lua")
  end,

  test_observability_raises_split_issue_for_conflict_hotspot = function()
    mock_env({ FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log" })
    mock_empty_observe_lists()
    t.mock_command("tail -n 200 /var/log/fkst-devloop.log", {
      stdout = table.concat({
        conflict_log_line(10, 101, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 60)),
        conflict_log_line(11, 102, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 3600)),
        conflict_log_line(12, 103, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 6 * 24 * 60 * 60)),
        conflict_log_line(13, 104, "packages/github-devloop/core/validators.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 60)),
      }, "\n") .. "\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_observability(opts("conflict-hotspot", {
      FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log",
    }))

    t.eq(result.exit_code, 0)
    local create = find_raise(result.raises, "github-proxy.github_issue_create_request")
    t.is_true(create ~= nil)
    local payload = create.payload
    t.eq(payload.schema, "github-proxy.issue-create.v1")
    t.eq(payload.repo, "owner/repo")
    t.eq(payload.title, "Split conflict hotspot: packages/github-devloop/core/payloads.lua")
    t.eq(payload.dedup_key, "conflict-hotspot/owner/repo/packages-github-devloop-core-payloads.lua")
    t.eq(payload.parent_comment_target.repo, "owner/repo")
    t.eq(payload.parent_comment_target.issue_number, "10")
    t.eq(payload.source_ref.kind, "external")
    t.eq(payload.source_ref.ref, "owner/repo#conflict-hotspot/packages-github-devloop-core-payloads.lua")
    t.is_true(payload.body:find("Distinct PRs: 3 (101, 102, 103)", 1, true) ~= nil)
    t.is_true(payload.body:find("conflict_file=packages/github-devloop/core/payloads.lua pr=101", 1, true) ~= nil)
  end,

  test_observability_noops_when_conflict_log_source_is_unconfigured = function()
    mock_env()
    mock_empty_observe_lists()

    local result = run_observability()

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
    t.eq(count_calls("tail -n 200 /var/log/fkst-devloop.log"), 0)
  end,

  test_observability_noops_below_conflict_threshold = function()
    mock_env({ FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log" })
    mock_empty_observe_lists()
    t.mock_command("tail -n 200 /var/log/fkst-devloop.log", {
      stdout = table.concat({
        conflict_log_line(10, 101, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 60)),
        conflict_log_line(11, 102, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 3600)),
      }, "\n") .. "\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_observability(opts("conflict-below-threshold", {
      FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log",
    }))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
  end,

  test_observability_ignores_conflicts_outside_sliding_window = function()
    mock_env({ FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log" })
    mock_empty_observe_lists()
    t.mock_command("tail -n 200 /var/log/fkst-devloop.log", {
      stdout = table.concat({
        conflict_log_line(10, 101, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 8 * 24 * 60 * 60)),
        conflict_log_line(11, 102, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 9 * 24 * 60 * 60)),
        conflict_log_line(12, 103, "packages/github-devloop/core/payloads.lua", os.date("!%Y-%m-%dT%H:%M:%SZ", now() - 10 * 24 * 60 * 60)),
      }, "\n") .. "\n",
      stderr = "",
      exit_code = 0,
    })

    local result = run_observability(opts("conflict-outside-window", {
      FKST_DEVLOOP_CONFLICT_LOG_CMD = "tail -n 200 /var/log/fkst-devloop.log",
    }))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
  end,
}
