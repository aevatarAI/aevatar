local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local convergence_shared = require("devloop.convergence.shared")
local h = require("tests.devloop_helpers")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local t = h.t
local core = h.core
local decompose_lib = require("devloop.decompose")
local issue = h.issue
local reached = h.reached
local decompose_event = h.decompose_event
local opts = h.opts
local source_ref = h.source_ref
local run_observe = h.run_observe
local run_implement = h.run_implement
local run_review_pr = h.run_review_pr
local mock_issue_state = h.mock_issue_state
local mock_issue_implement_raw = h.mock_issue_implement_raw
local mock_issue_review = h.mock_issue_review
local count_calls = h.count_calls
local find_raise = h.find_raise
local find_causal_raise = h.find_causal_raise
local render_comment = h.render_comment
local json_string = h.json_string
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local codex_status = require("tests.codex_status_helpers")
local m_builders = require("devloop.markers.builders")
local proposal_id = "github-devloop/issue/owner/repo/42"

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

local function mock_linked_pr_state(comments, state, exit_code, times)
  local rendered_comments = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(rendered_comments, render_comment(comment))
  end
  local stderr = ""
  if exit_code ~= nil and exit_code ~= 0 then
    stderr = "pr view failed"
  end
  entity_read_mocks.mock_pr_view_raw_selector(t, {}, entity_read_mocks.pr_origin_selector, {
    stdout = string.format(
      '{"headRefName":"devloop-owner-repo-42-01HY","headRefOid":"def456","baseRefName":"dev","state":"%s","updatedAt":"2026-06-03T02:03:04Z","comments":[%s]}\n',
      json_string(state or "OPEN"),
      table.concat(rendered_comments, ",")
    ),
    stderr = stderr,
    exit_code = exit_code or 0,
  }, times or 1)
end

local function mock_decompose_child_issue_list(event, indexes)
  local rendered = {}
  for _, index in ipairs(indexes or {}) do
    table.insert(rendered, string.format(
      '{"number":%d,"title":"Child %d","state":"OPEN","author":{"login":"fkst-test-bot"},"body":"%s","url":"https://github.example/owner/repo/issues/%d"}',
      100 + index,
      index,
      json_string(decompose_lib.decompose_child_marker(core, event.proposal_id, event.version, event.pr_number, index)),
      100 + index
    ))
  end
  t.mock_command(core.gh_issue_list_decompose_children_cmd("owner/repo", event.proposal_id), {
    stdout = "[" .. table.concat(rendered, ",") .. "]\n",
    stderr = "",
    exit_code = 0,
  })
end

local function merge_gate_fix_marker(event)
  return m_builders.merge_gate_marker(core, 
    event.proposal_id,
    event.pr_number,
    event.version,
    event.review_proposal_id,
    event.review_dedup_key,
    event.head_sha,
    nil,
    "rollup-red"
  )
end

local function assert_ready_redrive(result, expected_proposal_id, expected_dedup_key)
  t.eq(result.exit_code, 0)
  t.eq(find_raise(result.raises, "devloop_reviewing"), nil)
  t.eq(find_raise(result.raises, "devloop_merge_ready"), nil)
  local ready = find_raise(result.raises, "devloop_ready")
  t.is_true(ready ~= nil)
  t.eq(ready.payload.proposal_id, expected_proposal_id)
  t.eq(ready.payload.dedup_key, expected_dedup_key)
  t.eq(ready.payload.source_ref.ref, "owner/repo#issue/42")
end

local function assert_merged_terminal(result)
  t.eq(result.exit_code, 0)
  t.eq(find_raise(result.raises, "devloop_ready"), nil)
  t.eq(find_raise(result.raises, "devloop_reviewing"), nil)
  t.eq(find_raise(result.raises, "devloop_merge_ready"), nil)
  local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
  t.is_true(comment ~= nil)
  t.is_true(tostring(comment.payload.body):find('state="merged"', 1, true) ~= nil)
  t.is_true(tostring(comment.payload.body):find("fkst:github-devloop:merged:v1", 1, true) ~= nil)
  local merged_label = nil
  for _, raise in ipairs(result.raises or {}) do
    if raise.queue == "github-proxy.github_issue_label_request"
      and has_value(raise.payload.add_labels, "fkst-dev:merged") then
      merged_label = raise
      break
    end
  end
  t.is_true(merged_label ~= nil)
end

local function fresh_thinking_marker(proposal_id, version)
  return {
    body = core.state_marker(proposal_id, "thinking", version),
    created_at = os.date("!%Y-%m-%dT%H:%M:%SZ", now()),
  }
end

return {
  test_observe_issue_reraises_thinking_proposal_for_poll_self_heal = function()
    local event = issue()
    local original = payloads_builders.build_proposal(core, event)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      fresh_thinking_marker(original.proposal_id, original.dedup_key),
    })

    local first = run_observe(event, opts("observe-issue-thinking-self-heal-1"))
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 1)
    local first_proposal = find_raise(first.raises, "consensus.proposal").payload
    t.eq(first_proposal.schema, "consensus.proposal.v1")
    t.eq(first_proposal.proposal_id, original.proposal_id)
    t.eq(first_proposal.dedup_key, original.dedup_key .. "/replay")
    t.eq(first_proposal.source_ref.ref, "owner/repo#issue/42")

    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      fresh_thinking_marker(original.proposal_id, original.dedup_key),
    })
    local same = run_observe(event, opts("observe-issue-thinking-self-heal-same-fact"))
    t.eq(same.exit_code, 0)
    t.eq(#same.raises, 1)
    local same_proposal = find_raise(same.raises, "consensus.proposal").payload
    t.eq(same_proposal.dedup_key, first_proposal.dedup_key)
    t.eq(same_proposal.content_fetch, first_proposal.content_fetch)

    local updated_event = issue({ updated_at = "2026-06-03T01:02:04Z" })
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      fresh_thinking_marker(original.proposal_id, original.dedup_key),
    })
    local second = run_observe(updated_event, opts("observe-issue-thinking-self-heal-2"))
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 1)
    local second_proposal = find_raise(second.raises, "consensus.proposal").payload
    t.eq(second_proposal.dedup_key, payloads_builders.build_proposal(core, updated_event).dedup_key .. "/replay")
    t.is_true(second_proposal.dedup_key ~= first_proposal.dedup_key)
    t.is_true(second_proposal.content_fetch ~= first_proposal.content_fetch)
    t.eq(count_calls("--json body"), 0)
  end,

  test_observe_issue_replays_mid_loop_thinking_proposal_from_converge_marker = function()
    local event = issue()
    local original = payloads_builders.build_proposal(core, event)
    local base_version = original.dedup_key
    local sr_digest = convergence_shared.source_ref_digest(event.source_ref)
    local angle_digests = {
      { angle = "minimal", verdict = "abstain", digest = "needs-narrower-scope" },
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      fresh_thinking_marker(original.proposal_id, base_version),
      conv_rounds.converge_round_marker(core, original.proposal_id, base_version, sr_digest, 0, base_version, "Narrow the question", angle_digests),
    })

    local result = run_observe(event, opts("observe-issue-thinking-mid-loop-self-heal"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    local proposal = find_raise(result.raises, "consensus.proposal").payload
    t.eq(proposal.dedup_key, payloads_builders.build_proposal(core, event).dedup_key .. "/loop/1")
    t.eq(proposal.round, 1)
    t.eq(proposal.convergence_question, "Narrow the question")
    t.eq(proposal.prior_round_digests[1].digest, "needs-narrower-scope")
    t.eq(count_calls("--json body"), 0)
  end,

  test_observe_issue_skips_stale_lineage_thinking_replay = function()
    local old_event = issue()
    local event = issue({ updated_at = "2026-06-03T01:02:04Z" })
    local original = payloads_builders.build_proposal(core, old_event)
    local current = payloads_builders.build_proposal(core, event)
    local sr_digest = convergence_shared.source_ref_digest(event.source_ref)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      fresh_thinking_marker(current.proposal_id, current.dedup_key),
      conv_rounds.converge_round_marker(core, original.proposal_id, original.dedup_key, sr_digest, 0, original.dedup_key, "Old question", {
        { angle = "minimal", verdict = "abstain", digest = "old-lineage" },
      }),
    })

    local result = run_observe(event, opts("observe-issue-thinking-stale-lineage-replay"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    local proposal = find_raise(result.raises, "consensus.proposal").payload
    t.eq(proposal.dedup_key, current.dedup_key .. "/replay")
    t.eq(proposal.round, nil)
    t.eq(proposal.convergence_question, nil)
    t.eq(count_calls("--json body"), 0)
  end,

  test_observe_issue_replays_thinking_base_proposal_when_converge_marker_is_missing = function()
    local event = issue()
    local original = payloads_builders.build_proposal(core, event)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      {
        body = core.state_marker(original.proposal_id, "thinking", original.dedup_key .. "/loop/1"),
        created_at = os.date("!%Y-%m-%dT%H:%M:%SZ", now()),
      },
    })

    local result = run_observe(event, opts("observe-issue-thinking-missing-converge", {
      now = "2026-06-03T02:00:00Z",
    }))
    t.eq(result.exit_code, 0)
    local proposal = find_raise(result.raises, "consensus.proposal").payload
    t.eq(proposal.proposal_id, original.proposal_id)
    t.eq(proposal.dedup_key, original.dedup_key .. "/replay/loop/1")
    t.eq(proposal.source_ref.ref, "owner/repo#issue/42")
  end,

  test_observe_issue_timeout_redrives_plain_thinking_before_replay = function()
    local event = issue()
    local original = payloads_builders.build_proposal(core, event)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:thinking" }, "OPEN", {
      {
        body = core.state_marker(original.proposal_id, "thinking", original.dedup_key),
        created_at = "2026-06-03T00:00:00Z",
      },
    })

    local result = run_observe(event, opts("observe-issue-thinking-timeout-redrive", {
      now = "2026-06-03T02:00:00Z",
    }))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    local proposal = find_raise(result.raises, "consensus.proposal").payload
    t.eq(proposal.proposal_id, original.proposal_id)
    t.eq(proposal.dedup_key, original.dedup_key .. "/replay")
    t.eq(proposal.source_ref.ref, "owner/repo#issue/42")
    local attempt = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(attempt ~= nil)
  end,

  test_observe_issue_reraises_ready_for_poll_self_heal = function()
    local event = reached()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:ready" }, "OPEN", {
      {
        id = "IC_ready_self_heal",
        body = core.state_marker(event.proposal_id, "ready", event.dedup_key, "result-marker,ready-label,devloop-ready"),
        created_at = os.date("!%Y-%m-%dT%H:%M:%SZ", now()),
      },
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:ready" } }), opts("observe-issue-ready-self-heal"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 1)
    local ready_raise = find_raise(result.raises, "devloop_ready")
    t.eq(ready_raise.payload.schema, "github-devloop.ready.v1")
    t.eq(ready_raise.payload.proposal_id, event.proposal_id)
    t.eq(ready_raise.payload.source_ref.ref, "owner/repo#issue/42")
    t.is_true(ready_raise.payload.dedup_key:find("/redrive/ready/1", 1, true) ~= nil)
    t.eq(ready_raise.payload.ready_hand_off.comment_id, "IC_ready_self_heal")
    t.eq(ready_raise.payload.ready_hand_off.marker_version, event.dedup_key)
    t.eq(count_calls("--json body"), 0)
  end,

  test_observe_issue_ready_self_heal_does_not_duplicate_after_implementing = function()
    local event = reached()
    local ready_payload = payloads_builders.build_devloop_ready_payload(core, event)
    local branch = devloop_base.implement_branch("owner/repo", 42, ready_payload.dedup_key)
    local run_opts = opts("observe-issue-ready-self-heal-advanced")
    local exec_ref = core.implement_exec_ref(event.proposal_id, ready_payload.dedup_key)
    codex_status.seed_implement_codex_run(run_opts, event.proposal_id, ready_payload.dedup_key)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:implementing" }, "OPEN", {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
      core.state_marker(event.proposal_id, "implementing", ready_payload.dedup_key),
      core.implement_attempt_marker(event.proposal_id, ready_payload.dedup_key, 1, tostring(now()), exec_ref),
    })

    local observed = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:implementing" } }), run_opts)
    t.eq(observed.exit_code, 0)
    t.eq(find_raise(observed.raises, "devloop_ready"), nil)
    t.eq(count_calls("--json body"), 0)

    mock_issue_implement_raw({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "ready", event.dedup_key),
      core.state_marker(event.proposal_id, "implementing", ready_payload.dedup_key),
      core.implement_attempt_marker(event.proposal_id, ready_payload.dedup_key, 1, tostring(now()), exec_ref),
      m_builders.implementing_marker(core, event.proposal_id, ready_payload.dedup_key, branch, "abc123", "dev", "def456"),
      m_builders.pr_link_marker(core, event.proposal_id, 7, branch, ready_payload.dedup_key, "dev"),
    })
    local implemented = run_implement(ready_payload, opts("implement-ready-self-heal-advanced"))
    t.eq(implemented.exit_code, 0)
    t.eq(#implemented.raises, 0)
  end,

  test_observe_issue_legacy_pr_open_canonicalizes_instead_of_issue_side_reviewing_redrive = function()
    local event = reached()
    local ready_payload = payloads_builders.build_devloop_ready_payload(core, event)
    local comments = {
      core.state_marker(event.proposal_id, "pr-open", ready_payload.dedup_key),
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready_payload.dedup_key, "dev"),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", comments)
    mock_linked_pr_state({}, nil, nil, 2)

    local first = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("observe-issue-pr-open-review-kickoff-1"))
    t.eq(first.exit_code, 0)
    t.eq(#first.raises, 2)
    t.eq(find_raise(first.raises, "devloop_reviewing"), nil)
    t.eq(find_raise(first.raises, "github-proxy.github_pr_comment_request"), nil)
    local comment = find_raise(first.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(event.proposal_id, "awaiting-pr", ready_payload.dedup_key), 1, true) ~= nil)
    t.is_true(comment.payload.body:find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    local label = find_raise(first.raises, "github-proxy.github_issue_label_request")
    t.eq(label.payload.add_labels[1], "fkst-dev:awaiting-pr")

    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", comments)
    mock_linked_pr_state({}, nil, nil, 2)
    local second = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("observe-issue-pr-open-review-kickoff-2"))
    t.eq(second.exit_code, 0)
    t.eq(#second.raises, 2)
    t.eq(find_raise(second.raises, "devloop_reviewing"), nil)
    local second_comment = find_raise(second.raises, "github-proxy.github_issue_comment_request")
    t.is_true(second_comment ~= nil)
    t.is_true(second_comment.payload.body:find(core.state_marker(event.proposal_id, "awaiting-pr", ready_payload.dedup_key), 1, true) ~= nil)
  end,

  test_observe_issue_pr_open_timeout_redrive_canonicalizes_legacy_issue_state = function()
    local event = reached()
    local ready_payload = payloads_builders.build_devloop_ready_payload(core, event)
    local comments = {
      {
        body = core.state_marker(event.proposal_id, "pr-open", ready_payload.dedup_key),
        created_at = "2026-06-03T01:00:00Z",
      },
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready_payload.dedup_key, "dev"),
    }
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", comments)
    mock_linked_pr_state({}, nil, nil, 2)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" }, source = "liveness-scan" }), opts("observe-issue-pr-open-timeout-redrive", {
      now = "2026-06-03T03:00:00Z",
    }))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "devloop_reviewing"), nil)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request")
    t.is_true(comment ~= nil)
    t.is_true(comment.payload.body:find(core.state_marker(event.proposal_id, "awaiting-pr", ready_payload.dedup_key), 1, true) ~= nil)
    t.is_true(comment.payload.body:find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
  end,

  test_observe_issue_reconciles_blocked_terminal_label_from_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/13"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:reviewing" }, "OPEN", {
      core.state_marker(proposal_id, "blocked", version),
    })
    mock_decompose_child_issue_list({ proposal_id = proposal_id }, {})

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:reviewing" } }), opts("observe-issue-terminal-label-reconcile-blocked"))
    t.eq(result.exit_code, 0)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(label_raise ~= nil)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:blocked")
    t.is_true(has_value(label_raise.payload.remove_labels, "fkst-dev:reviewing"))
    t.eq(label_raise.payload.source_ref.ref, "owner/repo#issue/42")
    t.eq(find_raise(result.raises, "devloop_reviewing"), nil)
  end,

  test_observe_issue_reconciles_issue_terminal_label_over_linked_pr_state = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/fix/13"
    local link_version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:reviewing" }, "OPEN", {
      m_builders.pr_link_marker(core, proposal_id, 7, "devloop-owner-repo-42-01HY", link_version, "dev"),
      core.state_marker(proposal_id, "blocked", version),
    })
    mock_linked_pr_state({
      core.state_marker(proposal_id, "reviewing", link_version),
    })
    mock_decompose_child_issue_list({ proposal_id = proposal_id }, {})

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:reviewing" } }), opts("observe-issue-terminal-label-over-pr-state"))
    t.eq(result.exit_code, 0)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(label_raise ~= nil)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:blocked")
    t.is_true(has_value(label_raise.payload.remove_labels, "fkst-dev:reviewing"))
    t.eq(has_value(label_raise.payload.remove_labels, "fkst-dev:thinking"), false)
    t.eq(label_raise.payload.dedup_key, base_ids.dedup_key({
      "reconcile",
      "label",
      proposal_id,
      "blocked",
      version,
    }))
    t.eq(find_raise(result.raises, "devloop_reviewing"), nil)
  end,

  test_observe_issue_reconciles_merged_terminal_label_from_marker = function()
    local proposal_id = "github-devloop/issue/owner/repo/42"
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:merge-ready" }, "OPEN", {
      core.state_marker(proposal_id, "merged", version),
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:merge-ready" } }), opts("observe-issue-terminal-label-reconcile-merged"))
    t.eq(result.exit_code, 0)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(label_raise ~= nil)
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:merged")
    t.is_true(has_value(label_raise.payload.remove_labels, "fkst-dev:merge-ready"))
    t.eq(label_raise.payload.source_ref.ref, "owner/repo#issue/42")
  end,

  test_observe_issue_missing_reviewing_label_does_not_change_pr_local_state = function()
    local event = reached()
    local ready_payload = payloads_builders.build_devloop_ready_payload(core, event)
    mock_issue_state({ "fkst-dev:enabled" }, "OPEN", {
      core.state_marker(event.proposal_id, "pr-open", ready_payload.dedup_key),
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready_payload.dedup_key, "dev"),
    })
    mock_linked_pr_state({
      core.state_marker(event.proposal_id, "reviewing", ready_payload.dedup_key),
    })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled" } }), opts("observe-issue-pr-local-reviewing-no-label"))
    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "consensus.proposal"), nil)
    local label_raise = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.eq(label_raise.payload.add_labels[1], "fkst-dev:awaiting-pr")
  end,

  test_observe_issue_linked_pr_fetch_failure_fails_closed = function()
    local event = reached()
    local ready_payload = payloads_builders.build_devloop_ready_payload(core, event)
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:pr-open" }, "OPEN", {
      core.state_marker(event.proposal_id, "pr-open", ready_payload.dedup_key),
      m_builders.pr_link_marker(core, event.proposal_id, 7, "devloop-owner-repo-42-01HY", ready_payload.dedup_key, "dev"),
    })
    mock_linked_pr_state({}, "OPEN", 1)

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:pr-open" } }), opts("observe-issue-pr-local-fetch-failure"))
    t.eq(result.exit_code, 1)
    t.eq(#result.raises, 0)
  end,

  test_observe_issue_blocked_decomposed_marker_reraises_missing_children = function()
    local event = decompose_event()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      m_builders.pr_link_marker(core, event.proposal_id, event.pr_number, "devloop-owner-repo-42-01HY", event.version, "dev"),
      core.state_marker(event.proposal_id, "blocked", event.version),
      merge_gate_fix_marker(event),
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 3),
    })
    mock_linked_pr_state({})
    mock_decompose_child_issue_list(event, {})

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:blocked" } }), opts("observe-issue-decomposed-missing-children"))

    t.eq(result.exit_code, 0)
    local decompose = find_raise(result.raises, "github-devloop-decompose.devloop_decompose")
    t.eq(decompose.payload.schema, "github-devloop.decompose.v1")
    t.eq(decompose.payload.proposal_id, event.proposal_id)
    t.eq(decompose.payload.version, event.version)
    t.eq(decompose.payload.pr_number, event.pr_number)
    t.eq(decompose.payload.review_proposal_id, event.review_proposal_id)
    t.eq(decompose.payload.review_dedup_key, event.review_dedup_key)
    t.eq(decompose.payload.head_sha, event.head_sha)
    t.eq(decompose.payload.source_ref.ref, "owner/repo#pr/7")
  end,

  test_observe_issue_blocked_decomposed_marker_skips_when_children_complete = function()
    local event = decompose_event()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      m_builders.pr_link_marker(core, event.proposal_id, event.pr_number, "devloop-owner-repo-42-01HY", event.version, "dev"),
      core.state_marker(event.proposal_id, "blocked", event.version),
      merge_gate_fix_marker(event),
      decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 3),
    })
    mock_linked_pr_state({})
    mock_decompose_child_issue_list(event, { 1, 2, 3 })

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:blocked" } }), opts("observe-issue-decomposed-complete-children"))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-devloop-decompose.devloop_decompose"), nil)
  end,

  test_observe_issue_blocked_decomposed_marker_refuses_untrusted_marker = function()
    local event = decompose_event()
    mock_issue_state({ "fkst-dev:enabled", "fkst-dev:blocked" }, "OPEN", {
      m_builders.pr_link_marker(core, event.proposal_id, event.pr_number, "devloop-owner-repo-42-01HY", event.version, "dev"),
      core.state_marker(event.proposal_id, "blocked", event.version),
      merge_gate_fix_marker(event),
      {
        body = decompose_lib.decomposed_marker(core, event.proposal_id, event.version, event.pr_number, 3),
        author_login = "mallory",
      },
    })
    mock_linked_pr_state({})
    mock_decompose_child_issue_list(event, {})

    local result = run_observe(issue({ labels = { "fkst-dev:enabled", "fkst-dev:blocked" } }), opts("observe-issue-decomposed-forged"))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-devloop-decompose.devloop_decompose"), nil)
  end,
}
