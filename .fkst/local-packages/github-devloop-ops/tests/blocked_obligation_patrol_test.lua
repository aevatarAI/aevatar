local h = require("tests.devloop_ops_helpers")
local t = h.t
local core = h.core
local conv_reconcile = require("devloop.convergence.reconcile")
local testing = require("testkit.testing")

local repo = "owner/repo"
local issue_number = 42
local proposal_id = "github-devloop/issue/owner/repo/42"
local ready_version = "github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
local blocked_version = conv_reconcile.timeout_reconcile_state_version(ready_version, "ready", 3)

local function bot_comment(body)
  return {
    body = body,
    author_login = "fkst-test-bot",
    created_at = "2026-06-03T01:10:03Z",
  }
end

local function state_comment(state, version)
  return bot_comment(core.state_marker(proposal_id, state, version))
end

local function timeout_comment()
  return bot_comment(conv_reconcile.timeout_reconcile_marker(proposal_id, ready_version, "ready", 3, "drop", {
    terminal_version = blocked_version,
    from_state = "ready",
    from_version = ready_version,
    age_minutes = 1441,
    budget_minutes = 1440,
    attempt = 3,
    attempt_limit = 3,
    driving_queue = "github-devloop.devloop_ready",
    reason_class = "state-output-obligation-timeout",
    source_ref = {
      kind = "external",
      ref = "owner/repo#issue/42",
    },
  }))
end

local function entity(extra_comments)
  local comments = {
    state_comment("blocked", blocked_version),
    timeout_comment(),
  }
  for _, comment in ipairs(extra_comments or {}) do
    table.insert(comments, comment)
  end
  return {
    repo = repo,
    number = issue_number,
    proposal_id = proposal_id,
    comments = comments,
    current_state = require("devloop.entity").current_entity_state(comments, proposal_id),
  }
end

local function created_marker(dedup_key, created_issue_number)
  return bot_comment('Opened sub-issue #' .. tostring(created_issue_number) .. ' for this task.\n\n'
    .. '<!-- fkst:github-proxy:issue-created:v1 dedup="' .. tostring(dedup_key)
    .. '" issue="' .. tostring(created_issue_number)
    .. '" -->')
end

local function observed_escalation_issue(dedup_key, issue_number)
  return {
    issue_number = tonumber(issue_number),
    parent_issue = {
      body = "Escalation issue body.\n\n<!-- fkst:github-proxy:issue-create:" .. tostring(dedup_key) .. " -->\n",
      author_login = "fkst-test-bot",
      comments = {},
    },
  }
end

local function observed_semantic_escalation_issue(dedup_key, issue_number, terminal_version)
  return {
    issue_number = tonumber(issue_number),
    parent_issue = {
      body = "Escalation issue body.\n\n"
        .. core.output_obligation_escalation_marker({
          proposal_id = proposal_id,
          terminal_version = terminal_version or blocked_version,
          dedup_key = dedup_key,
          reason_class = "state-output-obligation-timeout",
          source_repo = repo,
          issue_number = issue_number,
        })
        .. "\n",
      author_login = "fkst-test-bot",
      comments = {},
    },
  }
end

local function find_raise(raises, queue)
  for _, raised in ipairs(raises or {}) do
    if raised.queue == queue then
      return raised
    end
  end
  return nil
end

local function mock_observe_env()
  t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', {
    stdout = "fkst-test-bot",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command('printf %s "$FKST_GITHUB_REPO"', {
    stdout = repo,
    stderr = "",
    exit_code = 0,
  })
end

local function with_observability_stubs(observed_entity, fn)
  local originals = {
    collect_observability_entities = core.collect_observability_entities,
    collect_recent_merged_prs = core.collect_recent_merged_prs,
    collect_recent_merged_issues = core.collect_recent_merged_issues,
    reap_orphan_prs = core.reap_orphan_prs,
    observe_conflict_hotspots = core.observe_conflict_hotspots,
    render_observability_dashboard = core.render_observability_dashboard,
    publish_observability_dashboard = core.publish_observability_dashboard,
    observability_topology_mermaid = core.observability_topology_mermaid,
  }

  core.collect_observability_entities = function()
    return {
      list = { observed_entity },
      counts = { blocked = 1 },
      stalls = {},
      state_gap_report = { edges = {} },
      now_seconds = now(),
    }
  end
  core.collect_recent_merged_prs = function() return {} end
  core.collect_recent_merged_issues = function() return {} end
  core.reap_orphan_prs = function() end
  core.observe_conflict_hotspots = function() return { facts = 0, hotspots = 0, raised = 0 } end
  core.render_observability_dashboard = function()
    return { hash = "test-dashboard-hash", body = "test dashboard" }
  end
  core.publish_observability_dashboard = function() return "dry-run" end
  core.observability_topology_mermaid = function() return nil end

  local ok, result = pcall(fn)
  for name, original in pairs(originals) do
    core[name] = original
  end
  if not ok then
    error(result)
  end
  return result
end

return {
  test_output_obligation_problem_identity_is_shared_across_terminal_instances = function()
    local first = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })
    local second = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = "github-devloop/issue/owner/repo/84",
      terminal_version = blocked_version .. "/another-terminal",
      source_ref = { kind = "external", ref = "owner/repo#issue/84" },
    })
    local distinct_class = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "decompose-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })

    t.eq(first.dedup_key, "output-obligation/blocked/owner/repo/state-output-obligation-timeout")
    t.eq(first.dedup_key, second.dedup_key)
    t.is_true(first.dedup_key ~= distinct_class.dedup_key)
  end,

  test_semantic_problem_issue_drains_later_terminal_instance = function()
    local first = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })
    local later_terminal_version = blocked_version .. "/another-terminal"
    local later = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = "github-devloop/issue/owner/repo/84",
      terminal_version = later_terminal_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/84" },
    })

    local edge = core.output_obligation_failure_drain_edge({}, later.dedup_key, {
      observed_semantic_escalation_issue(first.dedup_key, 2226, first.terminal_version),
    }, nil, later_terminal_version)

    t.eq(edge.kind, "output-obligation-escalation-issue")
    t.eq(edge.issue_id, "2226")
    t.eq(edge.terminal_version, first.terminal_version)
  end,

  test_legacy_terminal_scoped_issue_drains_the_matching_problem_class = function()
    local fact = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })
    local legacy_dedup_key = "output-obligation/blocked/owner/repo/"
      .. proposal_id .. "/" .. blocked_version .. "/state-output-obligation-timeout"

    local edge = core.output_obligation_failure_drain_edge({}, fact.dedup_key, {
      observed_escalation_issue(legacy_dedup_key, 3353),
    }, nil, fact.terminal_version, fact.reason_class, fact.source_repo)

    t.eq(edge.kind, "legacy-output-obligation-escalation-issue")
    t.eq(edge.issue_id, "3353")
    t.eq(edge.reason_class, fact.reason_class)
  end,

  test_classifier_separates_blocked_output_obligation_from_dead_letter = function()
    local fact, reason = core.classify_output_obligation_failure({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })

    t.eq(reason, nil)
    t.eq(fact.failure_kind, "OutputObligationFailure")
    t.eq(fact.reason_class, "state-output-obligation-timeout")
    t.eq(fact.terminal_state, "blocked")
    t.eq(fact.source_repo, repo)
    t.eq(fact.issue_number, tostring(issue_number))

    local dead_letter_decision = core.failure_triage_decision({
      terminal_state = "blocked",
      reason_class = "state-output-obligation-timeout",
      proposal_id = proposal_id,
      terminal_version = blocked_version,
      source_ref = { kind = "external", ref = "owner/repo#issue/42" },
    })
    t.eq(dead_letter_decision.action, "skip")
    t.eq(dead_letter_decision.reason, "missing-queue")
  end,

  test_blocked_obligation_patrol_creates_one_issue_request_and_marks_existing_edge = function()
    local first = core.blocked_obligation_patrol_once(entity())

    t.eq(#first, 1)
    local raised = first[1]
    t.eq(raised.queue, "github-proxy.github_issue_create_request")
    local request = raised.payload
    t.eq(request.schema, "github-proxy.issue-create.v1")
    t.eq(request.repo, repo)
    t.eq(request.parent_comment_target.repo, repo)
    t.eq(request.parent_comment_target.issue_number, issue_number)
    t.eq(request.source_ref.ref, "owner/repo#issue/42")
    t.is_true(request.title:find("state-output-obligation-timeout", 1, true) ~= nil)
    t.eq(request.title:find("#42", 1, true), nil)
    t.is_true(request.body:find("`failure_kind`: `OutputObligationFailure`", 1, true) ~= nil)
    t.is_true(request.body:find("`reason_class`: `state-output-obligation-timeout`", 1, true) ~= nil)
    t.is_true(request.body:find("`parent`: `owner/repo#42`", 1, true) ~= nil)
    t.is_true(request.body:find("`proposal_id`: `" .. proposal_id .. "`", 1, true) ~= nil)
    t.is_true(request.body:find("producer-owned invariant", 1, true) ~= nil)
    t.is_true(request.body:find("idempotent", 1, true) ~= nil)
    t.is_true(request.body:find("timeout and retry", 1, true) ~= nil)
    t.is_true(request.body:find("fkst:github-devloop-ops:output-obligation-escalation:v1", 1, true) ~= nil)
    t.is_true(request.body:find('terminal_version="' .. blocked_version .. '"', 1, true) ~= nil)

    local edge = core.output_obligation_failure_drain_edge({ created_marker(request.dedup_key, 2030) }, request.dedup_key)
    t.eq(edge.kind, "superseded-by-escalation-issue")
    t.eq(edge.issue_id, "2030")

    local second = core.blocked_obligation_patrol_once(entity({ created_marker(request.dedup_key, 2030) }))
    t.eq(#second, 0)
  end,

  test_blocked_obligation_patrol_marks_semantic_escalation_issue_body_edge = function()
    local first = core.blocked_obligation_patrol_once(entity())
    t.eq(#first, 1)
    local dedup_key = first[1].payload.dedup_key

    local edge = core.output_obligation_failure_drain_edge({}, dedup_key, {
      observed_semantic_escalation_issue(dedup_key, 2226, blocked_version),
    }, nil, blocked_version)
    t.eq(edge.kind, "output-obligation-escalation-issue")
    t.eq(edge.issue_id, "2226")
    t.eq(edge.terminal_version, blocked_version)

    local second = core.blocked_obligation_patrol_once(entity(), {
      observed_semantic_escalation_issue(dedup_key, 2226, blocked_version),
    })
    t.eq(#second, 0)
  end,

  test_blocked_obligation_patrol_accepts_same_class_semantic_issue_from_another_terminal = function()
    local first = core.blocked_obligation_patrol_once(entity())
    t.eq(#first, 1)
    local dedup_key = first[1].payload.dedup_key

    local second = core.blocked_obligation_patrol_once(entity(), {
      observed_semantic_escalation_issue(dedup_key, 2226, blocked_version .. "/stale"),
    })
    t.eq(#second, 0)
  end,

  test_blocked_obligation_patrol_marks_observed_escalation_issue_body_edge = function()
    local first = core.blocked_obligation_patrol_once(entity())
    t.eq(#first, 1)
    local dedup_key = first[1].payload.dedup_key

    local edge = core.output_obligation_failure_drain_edge({}, dedup_key, {
      observed_escalation_issue(dedup_key, 2028),
    })
    t.eq(edge.kind, "superseded-by-observed-escalation-issue")
    t.eq(edge.issue_id, "2028")

    local second = core.blocked_obligation_patrol_once(entity(), {
      observed_escalation_issue(dedup_key, 2028),
    })
    t.eq(#second, 0)
  end,

  test_blocked_obligation_patrol_marks_recent_closed_escalation_issue_body_edge = function()
    local first = core.blocked_obligation_patrol_once(entity())
    t.eq(#first, 1)
    local dedup_key = first[1].payload.dedup_key

    local edge = core.output_obligation_failure_drain_edge({}, dedup_key, {}, {
      observed_escalation_issue(dedup_key, 2028),
    })
    t.eq(edge.kind, "superseded-by-observed-escalation-issue")
    t.eq(edge.issue_id, "2028")

    local second = core.blocked_obligation_patrol_once(entity(), {}, {
      observed_escalation_issue(dedup_key, 2028),
    })
    t.eq(#second, 0)
  end,

  test_blocked_obligation_patrol_ignores_untrusted_escalation_issue_body_edge = function()
    local first = core.blocked_obligation_patrol_once(entity())
    t.eq(#first, 1)
    local dedup_key = first[1].payload.dedup_key
    local untrusted = observed_escalation_issue(dedup_key, 2028)
    untrusted.parent_issue.author_login = "mallory"

    local second = core.blocked_obligation_patrol_once(entity(), { untrusted })
    t.eq(#second, 1)
  end,

  test_observability_patrol_raises_issue_request_for_blocked_obligation = function()
    mock_observe_env()
    local department = require("departments.observability.main")

    local result = with_observability_stubs(entity(), function()
      return testing.run_fake(department, {
        queue = "devloop_observe_tick",
        payload = { schema = "github-devloop.observe-tick.v1" },
      })
    end)

    local raised = find_raise(result.raises, "github-proxy.github_issue_create_request")
    t.is_true(raised ~= nil)
    t.eq(raised.payload.schema, "github-proxy.issue-create.v1")
    t.eq(raised.payload.repo, repo)
    t.eq(raised.payload.parent_comment_target.issue_number, issue_number)
    t.is_true(raised.payload.body:find("`failure_kind`: `OutputObligationFailure`", 1, true) ~= nil)
  end,

  test_observability_patrol_skips_when_escalation_issue_body_is_observed = function()
    mock_observe_env()
    local department = require("departments.observability.main")
    local parent = entity()
    local request = core.blocked_obligation_patrol_once(parent)[1].payload

    local result = with_observability_stubs(parent, function()
      local originals = {
        collect_observability_entities = core.collect_observability_entities,
      }
      core.collect_observability_entities = function()
        return {
          list = {
            parent,
            observed_escalation_issue(request.dedup_key, 2028),
          },
          counts = { blocked = 1 },
          stalls = {},
          state_gap_report = { edges = {} },
          now_seconds = now(),
        }
      end
      local ok, observed_result = pcall(function()
        return testing.run_fake(department, {
          queue = "devloop_observe_tick",
          payload = { schema = "github-devloop.observe-tick.v1" },
        })
      end)
      core.collect_observability_entities = originals.collect_observability_entities
      if not ok then
        error(observed_result)
      end
      return observed_result
    end)

    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
  end,

  test_observability_patrol_skips_when_escalation_issue_body_is_recently_closed = function()
    mock_observe_env()
    local department = require("departments.observability.main")
    local parent = entity()
    local request = core.blocked_obligation_patrol_once(parent)[1].payload

    local result = with_observability_stubs(parent, function()
      local originals = {
        collect_observability_entities = core.collect_observability_entities,
        collect_recent_merged_issues = core.collect_recent_merged_issues,
      }
      core.collect_observability_entities = function()
        return {
          list = { parent },
          counts = { blocked = 1 },
          stalls = {},
          state_gap_report = { edges = {} },
          now_seconds = now(),
        }
      end
      core.collect_recent_merged_issues = function()
        return {
          observed_escalation_issue(request.dedup_key, 2028),
        }
      end
      local ok, observed_result = pcall(function()
        return testing.run_fake(department, {
          queue = "devloop_observe_tick",
          payload = { schema = "github-devloop.observe-tick.v1" },
        })
      end)
      core.collect_observability_entities = originals.collect_observability_entities
      core.collect_recent_merged_issues = originals.collect_recent_merged_issues
      if not ok then
        error(observed_result)
      end
      return observed_result
    end)

    t.eq(find_raise(result.raises, "github-proxy.github_issue_create_request"), nil)
  end,
}
