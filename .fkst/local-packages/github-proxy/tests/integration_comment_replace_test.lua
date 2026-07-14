local h = require("tests.proxy_integration_helpers")
local t = h.t
local core = h.core
local opts = h.opts
local mock_write_env = h.mock_write_env
local mock_bot_env = h.mock_bot_env
local mock_pr_comment_view = h.mock_pr_comment_view
local mock_pr_comment_write = h.mock_pr_comment_write
local count_calls = h.count_calls
local pr_comment_create = "gh api --method POST repos/owner/x/issues/7/comments"

local function event(extra)
  local payload = {
    schema = "github-proxy.v1",
    repo = "owner/x",
    pr_number = 7,
    body = "Working: fix\n\n<!-- fkst:generic-workflow:work-card:v1 proposal=\"generic-workflow/issue/owner/x/42\" -->",
    dedup_key = "work-card/generic-workflow/issue/owner/x/42/fix/v1/running",
    replace_marker = "<!-- fkst:generic-workflow:work-card:v1 proposal=\"generic-workflow/issue/owner/x/42\" -->",
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

local function mock_comment_edit()
  t.mock_command("gh api --method PATCH repos/owner/x/issues/comments/123456 --field body=/tmp/fkst-github-proxy-comment-owner_x-pr-7.md", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_comment_edit_result(comment_id, exit_code, stderr)
  t.mock_command("gh api --method PATCH repos/owner/x/issues/comments/" .. tostring(comment_id) .. " --field body=/tmp/fkst-github-proxy-comment-owner_x-pr-7.md", {
    stdout = "",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function timeout_attempt_body(round)
  return "generic-workflow timeout redrive attempt: implementing " .. tostring(round)
    .. "\n\n"
    .. '<!-- fkst:generic-workflow:timeout-attempt:v2 proposal="generic-workflow/issue/owner/x/42" state="implementing" liveness_class_id="producing_revision" generation_key="gen-1" round="' .. tostring(round) .. '" dedup="timeout-attempt:v2:implementing/producing_revision/gen-1/' .. tostring(round) .. '" source_ref_kind="external" source_ref="owner/x#issue/42" -->'
    .. "\n"
    .. '<!-- fkst:generic-workflow:timeout-attempt:latest:v1 proposal="generic-workflow/issue/owner/x/42" state="implementing" liveness_class_id="producing_revision" generation_key="gen-1" -->'
    .. "\n⟦AI:FKST⟧"
end

return {
  test_replace_marker_edits_existing_trusted_comment = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        id = "IC_kwDOSwWu288AAAABF40Vmg",
        databaseId = 123456,
        body = "old card\n" .. event().payload.replace_marker,
        author_login = "fkst-test-bot",
      },
    })
    mock_comment_edit()
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("comment-replace-edit", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh api --method PATCH repos/owner/x/issues/comments/123456 --field body=@"), 1)
    t.eq(count_calls("issues/comments/IC_kwDOSwWu288AAAABF40Vmg"), 0)
    t.eq(count_calls(pr_comment_create), 0)
  end,

  test_replace_marker_creates_when_card_is_absent = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({})
    mock_comment_edit()
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("comment-replace-create", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh api --method PATCH"), 0)
    t.eq(count_calls(pr_comment_create), 1)
  end,

  test_replace_marker_falls_back_to_create_when_edit_target_is_stale = function()
    t.eq(core.stale_comment_target_error_class(), "stale-comment-target")
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        databaseId = 123456,
        body = "old card\n" .. event().payload.replace_marker,
        author_login = "fkst-test-bot",
      },
    })
    mock_comment_edit_result(123456, 1, "gh: Not Found")
    mock_pr_comment_view({})
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("comment-replace-stale-edit-create", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 2)
    t.eq(count_calls("gh api --method PATCH repos/owner/x/issues/comments/123456 --field body=@"), 1)
    t.eq(count_calls(pr_comment_create), 1)
  end,

  test_replace_marker_rereads_once_when_edit_404_then_edits_refreshed_comment = function()
    mock_write_env("1")
    mock_bot_env()
    mock_pr_comment_view({
      {
        databaseId = 123456,
        body = "old card\n" .. event().payload.replace_marker,
        author_login = "fkst-test-bot",
      },
    })
    mock_comment_edit_result(123456, 1, "HTTP 404: Not Found")
    mock_pr_comment_view({
      {
        databaseId = 654321,
        body = "new card\n" .. event().payload.replace_marker,
        author_login = "fkst-test-bot",
      },
    })
    mock_comment_edit_result(654321)
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event(), opts("comment-replace-404-reread-edit", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 2)
    t.eq(count_calls("gh api --method PATCH repos/owner/x/issues/comments/123456 --field body=@"), 1)
    t.eq(count_calls("gh api --method PATCH repos/owner/x/issues/comments/654321 --field body=@"), 1)
    t.eq(count_calls(pr_comment_create), 0)
  end,

  test_parse_issue_comments_preserves_comment_id = function()
    local comments = core.parse_issue_comments('{"comments":[{"id":"IC_kwabc","databaseId":999,"body":"hello","author":{"login":"fkst-test-bot"}}]}')
    t.eq(comments[1].id, "999")
    t.eq(core.trusted_comment_with_fragment(comments, "hello", "fkst-test-bot").id, "999")
  end,

  test_timeout_attempt_replace_skips_stale_lower_round = function()
    mock_write_env("1")
    mock_bot_env()
    local replace_marker = '<!-- fkst:generic-workflow:timeout-attempt:latest:v1 proposal="generic-workflow/issue/owner/x/42" state="implementing" liveness_class_id="producing_revision" generation_key="gen-1" -->'
    mock_pr_comment_view({
      {
        databaseId = 123456,
        body = timeout_attempt_body(3),
        author_login = "fkst-test-bot",
      },
    })
    mock_pr_comment_write()

    local result = t.run_department("departments/github_pr_comment/main.lua", event({
      body = timeout_attempt_body(2),
      dedup_key = "timeout-attempt:v2/generic-workflow/issue/owner/x/42/implementing/producing_revision/gen-1/2",
      replace_marker = replace_marker,
    }), opts("comment-replace-timeout-attempt-stale", {
      FKST_GITHUB_WRITE = "1",
    }))

    t.eq(result.exit_code, 0)
    t.eq(count_calls("gh api --paginate --slurp repos/owner/x/issues/7/comments?per_page=100"), 1)
    t.eq(count_calls("gh api --method PATCH"), 0)
    t.eq(count_calls(pr_comment_create), 0)
    t.eq(#result.raises, 0)
  end,
}
