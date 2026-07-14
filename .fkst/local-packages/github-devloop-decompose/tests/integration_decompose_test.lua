local devloop_base = require("devloop.base")
local h = require("tests.devloop_helpers")
local conv_reconcile = require("devloop.convergence.reconcile")
local conv_attempts = require("devloop.convergence.attempts")
local t = h.t
local core = h.core
local opts = h.opts
local decompose_event = h.decompose_event
local run_decompose = h.run_decompose
local mock_bot_env = h.mock_bot_env
local mock_issue_decompose = h.mock_issue_decompose
local find_raise = h.find_raise
local count_calls = h.count_calls
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local decompose_lib = require("devloop.decompose")
local m_builders = require("devloop.markers.builders")

local blocked_comments

local function mock_pr_view(event, comments, updated_at)
  local selected = {
    m_builders.pr_origin_marker(core, event.proposal_id, "42", "devloop-owner-repo-42-01HY", event.version, "dev"),
  }
  for _, comment in ipairs(comments) do
    table.insert(selected, comment)
  end
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = selected,
    head = "devloop-owner-repo-42-01HY",
    head_sha = "def456",
    base_branch = "dev",
    state = "OPEN",
    updated_at = updated_at or "2026-06-03T02:03:04Z",
  }, entity_read_mocks.pr_origin_selector, 1)
end

local function run_decompose_with_post_marker(event, run_opts, count)
  h.mock_default_issue_claim()
  h.take_pr_phase_comments()
  mock_pr_view(event, blocked_comments(event), "2026-06-03T02:03:04Z")
  mock_pr_view(event, blocked_comments(event), "2026-06-03T02:03:04Z")
  mock_pr_view(event, blocked_comments(event, {
    decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, count),
  }), "2026-06-03T02:03:05Z")
  return t.run_department("departments/decompose/main.lua", {
    queue = "devloop_decompose",
    payload = event,
  }, run_opts)
end

local function mock_write_env(value)
  t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
    stdout = value or "1",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_write_env_real()
  mock_write_env("1")
  mock_write_env("1")
  mock_write_env("1")
  mock_write_env("1")
end

local function mock_pr_comment_write(exit_code)
  t.mock_command("gh pr comment", {
    stdout = "",
    stderr = exit_code == 0 and "" or "forced pr comment failure",
    exit_code = exit_code or 0,
  })
end

local function mock_child_issue_list(event, indexes)
  local rendered = {}
  for _, index in ipairs(indexes or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"title":"Child %d","state":"OPEN","author":{"login":"fkst-test-bot"},"body":"%s","url":"https://github.example/owner/repo/issues/%d"}',
      100 + index,
      index,
      h.json_string(decompose_lib.decompose_child_marker(core, event.proposal_id, event.version, event.pr_number, index)),
      100 + index
    ))
  end
  t.mock_command(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_child_issue_list_repeated(event, indexes, times)
  for _ = 1, times do
    mock_child_issue_list(event, indexes)
  end
end

local function issue_created_marker(dedup_key, issue_number)
  return '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. tostring(dedup_key)
    .. '" issue="' .. tostring(issue_number)
    .. '" -->'
end

local function child_dedup_key(event, index)
  event.current_issue_body = "Original body"
  return core.build_issue_create_request("owner/repo", event, {
    title = "Child " .. tostring(index),
    body = "Child body " .. tostring(index),
  }, index).dedup_key
end

local function trusted_comment(body)
  return {
    body = body,
    author_login = devloop_base.trusted_bot_login(),
  }
end

blocked_comments = function(event, extra)
  local comments = {
    m_builders.pr_origin_marker(core, event.proposal_id, "42", "devloop-owner-repo-42-01HY", event.version, "dev"),
    core.state_marker(event.proposal_id, "blocked", event.version),
    conv_reconcile.fix_reconcile_marker(core, event.proposal_id, event.version, "drop"),
  }
  for _, comment in ipairs(extra or {}) do
    table.insert(comments, comment)
  end
  return comments
end

local function mock_decompose_codex(stdout)
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 2 do
    t.mock_command("test -d", { stdout = "", stderr = "", exit_code = 1 })
  end
  t.mock_command("install -d -m 0755", { stdout = "", stderr = "", exit_code = 0 })
  t.mock_command("mktemp -d", {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime/context/.bundle-tmp.decompose\n",
    stderr = "",
    exit_code = 0,
  })
  entity_read_mocks.mock_issue_view_raw_selector(t, {}, "title,body,updatedAt,labels,comments,state", {
    stdout = '{"title":"Original large issue","body":"Original body","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[{"name":"fkst-dev:blocked"}],"comments":[]}\n',
  })
  entity_read_mocks.mock_pr_view_raw_selector(t, {}, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels", {
    stdout = '{"title":"PR title","body":"PR body","headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"OPEN","updatedAt":"2026-06-04T01:02:03Z","comments":[],"labels":[]}\n',
  })
  t.mock_command("gh pr diff", {
    stdout = "diff --git a/file.lua b/file.lua\n+return true\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh pr diff '7' --repo 'owner/repo' --name-only", {
    stdout = "file.lua\n",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 6 do
    t.mock_command(" > ", { stdout = "", stderr = "", exit_code = 0 })
  end
  t.mock_command("python3 -c", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, 2 do
    t.mock_command("test -r", { stdout = "", stderr = "", exit_code = 0 })
  end
  for _ = 1, 8 do
    t.mock_command("wc -c < ", {
      stdout = "1\n",
      stderr = "",
      exit_code = 0,
    })
  end
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("mkdir -p", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("codex exec", {
    stdout = stdout,
    stderr = "",
    exit_code = 0,
  })
end

local function codex_calls()
  local calls = {}
  for _, call in ipairs(t.command_calls()) do
    if call.rendered:find("codex exec", 1, true) ~= nil then
      table.insert(calls, call)
    end
  end
  return calls
end

local function assert_decompose_judgment_call()
  local calls = codex_calls()
  t.eq(#calls, 1)
  t.is_true(calls[1].rendered:find(" -C ", 1, true) ~= nil)
  t.is_true(calls[1].rendered:find("/judgment-worktrees/github-devloop-decompose-", 1, true) ~= nil)
  t.is_nil(calls[1].rendered:find("/worktrees/", 1, true))
  t.is_true(calls[1].stdin:find("empty runtime scratch directory", 1, true) ~= nil)
  t.is_true(calls[1].stdin:find("Do not clone, checkout, fetch with git", 1, true) ~= nil)
  t.is_true(calls[1].stdin:find("diff.patch", 1, true) ~= nil)
end

local two_issue_json = [[{"issues":[{"title":"Extract a minimal retry helper","body":"Smaller scope: implement only the retry helper used by the blocked PR.\nNon-goals: do not change the whole workflow.\nAcceptance: helper tests pass."},{"title":"Wire retry helper into one call site","body":"Smaller scope: apply the helper to one review-gate path.\nNon-goals: do not rewrite unrelated states.\nAcceptance: focused integration test passes."}]}]]

return {
  test_decompose_writes_marker_and_raises_two_issue_create_requests = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Original large issue",
      body = "Original body that describes too much scope.",
    })
    mock_decompose_codex(two_issue_json)
    mock_pr_comment_write(0)
    mock_child_issue_list(event, {})

    local result = run_decompose_with_post_marker(event, opts("decompose-two-issues"), 2)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    local first = result.raises[1].payload
    local second = result.raises[2].payload
    t.eq(result.raises[1].queue, "github-proxy.github_issue_create_request")
    t.eq(result.raises[2].queue, "github-proxy.github_issue_create_request")
    t.eq(first.title, "Extract a minimal retry helper")
    t.eq(second.title, "Wire retry helper into one call site")
    t.is_true(first.dedup_key:find("decompose/github-devloop/issue/owner/repo/42/", 1, true) ~= nil)
    t.eq(first.parent_comment_target.repo, "owner/repo")
    t.eq(first.parent_comment_target.pr_number, 7)
    t.is_true(first.body:find("Parent PR: #7", 1, true) ~= nil)
    t.is_true(first.body:find("decompose-child:v1", 1, true) ~= nil)
    t.is_true(first.body:find('decompose-lineage:v1 root="github-devloop/issue/owner/repo/42" depth="1"', 1, true) ~= nil)
    t.eq(#first.labels, 0)
    t.eq(count_calls("codex exec"), 1)
    t.eq(count_calls(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id)), 1)
    assert_decompose_judgment_call()
  end,

  test_decompose_idempotent_skips_when_marker_visible = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event, {
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 1),
      issue_created_marker(child_dedup_key(event, 1), "101"),
    }))
    mock_child_issue_list(event, { 1 })

    local result = run_decompose(event, opts("decompose-idempotent"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
    t.eq(count_calls("codex exec"), 0)
    t.eq(count_calls(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id)), 1)
  end,

  test_decompose_marker_visible_reraises_missing_children_despite_stale_created_marker = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    event.current_issue_body = "Original body"
    local stale_dedup = core.build_issue_create_request("owner/repo", event, {
      title = "Extract a minimal retry helper",
      body = "Smaller scope: implement only the retry helper used by the blocked PR.\nNon-goals: do not change the whole workflow.\nAcceptance: helper tests pass.",
    }, 1).dedup_key
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Original large issue",
      body = "Original body that describes too much scope.",
    })
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event, {
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 2),
      issue_created_marker(stale_dedup, "101"),
    }))
    mock_pr_view(event, blocked_comments(event, {
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 2),
      issue_created_marker(stale_dedup, "101"),
    }))
    mock_child_issue_list_repeated(event, {}, 4)
    mock_decompose_codex(two_issue_json)

    local result = run_decompose(event, opts("decompose-idempotent-heal-zero"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    t.eq(result.raises[1].payload.title, "Extract a minimal retry helper")
    t.eq(result.raises[2].payload.title, "Wire retry helper into one call site")
    t.eq(count_calls("gh pr comment"), 0)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_decompose_marker_visible_reraises_only_partial_missing_child = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Original large issue",
      body = "Original body that describes too much scope.",
    })
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event, {
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 3),
    }))
    mock_pr_view(event, blocked_comments(event, {
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 3),
    }))
    mock_child_issue_list_repeated(event, { 1, 3 }, 3)
    mock_decompose_codex([[{"issues":[{"title":"One","body":"Smaller scope: one.\nNon-goals: none.\nAcceptance: one."},{"title":"Two","body":"Smaller scope: two.\nNon-goals: none.\nAcceptance: two."},{"title":"Three","body":"Smaller scope: three.\nNon-goals: none.\nAcceptance: three."}]}]])

    local result = run_decompose(event, opts("decompose-idempotent-heal-partial"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].payload.title, "Two")
    t.is_true(result.raises[1].payload.body:find('index="2"', 1, true) ~= nil)
    t.eq(count_calls("gh pr comment"), 0)
  end,

  test_decompose_marker_write_failure_does_not_raise_creates = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Original large issue",
      body = "Original body that describes too much scope.",
    })
    mock_pr_view(event, blocked_comments(event))
    mock_decompose_codex(two_issue_json)
    mock_pr_comment_write(1)

    local result = run_decompose(event, opts("decompose-marker-write-fails"))

    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
    t.eq(count_calls("gh pr comment"), 1)
    t.eq(count_calls("codex exec"), 1)
  end,

  test_decompose_writes_marker_before_raising_creates = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Original large issue",
      body = "Original body that describes too much scope.",
    })
    mock_decompose_codex(two_issue_json)
    mock_pr_comment_write(0)
    mock_child_issue_list_repeated(event, {}, 2)

    local result = run_decompose_with_post_marker(event, opts("decompose-marker-before-create"), 2)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    t.eq(count_calls("gh pr comment"), 1)
  end,

  test_decompose_depth_cap_skips_lineage_child = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event), {
      title = "Child issue",
      body = "Child body.\n\n" .. decompose_lib.decompose_lineage_marker(core, event.proposal_id, 1),
    })
    mock_pr_view(event, blocked_comments(event))

    local result = run_decompose(event, opts("decompose-depth-cap"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github-proxy.github_pr_comment_request")
    t.is_true(result.raises[1].payload.body:find("fkst:github-devloop:decompose-exhausted:v1", 1, true) ~= nil)
    t.is_true(result.raises[1].payload.body:find('reason_class="decompose-output-obligation-timeout"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)

  end,

  test_decompose_depth_cap_exhausted_marker_is_idempotent = function()
    local event = decompose_event()
    local exhausted_marker = conv_attempts.decompose_exhausted_marker(core, event.proposal_id, event.version, 1, event.source_ref)
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event, { trusted_comment(exhausted_marker) }))
    mock_pr_view(event, blocked_comments(event, { trusted_comment(exhausted_marker) }))

    local result = run_decompose(event, opts("decompose-depth-cap-idempotent"))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 0)
  end,

  test_decompose_parse_fail_retries_then_falls_back = function()
    local event = decompose_event()
    local run_opts = opts("decompose-parse-fail-retry")
    mock_bot_env()
    mock_write_env_real()
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event))
    h.take_pr_phase_comments()
    mock_decompose_codex("not json")
    mock_pr_view(event, blocked_comments(event))
    mock_pr_view(event, blocked_comments(event))

    h.mock_default_issue_claim()
    local first = t.run_department("departments/decompose/main.lua", {
      queue = "devloop_decompose",
      payload = event,
    }, run_opts)
    t.eq(first.exit_code, 1)
    t.eq(#first.raises, 0)

    mock_bot_env()
    mock_write_env_real()
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_decompose_codex("not json")
    mock_pr_comment_write(0)
    mock_child_issue_list(event, {})

    local second = run_decompose_with_post_marker(event, run_opts, 1)
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    local create = find_raise(second.raises, "github-proxy.github_issue_create_request").payload
    t.is_true(create.title:find("Rework blocked PR #7", 1, true) ~= nil)
    t.eq(count_calls("codex exec"), 2)
  end,

  test_decompose_caps_issue_plan_at_three = function()
    local event = decompose_event()
    mock_bot_env()
    mock_write_env_real()
    h.set_pr_phase_comments({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_issue_decompose({ "fkst-dev:blocked" }, blocked_comments(event))
    mock_decompose_codex([[{"issues":[{"title":"One","body":"Smaller scope: one.\nNon-goals: no extra.\nAcceptance: one."},{"title":"Two","body":"Smaller scope: two.\nNon-goals: no extra.\nAcceptance: two."},{"title":"Three","body":"Smaller scope: three.\nNon-goals: no extra.\nAcceptance: three."},{"title":"Four","body":"Smaller scope: four.\nNon-goals: no extra.\nAcceptance: four."}]}]])
    mock_pr_comment_write(0)
    mock_child_issue_list_repeated(event, {}, 3)

    local result = run_decompose_with_post_marker(event, opts("decompose-cap-three"), 3)

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 3)
    t.eq(result.raises[3].payload.title, "Three")
  end,
}
