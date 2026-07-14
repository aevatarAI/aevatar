local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local h = require("tests.devloop_helpers")
local contract_time = require("contract.time")
local conv_reconcile = require("devloop.convergence.reconcile")
local conv_attempts = require("devloop.convergence.attempts")
local m_rae = require("devloop.restart_actionable_epoch")
local t = h.t
local core = h.core
local replay_fields = require("devloop.replay_fields")
local opts = h.opts
local reconcile = h.reconcile
local review_reconcile = h.review_reconcile
local run_reconcile = h.run_reconcile
local run_review_reconcile = h.run_review_reconcile
local mock_issue_reconcile = h.mock_issue_reconcile
local mock_issue_review = h.mock_issue_review
local mock_bot_env = h.mock_bot_env
local find_raise = h.find_raise
local entity_read_mocks = require("tests.entity_read_mock_helpers")

local repo = "owner/repo"
local issue_number = 42
local proposal_id = "github-devloop/issue/owner/repo/42"
local now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T03:00:00Z")

local function restart_transition_row(state_name)
  return replay_fields.restart_transition_row(core.restart_transition_table(), state_name)
end

local function pr_list_json(branch, base_branch)
  return '[[{"number":7,"head":{"ref":"' .. branch .. '","sha":"0123456789abcdef0123456789abcdef01234567"},"base":{"ref":"' .. base_branch .. '"},"state":"open"}]]\n'
end

local function mock_branch_config()
  t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_UPSTREAM_BRANCH"), {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(devloop_base.read_env_command("FKST_DEVLOOP_INTEGRATION_BRANCH"), {
    stdout = "dev",
    stderr = "",
    exit_code = 0,
  })
end

local function run_timeout_reconcile(payload, run_opts)
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_timeout_reconcile",
    payload = payload,
  }, run_opts)
end

return {
  test_thinking_reconcile_blocks_when_live_version_outranks_convergence_base = function()
    local event = reconcile()
    local state_version = "github-devloop/issue/owner/repo/42/2026-06-14T05-22-55Z/intake/1287859418"
    mock_issue_reconcile({ "fkst-dev:thinking" }, {
      core.state_marker(event.proposal_id, "thinking", state_version),
    })

    local result = run_reconcile(event, opts("reconcile-terminal-thinking"))
    t.eq(result.exit_code, 0)
    t.eq(#result.raises, 2)
    local comment = find_raise(result.raises, "github-proxy.github_issue_comment_request").payload
    local version = conv_reconcile.reconcile_terminal_state_version(core, state_version, event.round)
    t.eq(core.versioned_transition_status({ state = "thinking", version = state_version }, { "thinking" }, "blocked", version), "apply")
    t.is_true(comment.body:find(core.state_marker(event.proposal_id, "blocked", version), 1, true) ~= nil)

    mock_issue_reconcile({ "fkst-dev:blocked" }, { comment.body })
    local idempotent = run_reconcile(event, opts("reconcile-terminal-thinking-idempotent"))
    t.eq(idempotent.exit_code, 0)
    t.eq(#idempotent.raises, 0)
  end,

  test_thinking_reconcile_does_not_override_advanced_state = function()
    local event = reconcile()
    local state_version = conv_reconcile.reconcile_terminal_state_version(core, "github-devloop/issue/owner/repo/42/2026-06-14T05-22-55Z/intake/1287859418", event.round)
    mock_issue_reconcile({ "fkst-dev:ready" }, {
      core.state_marker(event.proposal_id, "ready", state_version),
    })

    local ready_result = run_reconcile(event, opts("reconcile-terminal-ready"))
    t.eq(ready_result.exit_code, 0)
    t.eq(#ready_result.raises, 0)

    mock_issue_reconcile({ "fkst-dev:implementing" }, {
      core.state_marker(event.proposal_id, "implementing", state_version),
    })

    local implementing_result = run_reconcile(event, opts("reconcile-terminal-implementing"))
    t.eq(implementing_result.exit_code, 0)
    t.eq(#implementing_result.raises, 0)
  end,

  test_implementing_timeout_reconcile_adopts_open_pr_instead_of_blocking = function()
    local event = h.ready()
    local impl_version = event.dedup_key
    local state_version = impl_version .. "/timeout/implementing/2"
    local row = restart_transition_row("implementing")
    local state = {
      state = "implementing",
      version = state_version,
      proposal_id = proposal_id,
      marker_created_at = "2026-06-03T00:00:00Z",
    }
    local facts = {
      proposal_id = proposal_id,
      current = { comments = {} },
    }
    local original = fkst.codex_runs
    fkst.codex_runs = function()
      return { running = {}, recent = {} }
    end
    local ok, eval = pcall(function()
      return m_rae.actionable_epoch_resolve(core, row, state, facts, now_seconds)
    end)
    fkst.codex_runs = original
    if not ok then
      error(eval)
    end
    t.eq(eval.status, "actionable")
    t.eq(eval.signal.reason, "codex-run-not-running")
    local payload = conv_reconcile.build_devloop_timeout_reconcile_payload(core,
      row,
      state,
      proposal_id,
      entity_lib.issue_source_ref(repo, issue_number),
      3
    )
    mock_issue_reconcile({ "fkst-dev:implementing" }, {
      {
        body = core.state_marker(proposal_id, "implementing", state_version),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T00:00:00Z",
      },
      {
        body = conv_attempts.timeout_attempt_v2_marker(core,
          proposal_id,
          "implementing",
          "implementing.active",
          eval.generation_key,
          1,
          event.source_ref
        ),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T00:01:00Z",
      },
      {
        body = conv_attempts.timeout_attempt_v2_marker(core,
          proposal_id,
          "implementing",
          "implementing.active",
          eval.generation_key,
          2,
          event.source_ref
        ),
        author_login = "fkst-test-bot",
        created_at = "2026-06-03T00:02:00Z",
      },
    })
    local branch = devloop_base.implement_branch(repo, issue_number, core.implementation_base_version(impl_version))
    mock_branch_config()
    t.mock_command(core.gh_pr_list_head_base_cmd(repo, branch, "dev"), {
      stdout = pr_list_json(branch, "dev"),
      stderr = "",
      exit_code = 0,
    })

    local result = run_timeout_reconcile(payload, opts("timeout-reconcile-open-pr-adopts"))

    t.eq(result.exit_code, 0)
    t.eq(find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload_body, raised)
      return raised.queue == "github-proxy.github_issue_comment_request"
        and tostring(payload_body.body or ""):find('state="blocked"', 1, true) ~= nil
    end), nil)
    local issue_comment = find_raise(result.raises, "github-proxy.github_issue_comment_request", function(payload_body, raised)
      return raised.queue == "github-proxy.github_issue_comment_request"
        and tostring(payload_body.body or ""):find('state="awaiting-pr"', 1, true) ~= nil
    end)
    t.is_true(issue_comment ~= nil)
    t.is_true(tostring(issue_comment.payload.body):find("fkst:github-devloop:pr-delegation:v1", 1, true) ~= nil)
    t.is_true(tostring(issue_comment.payload.body):find('pr="7"', 1, true) ~= nil)
    t.eq(find_raise(result.raises, "github-proxy.github_pr_comment_request", function(payload_body)
      return tostring(payload_body.body or ""):find('state="blocked"', 1, true) ~= nil
    end), nil)
    local pr_comment = find_raise(result.raises, "github-proxy.github_pr_comment_request")
    t.is_true(pr_comment ~= nil)
    t.is_true(tostring(pr_comment.payload.body):find('state="pr-open"', 1, true) ~= nil)
    local label = find_raise(result.raises, "github-proxy.github_issue_label_request")
    t.is_true(label ~= nil)
    t.eq(label.payload.add_labels[1], "fkst-dev:awaiting-pr")
  end,

}
