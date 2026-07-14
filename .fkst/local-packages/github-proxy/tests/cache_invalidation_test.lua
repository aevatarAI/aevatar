local h = require("tests.proxy_integration_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_comment_view = h.mock_comment_view
local mock_comment_write = h.mock_comment_write
local mock_poll = h.mock_poll
local count_calls = h.count_calls
require("tests.entity_view_probe_helpers")

local function mock_issue_view(title)
  t.mock_command("gh api repos/owner/x/issues/42", {
    stdout = '{"title":"' .. tostring(title) .. '","body":"","state":"open","labels":[],"assignees":[],"updated_at":"2026-06-03T01:02:03Z"}\n',
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
    stdout = "[[]]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function issue_comment_event()
  return {
    queue = "github_issue_comment_request",
    payload = {
      schema = "github-proxy.v1",
      repo = "owner/x",
      issue_number = 42,
      body = "state marker",
      dedup_key = "state/comment/owner/x/42/v1",
      source_ref = {
        kind = "external",
        ref = "owner/x#issue/42",
      },
    },
  }
end

local function run_entity_view_probe(run_opts, consumer, marker_bearing, named_marker_reader)
  local result = t.run_department("departments/test_entity_view_probe/main.lua", {
    queue = "entity_view_probe",
    payload = {
      repo = "owner/x",
      kind = "issue",
      number = 42,
      updated_at = "2026-06-03T01:02:03Z",
      consumer = consumer,
      marker_bearing = marker_bearing,
      named_marker_reader = named_marker_reader,
    },
  }, run_opts)
  t.eq(result.exit_code, 0)
  t.eq(#result.raises, 1)
  return result.raises[1].payload
end

return {
  test_successful_comment_write_invalidates_poll_cache_for_same_updated_at = function()
    local run_opts = opts("post-write-poll-invalidation", {
      FKST_GITHUB_WRITE = "1",
    })
    local poll_event = { queue = "github_poll_tick", payload = {} }

    mock_poll()
    local first = t.run_department("departments/github_poll/main.lua", poll_event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)

    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local written = t.run_department("departments/github_comment/main.lua", issue_comment_event(), run_opts)
    t.eq(written.exit_code, 0)
    t.eq(count_calls("gh api --method POST repos/owner/x/issues/42/comments"), 1)

    mock_poll()
    local after_write = t.run_department("departments/github_poll/main.lua", poll_event, run_opts)
    t.eq(after_write.exit_code, 0)
    t.eq(after_write.raises[1].queue, "github_entity_changed")
    t.eq(after_write.raises[1].payload.type, "issue")
    t.eq(after_write.raises[1].payload.number, 42)
    t.eq(after_write.raises[1].payload.updated_at, "2026-06-03T01:02:03Z")
  end,

  test_successful_write_invalidates_same_updated_at_entity_view_cache = function()
    local run_opts = opts("post-write-view-invalidation", {
      FKST_GITHUB_WRITE = "1",
    })

    mock_issue_view("Before")
    t.mock_command("gh api repos/owner/x/issues/42 --jq .updated_at // .updatedAt // \"\"", {
      stdout = "2026-06-03T01:02:03Z\n",
      stderr = "",
      exit_code = 0,
    })
    local first = run_entity_view_probe(run_opts, "first")
    t.eq(first.exit_code, 0)
    t.is_true(first.stdout:find('"Before"', 1, true) ~= nil)

    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local written = t.run_department("departments/github_comment/main.lua", issue_comment_event(), run_opts)
    t.eq(written.exit_code, 0)

    mock_issue_view("After")
    local after = run_entity_view_probe(run_opts, "second")
    t.eq(after.exit_code, 0)
    t.is_true(after.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls("gh api repos/owner/x/issues/42"), 2)
  end,

  test_marker_bearing_fetch_bypasses_proxy_entity_view_cache = function()
    local run_opts = opts("proxy-marker-fresh")
    mock_issue_view("Before")
    t.mock_command("gh api repos/owner/x/issues/42 --jq .updated_at // .updatedAt // \"\"", {
      stdout = "2026-06-03T01:02:03Z\n",
      stderr = "",
      exit_code = 0,
    })
    local first = run_entity_view_probe(run_opts, "marker-reader")
    t.eq(first.exit_code, 0)

    mock_issue_view("After")
    local marker_read = run_entity_view_probe(run_opts, "marker-reader-2", true)
    t.eq(marker_read.exit_code, 0)
    t.is_true(marker_read.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls("gh api repos/owner/x/issues/42"), 2)
    t.is_true(run_opts.env.FKST_RUNTIME_ROOT ~= nil)
  end,

  test_named_marker_issue_fetch_bypasses_proxy_entity_view_cache = function()
    local run_opts = opts("proxy-named-marker-fresh")
    mock_issue_view("Before")
    t.mock_command("gh api repos/owner/x/issues/42 --jq .updated_at // .updatedAt // \"\"", {
      stdout = "2026-06-03T01:02:03Z\n",
      stderr = "",
      exit_code = 0,
    })
    local first = run_entity_view_probe(run_opts, "non-marker-reader")
    t.eq(first.exit_code, 0)
    t.is_true(first.stdout:find('"Before"', 1, true) ~= nil)

    mock_issue_view("After")
    local marker_read = run_entity_view_probe(run_opts, "state-reader", false, true)
    t.eq(marker_read.exit_code, 0)
    t.is_true(marker_read.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls("gh api repos/owner/x/issues/42"), 2)
  end,
}
