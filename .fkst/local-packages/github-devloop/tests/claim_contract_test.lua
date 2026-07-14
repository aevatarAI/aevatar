local devloop_base = require("devloop.base")
local m_claims = require("devloop.claims")
local h = require("tests.devloop_core_helpers")
local core = h.core
local forks = require("devloop.forks")
local t = h.t
local gh_argv = require("testkit.gh_argv_mock")

local function mock_bot(login, write_mode, write_reads)
  t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
    stdout = login or "fkst-test-bot",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_FORK_GRACE_HOURS"', {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  for _ = 1, write_reads or 2 do
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = write_mode or "",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function mock_managed_bot_logins(logins)
  t.mock_command('printf %s "$FKST_DEVLOOP_MANAGED_BOT_LOGINS"', {
    stdout = logins or "",
    stderr = "",
    exit_code = 0,
  })
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

local function ownership_json(logins, author_login)
  local rendered = {}
  for _, login in ipairs(logins or {}) do
    table.insert(rendered, string.format('{"login":"%s"}', tostring(login)))
  end
  return '{"assignees":[' .. table.concat(rendered, ",") .. '],"author":{"login":"'
    .. tostring(author_login or "fkst-test-bot") .. '"}}\n'
end

local function encode_json_string(value)
  return '"' .. tostring(value or "")
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\r", "\\r")
    :gsub("\n", "\\n")
    .. '"'
end

local function issue_state_json(fields)
  local selected = fields or {}
  local comments = {}
  for _, comment in ipairs(selected.comments or {}) do
    table.insert(comments, '{"body":' .. encode_json_string(comment.body or "")
      .. ',"author":{"login":' .. encode_json_string(comment.author_login or "fkst-test-bot") .. "}}")
  end
  return '{"title":' .. encode_json_string(selected.title or "Implement fork isolation")
    .. ',"createdAt":' .. encode_json_string(selected.created_at or "2026-06-03T01:00:00Z")
    .. ',"updatedAt":' .. encode_json_string(selected.updated_at or "2026-06-03T01:02:03Z")
    .. ',"state":' .. encode_json_string(selected.state or "OPEN")
    .. ',"labels":[],"comments":[' .. table.concat(comments, ",")
    .. '],"assignees":[],"author":{"login":' .. encode_json_string(selected.author_login or "human") .. "}}\n"
end

local function state(name, created_at)
  return {
    state = name or "thinking",
    version = "github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    marker_created_at = created_at or "1970-01-01T00:00:00Z",
  }
end

local function self_current(extra)
  local fields = extra or {}
  return {
    assignees = fields.assignees or {},
    title = fields.title or "Implement fork isolation",
    state = fields.state or "OPEN",
    author_login = fields.author_login or "fkst-test-bot",
    comments = fields.comments or {},
    created_at = fields.created_at or "2026-06-03T01:00:00Z",
    updated_at = fields.updated_at,
  }
end

local function iso_at(seconds)
  return os.date("!%Y-%m-%dT%H:%M:%SZ", seconds)
end

local function created_inside_grace()
  return iso_at(now())
end

local function created_after_grace()
  return iso_at(now() - (3 * 60 * 60) - 1)
end

local function capture_raises(fn)
  local old_raise = raise
  local raised = {}
  raise = function(queue, payload)
    table.insert(raised, {
      queue = queue,
      payload = payload,
    })
  end
  local ok, result = pcall(fn)
  raise = old_raise
  if not ok then
    error(result)
  end
  return result, raised
end

local function capture_warn_logs(fn)
  local previous_warn = log.warn
  local logs = {}
  log.warn = function(message)
    table.insert(logs, tostring(message))
  end
  local ok, result = pcall(fn)
  log.warn = previous_warn
  if not ok then
    error(result, 0)
  end
  return result, logs
end

local function capture_info_logs(fn)
  local previous_info = log.info
  local logs = {}
  log.info = function(message)
    table.insert(logs, tostring(message))
  end
  local ok, result = pcall(fn)
  log.info = previous_info
  if not ok then
    error(result, 0)
  end
  return result, logs
end

return {
  test_fork_grace_elapsed_uses_created_at_and_clamps_future_age = function()
    local elapsed, reason, age = m_claims.fork_grace_elapsed(core, "owner/repo", 42, {
      created_at = "2026-06-03T00:00:00Z",
      updated_at = "2026-06-03T23:59:00Z",
    }, 1782835200, 3 * 60 * 60)
    t.eq(elapsed, true)
    t.eq(reason, "fork-grace-elapsed")
    t.is_true(age >= 3 * 60 * 60)

    elapsed, reason, age = m_claims.fork_grace_elapsed(core, "owner/repo", 42, {
      created_at = "2999-01-01T00:00:00Z",
      updated_at = "2026-06-03T01:02:03Z",
    }, 1782835200, 3 * 60 * 60)
    t.eq(elapsed, false)
    t.eq(reason, "fork-grace-pending")
    t.eq(age, 0)

    elapsed, reason, age = m_claims.fork_grace_elapsed(core, "owner/repo", 42, {
      updated_at = "2026-06-03T01:02:03Z",
    }, 1782835200, 3 * 60 * 60)
    t.eq(elapsed, false)
    t.eq(reason, "fork-grace-age-unknown")
    t.eq(age, nil)
  end,

  test_issue_claim_state_is_current_assignees_only = function()
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot"), "unassigned")
    t.eq(m_claims.issue_claim_state(core, { { login = "fkst-test-bot" } }, "fkst-test-bot"), "self")
    t.eq(m_claims.issue_claim_state(core, { { login = "human" } }, "fkst-test-bot"), "other")
    t.eq(m_claims.issue_claim_state(core, { { login = "fkst-test-bot" }, { login = "other-bot" } }, "fkst-test-bot"), "other")
  end,

  test_is_self_owned_issue_allows_self_assignee_or_unassigned_self_author = function()
    t.eq(m_claims.is_self_owned_issue(core, nil, "fkst-test-bot"), false)
    t.eq(m_claims.is_self_owned_issue(core, { assignees = { "fkst-test-bot" }, author_login = "human" }, "fkst-test-bot"), true)
    t.eq(m_claims.is_self_owned_issue(core, { assignees = {}, author_login = "fkst-test-bot" }, "fkst-test-bot"), true)
    t.eq(m_claims.is_self_owned_issue(core, { assignees = {}, author_login = "human" }, "fkst-test-bot"), false)
    t.eq(m_claims.is_self_owned_issue(core, { assignees = { "human" }, author_login = "fkst-test-bot" }, "fkst-test-bot"), false)
  end,

  test_dry_run_claim_proceeds_without_assigning = function()
    mock_bot("fkst-test-bot", "")

    local ok = m_claims.claim_issue_for_management(core,
      "claim_contract",
      "owner/repo",
      42,
      self_current(),
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, true)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_claim_assigns_then_verifies_self_only_winner = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command("gh issue edit '42' --repo 'owner/repo' --add-assignee 'fkst-test-bot'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command(core.gh_issue_view_claim_cmd("owner/repo", 42), {
      stdout = ownership_json({ "fkst-test-bot" }),
      stderr = "",
      exit_code = 0,
    })

    local ok = m_claims.claim_issue_for_management(core,
      "claim_contract",
      "owner/repo",
      42,
      self_current(),
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, true)
    t.eq(count_calls("--add-assignee fkst-test-bot"), 1)
    t.eq(count_calls("--remove-assignee fkst-test-bot"), 0)
  end,

  test_claim_permission_denied_is_terminal_skip_without_crashing_or_unassigning = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command("gh issue edit '42' --repo 'owner/repo' --add-assignee 'fkst-test-bot'", {
      stdout = "",
      stderr = "GraphQL: Could not resolve to a User with the login of 'fkst-test-bot'. (permission-denied)\n",
      exit_code = 1,
    })

    local ok, captured_logs = capture_warn_logs(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current(),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(count_calls("--add-assignee fkst-test-bot"), 1)
    t.eq(count_calls("--remove-assignee fkst-test-bot"), 0)
    local logs = table.concat(captured_logs, "\n")
    t.is_true(logs:find("tag=SKIP", 1, true) ~= nil)
    t.is_true(logs:find("error_class=intake-skip-unclaimable", 1, true) ~= nil)
    t.is_true(logs:find("source_ref=external:owner/repo#issue/42", 1, true) ~= nil)
    t.is_true(logs:find("WHY=assign permission-denied is permanent", 1, true) ~= nil)
  end,

  test_claim_transient_assign_error_propagates = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command("gh issue edit '42' --repo 'owner/repo' --add-assignee 'fkst-test-bot'", {
      stdout = "",
      stderr = "HTTP 502: upstream unavailable\n",
      exit_code = 1,
    })

    local ok, err = pcall(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current(),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.is_true(tostring(err):find("gh-command-failed", 1, true) ~= nil)
    t.eq(count_calls("--add-assignee fkst-test-bot"), 1)
    t.eq(count_calls("--remove-assignee fkst-test-bot"), 0)
  end,

  test_claim_loss_unassigns_only_self_and_skips = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command("gh issue edit '42' --repo 'owner/repo' --add-assignee 'fkst-test-bot'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command(core.gh_issue_view_claim_cmd("owner/repo", 42), {
      stdout = ownership_json({ "other-bot" }),
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue edit '42' --repo 'owner/repo' --remove-assignee 'fkst-test-bot'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local ok = m_claims.claim_issue_for_management(core,
      "claim_contract",
      "owner/repo",
      42,
      self_current(),
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, false)
    t.eq(count_calls("--remove-assignee fkst-test-bot"), 1)
    t.eq(count_calls("--remove-assignee other-bot"), 0)
  end,

  test_non_self_assignee_is_never_touched = function()
    mock_bot("fkst-test-bot", "1")

    local ok = m_claims.claim_issue_for_management(core,
      "claim_contract",
      "owner/repo",
      42,
      { assignees = { { login = "human" } } },
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, false)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_other_author_unassigned_issue_inside_grace_skips_without_forking = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 44), {
      stdout = issue_state_json({ author_login = "human", created_at = created_inside_grace() }),
      stderr = "",
      exit_code = 0,
    })

    local ok, logs
    local _, raised = capture_raises(function()
      ok, logs = capture_info_logs(function()
        return m_claims.claim_issue_for_management(core,
          "claim_contract",
          "owner/repo",
          44,
          self_current({ author_login = "human", created_at = created_inside_grace() }),
          "github-devloop/issue/owner/repo/44"
        )
      end)
    end)

    t.eq(ok, false)
    t.eq(count_calls("gh issue edit"), 0)
    t.eq(#raised, 0)
    local joined = table.concat(logs, "\n")
    t.is_true(joined:find("outcome=skip-fork-grace", 1, true) ~= nil)
    t.is_true(joined:find("reason=fork-grace-pending", 1, true) ~= nil)
    t.is_true(joined:find("age_seconds=", 1, true) ~= nil)
    t.is_true(joined:find("grace_seconds=10800", 1, true) ~= nil)
  end,

  test_managed_bot_author_unassigned_issue_after_grace_skips_without_forking = function()
    mock_bot("fkst-test-bot", "1")
    mock_managed_bot_logins("peer-bot[bot],other-peer")
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 45), {
      stdout = issue_state_json({ author_login = "peer-bot[bot]", created_at = created_after_grace() }),
      stderr = "",
      exit_code = 0,
    })

    local ok, captured_logs = capture_info_logs(function()
      local result, raised = capture_raises(function()
        return m_claims.claim_issue_for_management(core,
          "claim_contract",
          "owner/repo",
          45,
          self_current({ author_login = "peer-bot[bot]", created_at = created_after_grace() }),
          "github-devloop/issue/owner/repo/45"
        )
      end)
      t.eq(#raised, 0)
      return result
    end)

    t.eq(ok, false)
    t.eq(count_calls("gh issue edit"), 0)
    local logs = table.concat(captured_logs, "\n")
    t.is_true(logs:find("outcome=skip-fork-peer-bot", 1, true) ~= nil)
  end,

  test_other_author_unassigned_issue_after_grace_raises_self_assigned_fork = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 43), {
      stdout = issue_state_json({ author_login = "human", created_at = created_after_grace() }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        43,
        self_current({ author_login = "human", created_at = created_after_grace() }),
        "github-devloop/issue/owner/repo/43"
      )
    end)

    t.eq(ok, false)
    t.eq(count_calls("gh issue edit"), 0)
    t.eq(#raised, 1)
    t.eq(raised[1].queue, "github-proxy.github_issue_create_request")
    t.eq(raised[1].payload.schema, "github-proxy.issue-create.v1")
    t.eq(raised[1].payload.assignees[1], "fkst-test-bot")
    t.eq(raised[1].payload.dedup_key, forks.fork_issue_dedup_key("owner/repo", 43))
    t.eq(raised[1].payload.post_create_blocked_by.blocked_issue_number, 43)
    t.eq(raised[1].payload.post_create_blocked_by.dedup_key, forks.fork_issue_dedup_key("owner/repo", 43) .. "/blocked-by")
  end,

  test_other_author_fork_revalidates_closed_issue_before_raise = function()
    mock_bot("fkst-test-bot", "1")
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 43), {
      stdout = issue_state_json({ state = "CLOSED", author_login = "human", created_at = created_after_grace() }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        43,
        self_current({ author_login = "human", state = "OPEN", created_at = created_after_grace() }),
        "github-devloop/issue/owner/repo/43"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_missing_author_unassigned_issue_skips_without_assigning_or_forking = function()
    mock_bot("fkst-test-bot", "1")

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        { assignees = {}, title = "Unknown author", comments = {} },
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_existing_fork_parent_ledger_skips_duplicate_fork = function()
    mock_bot("fkst-test-bot", "1")
    local dedup_key = forks.fork_issue_dedup_key("owner/repo", 42)
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 42), {
      stdout = issue_state_json({
        author_login = "human",
        comments = {
          {
            body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key .. '" issue="99" -->',
            author_login = "fkst-test-bot",
          },
        },
      }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current({
          author_login = "human",
          comments = {
            {
              body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key .. '" issue="99" -->',
              author_login = "fkst-test-bot",
            },
          },
        }),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_existing_peer_bot_fork_parent_ledger_skips_duplicate_fork = function()
    mock_bot("loning", "1")
    mock_managed_bot_logins("loning,ElonSG")
    local dedup_key = forks.fork_issue_dedup_key("owner/repo", 42)
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 42), {
      stdout = issue_state_json({
        author_login = "human",
        created_at = created_after_grace(),
        comments = {
          {
            body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key .. '" issue="99" -->',
            author_login = "ElonSG",
          },
        },
      }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current({
          author_login = "human",
          created_at = created_after_grace(),
          comments = {
            {
              body = '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. dedup_key .. '" issue="99" -->',
              author_login = "ElonSG",
            },
          },
        }),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_existing_fork_parent_intent_skips_duplicate_fork = function()
    mock_bot("fkst-test-bot", "1")
    local dedup_key = forks.fork_issue_dedup_key("owner/repo", 42)
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 42), {
      stdout = issue_state_json({
        author_login = "human",
        comments = {
          {
            body = '<!-- fkst:github-proxy:issue-create-intent:v1 dedup="' .. dedup_key .. '" -->',
            author_login = "fkst-test-bot",
          },
        },
      }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current({
          author_login = "human",
          comments = {
            {
              body = '<!-- fkst:github-proxy:issue-create-intent:v1 dedup="' .. dedup_key .. '" -->',
              author_login = "fkst-test-bot",
            },
          },
        }),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 0)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_forged_fork_parent_intent_does_not_suppress_fork = function()
    mock_bot("fkst-test-bot", "1")
    local dedup_key = forks.fork_issue_dedup_key("owner/repo", 42)
    t.mock_command(core.gh_issue_view_state_cmd("owner/repo", 42), {
      stdout = issue_state_json({
        author_login = "human",
        created_at = created_after_grace(),
        comments = {
          {
            body = '<!-- fkst:github-proxy:issue-create-intent:v1 dedup="' .. dedup_key .. '" -->',
            author_login = "human",
          },
        },
      }),
      stderr = "",
      exit_code = 0,
    })

    local ok, raised = capture_raises(function()
      return m_claims.claim_issue_for_management(core,
        "claim_contract",
        "owner/repo",
        42,
        self_current({
          author_login = "human",
          created_at = created_after_grace(),
          comments = {
            {
              body = '<!-- fkst:github-proxy:issue-create-intent:v1 dedup="' .. dedup_key .. '" -->',
              author_login = "human",
            },
          },
        }),
        "github-devloop/issue/owner/repo/42"
      )
    end)

    t.eq(ok, false)
    t.eq(#raised, 1)
    t.eq(raised[1].queue, "github-proxy.github_issue_create_request")
    t.eq(raised[1].payload.dedup_key, dedup_key)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_verify_pr_review_issue_claim_predicate_contract = function()
    mock_bot("fkst-test-bot", "")
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = { "fkst-test-bot" },
      author_login = "human",
    }, "github-devloop/issue/owner/repo/42"), true)
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = { "human" },
      author_login = "fkst-test-bot",
    }, "github-devloop/issue/owner/repo/42"), false)
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = {},
      author_login = "fkst-test-bot",
    }, "github-devloop/issue/owner/repo/42"), true)
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = {},
      author_login = "human",
    }, "github-devloop/issue/owner/repo/42"), false)
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", nil, nil, "github-devloop/pr/owner/repo/7"), false)
  end,

  test_verify_pr_review_issue_claim_uses_configured_claim_owner_before_assert = function()
    mock_bot("real-bot", "")

    local self_owned = m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = { "real-bot" },
      author_login = "human",
    }, "github-devloop/issue/owner/repo/42")
    local other_owned = m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = { "fkst-test-bot" },
      author_login = "human",
    }, "github-devloop/issue/owner/repo/42")
    devloop_base.configure_trusted_bot_login(nil)

    t.eq(self_owned, true)
    t.eq(other_owned, false)
  end,

  test_verify_pr_review_issue_claim_rederives_missing_ownership_and_fails_closed = function()
    mock_bot("fkst-test-bot", "")
    t.mock_command(core.gh_issue_view_claim_cmd("owner/repo", 42), {
      stdout = ownership_json({}, "fkst-test-bot"),
      stderr = "",
      exit_code = 0,
    })
    t.eq(m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, {
      assignees = {},
    }, "github-devloop/issue/owner/repo/42"), true)

    mock_bot("fkst-test-bot", "")
    t.mock_command(core.gh_issue_view_claim_cmd("owner/repo", 42), {
      stdout = "",
      stderr = "forced failure",
      exit_code = 1,
    })
    local ok = pcall(function()
      m_claims.verify_pr_review_issue_claim(core, "claim_contract", "owner/repo", 42, nil, "github-devloop/issue/owner/repo/42")
    end)
    t.eq(ok, false)
  end,
}
