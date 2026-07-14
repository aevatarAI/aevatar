local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local forks = require("devloop.forks")
local t = h.t
local core = h.core
local ready = h.ready
local run_implement = h.run_implement
local opts = h.opts
local mock_issue_implement = h.mock_issue_implement
local count_calls = h.count_calls
local find_raise = h.find_raise

local original_issue = 1663
local canonical_issue = 1715

local function original_view_with_fork_ledger()
  local dedup_key = forks.fork_issue_dedup_key("owner/repo", original_issue)
  local marker = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key
    .. '" issue="' .. tostring(canonical_issue) .. '" -->'
  return '{"title":"Original","createdAt":"2026-06-03T01:00:00Z","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[{"body":"'
    .. marker:gsub('"', '\\"')
    .. '","author":{"login":"fkst-test-bot"}}],"assignees":[],"author":{"login":"human"}}\n'
end

local function original_view_with_peer_fork_ledger()
  local dedup_key = forks.fork_issue_dedup_key("owner/repo", original_issue)
  local marker = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key
    .. '" issue="' .. tostring(canonical_issue) .. '" -->'
  return '{"title":"Original","createdAt":"2026-06-03T01:00:00Z","updatedAt":"2026-06-03T01:02:03Z","state":"OPEN","labels":[],"comments":[{"body":"'
    .. marker:gsub('"', '\\"')
    .. '","author":{"login":"ElonSG"}}],"assignees":[],"author":{"login":"human"}}\n'
end

local function find_duplicate_comment(raises)
  return find_raise(raises, "github-proxy.github_issue_comment_request", function(payload)
    return tostring(payload.body or ""):find("Duplicate fork for owner/repo#" .. tostring(original_issue), 1, true) ~= nil
      and tostring(payload.body or ""):find("canonical fork is #" .. tostring(canonical_issue), 1, true) ~= nil
  end)
end

local function find_duplicate_label(raises)
  return find_raise(raises, "github-proxy.github_issue_label_request", function(payload)
    for _, label in ipairs(payload.add_labels or {}) do
      if label == "fkst:duplicate-fork" then
        return true
      end
    end
    return false
  end)
end

local function command_count_snapshot()
  return {
    codex = count_calls("codex exec"),
    worktree_list = count_calls("git worktree list"),
    git_c = count_calls("git -C"),
    issue_close = count_calls("gh issue close"),
  }
end

local function mock_managed_bot_logins(logins)
  t.mock_command('printf %s "$FKST_DEVLOOP_MANAGED_BOT_LOGINS"', {
    stdout = logins or "",
    stderr = "",
    exit_code = 0,
  })
end

return {
  test_noncanonical_fork_exits_before_implementation = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
      forks.fork_origin_marker("owner/repo", original_issue, "human", entity_lib.issue_source_ref("owner/repo", original_issue)),
    })
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", original_issue), {
      stdout = original_view_with_fork_ledger(),
      stderr = "",
      exit_code = 0,
    })

    local before = command_count_snapshot()
    local result = run_implement(event, opts("implement-duplicate-fork", {
      FKST_GITHUB_WRITE = "",
    }))

    t.eq(result.exit_code, 0)
    t.is_true(find_duplicate_comment(result.raises) ~= nil)
    t.is_true(find_duplicate_label(result.raises) ~= nil)
    t.eq(count_calls("codex exec") - before.codex, 0)
    t.eq(count_calls("git worktree list") - before.worktree_list, 0)
    t.eq(count_calls("git -C") - before.git_c, 0)
    t.eq(count_calls("gh issue close") - before.issue_close, 0)
  end,

  test_peer_bot_authored_noncanonical_fork_exits_before_implementation = function()
    local event = ready()
    mock_issue_implement({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
    }, {
      author_login = "ElonSG",
      body = forks.fork_issue_body("owner/repo", original_issue, "human", entity_lib.issue_source_ref("owner/repo", original_issue)),
    })
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", original_issue), {
      stdout = original_view_with_peer_fork_ledger(),
      stderr = "",
      exit_code = 0,
    })
    mock_managed_bot_logins("loning,ElonSG")

    local before = command_count_snapshot()
    local result = run_implement(event, opts("implement-peer-duplicate-fork", {
      FKST_GITHUB_BOT_LOGIN = "loning",
      FKST_GITHUB_WRITE = "",
    }))

    t.eq(result.exit_code, 0)
    t.is_true(find_duplicate_comment(result.raises) ~= nil)
    t.is_true(find_duplicate_label(result.raises) ~= nil)
    t.eq(count_calls("codex exec") - before.codex, 0)
    t.eq(count_calls("git worktree list") - before.worktree_list, 0)
    t.eq(count_calls("git -C") - before.git_c, 0)
    t.eq(count_calls("gh issue close") - before.issue_close, 0)
  end,
}
