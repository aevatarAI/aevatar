local devloop_base = require("devloop.base")
local m_claims = require("devloop.claims")
local parsers_misc = require("devloop.parsers.misc")
local h = require("tests.devloop_core_helpers")
local core = h.core
local t = h.t
local config = require("devloop.config")

-- Mock the env reads a claim flow consults. Each mock_command registration is
-- consumed by one matching read (queued FIFO), mirroring claim_contract_test.lua's
-- mock_bot, which re-registers FKST_GITHUB_WRITE write_reads times. We register a
-- generous count so a whole claim flow's repeated env reads stay answered.
local function mock_env(login, claim_mode, write_mode, reads)
  local n = reads or 12
  for _ = 1, n do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = login or "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command('printf %s "$FKST_GITHUB_CLAIM_MODE"', {
      stdout = claim_mode or "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', {
      stdout = write_mode or "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command('printf %s "$FKST_DEVLOOP_FORK_GRACE_HOURS"', {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function count_calls(needle)
  local count = 0
  for _, call in ipairs(t.command_calls()) do
    if call.rendered:find(needle, 1, true) ~= nil then
      count = count + 1
    end
  end
  return count
end

local function count_adapter_calls(flag, value)
  local count = 0
  for _, call in ipairs(t.command_calls()) do
    local rendered = tostring(call.rendered or "")
    if rendered == "" and type(call.argv) == "table" then
      rendered = table.concat(call.argv, " ")
    end
    if rendered == "" and type(call.args) == "table" then
      local values = {}
      if call.program ~= nil then
        table.insert(values, tostring(call.program))
      end
      for _, arg in ipairs(call.args) do
        table.insert(values, tostring(arg))
      end
      rendered = table.concat(values, " ")
    end
    if rendered:find(tostring(flag), 1, true) ~= nil
      and rendered:find(tostring(value), 1, true) ~= nil then
      count = count + 1
    end
  end
  return count
end

local function ownership_json(logins, author_login, labels)
  local rendered_assignees = {}
  for _, login in ipairs(logins or {}) do
    table.insert(rendered_assignees, string.format('{"login":"%s"}', tostring(login)))
  end
  local rendered_labels = {}
  for _, label in ipairs(labels or {}) do
    table.insert(rendered_labels, string.format('{"name":"%s"}', tostring(label)))
  end
  return '{"assignees":[' .. table.concat(rendered_assignees, ",")
    .. '],"author":{"login":"' .. tostring(author_login or "fkst-test-bot")
    .. '"},"labels":[' .. table.concat(rendered_labels, ",") .. "]}\n"
end

local claimed_label = m_claims.claimed_label(core)

return {
  -- (a) [bot] normalization on BOTH sides of the author-vs-bot comparison.
  test_strip_bot_login_suffix_is_nil_safe_and_no_op_for_users = function()
    t.eq(devloop_base.strip_bot_login_suffix("octocat"), "octocat")
    t.eq(devloop_base.strip_bot_login_suffix("chronoai-bot[bot]"), "chronoai-bot")
    -- Nil-safe: nil in → nil out (preserves existing nil semantics for an
    -- unconfigured bot login / missing author).
    t.eq(devloop_base.strip_bot_login_suffix(nil), nil)
    -- Only a trailing [bot] is stripped.
    t.eq(devloop_base.strip_bot_login_suffix("user[bot]name"), "user[bot]name")
  end,

  test_configure_trusted_bot_login_normalizes_bracket_bot_suffix = function()
    t.eq(devloop_base.configure_trusted_bot_login("chronoai-bot[bot]"), "chronoai-bot")
    t.eq(devloop_base.trusted_bot_login(), "chronoai-bot")
    t.eq(devloop_base.configure_trusted_bot_login("plain-bot"), "plain-bot")
    t.eq(devloop_base.trusted_bot_login(), "plain-bot")
    devloop_base.configure_trusted_bot_login(nil)
  end,

  test_comment_author_login_normalizes_bracket_bot_suffix = function()
    t.eq(parsers_misc.comment_author_login(core, { author_login = "chronoai-bot[bot]" }), "chronoai-bot")
    t.eq(parsers_misc.comment_author_login(core, { author = { login = "chronoai-bot[bot]" } }), "chronoai-bot")
    t.eq(parsers_misc.comment_author_login(core, { user = { login = "chronoai-bot[bot]" } }), "chronoai-bot")
    t.eq(parsers_misc.comment_author_login(core, { author_login = "octocat" }), "octocat")
  end,

  test_authorless_comment_is_not_trusted_by_default_test_bot_login = function()
    devloop_base.configure_trusted_bot_login(nil)
    t.eq(devloop_base.trusted_bot_login(), core._test_bot_login)
    t.is_nil(parsers_misc.comment_author_login(core, { body = "authorless" }))
    t.eq(parsers_misc._is_trusted_comment(core, { body = "authorless" }), false)
    t.eq(parsers_misc._is_trusted_comment(core, { author = nil, user = nil, body = "authorless" }), false)
  end,

  -- Bare-config vs [bot]-author: trusted.
  test_bare_config_trusts_bracket_bot_author = function()
    devloop_base.configure_trusted_bot_login("chronoai-bot")
    t.eq(parsers_misc._is_trusted_comment(core, { author_login = "chronoai-bot[bot]", body = "x" }), true)
    devloop_base.configure_trusted_bot_login(nil)
  end,

  -- [bot]-config vs [bot]-author: trusted.
  test_bracket_bot_config_trusts_bracket_bot_author = function()
    devloop_base.configure_trusted_bot_login("chronoai-bot[bot]")
    t.eq(parsers_misc._is_trusted_comment(core, { author_login = "chronoai-bot[bot]", body = "x" }), true)
    devloop_base.configure_trusted_bot_login(nil)
  end,

  -- bare-config vs bare-author: trusted (and unrelated logins untrusted).
  test_bare_config_trusts_bare_author_and_rejects_others = function()
    devloop_base.configure_trusted_bot_login("chronoai-bot")
    t.eq(parsers_misc._is_trusted_comment(core, { author_login = "chronoai-bot", body = "x" }), true)
    t.eq(parsers_misc._is_trusted_comment(core, { author_login = "someone-else", body = "x" }), false)
    devloop_base.configure_trusted_bot_login(nil)
  end,

  -- [bot]-config vs bare-author: also trusted (both sides normalized).
  test_bracket_bot_config_trusts_bare_author = function()
    devloop_base.configure_trusted_bot_login("chronoai-bot[bot]")
    t.eq(parsers_misc._is_trusted_comment(core, { author_login = "chronoai-bot", body = "x" }), true)
    devloop_base.configure_trusted_bot_login(nil)
  end,

  -- (b) label-mode claim state + ownership derived from the claimed label.
  test_label_mode_claim_state_derives_from_claimed_label = function()
    mock_env("fkst-test-bot", "label", "")
    -- No claimed label => unclaimed regardless of assignees.
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot", {}), "unassigned")
    t.eq(m_claims.issue_claim_state(core, { { login = "someone" } }, "fkst-test-bot", { "fkst-dev:enabled" }), "unassigned")
    -- Claimed label present => self.
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot", { claimed_label }), "self")
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot", { "fkst-dev:enabled", claimed_label }), "self")
  end,

  test_label_mode_is_self_owned_uses_label_presence = function()
    mock_env("fkst-test-bot", "label", "")
    t.eq(m_claims.is_self_owned_issue(core, { assignees = {}, labels = { claimed_label }, author_login = "human" }, "fkst-test-bot"), true)
    -- Unassigned + self author still self-owned (fork-and-block isolation).
    t.eq(m_claims.is_self_owned_issue(core, { assignees = {}, labels = {}, author_login = "fkst-test-bot" }, "fkst-test-bot"), true)
    -- Unclaimed + other author => not self-owned.
    t.eq(m_claims.is_self_owned_issue(core, { assignees = {}, labels = {}, author_login = "human" }, "fkst-test-bot"), false)
  end,

  test_label_mode_claim_adds_label_then_verifies_winner = function()
    mock_env("fkst-test-bot", "label", "1")
    t.mock_command("gh issue edit 42 --repo owner/repo --add-label '" .. claimed_label .. "'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author,labels", {
      stdout = ownership_json({}, "fkst-test-bot", { claimed_label }),
      stderr = "",
      exit_code = 0,
    })

    local ok = m_claims.claim_issue_for_management(core,
      "claim_mode",
      "owner/repo",
      42,
      { assignees = {}, labels = {}, author_login = "fkst-test-bot", comments = {} },
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, true)
    t.eq(count_adapter_calls("--add-label", claimed_label), 1)
    t.eq(count_adapter_calls("--remove-label", claimed_label), 0)
    -- Assignee-mode commands are never issued in label-mode.
    t.eq(count_adapter_calls("--add-assignee", "fkst-test-bot"), 0)
  end,

  test_label_mode_claim_loss_removes_label_and_skips = function()
    mock_env("fkst-test-bot", "label", "1")
    t.mock_command("gh issue edit 42 --repo owner/repo --add-label '" .. claimed_label .. "'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    -- Verification view shows the label is gone (lost the race).
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author,labels", {
      stdout = ownership_json({}, "fkst-test-bot", {}),
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue edit 42 --repo owner/repo --remove-label '" .. claimed_label .. "'", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local ok = m_claims.claim_issue_for_management(core,
      "claim_mode",
      "owner/repo",
      42,
      { assignees = {}, labels = {}, author_login = "fkst-test-bot", comments = {} },
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, false)
    t.eq(count_adapter_calls("--add-label", claimed_label), 1)
    t.eq(count_adapter_calls("--remove-label", claimed_label), 1)
  end,

  test_label_mode_self_owned_short_circuits_without_writes = function()
    mock_env("fkst-test-bot", "label", "1")
    local ok = m_claims.claim_issue_for_management(core,
      "claim_mode",
      "owner/repo",
      42,
      { assignees = {}, labels = { claimed_label }, author_login = "human", comments = {} },
      "github-devloop/issue/owner/repo/42"
    )
    t.eq(ok, true)
    t.eq(count_calls("gh issue edit"), 0)
  end,

  test_label_mode_verify_issue_claim_reads_labels = function()
    mock_env("fkst-test-bot", "label", "")
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author,labels", {
      stdout = ownership_json({}, "fkst-test-bot", { claimed_label }),
      stderr = "",
      exit_code = 0,
    })
    t.eq(m_claims.verify_issue_claim(core, "owner/repo", 42, "fkst-test-bot"), true)

    mock_env("fkst-test-bot", "label", "")
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author,labels", {
      stdout = ownership_json({}, "fkst-test-bot", {}),
      stderr = "",
      exit_code = 0,
    })
    t.eq(m_claims.verify_issue_claim(core, "owner/repo", 42, "fkst-test-bot"), false)
  end,

  test_label_mode_claim_view_projects_labels = function()
    mock_env("fkst-test-bot", "label", "")
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author,labels", {
      stdout = ownership_json({}, "fkst-test-bot", { claimed_label }),
      stderr = "",
      exit_code = 0,
    })
    local ownership = m_claims.read_current_issue_ownership(core, "owner/repo", 42)
    t.eq(ownership.labels[1], claimed_label)
    t.eq(m_claims.issue_claim_state(core, ownership.assignees, "fkst-test-bot", ownership.labels), "self")
  end,

  -- (c) assignee-mode (default) is unchanged: unknown/empty claim mode behaves
  -- exactly like today's assignee claim.
  test_default_mode_is_assignee_claim_state = function()
    mock_env("fkst-test-bot", "", "")
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot"), "unassigned")
    t.eq(m_claims.issue_claim_state(core, { { login = "fkst-test-bot" } }, "fkst-test-bot"), "self")
    t.eq(m_claims.issue_claim_state(core, { { login = "human" } }, "fkst-test-bot"), "other")
    -- A claimed label is irrelevant in assignee-mode.
    t.eq(m_claims.issue_claim_state(core, {}, "fkst-test-bot", { claimed_label }), "unassigned")
  end,

  test_unknown_mode_falls_back_to_assignee = function()
    mock_env("fkst-test-bot", "bogus-mode", "")
    t.eq(config.claim_mode(), "assignee")
    t.eq(m_claims.issue_claim_state(core, { { login = "fkst-test-bot" } }, "fkst-test-bot"), "self")
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author", {
      stdout = ownership_json({ "fkst-test-bot" }, "fkst-test-bot"),
      stderr = "",
      exit_code = 0,
    })
    local ownership = m_claims.read_current_issue_ownership(core, "owner/repo", 42)
    t.eq(m_claims.issue_claim_state(core, ownership.assignees, "fkst-test-bot", ownership.labels), "self")
  end,

  test_assignee_mode_claim_assigns_then_verifies = function()
    mock_env("fkst-test-bot", "", "1")
    t.mock_command("gh issue edit 42 --repo owner/repo --add-assignee fkst-test-bot", {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command("gh issue view 42 --repo owner/repo --json assignees,author", {
      stdout = ownership_json({ "fkst-test-bot" }, "fkst-test-bot"),
      stderr = "",
      exit_code = 0,
    })

    local ok = m_claims.claim_issue_for_management(core,
      "claim_mode",
      "owner/repo",
      42,
      { assignees = {}, author_login = "fkst-test-bot", comments = {} },
      "github-devloop/issue/owner/repo/42"
    )

    t.eq(ok, true)
    t.eq(count_adapter_calls("--add-assignee", "fkst-test-bot"), 1)
    -- No label-mode commands leak into assignee-mode.
    t.eq(count_adapter_calls("--add-label", claimed_label), 0)
  end,

  -- claim_owner normalizes the configured bot login at its single source.
  test_claim_owner_returns_bare_slug_for_bracket_bot_config = function()
    mock_env("chronoai-bot[bot]", "", "")
    t.eq(m_claims.claim_owner(), "chronoai-bot")
    devloop_base.configure_trusted_bot_login(nil)
  end,
}
