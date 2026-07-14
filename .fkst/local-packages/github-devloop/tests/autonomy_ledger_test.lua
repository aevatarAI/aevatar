local h = require("tests.devloop_core_helpers")
local m_facts = require("devloop.markers.facts")
local core = h.core
local t = h.t
local contract_time = require("contract.time")
local no_revert_reopen = require("devloop.autonomy.no_revert_reopen")
local autonomy_ledger = require("devloop.autonomy_ledger")
local m_builders = require("devloop.markers.builders")

local function mock_check_runs(json)
  t.mock_command("gh api 'repos/owner/repo/commits/def456/check-runs'", {
    stdout = json,
    stderr = "",
    exit_code = 0,
  })
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

local function trusted_comment(body, created_at, id)
  return {
    id = id,
    body = body,
    author_login = "fkst-test-bot",
    created_at = created_at,
    url = "https://github.example/owner/repo/issues/42#issuecomment-" .. tostring(id or 1),
  }
end

local function ledger_event(fields)
  local event = {
    kind = "claim",
    version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
    stage_rank = 100,
    sequence = 1,
  }
  for key, value in pairs(fields or {}) do
    event[key] = value
  end
  return event
end

local function assert_not_before(left, right)
  t.eq(autonomy_ledger._autonomy_event_before(core, left, right), false)
end

local function assert_strictly_before(left, right)
  t.is_true(autonomy_ledger._autonomy_event_before(core, left, right))
  assert_not_before(right, left)
end

local function assert_transitive(events)
  for _, a in ipairs(events) do
    for _, b in ipairs(events) do
      for _, c in ipairs(events) do
        if autonomy_ledger._autonomy_event_before(core, a, b) and autonomy_ledger._autonomy_event_before(core, b, c) then
          t.is_true(autonomy_ledger._autonomy_event_before(core, a, c))
        end
      end
    end
  end
end

return {
  test_autonomy_event_comparator_uses_global_nil_created_seconds_policy = function()
    local earlier = ledger_event({ comment_created_at = "2026-06-03T01:00:00Z", sequence = 30 })
    local later = ledger_event({ comment_created_at = "2026-06-03T01:00:01Z", sequence = 20 })
    local same_time_low_sequence = ledger_event({ comment_created_at = "2026-06-03T01:00:01Z", sequence = 1 })
    local same_time_high_sequence = ledger_event({ comment_created_at = "2026-06-03T01:00:01Z", sequence = 2 })
    local nil_low_sequence = ledger_event({ sequence = 1 })
    local nil_high_sequence = ledger_event({ sequence = 2 })

    assert_strictly_before(earlier, later)
    assert_strictly_before(same_time_low_sequence, same_time_high_sequence)
    assert_strictly_before(later, nil_low_sequence)
    assert_not_before(nil_low_sequence, later)
    assert_strictly_before(nil_low_sequence, nil_high_sequence)

    assert_transitive({
      ledger_event({ sequence = 1 }),
      ledger_event({ comment_created_at = "2026-06-03T01:00:00Z", sequence = 2 }),
      ledger_event({ comment_created_at = "2026-06-03T01:00:01Z", sequence = 0 }),
      ledger_event({ sequence = 3 }),
    })
  end,

  test_valid_autonomous_merge_stays_pending_until_all_required_gates_pass = function()
    local gates = {
      human_touch = "pass",
      pre_merge_ci = "pass",
      evidence_manifest = "pending",
      post_merge_probe = "pending",
      no_revert_reopen = "pending",
      cost_budget = "pending",
    }
    t.eq(autonomy_ledger.autonomy_valid_autonomous_merge(core, gates), "pending")

    gates.evidence_manifest = "pass"
    gates.post_merge_probe = "pass"
    gates.no_revert_reopen = "pass"
    gates.cost_budget = "pass"
    t.eq(autonomy_ledger.autonomy_valid_autonomous_merge(core, gates), "true")

    gates.cost_budget = "fail"
    t.eq(autonomy_ledger.autonomy_valid_autonomous_merge(core, gates), "false")
  end,

  test_autonomy_result_marker_recomputes_pending_predicate = function()
    local record = {
      proposal_id = "github-devloop/issue/owner/repo/42",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/2",
      head_sha = "def456",
      task_class = "L2",
      human_touch_count = 0,
      pre_merge_ci = "pass",
      rounds = 2,
      retry_count = 2,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pending",
        post_merge_probe = "pending",
        no_revert_reopen = "pending",
        cost_budget = "pending",
      },
      valid_autonomous_merge = "true",
    }

    local marker = autonomy_ledger.autonomy_result_marker(core, record)
    t.is_true(marker:find('valid_autonomous_merge="pending"', 1, true) ~= nil)
    t.is_true(marker:find('codex_calls="null"', 1, true) ~= nil)
    t.is_true(marker:find('post_merge_probe_green="pending"', 1, true) ~= nil)
    local fact = autonomy_ledger.autonomy_result_fact(core, { marker }, record.proposal_id, record.pr_number, record.version, record.head_sha)
    t.eq(fact.valid_autonomous_merge, "pending")
    t.eq(fact.task_class, "L2")
    t.eq(fact.retry_count, 2)
    t.eq(fact.codex_calls, nil)
  end,

  test_post_merge_probe_gate_uses_existing_rollup_and_fails_closed = function()
    local green_gate = autonomy_ledger.autonomy_post_merge_probe_gate(core, {
      head_sha = "def456",
      status_check_rollup = {
        { status = "COMPLETED", conclusion = "SUCCESS" },
      },
    })
    t.eq(green_gate, "pass")

    local red_gate = autonomy_ledger.autonomy_post_merge_probe_gate(core, {
      head_sha = "def456",
      status_check_rollup = {
        { status = "COMPLETED", conclusion = "FAILURE" },
      },
    })
    t.eq(red_gate, "fail")

    mock_check_runs('{"total_count":0,"check_runs":[]}\n')
    local missing_gate = autonomy_ledger.autonomy_post_merge_probe_gate(core, {
      head_sha = "def456",
      status_check_rollup = {},
    }, { repo = "owner/repo" })
    t.eq(missing_gate, "fail")
  end,

  test_no_revert_reopen_gate_is_pending_fail_then_pass_from_source_facts = function()
    local fact = {
      proposal_id = "github-devloop/issue/owner/repo/42",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = "v1",
      head_sha = "def456",
      merged_at = "2026-06-03T01:30:00Z",
    }
    local closed_issue = {
      number = 42,
      state = "CLOSED",
      state_reason = "COMPLETED",
    }
    local clean_scan = {
      issue = closed_issue,
      recent_merged_prs = {
        { number = 7, title = "Implement AVM fact", merged_at = "2026-06-03T01:30:00Z" },
      },
      recent_merged_issues = { closed_issue },
    }

    local before_window = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-05T01:30:00Z"),
    })
    t.eq(before_window, "pending")

    local reverted = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = {
        clean_scan.recent_merged_prs[1],
        { number = 8, title = "Revert \"Implement AVM fact\" (#7)", body = "Reverts #7.", merged_at = "2026-06-04T01:30:00Z" },
      },
      recent_merged_issues = clean_scan.recent_merged_issues,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(reverted, "fail")

    local reopened = no_revert_reopen.gate(fact, {
      issue = {
        number = 42,
        state = "OPEN",
        state_reason = "REOPENED",
        updated_at = "2026-06-04T01:30:00Z",
      },
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(reopened, "fail")

    local after_window_without_scan = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(after_window_without_scan, "pending")

    local incomplete_commit_scan = no_revert_scan()
    incomplete_commit_scan.revert_commits_complete = nil
    local after_window_without_commit_scan = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      no_revert_reopen_scan = incomplete_commit_scan,
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(after_window_without_commit_scan, "pending")

    local after_window = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      no_revert_reopen_scan = no_revert_scan(),
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(after_window, "pass")

    local commit_reverted = no_revert_reopen.gate(fact, {
      issue = clean_scan.issue,
      recent_merged_prs = clean_scan.recent_merged_prs,
      recent_merged_issues = clean_scan.recent_merged_issues,
      revert_commits = {
        {
          sha = "abc1234",
          subject = "Revert \"Implement AVM fact\"",
          message = "This reverts PR #7.",
          committed_at = "2026-06-04T01:30:00Z",
        },
      },
      no_revert_reopen_scan = no_revert_scan(),
      now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
    })
    t.eq(commit_reverted, "fail")
  end,

  test_merged_marker_carries_canonical_autonomy_result_record = function()
    local record = {
      proposal_id = "github-devloop/issue/owner/repo/42",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      head_sha = "def456",
      task_class = "L2",
      human_touch_count = 0,
      rounds = 1,
      retry_count = 0,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pending",
        post_merge_probe = "pending",
        no_revert_reopen = "pending",
        cost_budget = "pending",
      },
    }

    local marker = m_builders.merged_marker(core, record.proposal_id, record.pr_number, record.version, record.head_sha, record)
    t.is_true(marker:find("fkst:github-devloop:merged:v1", 1, true) ~= nil)
    t.is_true(marker:find('autonomy_result="v1"', 1, true) ~= nil)
    t.is_true(marker:find('valid_autonomous_merge="pending"', 1, true) ~= nil)
    t.is_true(marker:find('gate_evidence_manifest="pending"', 1, true) ~= nil)
    local fact = m_facts.merged_fact(core, { marker }, record.proposal_id, record.pr_number, record.version)
    t.eq(fact.autonomy_result.valid_autonomous_merge, "pending")
    t.eq(fact.autonomy_result.task_class, "L2")
  end,

  test_autonomy_result_fact_recomputes_predicate_from_parsed_gates = function()
    local record = {
      proposal_id = "github-devloop/issue/owner/repo/42",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/2",
      head_sha = "def456",
      task_class = "L2",
      human_touch_count = 0,
      rounds = 2,
      retry_count = 2,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pending",
        post_merge_probe = "pending",
        no_revert_reopen = "pending",
        cost_budget = "pending",
      },
    }

    local marker = autonomy_ledger.autonomy_result_marker(core, record):gsub(
      'valid_autonomous_merge="pending"',
      'valid_autonomous_merge="true"'
    )
    local fact = autonomy_ledger.autonomy_result_fact(core, { marker }, record.proposal_id, record.pr_number, record.version, record.head_sha)
    t.eq(fact.valid_autonomous_merge, "pending")
  end,

  test_autonomy_auditor_rejects_forged_green_probe_without_matching_run = function()
    local record = {
      proposal_id = "github-devloop/issue/owner/repo/42",
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/2",
      head_sha = "def456",
      task_class = "L2",
      human_touch_count = 0,
      pre_merge_ci = "pass",
      rounds = 2,
      retry_count = 2,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    }

    mock_check_runs('{"total_count":0,"check_runs":[]}\n')
    local marker = autonomy_ledger.autonomy_result_marker(core, record)
    local fact = autonomy_ledger.autonomy_audited_result_fact(
      core,
      { marker },
      record.proposal_id,
      record.pr_number,
      record.version,
      record.head_sha,
      { repo = "owner/repo", merge_commit_sha = "def456" }
    )
    t.eq(fact.valid_autonomous_merge, "invalid_self_attested")
    t.eq(fact.gates.post_merge_probe, "fail")
    t.eq(fact.audit_reason, "missing-status-rollup")
    t.eq(fact.audit_gates.post_merge_probe, "fail")
  end,

  test_autonomy_auditor_promotes_no_revert_gate_and_attempt_projection_after_window = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local head_sha = "abcdef1"
    local autonomy_record = {
      proposal_id = proposal_id,
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = version,
      head_sha = head_sha,
      task_class = "L1",
      human_touch_count = 0,
      rounds = 1,
      retry_count = 0,
      codex_calls = 3,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pending",
        cost_budget = "pass",
      },
    }
    local comments = {
      trusted_comment(core.implement_attempt_marker(proposal_id, version, 1, "100"), "2026-06-03T01:00:00Z", 1101),
      trusted_comment(autonomy_ledger.autonomy_result_marker(core, autonomy_record), "2026-06-03T01:31:00Z", 1103),
      trusted_comment(m_builders.merged_marker(core, proposal_id, "7", version, head_sha, autonomy_record), "2026-06-03T01:30:00Z", 1102),
    }

    local fact = autonomy_ledger.autonomy_audited_result_fact(
      core,
      comments,
      proposal_id,
      "7",
      version,
      head_sha,
      {
        repo = "owner/repo",
        merge_commit_sha = head_sha,
        merged_at = "2026-06-03T01:30:00Z",
        issue = { number = 42, state = "CLOSED", state_reason = "COMPLETED" },
        recent_merged_prs = {
          { number = 7, title = "Implement AVM fact", merged_at = "2026-06-03T01:30:00Z" },
        },
        recent_merged_issues = {
          { number = 42, state = "CLOSED", state_reason = "COMPLETED" },
        },
        no_revert_reopen_scan = no_revert_scan(),
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
        status_check_rollup = {
          { status = "COMPLETED", conclusion = "SUCCESS" },
        },
      }
    )

    t.eq(fact.gates.no_revert_reopen, "pass")
    t.eq(fact.valid_autonomous_merge, "true")
    t.eq(fact.avm_rate_numerator, 1)
    t.eq(fact.avm_rate_denominator, 1)
    t.eq(fact.attempt_projection.valid_merges, 1)
    t.eq(fact.attempts[1].autonomy_result.valid_autonomous_merge, "true")
  end,

  test_autonomy_attempt_projection_counts_reattempts_from_existing_markers = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local first_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local second_version = first_version .. "/reimplement/2"
    local head_sha = "abcdef1"
    local autonomy_record = {
      proposal_id = proposal_id,
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = second_version,
      head_sha = head_sha,
      task_class = "L2",
      human_touch_count = 0,
      rounds = 2,
      retry_count = 0,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    }
    local comments = {
      trusted_comment(core.implement_attempt_marker(proposal_id, first_version, 1, "100"), "2026-06-03T01:00:00Z", 1001),
      trusted_comment(core.state_marker(proposal_id, "blocked", first_version), "2026-06-03T01:10:00Z", 1002),
      trusted_comment(core.implement_attempt_marker(proposal_id, second_version, 2, "200"), "2026-06-03T01:20:00Z", 1003),
      trusted_comment(m_builders.merged_marker(core, proposal_id, "7", second_version, head_sha, autonomy_record), "2026-06-03T01:30:00Z", 1004),
    }

    local projection = autonomy_ledger.autonomy_attempt_projection(core, comments, "owner/repo", "42")
    t.eq(projection.total_attempts, 2)
    t.eq(projection.outcomes.blocked, 1)
    t.eq(projection.outcomes.merged, 1)
    t.eq(projection.valid_merges, 1)
    t.eq(projection.attempts[1].claim_marker_id, 1001)
    t.eq(projection.attempts[1].outcome, "blocked")
    t.eq(projection.attempts[2].claim_marker_id, 1003)
    t.eq(projection.attempts[2].outcome, "merged")
    t.eq(projection.attempts[2].autonomy_result.valid_autonomous_merge, "true")
    t.eq(autonomy_ledger.autonomy_attempt_denominator(core, comments, "owner/repo", "42"), 2)
  end,

  test_autonomy_attempt_projection_ignores_untrusted_attempt_markers = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local comments = {
      {
        body = core.implement_attempt_marker(proposal_id, version, 1, "100"),
        author_login = "mallory",
        created_at = "2026-06-03T01:00:00Z",
      },
      trusted_comment(core.implement_attempt_marker(proposal_id, version, 1, "100"), "2026-06-03T01:01:00Z", 1001),
    }

    local projection = autonomy_ledger.autonomy_attempt_projection(core, comments, "owner/repo", "42")
    t.eq(projection.total_attempts, 1)
    t.eq(projection.attempts[1].claim_marker_id, 1001)
  end,

  test_autonomy_auditor_exposes_derived_attempt_projection = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local first_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local second_version = first_version .. "/reimplement/2"
    local head_sha = "def456"
    local autonomy_record = {
      proposal_id = proposal_id,
      repo = "owner/repo",
      issue_number = "42",
      pr_number = "7",
      version = second_version,
      head_sha = head_sha,
      task_class = "L2",
      human_touch_count = 0,
      rounds = 2,
      retry_count = 0,
      codex_calls = nil,
      gates = {
        human_touch = "pass",
        pre_merge_ci = "pass",
        evidence_manifest = "pass",
        post_merge_probe = "pass",
        no_revert_reopen = "pass",
        cost_budget = "pass",
      },
    }
    local comments = {
      trusted_comment(core.implement_attempt_marker(proposal_id, first_version, 1, "100"), "2026-06-03T01:00:00Z", 1001),
      trusted_comment(core.state_marker(proposal_id, "blocked", first_version), "2026-06-03T01:10:00Z", 1002),
      trusted_comment(core.implement_attempt_marker(proposal_id, second_version, 2, "200"), "2026-06-03T01:20:00Z", 1003),
      trusted_comment(autonomy_ledger.autonomy_result_marker(core, autonomy_record), "2026-06-03T01:31:00Z", 1005),
      trusted_comment(m_builders.merged_marker(core, proposal_id, "7", second_version, head_sha, autonomy_record), "2026-06-03T01:30:00Z", 1004),
    }

    local fact = autonomy_ledger.autonomy_audited_result_fact(
      core,
      comments,
      proposal_id,
      "7",
      second_version,
      head_sha,
      {
        repo = "owner/repo",
        merge_commit_sha = head_sha,
        merged_at = "2026-06-03T01:30:00Z",
        issue = { number = 42, state = "CLOSED", state_reason = "COMPLETED" },
        recent_merged_prs = {
          { number = 7, title = "Implement AVM fact", merged_at = "2026-06-03T01:30:00Z" },
        },
        recent_merged_issues = {
          { number = 42, state = "CLOSED", state_reason = "COMPLETED" },
        },
        no_revert_reopen_scan = no_revert_scan(),
        now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-12T01:30:00Z"),
        status_check_rollup = {
          { status = "COMPLETED", conclusion = "SUCCESS" },
        },
      }
    )

    t.eq(fact.avm_rate_denominator, 2)
    t.eq(fact.avm_rate_numerator, 1)
    t.eq(fact.attempt_projection.total_attempts, 2)
    t.eq(fact.attempt_outcomes.blocked, 1)
    t.eq(fact.attempt_outcomes.merged, 1)
    t.eq(fact.attempts[1].claim_evidence.comment_id, 1001)
    t.eq(fact.attempts[1].terminal_evidence.comment_id, 1002)
    t.eq(fact.attempts[2].claim_evidence.comment_id, 1003)
    t.eq(fact.attempts[2].terminal_evidence.comment_id, 1004)
  end,

  test_task_class_uses_explicit_label_before_title_fallback = function()
    t.eq(autonomy_ledger.autonomy_task_class(core, {
      title = "fix scheduler regression",
      labels = { "fkst-avm:L4" },
    }), "L4")
    t.eq(autonomy_ledger.autonomy_task_class(core, {
      title = "docs: update readme",
      labels = {},
    }), "L0")
    t.eq(autonomy_ledger.autonomy_task_class(core, {
      title = "Add useful thing",
      labels = {},
    }), "unknown")
  end,
}
