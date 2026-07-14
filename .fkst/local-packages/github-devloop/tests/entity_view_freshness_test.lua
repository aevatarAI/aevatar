local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t
local seam = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
local devloop_entity_view = require("devloop.github_proxy_entity_view")

local function count_calls(needle)
  return gh_argv.count_calls(t, needle)
end

local function count_exact_calls(command)
  local count = 0
  local expected = h.argv_rendered(command)
  for _, call in ipairs(t.command_calls()) do
    local rendered = tostring(call.rendered or "")
    local normalized = h.argv_rendered(rendered)
    if normalized == expected then
      count = count + 1
    end
  end
  return count
end

local function decode(text)
  local ok, decoded = pcall(json.decode, text or "")
  t.eq(ok, true)
  return decoded
end

local function issue_rest_command(repo, number)
  return "gh api repos/" .. tostring(repo) .. "/issues/" .. tostring(number)
end

local function pr_rest_command(repo, number)
  return "gh api repos/" .. tostring(repo) .. "/pulls/" .. tostring(number)
end

local function comments_rest_command(repo, number)
  return "gh api --paginate --slurp "
    .. "repos/" .. tostring(repo) .. "/issues/" .. tostring(number) .. "/comments?per_page=100"
end

local function encode_json_string(value)
  return tostring(value or "")
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\b", "\\b")
    :gsub("\f", "\\f")
    :gsub("\n", "\\n")
    :gsub("\r", "\\r")
    :gsub("\t", "\\t")
end

local function seed_cached_view(repo, kind, number, stdout, updated_at, producer)
  cache_set(require("devloop.github_proxy_entity_view").entity_view_cache_key(core, repo, kind, number), '{"updated_at":"' .. encode_json_string(updated_at)
    .. '","producer":"' .. encode_json_string(producer or "seed")
    .. '","stdout":"' .. encode_json_string(stdout)
    .. '"}')
end

return {
  test_marker_issue_state_reader_accepts_explicit_timeout = function()
    seam.mock_issue_read_forms(t, {
      repo = "owner/repo",
      number = 42,
      title = "Timeout",
      updated_at = "2026-06-03T01:02:03Z",
      register_all_views = true,
      times = 1,
    })

    local ok, result = pcall(function()
      return require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, "owner/repo", 42, "2026-06-03T01:02:03Z", {
        timeout = 10,
      })
    end)

    t.eq(ok, true, tostring(result))
    t.eq(result.exit_code, 0)
    local seen_rest_read = 0
    for _, call in ipairs(t.command_calls()) do
      if tostring(call.rendered or "") == issue_rest_command("owner/repo", 42) then
        seen_rest_read = seen_rest_read + 1
      end
    end
    t.eq(seen_rest_read, 1)
  end,

  test_validator_match_serves_cached_issue_view_without_graphql = function()
    local repo = "owner/cache-hit"
    local issue_number = 4242
    local updated_at = "2026-06-03T01:02:03Z"
    local view_command = core.gh_issue_view_entity_cmd(repo, issue_number)
    local probe_command = core.gh_entity_updated_at_cmd(repo, "issue", issue_number)
    seam.mock_issue_read_forms(t, {
      repo = repo,
      number = issue_number,
      title = "Cached",
      updated_at = updated_at,
      register_all_views = true,
      times = 1,
    })
    seed_cached_view(repo, "issue", issue_number, seam.issue_view_stdout({
      repo = repo,
      number = issue_number,
      title = "Cached",
      updated_at = updated_at,
    }), updated_at)

    local second = require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, repo, issue_number, updated_at, {
      consumer = "state-reader",
    })

    t.eq(second.exit_code, 0)
    t.is_true(second.stdout:find('"Cached"', 1, true) ~= nil)
    t.eq(count_calls(view_command), 0)
    t.eq(count_calls(probe_command), 0)
  end,

  test_validator_mismatch_fetches_rest_issue_view_and_recaches = function()
    local repo = "owner/cache-miss"
    local issue_number = 4243
    local view_command = core.gh_issue_view_entity_cmd(repo, issue_number)
    local rest_command = issue_rest_command(repo, issue_number)
    local comments_command = comments_rest_command(repo, issue_number)
    seed_cached_view(repo, "issue", issue_number, seam.issue_view_stdout({
      repo = repo,
      number = issue_number,
      title = "Before",
      updated_at = "2026-06-03T01:02:03Z",
    }), "2026-06-03T01:02:03Z")
    seam.mock_issue_read_forms(t, {
      repo = repo,
      number = issue_number,
      title = "After",
      updated_at = "2026-06-03T01:02:04Z",
      register_all_views = true,
      times = 1,
    })
    local second = require("devloop.github_proxy_entity_view").fetch_issue_view(core, repo, issue_number, "2026-06-03T01:02:04Z", {
      consumer = "second-reader",
    })
    local third = require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, repo, issue_number, "2026-06-03T01:02:04Z")

    t.eq(second.exit_code, 0)
    t.is_true(second.stdout:find('"After"', 1, true) ~= nil)
    t.eq(third.exit_code, 0)
    t.is_true(third.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls(view_command), 0)
    t.eq(count_exact_calls(rest_command), 1)
    t.eq(count_exact_calls(comments_command), 1)
  end,

  test_force_fresh_issue_view_bypasses_cache_and_recaches = function()
    local repo = "owner/force-fresh"
    local issue_number = 4244
    local updated_at = "2026-06-03T01:02:03Z"
    local view_command = core.gh_issue_view_entity_cmd(repo, issue_number)
    local rest_command = issue_rest_command(repo, issue_number)
    local comments_command = comments_rest_command(repo, issue_number)
    seed_cached_view(repo, "issue", issue_number, seam.issue_view_stdout({
      repo = repo,
      number = issue_number,
      title = "Before",
      updated_at = updated_at,
    }), updated_at)
    seam.mock_issue_read_forms(t, {
      repo = repo,
      number = issue_number,
      title = "After",
      updated_at = updated_at,
      register_all_views = true,
      times = 1,
    })
    local forced = require("devloop.github_proxy_entity_view").fetch_issue_view(core, repo, issue_number, updated_at, {
      consumer = "claim-gate",
      force_fresh = true,
    })
    local cached = require("devloop.github_proxy_entity_view").fetch_issue_view_state(core, repo, issue_number, updated_at)

    t.eq(forced.exit_code, 0)
    t.is_true(forced.stdout:find('"After"', 1, true) ~= nil)
    t.is_true(cached.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls(view_command), 0)
    t.eq(count_exact_calls(rest_command), 1)
    t.eq(count_exact_calls(comments_command), 1)
  end,

  test_write_invalidation_forces_same_validator_issue_refetch = function()
    local repo = "owner/cache-invalidation"
    local issue_number = 4245
    local updated_at = "2026-06-03T01:02:03Z"
    local view_command = core.gh_issue_view_entity_cmd(repo, issue_number)
    local rest_command = issue_rest_command(repo, issue_number)
    local comments_command = comments_rest_command(repo, issue_number)
    seed_cached_view(repo, "issue", issue_number, seam.issue_view_stdout({
      repo = repo,
      number = issue_number,
      title = "Before",
      updated_at = updated_at,
    }), updated_at)

    devloop_entity_view.invalidate_entity_after_write(repo, "issue", issue_number)
    seam.mock_issue_read_forms(t, {
      repo = repo,
      number = issue_number,
      title = "After",
      updated_at = updated_at,
      register_all_views = true,
      times = 1,
    })

    local after = require("devloop.github_proxy_entity_view").fetch_issue_view(core, repo, issue_number, updated_at, {
      consumer = "second-reader",
    })

    t.eq(after.exit_code, 0)
    t.is_true(after.stdout:find('"After"', 1, true) ~= nil)
    t.eq(count_calls(view_command), 0)
    t.eq(count_exact_calls(rest_command), 1)
    t.eq(count_exact_calls(comments_command), 1)
    t.eq(cache_get(require("devloop.github_proxy_entity_view").entity_view_cache_key(core, repo, "issue", issue_number)) ~= "", true)
  end,

  test_no_validator_uses_rest_probe_before_serving_cached_pr_view = function()
    local repo = "owner/probe"
    local pr_number = 7
    local updated_at = "2026-06-03T01:02:03Z"
    local view_command = core.gh_pr_view_entity_cmd(repo, pr_number)
    local probe_command = core.gh_entity_updated_at_cmd(repo, "pr", pr_number)
    seed_cached_view(repo, "pr", pr_number, seam.pr_view_stdout({
      repo = repo,
      number = pr_number,
      head = "branch",
      head_sha = "abc123",
      updated_at = updated_at,
    }), updated_at)
    t.is_true(tostring(cache_get(require("devloop.github_proxy_entity_view").entity_view_cache_key(core, repo, "pr", pr_number)) or ""):find('"updated_at"', 1, true) ~= nil)
    seam.mock_pr_read_forms(t, {
      repo = repo,
      number = pr_number,
      head = "branch",
      head_sha = "abc123",
      updated_at = updated_at,
      register_all_views = true,
      times = 1,
    })
    local view_calls = 0
    local probe_calls = 0
    local ok, probed = pcall(function()
      return devloop_entity_view.fetch_pr_view_origin(repo, pr_number, nil, {
        consumer = "probe-reader",
      })
    end)

    t.eq(ok, true, tostring(probed))
    t.eq(probed.exit_code, 0)
    t.is_true(probed.stdout:find('"abc123"', 1, true) ~= nil)
    for _, call in ipairs(t.command_calls()) do
      local rendered = tostring(call.rendered or "")
      if rendered == view_command then
        view_calls = view_calls + 1
      end
      if h.argv_rendered(rendered) == h.argv_rendered(probe_command) then
        probe_calls = probe_calls + 1
      end
    end
    t.eq(view_calls, 0)
    t.eq(probe_calls, 1)
    t.eq(count_calls(view_command), 0)
  end,

  test_no_validator_probe_mismatch_fetches_rest_pr_view = function()
    local repo = "owner/probe-miss"
    local pr_number = 8
    local cached_updated_at = "2026-06-03T01:02:03Z"
    local current_updated_at = "2026-06-03T01:02:04Z"
    local view_command = core.gh_pr_view_entity_cmd(repo, pr_number)
    local probe_command = core.gh_entity_updated_at_cmd(repo, "pr", pr_number)
    local rest_command = pr_rest_command(repo, pr_number)
    local comments_command = comments_rest_command(repo, pr_number)
    seed_cached_view(repo, "pr", pr_number, seam.pr_view_stdout({
      repo = repo,
      number = pr_number,
      head = "branch",
      head_sha = "abc123",
      updated_at = cached_updated_at,
    }), cached_updated_at)
    seam.mock_pr_read_forms(t, {
      repo = repo,
      number = pr_number,
      head = "branch",
      head_sha = "def456",
      updated_at = current_updated_at,
      register_all_views = true,
      times = 1,
    })

    local probed = devloop_entity_view.fetch_pr_view_origin(repo, pr_number, nil, {
      consumer = "probe-reader",
    })

    t.eq(probed.exit_code, 0)
    t.is_true(probed.stdout:find('"def456"', 1, true) ~= nil)
    t.eq(count_calls(view_command), 0)
    t.eq(count_exact_calls(rest_command), 1)
    t.eq(count_exact_calls(comments_command), 1)
    t.eq(count_calls(probe_command), 1)
  end,

  test_observe_cache_miss_rest_pr_shape_preserves_mergeability_fields = function()
    local repo = "owner/rest-shape"
    local pr_number = 9
    seam.mock_pr_read_forms(t, {
      repo = repo,
      number = pr_number,
      head = "feature/rest-shape",
      head_sha = "cafebabe",
      base_branch = "dev",
      state = "OPEN",
      updated_at = "2026-06-03T01:02:04Z",
      mergeable = "CONFLICTING",
      mergeable_state = "dirty",
      labels = { "fkst-dev:reviewing" },
      comments = {
        { id = 9001, body = "marker", author_login = "fkst-test-bot" },
      },
      register_all_views = true,
      times = 1,
    })

    local result = devloop_entity_view.fetch_pr_view_origin(repo, pr_number, "2026-06-03T01:02:04Z", {
      consumer = "observe_pr",
    })
    local decoded = decode(result.stdout)

    t.eq(result.exit_code, 0)
    t.eq(decoded.headRefName, "feature/rest-shape")
    t.eq(decoded.headRefOid, "cafebabe")
    t.eq(decoded.baseRefName, "dev")
    t.eq(decoded.state, "OPEN")
    t.eq(decoded.updatedAt, "2026-06-03T01:02:04Z")
    t.eq(decoded.headRepository.nameWithOwner, repo)
    t.eq(decoded.headRepositoryOwner.login, "owner")
    t.eq(decoded.isCrossRepository, false)
    t.eq(decoded.mergeable, "CONFLICTING")
    t.eq(decoded.mergeStateStatus, "DIRTY")
    t.eq(decoded.comments[1].id, 9001)
    t.eq(decoded.comments[1].body, "marker")
    t.eq(decoded.comments[1].author.login, "fkst-test-bot")
  end,
}
