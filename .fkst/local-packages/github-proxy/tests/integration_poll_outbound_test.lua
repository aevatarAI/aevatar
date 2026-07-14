local h = require("tests.proxy_integration_helpers")
local t = h.t
local core = h.core
local issue_list_json = h.issue_list_json
local pr_list_json = h.pr_list_json
local runtime_root = h.runtime_root
local opts = h.opts
local mock_repo_env = h.mock_repo_env
local mock_proxy_replay_budget_env = h.mock_proxy_replay_budget_env
local mock_poll_label_prefix_env = h.mock_poll_label_prefix_env
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_issue_list = h.mock_issue_list
local mock_pr_list = h.mock_pr_list
local mock_poll = h.mock_poll
local json_string = h.json_string
local comment_json = h.comment_json
local mock_comment_view = h.mock_comment_view
local mock_comment_view_failure = h.mock_comment_view_failure
local mock_label_view = h.mock_label_view
local mock_comment_write = h.mock_comment_write
local mock_repo_label_list = h.mock_repo_label_list
local mock_label_create = h.mock_label_create
local mock_label_write = h.mock_label_write
local mock_pr_comment_view = h.mock_pr_comment_view
local mock_pr_comment_write = h.mock_pr_comment_write
local calls_matching = h.calls_matching
local count_calls = h.count_calls
local capture_comment_department_logs = h.capture_comment_department_logs
local capture_label_department_logs = h.capture_label_department_logs
local long_dedup = h.long_dedup
local reviewing_marker = h.reviewing_marker
local issue_comment_create = "gh api --method POST repos/owner/x/issues/42/comments"

local function mock_poll_env(replay_budget)
  mock_repo_env()
  mock_poll_label_prefix_env("adapter-")
  if replay_budget ~= nil then
    mock_proxy_replay_budget_env(replay_budget)
  end
end

local function pr_json(number, updated_at, state)
  return string.format(
    '{"number":%d,"title":"PR %d","html_url":"https://github.example/owner/x/pull/%d","updated_at":"%s","state":"%s","labels":[{"name":"review"}]}',
    number,
    number,
    number,
    updated_at,
    state or "open"
  )
end

local function pr_list_many_json(count, target_number, target_updated_at)
  local parts = {}
  for index = 1, count do
    table.insert(parts, pr_json(100 + index, string.format("2026-06-03T03:%02d:00Z", index % 60), "OPEN"))
  end
  table.insert(parts, pr_json(target_number, target_updated_at, "OPEN"))
  return "[[" .. table.concat(parts, ",") .. "]]\n"
end

local function issue_json(number, updated_at)
  return string.format(
    '{"number":%d,"title":"Issue %d","html_url":"https://github.example/owner/x/issues/%d","updated_at":"%s","state":"open","labels":[{"name":"adapter-enabled"}],"assignees":[{"login":"fkst-test-bot"}]}',
    number, number, number, updated_at
  )
end

local function issue_list_from(items)
  return "[[" .. table.concat(items, ",") .. "]]\n"
end

local function pr_list_from(items)
  return "[[" .. table.concat(items, ",") .. "]]\n"
end

local function numbers(raises)
  local result = {}
  for _, raised in ipairs(raises or {}) do
    table.insert(result, raised.payload.number)
  end
  return table.concat(result, ",")
end

local function find_entity_raise(raises, entity_type, number)
  for _, raised in ipairs(raises or {}) do
    if raised.payload.type == entity_type and tonumber(raised.payload.number) == tonumber(number) then
      return raised
    end
  end
  return nil
end

local function has_arg_pair(rendered, flag, value)
  local text = tostring(rendered or "")
  return text:find(tostring(flag) .. " '" .. tostring(value) .. "'", 1, true) ~= nil
    or text:find(tostring(flag) .. " " .. tostring(value), 1, true) ~= nil
end

return {
  test_inbound_poll_raises_issue_and_pr_then_cache_hit = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("inbound-cache-hit")

    mock_poll()
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(first.raises[1].queue, "github_entity_changed")
    t.eq(first.raises[1].payload.type, "issue")
    t.eq(first.raises[1].payload.repo, "owner/x")
    t.eq(first.raises[1].payload.number, 42)
    t.eq(first.raises[1].payload.title, "Bridge issue")
    t.eq(first.raises[1].payload.updated_at, "2026-06-03T01:02:03Z")
    t.eq(first.raises[1].payload.labels[1], "adapter-enabled")
    t.eq(first.raises[1].payload.labels[2], "bug")
    t.is_nil(first.raises[1].payload.view_cache_key)
    t.eq(first.raises[1].payload.dedup_key, "owner/x#issue#42@2026-06-03T01:02:03Z")
    t.eq(first.raises[1].payload.source_ref.kind, "external")
    t.eq(first.raises[1].payload.source_ref.ref, "owner/x#issue/42")
    t.eq(first.raises[2].queue, "github_entity_changed")
    t.eq(first.raises[2].payload.type, "pr")
    t.eq(first.raises[2].payload.repo, "owner/x")
    t.eq(first.raises[2].payload.number, 7)
    t.eq(first.raises[2].payload.title, "Bridge PR")
    t.eq(first.raises[2].payload.url, "https://github.example/owner/x/pull/7")
    t.eq(first.raises[2].payload.state, "OPEN")
    t.eq(first.raises[2].payload.labels[1], "review")
    t.eq(first.raises[2].payload.updated_at, "2026-06-03T02:03:04Z")
    t.is_nil(first.raises[2].payload.view_cache_key)
    t.eq(first.raises[2].payload.dedup_key, "owner/x#pr#7@2026-06-03T02:03:04Z")
    t.eq(first.raises[2].payload.source_ref.kind, "external")
    t.eq(first.raises[2].payload.source_ref.ref, "owner/x#pr/7")
    t.is_nil(first.raises[3])

    mock_poll()
    local second = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 2)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 2)
  end,

  test_inbound_poll_re_raises_when_updated_at_changes = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("inbound-updated-at-change")

    mock_poll()
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)

    mock_poll(
      issue_list_json("2026-06-04T05:06:07Z"),
      pr_list_json("2026-06-04T06:07:08Z")
    )
    local changed = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(changed.exit_code, 0)
    t.eq(#changed.raises, 2)
    t.eq(changed.raises[1].payload.type, "issue")
    t.eq(changed.raises[1].payload.updated_at, "2026-06-04T05:06:07Z")
    t.eq(changed.raises[1].payload.dedup_key, "owner/x#issue#42@2026-06-04T05:06:07Z")
    t.eq(changed.raises[2].payload.type, "pr")
    t.eq(changed.raises[2].payload.updated_at, "2026-06-04T06:07:08Z")
    t.eq(changed.raises[2].payload.dedup_key, "owner/x#pr#7@2026-06-04T06:07:08Z")
  end,

  test_inbound_poll_does_not_re_raise_closed_lifecycle_state_when_updated_at_changes = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("inbound-closed-change")

    mock_poll()
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(first.raises[1].payload.type, "issue")
    t.eq(first.raises[1].payload.state, "OPEN")

    mock_poll(
      "[]\n",
      pr_list_json()
    )
    local closed = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(closed.exit_code, 0)
    t.eq(#closed.raises, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 2)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 2)
  end,

  test_inbound_poll_open_pr_coverage_is_not_limited_by_terminal_volume = function()
    local event = { queue = "github_poll_tick", payload = {} }

    mock_poll_env("100")
    mock_issue_list("[]\n")
    mock_pr_list(pr_list_many_json(35, 12, "2026-06-02T00:00:00Z"))
    local result = t.run_department("departments/github_poll/main.lua", event, opts("open-pr-coverage", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "100",
    }))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 36)
    local target = find_entity_raise(result.raises, "pr", 12)
    t.is_true(target ~= nil)
    t.eq(target.queue, "github_entity_changed")
    t.eq(target.payload.updated_at, "2026-06-02T00:00:00Z")
    t.eq(target.payload.dedup_key, "owner/x#pr#12@2026-06-02T00:00:00Z")
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 1)
    t.eq(calls_matching("--state all")[1], nil)
  end,

  test_inbound_poll_paces_cold_replay_and_continues_next_cycle = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("replay-budget", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "2",
    })
    local issues = issue_list_from({
      issue_json(44, "2026-06-03T01:04:00Z"),
      issue_json(42, "2026-06-03T01:02:00Z"),
      issue_json(43, "2026-06-03T01:03:00Z"),
    })

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list("[]\n")
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(numbers(first.raises), "42,43")

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list("[]\n")
    local second = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    t.eq(second.raises[1].payload.number, 44)
  end,

  test_inbound_poll_replay_budget_is_shared_across_issue_and_pr_lanes = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("shared-replay-budget", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "2",
    })
    local issues = issue_list_from({
      issue_json(42, "2026-06-03T01:02:00Z"),
      issue_json(44, "2026-06-03T01:04:00Z"),
    })
    local prs = pr_list_from({
      pr_json(7, "2026-06-03T01:03:00Z"),
      pr_json(8, "2026-06-03T01:05:00Z"),
    })

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list(prs)
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(first.raises[1].payload.type, "issue")
    t.eq(first.raises[1].payload.number, 42)
    t.eq(first.raises[2].payload.type, "pr")
    t.eq(first.raises[2].payload.number, 7)

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list(prs)
    local second = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 2)
    t.eq(second.raises[1].payload.type, "issue")
    t.eq(second.raises[1].payload.number, 44)
    t.eq(second.raises[2].payload.type, "pr")
    t.eq(second.raises[2].payload.number, 8)
  end,

  test_inbound_poll_replay_budget_tie_breaks_shared_lanes_deterministically = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("shared-replay-budget-tie", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "2",
    })
    local timestamp = "2026-06-03T01:02:00Z"
    local issues = issue_list_from({
      issue_json(42, timestamp),
      issue_json(44, timestamp),
    })
    local prs = pr_list_from({
      pr_json(42, timestamp),
      pr_json(43, timestamp),
    })

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list(prs)
    local first = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(first.raises[1].payload.type, "issue")
    t.eq(first.raises[1].payload.number, 42)
    t.eq(first.raises[2].payload.type, "pr")
    t.eq(first.raises[2].payload.number, 42)

    mock_poll_env("2")
    mock_issue_list(issues)
    mock_pr_list(prs)
    local second = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 2)
    t.eq(second.raises[1].payload.type, "pr")
    t.eq(second.raises[1].payload.number, 43)
    t.eq(second.raises[2].payload.type, "issue")
    t.eq(second.raises[2].payload.number, 44)
  end,

  test_inbound_poll_defaults_cold_replay_budget_to_ten = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local items = {}
    for number = 1, 11 do
      table.insert(items, issue_json(number, string.format("2026-06-03T01:%02d:00Z", number)))
    end

    mock_poll_env("")
    mock_issue_list(issue_list_from(items))
    mock_pr_list("[]\n")
    local result = t.run_department("departments/github_poll/main.lua", event, opts("default-replay-budget"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 10)
    t.eq(result.raises[1].payload.number, 1)
    t.eq(result.raises[10].payload.number, 10)
  end,

  test_inbound_poll_prioritizes_cached_fresh_changes_over_replay_budget = function()
    local event = { queue = "github_poll_tick", payload = {} }
    local run_opts = opts("fresh-before-replay", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "1",
    })

    mock_poll_env("1")
    mock_issue_list(issue_list_from({
      issue_json(42, "2026-06-03T01:02:00Z"),
    }))
    mock_pr_list("[]\n")
    local seeded = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(seeded.exit_code, 0)
    t.eq(#seeded.raises, 1)

    mock_poll_env("1")
    mock_issue_list(issue_list_from({
      issue_json(43, "2026-06-03T01:03:00Z"),
      issue_json(42, "2026-06-03T01:05:00Z"),
      issue_json(44, "2026-06-03T01:04:00Z"),
    }))
    mock_pr_list("[]\n")
    local changed = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(changed.exit_code, 0)
    t.eq(#changed.raises, 2)
    t.eq(numbers(changed.raises), "42,43")
  end,

  test_inbound_poll_prioritizes_cold_intake_candidates_over_replay_budget = function()
    local event = { queue = "github_poll_tick", payload = {}, ts = "poll-cold" }
    local run_opts = opts("cold-intake-before-replay", { FKST_GITHUB_PROXY_REPLAY_BUDGET = "1" })
    mock_poll_env("1")
    local intake = '{"number":50,"title":"Issue 50","html_url":"https://github.example/owner/x/issues/50","updated_at":"2026-06-03T01:04:00Z","state":"open","labels":[{"name":"bug"}],"assignees":[]}'
    mock_issue_list(issue_list_from({ issue_json(42, "2026-06-03T01:02:00Z"), issue_json(43, "2026-06-03T01:03:00Z"), intake }))
    mock_pr_list("[]\n")
    local result = t.run_department("departments/github_poll/main.lua", event, run_opts)
    t.eq(result.exit_code, 0) t.eq(#result.raises, 2)
    t.eq(numbers(result.raises), "50,42")
    t.eq(result.raises[1].queue, "github_entity_changed")
    t.eq(result.raises[1].payload.schema, "github-proxy.v1")
    t.eq(result.raises[1].payload.type, "issue")
    t.eq(result.raises[1].payload.repo, "owner/x")
    t.eq(result.raises[1].payload.number, 50)
    t.eq(result.raises[1].payload.state, "OPEN")
    t.eq(result.raises[1].payload.labels[1], "bug")
    t.eq(result.raises[1].payload.dedup_key, "owner/x#issue#50@2026-06-03T01:04:00Z/poll/poll-cold")
    t.eq(result.raises[1].payload.source_ref.kind, "external")
    t.eq(result.raises[1].payload.source_ref.ref, "owner/x#issue/50")
  end,

  test_inbound_poll_level_replays_stateless_intake_candidates = function()
    local run_opts = opts("stateless-intake-level-replay", { FKST_GITHUB_PROXY_REPLAY_BUDGET = "1" })
    local intake = '{"number":50,"title":"Issue 50","html_url":"https://github.example/owner/x/issues/50","updated_at":"2026-06-03T01:04:00Z","state":"open","labels":[{"name":"bug"}],"assignees":[]}'
    local managed = issue_json(42, "2026-06-03T01:02:00Z")

    mock_poll_env("1")
    mock_issue_list(issue_list_from({ managed, intake }))
    mock_pr_list("[]\n")
    local first = t.run_department("departments/github_poll/main.lua", {
      queue = "github_poll_tick",
      payload = {},
      ts = "poll-1",
    }, run_opts)
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(numbers(first.raises), "50,42")
    t.eq(first.raises[1].payload.dedup_key, "owner/x#issue#50@2026-06-03T01:04:00Z/poll/poll-1")
    t.eq(first.raises[2].payload.dedup_key, "owner/x#issue#42@2026-06-03T01:02:00Z")

    mock_poll_env("1")
    mock_issue_list(issue_list_from({ managed, intake }))
    mock_pr_list("[]\n")
    local second = t.run_department("departments/github_poll/main.lua", {
      queue = "github_poll_tick",
      payload = {},
      ts = "poll-2",
    }, run_opts)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    t.eq(second.raises[1].payload.number, 50)
    t.eq(second.raises[1].payload.dedup_key, "owner/x#issue#50@2026-06-03T01:04:00Z/poll/poll-2")

    mock_poll_env("1")
    mock_issue_list(issue_list_from({
      '{"number":50,"title":"Issue 50","html_url":"https://github.example/owner/x/issues/50","updated_at":"2026-06-03T01:04:00Z","state":"open","labels":[{"name":"adapter-enabled"},{"name":"bug"}],"assignees":[]}',
      managed,
    }))
    mock_pr_list("[]\n")
    local labelled = t.run_department("departments/github_poll/main.lua", {
      queue = "github_poll_tick",
      payload = {},
      ts = "poll-3",
    }, run_opts)
    t.eq(labelled.exit_code, 0)
    t.eq(#labelled.raises, 1)
    t.eq(labelled.raises[1].payload.number, 50)
    t.eq(labelled.raises[1].payload.dedup_key, "owner/x#issue#50@2026-06-03T01:04:00Z")

    mock_poll_env("1")
    mock_issue_list(issue_list_from({
      '{"number":50,"title":"Issue 50","html_url":"https://github.example/owner/x/issues/50","updated_at":"2026-06-03T01:04:00Z","state":"open","labels":[{"name":"adapter-enabled"},{"name":"bug"}],"assignees":[]}',
      managed,
    }))
    mock_pr_list("[]\n")
    local cached_labelled = t.run_department("departments/github_poll/main.lua", {
      queue = "github_poll_tick",
      payload = {},
      ts = "poll-4",
    }, run_opts)
    t.eq(cached_labelled.exit_code, 0)
    t.eq(#cached_labelled.raises, 0)
  end,

  test_inbound_poll_rejects_invalid_replay_budget = function()
    mock_poll_env("0")
    mock_issue_list()
    mock_pr_list()

    local result = t.run_department("departments/github_poll/main.lua", { queue = "github_poll_tick", payload = {} }, opts("invalid-replay-budget", {
      FKST_GITHUB_PROXY_REPLAY_BUDGET = "0",
    }))
    t.eq(result.exit_code, 1)
  end,

  test_inbound_poll_continues_when_issue_list_fails = function()
    mock_repo_env()
    mock_issue_list("", 2, "forced issue list failure")
    mock_pr_list()

    local result = t.run_department("departments/github_poll/main.lua", { queue = "github_poll_tick", payload = {} }, opts("issue-list-fails"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github_entity_changed")
    t.eq(result.raises[1].payload.type, "pr")
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 1)
  end,

  test_inbound_poll_continues_when_pr_list_fails = function()
    mock_repo_env()
    mock_issue_list()
    mock_pr_list("", 2, "forced pr list failure")

    local result = t.run_department("departments/github_poll/main.lua", { queue = "github_poll_tick", payload = {} }, opts("pr-list-fails"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github_entity_changed")
    t.eq(result.raises[1].payload.type, "issue")
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 1)
  end,

  test_inbound_poll_rate_limit_failure_errors_for_retry = function()
    mock_repo_env()
    mock_issue_list("", 1, "API rate limit exceeded")
    mock_pr_list()

    local result = t.run_department("departments/github_poll/main.lua", { queue = "github_poll_tick", payload = {} }, opts("issue-list-rate-limit"))
    t.eq(result.exit_code, 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 0)
  end,

  test_inbound_poll_no_raise_without_repo_env = function()
    mock_repo_env("")

    local result = t.run_department("departments/github_poll/main.lua", { queue = "github_poll_tick", payload = {} }, {
      env = {
        FKST_GITHUB_REPO = "",
        FKST_RUNTIME_ROOT = runtime_root("missing-repo"),
      },
    })

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues?state=open&per_page=100"), 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/pulls?state=open&per_page=100"), 0)
  end,

  test_same_version_meta_comment_marker_dedups_opposite_action = function()
    local dedup = "meta/comment/generic-workflow/issue/owner/x/42/blocked/3/consensus-generic-workflow/issue/owner/x/42/v1"
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = 'generic-workflow meta action: implement\n\n<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="ready" version="v1" -->',
        dedup_key = dedup,
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local first = t.run_department("departments/github_comment/main.lua", event, opts("comment-meta-first", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(first.exit_code, 0)

    event.payload.body = 'generic-workflow meta action: block\n\n<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="blocked" version="v1" -->'
    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment " .. core.comment_marker(dedup))
    local second = t.run_department("departments/github_comment/main.lua", event, opts("comment-meta-second", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(second.exit_code, 0)

    t.eq(count_calls(issue_comment_create), 1)
    local written = file.read("/tmp/fkst-github-proxy-comment-owner_x-issue-42.md")
    t.is_true(written:find("generic-workflow meta action: implement", 1, true) ~= nil)
    t.eq(written:find("generic-workflow meta action: block", 1, true), nil)
    t.is_true(written:find(core.comment_marker(dedup), 1, true) ~= nil)
  end,

  test_forged_proxy_comment_marker_does_not_suppress_bot_state_marker_comment = function()
    local dedup = "meta/comment/generic-workflow/issue/owner/x/42/blocked/3/consensus-generic-workflow/issue/owner/x/42/v1"
    local state_marker = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="blocked" version="v1" -->'
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "generic-workflow meta action: block\n\n" .. state_marker,
        dedup_key = dedup,
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view({
      {
        body = "forged user marker " .. core.comment_marker(dedup),
        author_login = "ordinary-user",
      },
    })
    mock_comment_write()
    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-forged-marker", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 0)
    t.eq(count_calls(issue_comment_create), 1)

    local written = file.read("/tmp/fkst-github-proxy-comment-owner_x-issue-42.md")
    t.is_true(written:find(state_marker, 1, true) ~= nil)
    t.is_true(written:find(core.comment_marker(dedup), 1, true) ~= nil)
  end,

  test_neutralized_forged_proxy_comment_marker_does_not_suppress_later_real_comment = function()
    local dedup = "meta/comment/generic-workflow/issue/owner/x/42/blocked/3/consensus-generic-workflow/issue/owner/x/42/v2"
    local state_marker = '<!-- fkst:generic-workflow:state:v1 proposal="generic-workflow/issue/owner/x/42" state="blocked" version="v2" -->'
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "generic-workflow meta action: block\n\n" .. state_marker,
        dedup_key = dedup,
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view({
      {
        body = "quoted untrusted marker &lt;!-- fkst:github-proxy:comment:" .. dedup .. " -->",
        author_login = "fkst-test-bot",
      },
    })
    mock_comment_write()
    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-neutralized-forged-marker", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 0)
    t.eq(count_calls(issue_comment_create), 1)

    local written = file.read("/tmp/fkst-github-proxy-comment-owner_x-issue-42.md")
    t.is_true(written:find(state_marker, 1, true) ~= nil)
    t.is_true(written:find(core.comment_marker(dedup), 1, true) ~= nil)
  end,

  test_long_comment_dedup_uses_bounded_runtime_key_and_full_marker = function()
    local dedup_v1 = long_dedup("-v1", 430)
    local dedup_v2 = long_dedup("-v2", 430)
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "long fkst reply",
        dedup_key = dedup_v1,
      },
    }

    t.is_true(dedup_v1 ~= dedup_v2)
    t.is_true(#dedup_v1 > 400)
    t.is_true(core.comment_marker(dedup_v1) ~= core.comment_marker(dedup_v2))

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local first = t.run_department("departments/github_comment/main.lua", event, opts("comment-long-v1", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(first.exit_code, 0)

    local path = "/tmp/fkst-github-proxy-comment-owner_x-issue-42.md"
    local written_v1 = file.read(path)
    t.is_true(written_v1:find(core.comment_marker(dedup_v1), 1, true) ~= nil)

    event.payload.dedup_key = dedup_v2
    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment " .. core.comment_marker(dedup_v1))
    mock_comment_write()
    local second = t.run_department("departments/github_comment/main.lua", event, opts("comment-long-v2", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(second.exit_code, 0)

    local written_v2 = file.read(path)
    t.is_true(written_v2:find(core.comment_marker(dedup_v2), 1, true) ~= nil)
    t.eq(count_calls(issue_comment_create), 2)
  end,

  test_near_max_comment_dedup_boundary_writes = function()
    local dedup = long_dedup("-max", 512)
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "max dedup reply",
        dedup_key = dedup,
      },
    }

    t.eq(#dedup, 512)
    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-long-max", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 0)

    local written = file.read("/tmp/fkst-github-proxy-comment-owner_x-issue-42.md")
    t.is_true(written:find(core.comment_marker(dedup), 1, true) ~= nil)
    t.eq(count_calls(issue_comment_create), 1)
  end,

  test_comment_request_uses_payload_repo = function()
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/payload",
        issue_number = 42,
        body = "payload repo reply",
        dedup_key = "payload-repo-reply",
      },
    }

    mock_repo_env("owner/env")
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-payload-repo", {
      FKST_GITHUB_REPO = "owner/env",
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 0)

    local view_calls = calls_matching("gh api --paginate --slurp repos/owner/payload/issues/42/comments?per_page=100")
    t.eq(#view_calls, 1)
    t.is_true(view_calls[1].rendered:find("repos/owner/payload/issues/42/comments", 1, true) ~= nil)
    local comment_calls = calls_matching("gh api --method POST")
    t.eq(#comment_calls, 1)
    t.is_true(comment_calls[1].rendered:find("repos/owner/payload/issues/42/comments", 1, true) ~= nil)
  end,

  test_comment_real_write_failure_errors_for_retry = function()
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "fkst reply",
        dedup_key = "reply-failure",
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    t.mock_command("gh api --method POST repos/owner/x/issues/42/comments --field body=/tmp/fkst-github-proxy-comment-owner_x-issue-42.md", {
      stdout = "",
      stderr = "forced comment failure",
      exit_code = 1,
    })

    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-write-fails", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 1)
    t.eq(count_calls(issue_comment_create), 1)
  end,

  test_comment_real_write_view_failure_errors_for_retry = function()
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "fkst reply",
        dedup_key = "reply-view-failure",
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view_failure()

    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-view-fails", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 1)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100"), 1)
    t.eq(count_calls(issue_comment_create), 0)
  end,

  test_label_request_dry_run_write_and_rewrite = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = { "adapter-ready" },
        remove_labels = { "adapter-thinking" },
        dedup_key = "generic-workflow/issue/owner/x/42/result",
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    mock_write_env("")
    local dry_logs, dry_write_requests = capture_label_department_logs(
      "departments/github_issue_label/main.lua",
      event,
      ""
    )
    t.eq(dry_write_requests, 0)
    t.eq(dry_logs[1], "github-proxy dept=github_issue_label tag=OUTBOUND mode=dry-run repo=owner/x issue=42 add=adapter-ready remove=adapter-thinking dedup_key=generic-workflow/issue/owner/x/42/result reason=FKST_GITHUB_WRITE!=1")

    local dry = t.run_department("departments/github_issue_label/main.lua", event, opts("label-dry-run"))
    t.eq(dry.exit_code, 0)
    t.eq(count_calls("gh issue edit"), 0)

    local write_opts = opts("label-write", {
      FKST_GITHUB_WRITE = "1",
    })
    local real_logs, real_write_requests = capture_label_department_logs(
      "departments/github_issue_label/main.lua",
      event,
      "1"
    )
    t.eq(real_write_requests, 1)
    t.eq(real_logs[1], "github-proxy dept=github_issue_label tag=OUTBOUND mode=real repo=owner/x issue=42 add=adapter-ready remove=adapter-thinking dedup_key=generic-workflow/issue/owner/x/42/result")

    mock_write_env("1")
    mock_label_write()
    local write = t.run_department("departments/github_issue_label/main.lua", event, write_opts)
    t.eq(write.exit_code, 0)
    t.eq(count_calls("gh label list"), 1)
    t.eq(count_calls("gh label create"), 0)
    t.eq(count_calls("gh issue edit"), 1)
    local edit_calls = calls_matching("gh issue edit")
    t.is_true(has_arg_pair(edit_calls[1].rendered, "--add-label", "adapter-ready"))
    t.is_true(has_arg_pair(edit_calls[1].rendered, "--remove-label", "adapter-thinking"))

    mock_write_env("1")
    mock_label_write()
    local again = t.run_department("departments/github_issue_label/main.lua", event, write_opts)
    t.eq(again.exit_code, 0)
    t.eq(count_calls("gh label list"), 2)
    t.eq(count_calls("gh label create"), 0)
    t.eq(count_calls("gh issue edit"), 2)
  end,

  test_label_request_creates_missing_repo_label_before_add = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = { "adapter-fresh" },
        remove_labels = {},
        dedup_key = "generic-workflow/issue/owner/x/42/fresh-label",
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    mock_write_env("1")
    mock_repo_label_list({ "adapter-ready" })
    mock_label_create()
    t.mock_command("gh issue edit", { stdout = "", exit_code = 0 })
    local result = t.run_department("departments/github_issue_label/main.lua", event, opts("label-create-missing", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh label list"), 1)
    t.eq(count_calls("gh label create"), 1)
    t.eq(count_calls("gh issue edit"), 1)
    local create = calls_matching("gh label create")[1]
    t.is_true(create.rendered:find("adapter-fresh", 1, true) ~= nil)
    t.is_true(create.rendered:find("--repo owner/x", 1, true) ~= nil)
    local edit = calls_matching("gh issue edit")[1]
    t.is_true(has_arg_pair(edit.rendered, "--add-label", "adapter-fresh"))
  end,

  test_label_request_skips_remove_when_repo_label_is_missing = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = {},
        remove_labels = { "adapter-gone" },
        dedup_key = "generic-workflow/issue/owner/x/42/remove-gone-label",
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    mock_write_env("1")
    mock_repo_label_list({ "adapter-ready" })
    local result = t.run_department("departments/github_issue_label/main.lua", event, opts("label-remove-missing", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh label list"), 1)
    t.eq(count_calls("gh label create"), 0)
    t.eq(count_calls("gh issue edit"), 0)

    local logs, write_requests = capture_label_department_logs(
      "departments/github_issue_label/main.lua",
      event,
      "1",
      false
    )
    t.eq(write_requests, 1)
    t.eq(logs[1], "github-proxy dept=github_issue_label tag=OUTBOUND mode=real repo=owner/x issue=42 add= remove=adapter-gone dedup_key=generic-workflow/issue/owner/x/42/remove-gone-label")
    t.eq(logs[2], "github-proxy dept=github_issue_label tag=SKIP reason=no-effective-label-change repo=owner/x issue=42 add= remove=adapter-gone dedup_key=generic-workflow/issue/owner/x/42/remove-gone-label")
  end,

  test_long_label_dedup_uses_bounded_lock_key = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = { "adapter-ready" },
        remove_labels = {},
        dedup_key = long_dedup("-label", 430),
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    t.is_true(#event.payload.dedup_key > 400)
    mock_write_env("1")
    mock_label_write()
    local result = t.run_department("departments/github_issue_label/main.lua", event, opts("label-long-dedup", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh issue edit"), 1)
  end,

  test_label_request_writes_without_state_precondition = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = { "adapter-ready" },
        remove_labels = { "adapter-thinking" },
        dedup_key = "generic-workflow/issue/owner/x/42/ready-hint",
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    local write_opts = opts("label-no-precondition", {
      FKST_GITHUB_WRITE = "1",
    })

    mock_write_env("1")
    mock_label_write()
    local current = t.run_department("departments/github_issue_label/main.lua", event, write_opts)
    t.eq(current.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100"), 0)
    t.eq(count_calls("gh issue edit"), 1)
    local current_edit = calls_matching("gh issue edit")[1]
    t.is_true(has_arg_pair(current_edit.rendered, "--add-label", "adapter-ready"))
    t.is_true(has_arg_pair(current_edit.rendered, "--remove-label", "adapter-thinking"))
  end,

  test_label_request_applies_exclusive_hint_without_state_precondition = function()
    local event = {
      queue = "github_issue_label_request",
      payload = {
        schema = "github-proxy.label.v1",
        repo = "owner/x",
        issue_number = 42,
        add_labels = { "adapter-blocked" },
        remove_labels = { "adapter-blocked", "adapter-thinking", "adapter-ready" },
        dedup_key = "generic-workflow/issue/owner/x/42/blocked-hint",
        source_ref = {
          kind = "external",
          ref = "owner/x#issue/42",
        },
      },
    }

    mock_write_env("1")
    mock_label_write()
    local result = t.run_department("departments/github_issue_label/main.lua", event, opts("label-blocked-hint", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100"), 0)
    t.eq(count_calls("gh issue edit"), 1)
    local edit = calls_matching("gh issue edit")[1]
    t.is_true(has_arg_pair(edit.rendered, "--add-label", "adapter-blocked"))
    t.is_true(has_arg_pair(edit.rendered, "--remove-label", "adapter-ready"))
  end,

}
