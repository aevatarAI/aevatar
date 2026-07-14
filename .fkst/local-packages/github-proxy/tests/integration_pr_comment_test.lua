local h = require("tests.proxy_integration_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_pr_comment_view = h.mock_pr_comment_view
local mock_pr_comment_write = h.mock_pr_comment_write
local json_string = h.json_string
local count_calls = h.count_calls
local capture_comment_department_logs = h.capture_comment_department_logs
local pr_comment_create = "gh api --method POST repos/owner/x/issues/7/comments"

local function event(extra)
  local payload = {
    schema = "github-proxy.v1",
    repo = "owner/x",
    pr_number = 7,
    body = "PR-local review note",
    dedup_key = "review-result/comment/owner/x/7/v1",
    source_ref = {
      kind = "external",
      ref = "owner/x#pr/7",
    },
  }
  for key, value in pairs(extra or {}) do
    payload[key] = value
  end
  return {
    queue = "github_pr_comment_request",
    payload = payload,
  }
end

return {
  test_pr_comment_request_dry_run_logs_structured_outbound = function()
    local logs, write_requests = capture_comment_department_logs(
      "departments/github_pr_comment/main.lua",
      event(),
      ""
    )

    t.eq(write_requests, 1)
    t.eq(logs[1], "github-proxy dept=github_pr_comment tag=OUTBOUND mode=dry-run repo=owner/x pr=7 dedup_key=review-result/comment/owner/x/7/v1 reason=FKST_GITHUB_WRITE!=1")
  end,

  test_pr_comment_request_real_write_logs_structured_outbound = function()
    local logs, write_requests = capture_comment_department_logs(
      "departments/github_pr_comment/main.lua",
      event(),
      "1"
    )

    t.eq(write_requests, 1)
    t.eq(logs[1], "github-proxy dept=github_pr_comment tag=OUTBOUND mode=real repo=owner/x pr=7 dedup_key=review-result/comment/owner/x/7/v1")
  end,

  test_pr_comment_request_dry_run_does_not_view_or_write = function()
    mock_write_env("")

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("pr-comment-dry-run"))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 0)
    t.eq(count_calls("gh pr comment"), 0)
    t.eq(count_calls(pr_comment_create), 0)
  end,

  test_pr_comment_request_missing_required_fields_fail_closed = function()
    mock_write_env("1")
    mock_bot_env()

    local result = t.run_department("departments/github_pr_comment/main.lua", {
      queue = "github_pr_comment_request",
      payload = {
        schema = "github-proxy.v1",
        repo = "owner/x",
        body = "missing pr number",
        dedup_key = "missing/pr-number",
        source_ref = {
          kind = "external",
          ref = "owner/x#pr/7",
        },
      },
    }, opts("pr-comment-missing-pr", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 0)
    t.eq(count_calls("gh pr comment"), 0)
    t.eq(count_calls(pr_comment_create), 0)
  end,

  test_pr_comment_request_trusted_dedup_skips_write = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        id = "IC_graphql_existing",
        body = "already wrote\n" .. core.comment_marker("review-result/comment/owner/x/7/v1"),
        author = "fkst-test-bot",
      },
    })
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("pr-comment-dedup", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls(pr_comment_create), 0)
    t.eq(#result.raises, 0)
  end,

  test_pr_comment_duplicate_delivery_uses_marker_to_skip_second_post = function()
    local request = event()
    local marker = core.comment_marker(request.payload.dedup_key)

    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view("existing PR comment")
    mock_pr_comment_write()

    local first = t.run_department("departments/github_pr_comment/main.lua", request, opts("pr-comment-replay-first", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)
    t.eq(first.raises[1].queue, "github_comment_written")
    t.eq(first.raises[1].payload.request_dedup_key, request.payload.dedup_key)

    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        id = "IC_graphql_replay_marker",
        body = "created comment\n" .. marker,
        author_login = "fkst-test-bot",
      },
    })
    mock_pr_comment_write()

    local second = t.run_department("departments/github_pr_comment/main.lua", request, opts("pr-comment-replay-second", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 2)
    t.eq(count_calls(pr_comment_create), 1)
  end,

  test_pr_comment_request_real_write_uses_rest_create = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view("existing PR comment")
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("pr-comment-write", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls(pr_comment_create), 1)
  end,

  test_pr_comment_existing_marker_handoff_emits_write_confirm = function()
    local dedup_key = "review-result/comment/owner/x/7/v1"
    local marker = core.comment_marker(dedup_key)
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        id = "IC_graphql_existing",
        body = "already wrote\n" .. marker,
        author_login = "fkst-test-bot",
      },
    })
    t.mock_command("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100", {
      stdout = '[[{"id":765432,"body":"already wrote\\n' .. json_string(marker) .. '","user":{"login":"fkst-test-bot"}}]]\n',
      stderr = "",
      exit_code = 0,
    })
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event({
      handoff = {
        kind = "generic-workflow.reviewing",
        proposal_id = "generic-workflow/issue/owner/x/42",
        version = "v1",
        pr_number = 7,
      },
    }), opts("pr-comment-existing-marker-handoff", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github_comment_written")
    t.eq(result.raises[1].payload.target, "pr")
    t.eq(result.raises[1].payload.comment_id, "765432")
    t.eq(result.raises[1].payload.dedup_key, dedup_key .. "/written/765432")
    t.eq(result.raises[1].payload.request_dedup_key, dedup_key)
    t.eq(count_calls(pr_comment_create), 0)
  end,
}
