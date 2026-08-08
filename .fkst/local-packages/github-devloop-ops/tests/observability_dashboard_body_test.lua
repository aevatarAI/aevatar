local h = require("tests.devloop_ops_core_helpers")
local core = h.core
local t = h.t
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local autonomy_ledger = require("devloop.autonomy_ledger")
local m_builders = require("devloop.markers.builders")
local author_policy = require("testkit.github_author_policy")
require("departments.observability.main")

local old_dashboard_body_cap = 12000

local function mock_dashboard_env()
  for _ = 1, 4 do
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
      stdout = "fkst-test-bot",
      stderr = "",
      exit_code = 0,
    })
  end
end

local function no_revert_scan()
  return {
    schema = "github-devloop.no-revert-reopen-scan.v1",
    since_at = "2026-06-03T01:30:00Z",
    until_at = "2026-06-10T01:30:00Z",
    pr_reverts_complete = true,
    revert_commits_complete = true,
    issue_reopens_complete = true,
  }
end

local function large_mermaid(line_count)
  local lines = { "flowchart LR" }
  for index = 1, line_count do
    table.insert(lines, "  node_" .. tostring(index) .. " --> node_" .. tostring(index + 1))
  end
  return table.concat(lines, "\n")
end

local function trusted_comment(body, created_at, id)
  return {
    id = id,
    body = body,
    author_login = "fkst-test-bot",
    created_at = created_at or "2026-06-03T01:00:00Z",
  }
end

local function authored_comment(body, author_login, created_at, id)
  return {
    id = id,
    body = body,
    author_login = author_login,
    created_at = created_at or "2026-06-03T01:00:00Z",
  }
end

local function untrusted_comment(body, created_at, id)
  return {
    id = id,
    body = body,
    author_login = "mallory",
    created_at = created_at or "2026-06-03T01:00:00Z",
  }
end

local function attempt_marker(proposal_id, dedup_key, attempt, started_at, exec_ref)
  local marker = '<!-- fkst:github-devloop:implement-attempt:v1 proposal="' .. tostring(proposal_id)
    .. '" dedup="' .. tostring(dedup_key)
    .. '" attempt="' .. tostring(attempt)
    .. '" started_at="' .. tostring(started_at or "")
    .. '"'
  if exec_ref ~= nil and exec_ref ~= "" then
    marker = marker .. ' exec_ref="' .. tostring(exec_ref) .. '"'
  end
  return marker .. " -->"
end

local function autonomy_record(fields)
  local record = {
    proposal_id = fields.proposal_id,
    repo = "owner/repo",
    issue_number = fields.issue_number or "42",
    pr_number = fields.pr_number or "7",
    version = fields.version,
    head_sha = fields.head_sha or "def456",
    task_class = fields.task_class,
    human_touch_count = 0,
    rounds = fields.rounds or 1,
    retry_count = fields.retry_count or 0,
    codex_calls = fields.codex_calls,
    gates = fields.gates,
  }
  return record
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

local function assert_dashboard_marker_outside_fences(body)
  local marker_start = body:find("<!-- fkst:dashboard:v1", 1, true)
  t.is_true(marker_start ~= nil)

  local search_from = 1
  local last_close = nil
  while true do
    local opening = body:find("```mermaid", search_from, true)
    if opening == nil then
      break
    end
    local closing = body:find("\n```", opening + #"```mermaid", true)
    t.is_true(closing ~= nil)
    t.is_true(closing < marker_start)
    t.eq(body:sub(opening, closing):find("<!--", 1, true), nil)
    last_close = closing
    search_from = closing + #"\n```"
  end

  if last_close ~= nil then
    t.is_true(last_close < marker_start)
  end
end

return {
  test_avm_scoreboard_aggregates_by_task_level_without_total = function()
    local rows = core.aggregate_avm_scoreboard({
      {
        proposal_id = "github-devloop/issue/owner/repo/1",
        pr_number = 11,
        version = "v1",
        head_sha = "abc123",
        task_class = "L1",
        valid_autonomous_merge = "true",
        avm_rate_numerator = 1,
        avm_rate_denominator = 2,
        codex_calls = 6,
        rounds = 3,
        gates = { no_revert_reopen = "pass" },
        false_consensus = false,
      },
      {
        proposal_id = "github-devloop/issue/owner/repo/1",
        pr_number = 11,
        version = "v1",
        head_sha = "abc123",
        task_class = "L1",
        valid_autonomous_merge = "true",
        avm_rate_numerator = 1,
        avm_rate_denominator = 2,
        codex_calls = 6,
        rounds = 3,
        gates = { no_revert_reopen = "pass" },
        false_consensus = false,
      },
      {
        proposal_id = "github-devloop/issue/owner/repo/2",
        pr_number = 12,
        version = "v2",
        head_sha = "def456",
        task_class = "unknown",
        valid_autonomous_merge = "false",
        rounds = 4,
        gates = { no_revert_reopen = "fail" },
      },
    })
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end

    t.eq(by_level.L1.merges, 1)
    t.eq(by_level.L1.avm_numerator, 1)
    t.eq(by_level.L1.avm_denominator, 2)
    t.eq(by_level.L1.false_consensus_numerator, 0)
    t.eq(by_level.L1.false_consensus_denominator, 1)
    t.eq(by_level.unclassified.merges, 1)
    t.eq(by_level.unclassified.avm_denominator, 1)
    t.eq(by_level.unclassified.revert_numerator, 1)
    t.eq(by_level.unclassified.false_consensus_numerator, 0)
    t.eq(by_level.unclassified.false_consensus_denominator, 0)
    t.eq(core.render_avm_scoreboard_bucket(by_level.L1):find("TOTAL", 1, true), nil)
  end,

  test_dashboard_renders_avm_scoreboard_from_trusted_ledger_markers = function()
    mock_dashboard_env()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local first_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local second_version = first_version .. "/reimplement/2"
    local head_sha = "def456"
    local record = autonomy_record({
      proposal_id = proposal_id,
      version = second_version,
      head_sha = head_sha,
      task_class = "L4",
      rounds = 2,
      codex_calls = 8,
        gates = {
          human_touch = "pass",
          pre_merge_ci = "pass",
          evidence_manifest = "pass",
          post_merge_probe = "pass",
          no_revert_reopen = "pending",
          cost_budget = "pass",
        },
    })
    local comments = {
      trusted_comment(attempt_marker(proposal_id, first_version, 1, "100"), "2026-06-03T01:00:00Z", 1001),
      trusted_comment(core.state_marker(proposal_id, "blocked", first_version), "2026-06-03T01:10:00Z", 1002),
      trusted_comment(attempt_marker(proposal_id, second_version, 2, "200"), "2026-06-03T01:20:00Z", 1003),
      trusted_comment(m_builders.merged_marker(core, proposal_id, "7", second_version, head_sha, record), "2026-06-03T01:30:00Z", 1004),
      untrusted_comment(m_builders.merged_marker(core, proposal_id, "7", second_version, head_sha, record), "2026-06-03T01:31:00Z", 1005),
    }
    local dashboard = core.render_observability_dashboard({
      entities = {
        {
          proposal_id = proposal_id,
          issue_number = 42,
          pr_number = 7,
          title = "Security API recovery change",
          state = { state = "merged", version = second_version },
          parent_issue = { comments = comments },
          pr = { comments = comments },
        },
        {
          proposal_id = "github-devloop/issue/owner/repo/43",
          issue_number = 43,
          pr_number = 8,
          title = "Unclassified change",
          autonomy_results = {
            {
              proposal_id = "github-devloop/issue/owner/repo/43",
              pr_number = 8,
              version = "v3",
              head_sha = "fedcba",
              task_class = "unknown",
              valid_autonomous_merge = "pending",
              rounds = 1,
              gates = { no_revert_reopen = "pending" },
            },
          },
        },
      },
      counts = { merged = 1 },
      stalls = {},
      topology_mermaid = "",
      now_seconds = 1770000000,
    })

    t.is_true(dashboard.body:find("## AVM scoreboard by task level", 1, true) ~= nil)
    t.is_true(dashboard.body:find(
      "- L4 merges=1 AVM-rate=0/2 (0%) cost-per-AVM=n/a revert-rate=n/a median-rounds=2 false-consensus-rate=n/a",
      1,
      true
    ) ~= nil)
    t.is_true(dashboard.body:find("- unclassified merges=1 AVM-rate=0/1 (0%) cost-per-AVM=unknown", 1, true) ~= nil)
    t.eq(dashboard.body:find("TOTAL", 1, true), nil)
  end,

  test_false_consensus_detector_flags_explicit_merged_revert_pr = function()
    mock_dashboard_env()
    local proposal_id = "github-devloop/issue/owner/repo/44"
    local version = "ready/consensus-github-devloop/issue/owner/repo/44/2026-06-03T01-02-03Z"
    local head_sha = "abcdef1"
    local record = autonomy_record({
      proposal_id = proposal_id,
      issue_number = "44",
      pr_number = "9",
      version = version,
      head_sha = head_sha,
      task_class = "L2",
      codex_calls = 3,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    })
    local recent_prs = {
      {
        number = 9,
        title = "Implement AVM fact",
        merged_at = "2026-06-03T01:30:00Z",
        comments = {
          trusted_comment(m_builders.merged_marker(core, proposal_id, "9", version, head_sha, record), "2026-06-03T01:30:00Z", 2001),
        },
      },
      {
        number = 10,
        title = "Revert \"Implement AVM fact\" (#9)",
        body = "Reverts #9.",
        merged_at = "2026-06-03T02:30:00Z",
        comments = {},
      },
    }
    local facts = core.collect_avm_scoreboard_facts({}, 1770000000, recent_prs)
    local rows = core.aggregate_avm_scoreboard(facts)
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end

    t.eq(by_level.L2.false_consensus_numerator, 1)
    t.eq(by_level.L2.false_consensus_denominator, 1)
    t.eq(by_level.L2.revert_numerator, 1)
    t.eq(by_level.L2.revert_denominator, 1)
    local pairs = core.false_consensus_pairs(facts)
    t.eq(#pairs, 1)
    t.eq(pairs[1].reverted_pr, 9)
    t.eq(pairs[1].revert_pr, 10)
    t.eq(pairs[1].evidence, "explicit-revert-pr")
  end,

  test_false_consensus_detector_flags_direct_revert_commit = function()
    mock_dashboard_env()
    local proposal_id = "github-devloop/issue/owner/repo/45"
    local version = "ready/consensus-github-devloop/issue/owner/repo/45/2026-06-03T01-02-03Z"
    local head_sha = "abcdef2"
    local record = autonomy_record({
      proposal_id = proposal_id,
      issue_number = "45",
      pr_number = "11",
      version = version,
      head_sha = head_sha,
      task_class = "L2",
      codex_calls = 3,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    })
    local facts = core.collect_avm_scoreboard_facts({}, 1770000000, {
      {
        number = 11,
        title = "Implement AVM fact",
        merged_at = "2026-06-03T01:30:00Z",
        comments = {
          trusted_comment(m_builders.merged_marker(core, proposal_id, "11", version, head_sha, record), "2026-06-03T01:30:00Z", 2021),
        },
      },
    }, {}, {
      {
        sha = "abc1234",
        subject = "Revert \"Implement AVM fact\"",
        message = "This reverts PR #11.",
        committed_at = "2026-06-04T01:30:00Z",
      },
    })
    local rows = core.aggregate_avm_scoreboard(facts)
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end

    t.eq(by_level.L2.false_consensus_numerator, 1)
    t.eq(by_level.L2.revert_numerator, 1)
    local pairs = core.false_consensus_pairs(facts)
    t.eq(#pairs, 1)
    t.eq(pairs[1].reverted_pr, 11)
    t.eq(pairs[1].revert_commit, "abc1234")
    t.eq(pairs[1].evidence, "revert-commit")
  end,

  test_avm_scoreboard_promotes_no_revert_gate_after_clean_window = function()
    mock_dashboard_env()
    local proposal_id = "github-devloop/issue/owner/repo/47"
    local version = "ready/consensus-github-devloop/issue/owner/repo/47/2026-06-03T01-02-03Z"
    local head_sha = "abc4747"
    local record = autonomy_record({
      proposal_id = proposal_id,
      issue_number = "47",
      pr_number = "14",
      version = version,
      head_sha = head_sha,
      task_class = "L1",
      codex_calls = 2,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pending",
        cost_budget = "pass",
      },
    })
    local recent_prs = {
      {
        number = 14,
        title = "Implement stable AVM gate",
        merged_at = "2026-06-03T01:30:00Z",
        comments = {
          trusted_comment(m_builders.merged_marker(core, proposal_id, "14", version, head_sha, record), "2026-06-03T01:30:00Z", 2011),
        },
      },
    }
    local recent_issues = {
      {
        number = 47,
        title = "Implement stable AVM gate",
        state = "CLOSED",
        state_reason = "COMPLETED",
        comments = {},
      },
    }
    local facts = core.collect_avm_scoreboard_facts({}, 1781227800, recent_prs, recent_issues)
    t.eq(facts[1].gates.no_revert_reopen, "pending")

    recent_prs[1].no_revert_reopen_scan = no_revert_scan()
    facts = core.collect_avm_scoreboard_facts({}, 1781227800, recent_prs, recent_issues)
    local rows = core.aggregate_avm_scoreboard(facts)
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end

    t.eq(facts[1].gates.no_revert_reopen, "pass")
    t.eq(facts[1].valid_autonomous_merge, "true")
    t.eq(by_level.L1.avm_numerator, 1)
    t.eq(by_level.L1.avm_denominator, 1)
    t.eq(by_level.L1.revert_numerator, 0)
    t.eq(by_level.L1.revert_denominator, 1)
  end,

  test_false_consensus_detector_requires_exact_pr_reference = function()
    mock_dashboard_env()
    local record = autonomy_record({
      proposal_id = "github-devloop/issue/owner/repo/46",
      issue_number = "46",
      pr_number = "12",
      version = "ready/consensus-github-devloop/issue/owner/repo/46/2026-06-03T01-02-03Z",
      head_sha = "abc1212",
      task_class = "L2",
      gates = { no_revert_reopen = "pass" },
    })
    local facts = core.collect_avm_scoreboard_facts({}, 1770000000, {
      {
        number = 12,
        merged_at = "2026-06-03T01:30:00Z",
        comments = {
          trusted_comment(m_builders.merged_marker(core, record.proposal_id, "12", record.version, record.head_sha, record), "2026-06-03T01:30:00Z", 2002),
        },
      },
      {
        number = 13,
        title = "Revert unrelated change (#123)",
        body = "Reverts #123.",
        merged_at = "2026-06-03T02:30:00Z",
        comments = {},
      },
    })
    local pairs = core.false_consensus_pairs(facts)
    t.eq(#pairs, 0)
  end,

  test_recent_merged_issues_feed_closed_issue_autonomy_markers_to_avm_scoreboard = function()
    mock_dashboard_env()
    author_policy.mock_env(t, {
      env = {
        FKST_DEVLOOP_MANAGED_BOT_LOGINS = "loning,ElonSG",
      },
    }, {
      times = 8,
    })
    local marker = '<!-- fkst:github-devloop:autonomy-result:v1'
      .. ' proposal="github-devloop/issue/ChronoAIProject/fkst-packages/1649"'
      .. ' repo="ChronoAIProject/fkst-packages"'
      .. ' issue="1649"'
      .. ' pr="1650"'
      .. ' version="ready/consensus-github-devloop/issue/ChronoAIProject/fkst-packages/1649/intake/1746481111/loop/1"'
      .. ' head_sha="dc70f738081a09e25769cc7568c3c8c14f830d25"'
      .. ' task_class="L2"'
      .. ' human_touch_count="0"'
      .. ' pre_merge_ci="pass"'
      .. ' rounds="2"'
      .. ' retry_count="0"'
      .. ' codex_calls="5"'
      .. ' gate_human_touch="pass"'
      .. ' gate_evidence_manifest="pass"'
      .. ' gate_post_merge_probe="pending"'
      .. ' post_merge_probe_green="pending"'
      .. ' gate_no_revert_reopen="pending"'
      .. ' gate_cost_budget="pass"'
      .. ' valid_autonomous_merge="pending"'
      .. ' -->'
    entity_read_mocks.mock_issue_list_command(t, core.gh_issue_list_recent_closed_cmd("owner/repo", 25), {
      {
        number = 1649,
        title = "AVM scoreboard still reads merges=0 after #1646",
        closed_at = "2026-06-29T03:44:36Z",
        labels = { "fkst-dev:enabled", "fkst-dev:merged" },
      },
    })
    entity_read_mocks.mock_issue_view_selector(t, {
      number = 1649,
      title = "AVM scoreboard still reads merges=0 after #1646",
      state = "CLOSED",
      labels = { "fkst-dev:enabled", "fkst-dev:merged" },
      comments = {
        authored_comment(marker, "loning", "2026-06-29T03:44:36Z", 4001),
      },
    }, "title,body,comments,state,stateReason,assignees,author")

    local recent_issues = core.collect_recent_merged_issues("owner/repo", core.observability_limits(), now() + 90)
    local facts = core.collect_avm_scoreboard_facts({}, 1770000000, {}, recent_issues)
    local rows = core.aggregate_avm_scoreboard(facts)
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end

    t.eq(#recent_issues, 1)
    t.eq(by_level.L2.merges, 1)
    t.eq(by_level.L2.avm_denominator, 1)
    t.eq(core.render_avm_scoreboard_bucket(by_level.L2):find("AVM-rate=0/1 (0%)", 1, true) ~= nil, true)
  end,

  test_avm_scoreboard_uses_managed_bot_trust_and_logs_marker_rejections = function()
    mock_dashboard_env()
    author_policy.mock_env(t, {
      env = {
        FKST_DEVLOOP_MANAGED_BOT_LOGINS = "loning,ElonSG",
      },
    }, {
      times = 8,
    })
    local record = autonomy_record({
      proposal_id = "github-devloop/issue/owner/repo/1655",
      issue_number = "1655",
      pr_number = "1656",
      version = "ready/consensus-github-devloop/issue/owner/repo/1655/2026-06-29T05-36-07Z",
      head_sha = "dc70f738081a09e25769cc7568c3c8c14f830d25",
      task_class = "L3",
      codex_calls = 3,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pending",
        no_revert_reopen = "pending",
        cost_budget = "pass",
      },
    })
    local valid_marker = autonomy_ledger.autonomy_result_marker(record)
    local malformed_marker = '<!-- fkst:github-devloop:autonomy-result:v1'
      .. ' proposal="github-devloop/issue/owner/repo/1655"'
      .. ' pr="1656"'
      .. ' -->'
    local comments = {
      authored_comment(valid_marker, "ElonSG", "2026-06-29T05:40:00Z", 4101),
      authored_comment(valid_marker, "mallory", "2026-06-29T05:41:00Z", 4102),
      authored_comment(malformed_marker, "loning", "2026-06-29T05:42:00Z", 4103),
    }

    local facts, logs = capture_warn_logs(function()
      return core.collect_avm_scoreboard_facts({ { comments = comments } }, 1770000000, {}, {})
    end)
    local rows = core.aggregate_avm_scoreboard(facts)
    local by_level = {}
    for _, row in ipairs(rows) do
      by_level[row.level] = row
    end
    local joined_logs = table.concat(logs, "\n")

    t.eq(by_level.L3.merges, 1)
    t.is_true(joined_logs:find("tag=AVM_MARKER_COMMENT_REJECTED reason=untrusted_author author=mallory", 1, true) ~= nil)
    t.is_true(joined_logs:find("tag=AVM_MARKER_REJECTED reason=missing_identity author=loning", 1, true) ~= nil)
  end,

  test_dashboard_lists_false_consensus_churn_pairs = function()
    mock_dashboard_env()
    local proposal_id = "github-devloop/issue/owner/repo/45"
    local version = "ready/consensus-github-devloop/issue/owner/repo/45/2026-06-03T01-02-03Z"
    local head_sha = "abc9999"
    local record = autonomy_record({
      proposal_id = proposal_id,
      issue_number = "45",
      pr_number = "12",
      version = version,
      head_sha = head_sha,
      task_class = "L1",
      codex_calls = 2,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    })
    local dashboard = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      recent_merged_prs = {
        {
          number = 12,
          merged_at = "2026-06-03T01:30:00Z",
          comments = {
            trusted_comment(m_builders.merged_marker(core, proposal_id, "12", version, head_sha, record), "2026-06-03T01:30:00Z", 3001),
          },
        },
        {
          number = 13,
          title = "Revert \"Change that passed review\" (#12)",
          body = "Reverts #12.",
          merged_at = "2026-06-03T02:30:00Z",
          comments = {},
        },
      },
      now_seconds = 1770000000,
    })

    t.is_true(dashboard.body:find("## False consensus churn", 1, true) ~= nil)
    t.is_true(dashboard.body:find("PR #12 reverted-by PR #13 evidence=explicit-revert-pr", 1, true) ~= nil)
    t.is_true(dashboard.body:find("false-consensus-rate=1/1 (100%)", 1, true) ~= nil)
  end,

  test_dashboard_renders_large_topology_without_old_cap_cutting_mermaid = function()
    mock_dashboard_env()
    local mermaid = large_mermaid(900)
    t.is_true(#mermaid > old_dashboard_body_cap)

    local dashboard = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      topology_mermaid = mermaid,
      now_seconds = 1770000000,
    })

    t.is_true(#dashboard.body > old_dashboard_body_cap)
    t.is_true(dashboard.body:find("node_900 --> node_901", 1, true) ~= nil)
    t.is_true(dashboard.body:find("## Board by state", 1, true) ~= nil)
    t.is_true(dashboard.body:find("## Ready", 1, true) ~= nil)
    t.is_true(dashboard.body:find("## Blocked", 1, true) ~= nil)
    t.is_true(dashboard.body:find("## Stall suspects", 1, true) ~= nil)
    t.is_true(dashboard.body:find("## Footer", 1, true) ~= nil)
    assert_dashboard_marker_outside_fences(dashboard.body)
  end,

  test_dashboard_forced_cap_drops_whole_sections_without_open_fence = function()
    mock_dashboard_env()
    local forced_cap = 2500
    local dashboard = core.render_observability_dashboard({
      entities = {},
      counts = {},
      stalls = {},
      topology_mermaid = large_mermaid(900),
      now_seconds = 1770000000,
      max_body_len = forced_cap,
    })

    t.is_true(#dashboard.body <= forced_cap)
    t.eq(dashboard.body:find("node_900 --> node_901", 1, true), nil)
    assert_dashboard_marker_outside_fences(dashboard.body)
  end,
}
