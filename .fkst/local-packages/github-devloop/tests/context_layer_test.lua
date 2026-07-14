local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_helpers")
local fixtures = require("tests.production_fixture_helpers")
local v_validate_proposal = require("devloop.validators.validate_proposal")
require("tests.board_digest_probe_helpers")
local core = h.core
local t = h.t
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")

local function assert_language_preamble(prompt)
  t.is_true(prompt:find("Write all output in English; quote code identifiers and cited originals verbatim.", 1, true) ~= nil)
end

local function assert_judge_preamble_slots(prompt)
  assert_language_preamble(prompt)
  t.is_true(prompt:find("Before judging, identify the established theory or industry best practice governing this problem class", 1, true) ~= nil)
  t.is_true(prompt:find("grounds for rejection or narrowing", 1, true) ~= nil)
  t.is_nil(prompt:find("Before acting, identify the established theory or industry best practice governing this change", 1, true))
end

local function assert_actor_preamble_slots(prompt)
  assert_language_preamble(prompt)
  t.is_true(prompt:find("Before acting, identify the established theory or industry best practice governing this change", 1, true) ~= nil)
  t.is_true(prompt:find("surface that blocker explicitly instead of silently improvising or claiming success", 1, true) ~= nil)
  t.is_nil(prompt:find("grounds for rejection or narrowing", 1, true))
end

local function assert_github_entity_history(prompt)
  t.is_true(prompt:find("Before judging, read the local context files named below.", 1, true) ~= nil)
  t.is_nil(prompt:find("gh issue view --comments / gh pr view --comments", 1, true))
end

local function prompt_issue()
  return {
    title = "Implement decision recorder",
    body = "Issue body",
    comments = {
      { body = "Previous note", author_login = "fkst-test-bot" },
    },
  }
end

local function issue_list_json(count)
  local items = {}
  for n = 1, count do
    table.insert(items, string.format(
      '{"number":%d,"title":"Issue title number %d that is intentionally long enough to trim after sixty characters","labels":[{"name":"fkst-dev:thinking"}]}',
      n,
      n
    ))
  end
  return "[" .. table.concat(items, ",") .. "]"
end

local function pr_list_json(count)
  local items = {}
  for n = 1, count do
    table.insert(items, string.format(
      '{"number":%d,"title":"PR title number %d","labels":[{"name":"fkst-dev:reviewing"}]}',
      n + 100,
      n
    ))
  end
  return "[" .. table.concat(items, ",") .. "]"
end

local function encode_json_string(value)
  return tostring(value or ""):gsub("\\", "\\\\"):gsub('"', '\\"'):gsub("\n", "\\n")
end

local function closed_issue_list_json(items)
  local rendered = {}
  for _, item in ipairs(items or {}) do
    local labels = {}
    for _, label in ipairs(item.labels or {}) do
      table.insert(labels, '{"name":"' .. encode_json_string(label) .. '"}')
    end
    table.insert(rendered, string.format(
      '{"number":%d,"title":"%s","closedAt":"%s","labels":[%s]}',
      item.number,
      encode_json_string(item.title or "Closed issue"),
      encode_json_string(item.closed_at or "2026-06-01T01:02:03Z"),
      table.concat(labels, ",")
    ))
  end
  return "[" .. table.concat(rendered, ",") .. "]"
end

local function assert_valid_utf8(value)
  local ok, len = pcall(utf8.len, tostring(value or ""))
  t.is_true(ok and len ~= nil)
end

local function mock_board_lists(issue_count, pr_count, repo)
  repo = repo or "owner/repo"
  entity_read_mocks.mock_issue_board_digest_list_raw(t, repo, {
    stdout = issue_list_json(issue_count),
  })
  entity_read_mocks.mock_pr_board_digest_list_raw(t, repo, {
    stdout = pr_list_json(pr_count),
  })
  entity_read_mocks.mock_issue_list_raw_command(t, core.gh_issue_list_recent_closed_cmd(repo, 30), {
    stdout = closed_issue_list_json({
      { number = 80, title = "Closed recurring widget sync retry fix", labels = { "error-class:retry", "fingerprint:widget-sync" } },
      { number = 81, title = "Closed widget sync backoff patch", labels = { "fingerprint:widget-sync" } },
    }),
  })
end

local function mock_board_lists_closed_failure(issue_count, pr_count, repo)
  repo = repo or "owner/repo"
  entity_read_mocks.mock_issue_board_digest_list_raw(t, repo, {
    stdout = issue_list_json(issue_count),
  })
  entity_read_mocks.mock_pr_board_digest_list_raw(t, repo, {
    stdout = pr_list_json(pr_count),
  })
  entity_read_mocks.mock_issue_list_raw_command(t, core.gh_issue_list_recent_closed_cmd(repo, 30), {
    stdout = "",
    stderr = "closed issue query failed",
    exit_code = 1,
  })
end

local function mock_board_title(title, repo)
  repo = repo or "owner/repo"
  entity_read_mocks.mock_issue_board_digest_list_raw(t, repo, {
    stdout = '[{"number":1,"title":"' .. encode_json_string(title) .. '","labels":[{"name":"fkst-dev:thinking"}]}]',
  })
  entity_read_mocks.mock_pr_board_digest_list_raw(t, repo, {
    stdout = "[]",
  })
  entity_read_mocks.mock_issue_list_raw_command(t, core.gh_issue_list_recent_closed_cmd(repo, 30), {
    stdout = "[]",
  })
end

local function count_calls(needle)
  return gh_argv.count_calls(t, needle)
end

local function find_raise(raises, queue)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue then
      return raised
    end
  end
  return nil
end

local function run_probe(payload, opts)
  return t.run_department("departments/test_board_digest_probe/main.lua", {
    queue = "board_digest_probe",
    payload = payload,
  }, opts)
end

local function probe_result(result)
  local raised = find_raise(result.raises, "board_digest_result")
  return raised and raised.payload or nil
end

return {
  test_devloop_prompt_preamble_language_env = function()
    t.eq(devloop_base.read_env_command("FKST_OUTPUT_LANG"), 'printf %s "$FKST_OUTPUT_LANG"')
    t.eq(core.output_language(function(_cmd)
      return { stdout = "zh", stderr = "", exit_code = 0 }
    end), "zh")
    t.eq(core.output_language(function(_cmd)
      return { stdout = "unknown", stderr = "", exit_code = 0 }
    end), "en")
    t.is_true(core.prompt_preamble(function(_cmd)
      return { stdout = "zh", stderr = "", exit_code = 0 }
    end):find("Write all prose output in Simplified Chinese", 1, true) ~= nil)
  end,

  test_devloop_issue_prompt_includes_scoped_github_history = function()
    local issue = prompt_issue()
    local manifest = "Read these local files for your complete context.\nIssue JSON: /tmp/ctx/issue.json\nBoard digest: /tmp/ctx/board.txt\nPR diff patch: /tmp/ctx/diff.patch"
    local actor_prompts = {
      core.build_implement_prompt("github-devloop/issue/owner/repo/42", issue, "Approved framing.", manifest),
    }

    for _, prompt in ipairs(actor_prompts) do
      assert_actor_preamble_slots(prompt)
      assert_github_entity_history(prompt)
      t.is_true(prompt:find("/tmp/ctx/issue.json", 1, true) ~= nil)
      t.is_nil(prompt:find("gh issue", 1, true))
      t.is_nil(prompt:find("gh pr", 1, true))
      t.is_nil(prompt:find("gh api", 1, true))
      t.is_nil(prompt:find("empty runtime scratch directory", 1, true))
      t.is_nil(prompt:find("{{", 1, true))
    end
  end,

  test_board_digest_in_thinking_proposal_is_bounded_and_cached_per_tick = function()
    h.mock_bot_env()
    h.mock_issue_state({ "fkst-dev:enabled" }, "OPEN", {})
    mock_board_lists(55, 10)

    local event = {
      queue = "github-proxy.github_entity_changed",
      ts = "2026-06-10T01:02:03Z",
      payload = h.issue(),
    }
    local opts = h.opts("board-digest-cache")
    local first = h.run_observe(event.payload, opts)
    h.mock_issue_state({ "fkst-dev:enabled" }, "OPEN", {})
    local second = h.run_observe(event.payload, opts)
    local proposal = find_raise(first.raises, "consensus.proposal").payload

    t.is_true(proposal.content_fetch:find("runtime-cache:", 1, true) == 1)
    t.is_true(proposal.body:find("GitHub issue", 1, true) ~= nil)
    t.is_nil(proposal.body:find("#101 ", 1, true))
    t.eq(count_calls(h.argv_rendered(core.gh_issue_list_board_digest_cmd("owner/repo"))), 0)
    t.eq(count_calls(h.argv_rendered(core.gh_pr_list_board_digest_cmd("owner/repo"))), 0)
    t.eq(count_calls("gh issue list --repo owner/repo --state closed --limit 30 --json number,title,closedAt,labels"), 0)
    t.eq(find_raise(second.raises, "consensus.proposal").payload.body, proposal.body)
  end,

  test_board_digest_cache_key_includes_repo = function()
    mock_board_lists(1, 0, "owner/repo")
    mock_board_lists(2, 0, "other/repo")
    local run_opts = h.opts("board-digest-cross-repo")

    local first = probe_result(run_probe({
      mode = "block",
      repo = "owner/repo",
      tick = "2026-06-10T02:02:03Z",
    }, run_opts)).body
    local second = probe_result(run_probe({
      mode = "block",
      repo = "other/repo",
      tick = "2026-06-10T02:02:03Z",
    }, run_opts)).body

    t.is_true(first:find("#1 [fkst-dev:thinking] Issue title number 1", 1, true) ~= nil)
    t.is_true(first:find("Recent closed issues for recurrence judgment:", 1, true) ~= nil)
    t.is_true(first:find("#80 [closed] Closed recurring widget sync retry fix", 1, true) ~= nil)
    t.is_true(first:find("fingerprint:widget-sync", 1, true) ~= nil)
    t.is_nil(first:find("#2 [fkst-dev:thinking] Issue title number 2", 1, true))
    t.is_true(second:find("#2 [fkst-dev:thinking] Issue title number 2", 1, true) ~= nil)
    t.eq(count_calls(h.argv_rendered(core.gh_issue_list_board_digest_cmd("owner/repo"))), 1)
    t.eq(count_calls("gh issue list --repo owner/repo --state closed --limit 30 --json number,title,closedAt,labels"), 1)
    t.eq(count_calls(h.argv_rendered(core.gh_issue_list_board_digest_cmd("other/repo"))), 1)
    t.eq(count_calls("gh issue list --repo other/repo --state closed --limit 30 --json number,title,closedAt,labels"), 1)
  end,

  test_board_digest_feeds_existing_context_path_from_local_board_command = function()
    t.mock_command('printf %s "$FKST_DEVLOOP_BOARD_CMD"', {
      stdout = "scripts/run.sh board --ttl 60",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("scripts/run.sh board --ttl 60", {
      stdout = "fkst-dev local board\nsource=observe cached_at=2026-06-10T02:02:03Z durable_root=.fkst/durable\n",
      stderr = "",
      exit_code = 0,
    })

    local body = probe_result(run_probe({
      mode = "block",
      repo = "owner/repo",
      tick = "2026-06-10T02:02:03Z",
    }, h.opts("board-digest-feed-through"))).body

    t.is_true(body:find("Board feed-through from FKST_DEVLOOP_BOARD_CMD:", 1, true) ~= nil)
    t.is_true(body:find("fkst-dev local board", 1, true) ~= nil)
    t.is_true(body:find("source=observe", 1, true) ~= nil)
    t.eq(count_calls(h.argv_rendered(core.gh_issue_list_board_digest_cmd("owner/repo"))), 0)
    t.eq(count_calls(h.argv_rendered(core.gh_pr_list_board_digest_cmd("owner/repo"))), 0)
  end,

  test_board_digest_keeps_open_context_when_closed_digest_fetch_fails = function()
    mock_board_lists_closed_failure(2, 1)

    local result = probe_result(run_probe({
      mode = "block",
      repo = "owner/repo",
      tick = "2026-06-10T02:07:03Z",
    }, h.opts("board-digest-closed-fetch-fails"))).body

    t.is_true(result:find("Open items snapshot:", 1, true) ~= nil)
    t.is_true(result:find("#1 [fkst-dev:thinking] Issue title number 1", 1, true) ~= nil)
    t.is_true(result:find("#101 [fkst-dev:reviewing] PR title number 1", 1, true) ~= nil)
    t.is_true(result:find("Recent closed issues for recurrence judgment:", 1, true) ~= nil)
    t.is_true(result:find("(none fetched)", 1, true) ~= nil)
  end,

  test_truncate_utf8_handles_mixed_width_boundaries = function()
    local cjk = fixtures.cjk_char()
    local mixed = "ab" .. cjk .. "cd"
    local emoji = fixtures.emoji_char()

    t.eq(base_ids.truncate_utf8(mixed, 2), "ab")
    t.eq(base_ids.truncate_utf8(mixed, 3), "ab")
    t.eq(base_ids.truncate_utf8(mixed, 4), "ab")
    t.eq(base_ids.truncate_utf8(mixed, 5), "ab" .. cjk)
    t.eq(base_ids.truncate_utf8(mixed, 6), "ab" .. cjk .. "c")
    t.eq(base_ids.truncate_utf8("", 3), "")
    t.eq(base_ids.truncate_utf8(cjk, 2), "")
    t.eq(base_ids.truncate_utf8(emoji .. "x", 3), "")
    t.eq(base_ids.truncate_utf8("ab" .. emoji .. "x", 6), "ab" .. emoji)
    assert_valid_utf8(base_ids.truncate_utf8(mixed, 1))
    assert_valid_utf8(base_ids.truncate_utf8(mixed, 7))
    assert_valid_utf8(base_ids.truncate_utf8("ab" .. emoji .. "x", 5))
    assert_valid_utf8(base_ids.truncate_utf8("ab" .. emoji .. "x", 6))
  end,

  test_board_digest_title_truncation_keeps_utf8_valid_before_cache_set = function()
    local title = fixtures.board_digest_boundary_title()
    mock_board_title(title)

    local result = run_probe({
      mode = "block",
      repo = "owner/repo",
      tick = "2026-06-10T02:12:03Z",
    }, h.opts("board-digest-utf8-title"))

    t.eq(result.exit_code, 0)
    local body = probe_result(result).body
    assert_valid_utf8(body)
    t.is_true(body:find("#1 [fkst-dev:thinking] " .. string.rep("a", 59), 1, true) ~= nil)
    t.is_nil(body:find(fixtures.cjk_char(), 1, true))
  end,

  test_board_digest_overflow_truncates_optional_context = function()
    mock_board_lists(4, 0)
    local proposal = {
      schema = "consensus.proposal.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      body = string.rep("x", core._max_body_len - 24),
    }

    local result = probe_result(run_probe({
      mode = "append",
      proposal = proposal,
      repo = "owner/repo",
      tick = "2026-06-10T03:02:03Z",
    }, h.opts("board-digest-overflow"))).proposal

    t.eq(#result.body, core._max_body_len)
    t.is_true(result.body:find("BEGIN UNTRUSTED", 1, true) ~= nil)
  end,

  test_digest_injection_covers_remaining_proposal_entry_points = function()
    mock_board_lists(2, 1)
    local tick = "2026-06-10T04:02:03Z"
    local source_ref = { kind = "external", ref = "owner/repo#issue/42" }
    local pr_source_ref = { kind = "external", ref = "owner/repo#pr/7" }
    local current = {
      title = "Implement decision recorder",
      updated_at = "2026-06-03T01:02:03Z",
    }
    local converge = {
      narrowed_question = "Does the narrowed implementation keep the source_ref contract?",
      angle_digests = {
        { angle = "minimal", verdict = "approve", digest = "ok" },
      },
    }

    local run_opts = h.opts("board-digest-entry-points")
    local loop = probe_result(run_probe({
      mode = "board_loop",
      repo = "owner/repo",
      issue_number = "42",
      current = current,
      source_ref = source_ref,
      n = 2,
      converge = converge,
      tick = tick,
    }, run_opts)).proposal
    local review = probe_result(run_probe({
      mode = "board_review",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = 7,
      version = "version",
      head_sha = "abcdef123456",
      current = current,
      source_ref = pr_source_ref,
      tick = tick,
    }, run_opts)).proposal
    local review_loop = probe_result(run_probe({
      mode = "board_review_loop",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = 7,
      version = "version",
      head_sha = "abcdef123456",
      current = current,
      source_ref = pr_source_ref,
      n = 3,
      converge = converge,
      tick = tick,
    }, run_opts)).proposal

    for _, proposal in ipairs({ loop, review, review_loop }) do
      t.is_true(proposal.body:find("BEGIN UNTRUSTED ISSUE DATA", 1, true) ~= nil)
      t.is_true(proposal.body:find("Open items snapshot:", 1, true) ~= nil)
      t.is_true(proposal.body:find("Recent closed issues for recurrence judgment:", 1, true) ~= nil)
      t.is_true(v_validate_proposal.validate_proposal(core, proposal))
    end
    t.eq(loop.round, 2)
    t.eq(review_loop.round, 3)
    t.eq(count_calls(h.argv_rendered(core.gh_issue_list_board_digest_cmd("owner/repo"))), 1)
    t.eq(count_calls("gh issue list --repo owner/repo --state closed --limit 30 --json number,title,closedAt,labels"), 1)
  end,
}
