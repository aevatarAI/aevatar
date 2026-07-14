local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local entity_mocks = require("tests.entity_read_mock_helpers")
local contract_time = require("contract.time")
local m_facts = require("devloop.markers.facts")
local core = h.core
local t = h.t
local replay_fields = require("devloop.replay_fields")
local autonomy_ledger = require("devloop.autonomy_ledger")
local m_builders = require("devloop.markers.builders")

local repo = "owner/repo"
local issue_number = 42
local pr_number = 7
local parent = "github-devloop/issue/owner/repo/42"
local child_pr = "github-devloop/pr/owner/repo/7"
local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local delegation = "g1"
local head_sha = "0123456789abcdef0123456789abcdef01234567"
local merge_commit_sha = "1111111111111111111111111111111111111111"
local integration_branch = "integration/dev"
local upstream_branch = "dev"
local upstream_head_sha = "fedcba9876543210fedcba9876543210fedcba98"

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function comment(body, author, created_at)
  return {
    id = tostring(created_at or body):gsub("[^%w_%-]", "_"):sub(1, 60),
    body = body,
    author_login = author or core._test_bot_login,
    created_at = created_at or "2026-06-03T01:00:00Z",
  }
end

local function find_raise(raises, queue, predicate)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue and (predicate == nil or predicate(raised.payload or {}, raised)) then
      return raised
    end
  end
  return nil
end

local function count_raises(raises, queue)
  local count = 0
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue then
      count = count + 1
    end
  end
  return count
end

local function count_calls(needle)
  return h.count_calls(needle)
end

local function mock_issue_close()
  t.mock_command("gh issue close", {
    stdout = "closed\n",
    stderr = "",
    exit_code = 0,
  })
end

local function run_timeout_reconcile(payload, opts)
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_timeout_reconcile",
    payload = payload,
  }, opts)
end

local function parent_comments(fields)
  local f = fields or {}
  local state = f.state or "awaiting-pr"
  local state_version = f.version or version
  local comments = {
    comment(core.state_marker(parent, state, state_version), core._test_bot_login, f.created_at or "2026-06-03T01:02:03Z"),
  }
  if f.delegation ~= false then
    table.insert(comments, comment(m_builders.pr_delegation_marker(core, 
      f.parent or parent,
      f.child or child_pr,
      f.pr_number or pr_number,
      f.delegation_version or state_version,
      f.delegation_generation or delegation
    ), core._test_bot_login, "2026-06-03T01:03:03Z"))
  end
  return comments
end

local function child_comments(state, child_version, opts)
  local options = opts or {}
  local effective_version = child_version or version
  local base_branch = options.base_branch or integration_branch
  local body = m_builders.pr_origin_marker(core, parent, issue_number, "devloop-owner-repo-42-01HY", effective_version, base_branch)
    .. "\n" .. core.state_marker(parent, state, effective_version)
  if state == "merged" then
    body = body .. "\n" .. m_builders.merged_marker(core, parent, pr_number, effective_version, head_sha)
  end
  return {
    comment(body, core._test_bot_login, "2026-06-03T01:04:03Z"),
  }
end

local function child_origin_only_comments()
  return {
    comment(m_builders.pr_origin_marker(core, parent, issue_number, "devloop-owner-repo-42-01HY", version, integration_branch), core._test_bot_login, "2026-06-03T01:04:03Z"),
  }
end

local function child_merged_comments_with_kept_promotion()
  return {
    comment(m_builders.pr_origin_marker(core, parent, issue_number, "devloop-owner-repo-42-01HY", version, integration_branch)
      .. "\n" .. core.state_marker(parent, "merged", version)
      .. "\n" .. m_builders.merged_marker(core, parent, pr_number, version, head_sha), core._test_bot_login, "2026-06-03T01:04:03Z"),
  }
end

local function mock_env()
  h.mock_bot_env()
  h.mock_write_env("")
  t.mock_command("gh api graphql", {
    stdout = '{"data":{"repository":{"issue":{"blockedBy":{"nodes":[]}}}}}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_real_write_env()
  h.mock_bot_env()
  for _ = 1, 4 do
    h.mock_write_env("1")
  end
  t.mock_command("gh api graphql", {
    stdout = '{"data":{"repository":{"issue":{"blockedBy":{"nodes":[]}}}}}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function mock_branch_config(split)
  t.mock_command('printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"', {
    stdout = upstream_branch,
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"', {
    stdout = split == false and upstream_branch or integration_branch,
    stderr = "",
    exit_code = 0,
  })
end

local function mock_rollup_landing(exit_code)
  t.mock_command(core.git_fetch_branch_cmd("origin", upstream_branch), {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(core.git_remote_branch_head_cmd("origin", upstream_branch), {
    stdout = upstream_head_sha .. "\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git merge-base --is-ancestor " .. merge_commit_sha .. " " .. upstream_head_sha, {
    stdout = "",
    stderr = "",
    exit_code = exit_code,
  })
end

local function mock_reads(issue_comments, pr_comments, opts)
  local options = opts or {}
  entity_mocks.mock_issue_view_selector(t, {
    repo = repo,
    number = issue_number,
    labels = options.labels or { "fkst-dev:enabled", "fkst-dev:awaiting-pr" },
    comments = issue_comments,
    assignees = { "fkst-test-bot" },
    author_login = "fkst-test-bot",
  }, "title,body,comments,labels,state,createdAt,updatedAt,assignees,author")
  entity_mocks.mock_pr_view_selector(t, {
    repo = repo,
    number = options.pr_number or pr_number,
    comments = pr_comments,
    head = "devloop-owner-repo-42-01HY",
    head_sha = head_sha,
    merge_commit_sha = options.merge_commit_sha or merge_commit_sha,
    state = options.pr_state or "OPEN",
    base_branch = options.base_branch or integration_branch,
    labels = {},
  }, entity_mocks.pr_origin_selector, options.pr_view_times)
end

local function run_observe(issue_comments, pr_comments, opts)
  local options = opts or {}
  if options.write == "real" then
    mock_real_write_env()
  else
    mock_env()
  end
  mock_reads(issue_comments, pr_comments, options)
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = {
      schema = "github-proxy.v1",
      type = "issue",
      repo = repo,
      number = issue_number,
      title = "Implement decision recorder",
      state = "OPEN",
      updated_at = "2026-06-03T01:02:03Z",
      labels = { "fkst-dev:enabled", "fkst-dev:awaiting-pr" },
      dedup_key = "owner/repo#issue#42@2026-06-03T01:02:03Z",
      source_ref = entity_lib.issue_source_ref(repo, issue_number),
    },
  })
end

local function run_pr_observe(issue_comments, pr_comments, opts)
  local options = opts or {}
  if options.write == "real" then
    mock_real_write_env()
  else
    mock_env()
  end
  options.pr_view_times = options.pr_view_times or 2
  mock_reads(issue_comments, pr_comments, options)
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = {
      schema = "github-proxy.v1",
      type = "pr",
      repo = repo,
      number = pr_number,
      state = "OPEN",
      updated_at = "2026-06-03T02:03:04Z",
      dedup_key = "owner/repo#pr#7@2026-06-03T02:03:04Z",
      source_ref = entity_lib.pr_source_ref(repo, pr_number),
    },
  })
end

local function resume_comment(result)
  return find_raise(result.raises, "github-proxy.github_issue_comment_request")
end

local function assert_resume_has_autonomy_result(resume)
  t.is_true(resume ~= nil)
  t.is_true(resume.payload.body:find('state="merged"', 1, true) ~= nil)
  t.is_true(resume.payload.body:find("fkst:github-devloop:merged:v1", 1, true) ~= nil)
  t.is_true(resume.payload.body:find("fkst:github-devloop:autonomy-result:v1", 1, true) ~= nil)
  local merged_marker = resume.payload.body:match("<!%-%- fkst:github%-devloop:merged:v1.-%-%->")
  t.is_true(merged_marker:find('autonomy_result="v1"', 1, true) ~= nil)
  local avm = autonomy_ledger.autonomy_result_fact(core, { resume.payload.body }, parent, pr_number, version, head_sha)
  t.is_true(avm ~= nil)
  t.eq(avm.issue_number, issue_number)
  t.eq(avm.pr_number, pr_number)
  t.eq(avm.valid_autonomous_merge, "pending")
  t.eq(avm.gates.post_merge_probe, "pending")
end

return {
  test_child_merged_reconciles_parent_to_merged = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_comments("merged"), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_parent_poll_reconciles_canonical_merged_child_pr_without_child_terminal_markers = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_origin_only_comments(), {
      write = "real",
      pr_state = "MERGED",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_parent_poll_reconciles_canonical_merged_child_pr_over_stale_nonterminal_marker = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_comments("merge-ready"), {
      write = "real",
      pr_state = "MERGED",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_parent_poll_reconciles_canonical_merged_child_pr_over_stale_closed_marker = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_comments("closed-unmerged"), {
      write = "real",
      pr_state = "MERGED",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_pr_entity_changed_child_merged_reconciles_parent_to_merged = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_pr_observe(parent_comments(), child_comments("merged"), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    t.is_true(resume ~= nil)
    t.is_true(resume.payload.body:find('state="merged"', 1, true) ~= nil)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_child_merged_with_kept_issue_promotion_closes_issue_once = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_merged_comments_with_kept_promotion(), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_split_topology_child_merged_uses_merge_commit_landing_not_head_ancestry = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(0)
    local result = run_observe(parent_comments(), child_comments("merged"), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 1)
    t.eq(count_calls("git fetch 'origin' '" .. upstream_branch .. "'"), 1)
    t.eq(count_calls("git merge-base --is-ancestor " .. merge_commit_sha .. " " .. upstream_head_sha), 1)
    t.eq(count_calls("git merge-base --is-ancestor " .. head_sha .. " " .. upstream_head_sha), 0)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_split_topology_open_child_with_merged_marker_does_not_advance_parent = function()
    mock_issue_close()
    mock_branch_config()
    local result = run_observe(parent_comments(), child_comments("merged"), { write = "real" })

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 0)
    t.eq(count_calls("git fetch 'origin' '" .. upstream_branch .. "'"), 0)
    t.eq(count_calls("git merge-base --is-ancestor"), 0)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 0)
  end,

  test_split_topology_canonical_merged_child_waits_until_merge_commit_lands_on_upstream = function()
    mock_issue_close()
    mock_branch_config()
    mock_rollup_landing(1)
    local result = run_observe(parent_comments(), child_comments("merged"), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 0)
    t.eq(count_calls("git merge-base --is-ancestor " .. merge_commit_sha .. " " .. upstream_head_sha), 1)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 0)
  end,

  test_single_branch_topology_child_merged_does_not_require_rollup_probe = function()
    mock_issue_close()
    mock_branch_config(false)
    local result = run_observe(parent_comments(), child_comments("merged", nil, { base_branch = upstream_branch }), {
      base_branch = upstream_branch,
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    assert_resume_has_autonomy_result(resume)
    t.eq(count_calls("git fetch 'origin' '" .. upstream_branch .. "'"), 0)
    t.eq(count_calls("git merge-base --is-ancestor"), 0)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 1)
  end,

  test_single_branch_topology_child_merged_requires_current_upstream_base = function()
    mock_issue_close()
    mock_branch_config(false)
    local result = run_observe(parent_comments(), child_comments("merged"), {
      pr_state = "MERGED",
      write = "real",
    })

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 0)
    t.eq(count_calls("git fetch 'origin' '" .. upstream_branch .. "'"), 0)
    t.eq(count_calls("git merge-base --is-ancestor"), 0)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), 0)
  end,

  test_child_closed_unmerged_reconciles_parent_to_ready_generation = function()
    local result = run_observe(parent_comments(), child_comments("closed-unmerged"))

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    t.is_true(resume ~= nil)
    t.is_true(resume.payload.body:find('state="ready"', 1, true) ~= nil)
    t.is_true(resume.payload.body:find("/reimplement/1", 1, true) ~= nil)
  end,

  test_child_closed_unmerged_blocks_at_reimplementation_budget = function()
    local exhausted = version .. "/reimplement/12"
    local result = run_observe(
      parent_comments({ version = exhausted, delegation_version = exhausted }),
      child_comments("closed-unmerged", exhausted)
    )

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    t.is_true(resume ~= nil)
    t.is_true(resume.payload.body:find('state="blocked"', 1, true) ~= nil)
    t.is_true(resume.payload.body:find("replacement-budget-exhausted", 1, true) ~= nil)
  end,

  test_child_blocked_reconciles_parent_to_blocked = function()
    local result = run_observe(parent_comments(), child_comments("blocked"))

    t.eq(result.exit_code, 0)
    local resume = resume_comment(result)
    t.is_true(resume ~= nil)
    t.is_true(resume.payload.body:find('state="blocked"', 1, true) ~= nil)
    t.is_true(resume.payload.body:find("child-pr-blocked", 1, true) ~= nil)
  end,

  test_child_nonterminal_defers_without_parent_cas = function()
    local result = run_observe(parent_comments(), child_comments("merge-ready"))

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 0)
  end,

  test_missing_delegation_fails_closed_without_stale_cas = function()
    local result = run_observe(parent_comments({ delegation = false }), child_comments("merged"))

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
  end,

  test_stale_generation_delegation_fails_closed_without_stale_cas = function()
    local result = run_observe(
      parent_comments({ delegation_version = version .. "/old" }),
      child_comments("merged")
    )

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
  end,

  test_idempotent_repoll_after_parent_transition_is_noop = function()
    local close_calls_before = count_calls("gh issue close 42 --repo owner/repo")
    local result = run_observe(parent_comments({ state = "merged" }), child_comments("merged"), {
      labels = { "fkst-dev:enabled", "fkst-dev:merged" },
      write = "real",
    })

    t.eq(result.exit_code, 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_comment_request"), 0)
    t.eq(count_raises(result.raises, "github-proxy.github_issue_label_request"), 0)
    t.eq(count_calls("gh issue close 42 --repo owner/repo"), close_calls_before)
  end,

  test_over_budget_awaiting_pr_timeout_reconcile_writes_why_terminal = function()
    local state = {
      state = "awaiting-pr",
      version = version .. "/timeout/awaiting-pr/2",
      proposal_id = parent,
      marker_created_at = "2025-01-01T00:00:00Z",
    }
    local row = restart_transition_row("awaiting-pr")
    local comments = parent_comments({ version = state.version, delegation_version = state.version })
    local facts = {
      proposal_id = parent,
      source_ref = entity_lib.issue_source_ref(repo, issue_number),
      current = { comments = comments },
      current_pr = { comments = {} },
      ["pr-delegation"] = m_facts.pr_delegation_fact(core, comments, parent, state.version),
      fresh_current_state = state,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-12-01T01:02:03Z"),
    }
    local raised = {}
    local original_log_raise = core.log_raise
    core.log_raise = function(_, _, queue, payload)
      table.insert(raised, { queue = queue, payload = payload })
    end
    local ok, err = pcall(function()
      t.eq(core.maybe_timeout_redrive_from_table("observe_issue", {
        repo = repo,
        number = issue_number,
        source_ref = entity_lib.issue_source_ref(repo, issue_number),
      }, state, row, facts), true)
    end)
    core.log_raise = original_log_raise
    if not ok then
      error(err)
    end
    local reconcile = find_raise(raised, "devloop_timeout_reconcile")

    t.is_true(reconcile ~= nil)
    t.eq(reconcile.payload.state, "awaiting-pr")
    t.eq(reconcile.payload.issue_version, state.version)
    t.eq(reconcile.payload.round, 3)
    mock_env()
    entity_mocks.mock_issue_view_selector(t, {
      repo = repo,
      number = issue_number,
      labels = { "fkst-dev:enabled", "fkst-dev:awaiting-pr" },
      comments = parent_comments({
        version = state.version,
        delegation_version = state.version,
        created_at = "2025-01-01T00:00:00Z",
      }),
      assignees = { "fkst-test-bot" },
      author_login = "fkst-test-bot",
    }, "title,updatedAt,labels,comments,state")

    local result = run_timeout_reconcile(reconcile.payload, h.opts("awaiting-pr-timeout-reconcile-terminal"))

    t.eq(result.exit_code, 0)
    local terminal = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(terminal ~= nil)
    t.is_true(terminal.payload.body:find('state="blocked"', 1, true) ~= nil)
    t.is_true(terminal.payload.body:find("reason_class=state-output-obligation-timeout", 1, true) ~= nil)
    t.is_true(label ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:blocked")
  end,
}
