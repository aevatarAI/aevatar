local h = require("tests.proxy_integration_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local mock_repo_env = h.mock_repo_env
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_comment_view = h.mock_comment_view
local mock_comment_write = h.mock_comment_write
local json_string = h.json_string
local calls_matching = h.calls_matching
local count_calls = h.count_calls
local capture_comment_department_logs = h.capture_comment_department_logs

local issue_comment_create = "gh api --method POST repos/owner/x/issues/42/comments"

return {
  test_outbound_dry_run_write_and_marker_idempotency = function()
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "fkst reply",
        dedup_key = "reply-42",
      },
    }

    local dry_logs, dry_write_requests = capture_comment_department_logs(
      "departments/github_comment/main.lua",
      event,
      ""
    )
    t.eq(dry_write_requests, 1)
    t.eq(dry_logs[1], "github-proxy dept=github_comment tag=OUTBOUND mode=dry-run repo=owner/x issue=42 dedup_key=reply-42 reason=FKST_GITHUB_WRITE!=1")

    local real_logs, real_write_requests = capture_comment_department_logs(
      "departments/github_comment/main.lua",
      event,
      "1"
    )
    t.eq(real_write_requests, 1)
    t.eq(real_logs[1], "github-proxy dept=github_comment tag=OUTBOUND mode=real repo=owner/x issue=42 dedup_key=reply-42")

    mock_repo_env()
    mock_write_env("")
    local dry = t.run_department("departments/github_comment/main.lua", event, opts("comment-dry-run"))
    t.eq(dry.exit_code, 0)
    t.eq(count_calls("gh issue comment"), 0)
    t.eq(count_calls(issue_comment_create), 0)

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment")
    mock_comment_write()
    local write = t.run_department("departments/github_comment/main.lua", event, opts("comment-write", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(write.exit_code, 0)

    local written = file.read("/tmp/fkst-github-proxy-comment-owner_x-issue-42.md")
    t.is_true(written:find("fkst reply", 1, true) ~= nil)
    t.is_true(written:find("<!-- fkst:github-proxy:comment:reply-42 -->", 1, true) ~= nil)

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view("existing comment <!-- fkst:github-proxy:comment:reply-42 -->")
    t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
      stdout = '[[{"id":123456,"body":"existing comment <!-- fkst:github-proxy:comment:reply-42 -->","user":{"login":"fkst-test-bot"}}]]\n',
      stderr = "",
      exit_code = 0,
    })
    local again = t.run_department("departments/github_comment/main.lua", event, opts("comment-write", {
      FKST_GITHUB_WRITE = "1",
    }))
    t.eq(again.exit_code, 0)

    local comment_calls = calls_matching(issue_comment_create)
    t.eq(#comment_calls, 1)
    t.is_true(comment_calls[1].rendered:find(issue_comment_create, 1, true) ~= nil)
    t.eq(comment_calls[1].rendered:find("github.com", 1, true), nil)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100"), 2)
  end,

  test_existing_comment_replay_emits_rest_comment_id = function()
    local dedup_key = "reply-42"
    local marker = core.comment_marker(dedup_key)
    local event = {
      queue = "github_issue_comment_request",
      payload = {
        repo = "owner/x",
        issue_number = 42,
        body = "fkst reply",
        dedup_key = dedup_key,
        handoff = {
          kind = "generic-workflow.ready",
          proposal_id = "generic-workflow/issue/owner/x/42",
          version = "v1",
          source_ref = {
            kind = "external",
            ref = "owner/x#issue/42",
          },
        },
      },
    }

    mock_repo_env()
    mock_write_env("1")
    mock_bot_env()
    mock_comment_view({
      {
        id = "IC_graphql_node_id",
        body = "existing comment " .. marker,
        author_login = "fkst-test-bot",
      },
    })
    t.mock_command("gh api --paginate --slurp repos/owner/x/issues/42/comments?per_page=100", {
      stdout = '[[{"id":123456,"body":"existing comment ' .. json_string(marker) .. '","user":{"login":"fkst-test-bot"}}]]\n',
      stderr = "",
      exit_code = 0,
    })

    local result = t.run_department("departments/github_comment/main.lua", event, opts("comment-existing-marker-rest-id", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    t.eq(result.raises[1].queue, "github_comment_written")
    t.eq(result.raises[1].payload.comment_id, "123456")
    t.eq(result.raises[1].payload.dedup_key, dedup_key .. "/written/123456")
    t.eq(count_calls(issue_comment_create), 0)
  end,
}
