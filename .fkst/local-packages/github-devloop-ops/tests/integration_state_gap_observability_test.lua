local h = require("tests.devloop_ops_helpers")
local t = h.t
local core = h.core
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
local m_builders = require("devloop.markers.builders")

local function opts(name)
  return {
    env = {
      FKST_RUNTIME_ROOT = "/tmp/fkst-packages-test/github-devloop/" .. tostring(now()) .. "/" .. tostring(name),
      FKST_GITHUB_REPO = "owner/repo",
      FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      FKST_GITHUB_WRITE = "",
      FKST_DEVLOOP_UPSTREAM_BRANCH = "dev",
      FKST_DEVLOOP_INTEGRATION_BRANCH = "integration/dev",
    },
  }
end

local function run_observability()
  return t.run_department("departments/observability/main.lua", {
    queue = "devloop_observe_tick",
    payload = { schema = "github-devloop.observe-tick.v1" },
  }, opts("state-gap-observability"))
end

local function mock_env()
  for _ = 1, 8 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = "fkst-test-bot",
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
      stdout = "",
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

local function encode_json_string(value)
  return tostring(value or ""):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\n", "\\n")
end

local function render_comment(body, author, created_at)
  return string.format(
    '{"body":"%s","author":{"login":"%s"},"createdAt":"%s"}',
    encode_json_string(body),
    encode_json_string(author or "fkst-test-bot"),
    encode_json_string(created_at or "2026-06-03T01:02:03Z")
  )
end

local function wait_marker(proposal_id, version, unmet)
  local items = {}
  for _, number in ipairs(unmet or {}) do
    table.insert(items, tostring(number))
  end
  return '<!-- fkst:github-devloop:dependency-wait:v1 proposal="' .. tostring(proposal_id)
    .. '" version="' .. tostring(version)
    .. '" hold_kind="waiting" reason="waiting-on-dependency" unmet="' .. table.concat(items, ",")
    .. '" -->'
end

local function mock_all_issue_lists(numbers)
  local rendered = {}
  for _, number in ipairs(numbers or {}) do
    table.insert(rendered, string.format('{"number":%d,"state":"open"}', number))
  end
  t.mock_command(core.gh_issue_list_observe_cmd("owner/repo", core._enabled_label, 1, true), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
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
end

local function mock_pr_list(numbers)
  local rendered = {}
  for _, number in ipairs(numbers or {}) do
    table.insert(rendered, string.format('{"number":%d,"state":"open"}', number))
  end
  t.mock_command(core.gh_pr_list_observe_cmd("owner/repo", 1, true), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
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

local function mock_issue_view(comments, number)
  entity_read_mocks.mock_issue_view_selector(t, {
    number = number,
    title = "Observed issue",
    state = "OPEN",
    comments = comments,
  }, "title,body,comments,state,stateReason,assignees,author")
end

local function mock_pr_view(comments)
  entity_read_mocks.mock_pr_view_selector(t, {
    head = "devloop-owner-repo-42",
    head_sha = "def456",
    base_branch = "integration/dev",
    state = "OPEN",
    updated_at = "2026-06-03T02:03:04Z",
    comments = comments,
  }, entity_read_mocks.pr_origin_selector)
end

local observability_pipeline = nil

local function run_observability_pipeline(event)
  local old_pipeline = pipeline
  local module = require("departments.observability.main")
  observability_pipeline = module.pipeline or pipeline or observability_pipeline
  pipeline = old_pipeline
  local run = observability_pipeline
  if type(run) ~= "function" then
    error("github-devloop: observability department pipeline missing")
  end
  event = event or { queue = "devloop_observe_tick", payload = { schema = "github-devloop.observe-tick.v1", cursor = "alpha" } }
  run(event)
end

local function gap_logs(event)
  local logs = {}
  local old_log = log
  log = {
    info = function(message)
      table.insert(logs, tostring(message))
    end,
    warn = function(message)
      table.insert(logs, tostring(message))
    end,
    error = function(message)
      table.insert(logs, tostring(message))
    end,
  }

  local ok, err = pcall(function()
    run_observability_pipeline(event)
  end)

  log = old_log
  if not ok then
    error(err)
  end
  return logs
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

return {
  test_logs_state_gap_edges_from_trusted_marker_stream = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    mock_env()
    mock_all_issue_lists({ 42 })
    mock_pr_list({})
    mock_issue_view({
      render_comment(core.state_marker(proposal_id, "ready", "v1"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(core.state_marker(proposal_id, "blocked", "v1"), "mallory", "2026-06-03T01:01:00Z"),
      render_comment(core.state_marker(proposal_id, "implementing", "v1"), "fkst-test-bot", "2026-06-03T03:10:00Z"),
    })

    local logs = table.concat(gap_logs(), "\n")

    t.is_true(logs:find("tag=GAP_EDGE", 1, true) ~= nil)
    t.is_true(logs:find("proposal_id=" .. proposal_id, 1, true) ~= nil)
    t.is_true(logs:find("gap_edge=ready->implementing", 1, true) ~= nil)
    t.is_true(logs:find("gap_seconds=7800", 1, true) ~= nil)
    t.is_true(logs:find("budget_seconds=7200", 1, true) ~= nil)
    t.is_true(logs:find("budget_status=over-budget", 1, true) ~= nil)
    t.is_true(logs:find("wait_class=visibility-retry", 1, true) ~= nil)
    t.is_true(logs:find("ready->blocked", 1, true) == nil)
  end,

  test_attributes_dependency_wait_from_trusted_marker_stream = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    mock_env()
    mock_all_issue_lists({ 42 })
    mock_pr_list({})
    mock_issue_view({
      render_comment(core.state_marker(proposal_id, "ready", "v1"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(wait_marker(proposal_id, "v1", { 7 }), "fkst-test-bot", "2026-06-03T01:05:00Z"),
      render_comment(wait_marker(proposal_id, "v1", { 8 }), "mallory", "2026-06-03T01:06:00Z"),
      render_comment(core.state_marker(proposal_id, "implementing", "v1"), "fkst-test-bot", "2026-06-03T01:50:00Z"),
    })

    local logs = table.concat(gap_logs(), "\n")

    t.is_true(logs:find("gap_edge=ready->implementing", 1, true) ~= nil)
    t.is_true(logs:find("wait_class=dependency-gate", 1, true) ~= nil)
    t.is_true(logs:find("classes dependency-gate 1", 1, true) ~= nil)
  end,

  test_dashboard_renders_p50_p95_max_and_worst_offenders = function()
    local proposal_42 = "github-devloop/issue/owner/repo/42"
    local proposal_43 = "github-devloop/issue/owner/repo/43"
    mock_env()
    mock_all_issue_lists({ 42, 43 })
    mock_pr_list({})
    mock_issue_view({
      render_comment(core.state_marker(proposal_42, "ready", "v1"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(core.state_marker(proposal_42, "implementing", "v1"), "fkst-test-bot", "2026-06-03T03:10:00Z"),
    }, 42)
    mock_issue_view({
      render_comment(core.state_marker(proposal_43, "ready", "v1"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(core.state_marker(proposal_43, "implementing", "v1"), "fkst-test-bot", "2026-06-03T01:10:00Z"),
    }, 43)

    local logs = table.concat(gap_logs(), "\n")

    t.is_true(logs:find("## State-gap latency", 1, true) ~= nil)
    t.is_true(logs:find("ready->implementing: count 2, P50 10m 0s, P95 2h 10m, max 2h 10m, budget 2h 0m, near 0, over 1", 1, true) ~= nil)
    t.is_true(logs:find("classes visibility-retry 2", 1, true) ~= nil)
    t.is_true(logs:find("handoff unknown 2", 1, true) ~= nil)
    t.is_true(logs:find("worst #42 2h 10m, #43 10m 0s", 1, true) ~= nil)
  end,

  test_state_gap_stream_spans_issue_and_pr_marker_comments = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    mock_env()
    mock_all_issue_lists({ 42 })
    mock_pr_list({})
    mock_issue_view({
      render_comment(core.state_marker(proposal_id, "pr-open", "v1"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(m_builders.pr_link_marker(proposal_id, 7, "devloop-owner-repo-42", "v1", "integration/dev"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
    })
    mock_pr_view({
      render_comment(m_builders.pr_origin_marker(proposal_id, "42", "devloop-owner-repo-42", "v1", "integration/dev"), "fkst-test-bot", "2026-06-03T01:00:00Z"),
      render_comment(core.state_marker(proposal_id, "reviewing", "v1"), "fkst-test-bot", "2026-06-03T01:03:00Z"),
    })

    local logs = table.concat(gap_logs(), "\n")
    t.is_true(logs:find("gap_edge=pr-open->reviewing", 1, true) ~= nil)
    t.is_true(logs:find("gap_seconds=180", 1, true) ~= nil)
    t.is_true(logs:find("proposal_id=" .. proposal_id, 1, true) ~= nil)
  end,
}
