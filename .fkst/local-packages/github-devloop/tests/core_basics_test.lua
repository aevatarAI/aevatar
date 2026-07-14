local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local h = require("tests.devloop_core_helpers")
local payloads_builders = require("devloop.payloads.builders")
local v_validate_proposal = require("devloop.validators.validate_proposal")
local core = h.core
local error_facts = require("contract.error_facts")
local github_risk = require("devloop.github_risk")
local t = h.t
local source_ref = h.source_ref
local issue = h.issue
local config = require("devloop.config")

return {
  test_devloop_config_defaults_and_validation = function()
    local responses = {
      ['printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"'] = { stdout = "", exit_code = 0 },
      ["git rev-parse --abbrev-ref HEAD"] = { stdout = "dev\n", exit_code = 0 },
      ['printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"'] = { stdout = "", exit_code = 0 },
      ['printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"'] = { stdout = "", exit_code = 0 },
      ['printf %s "$FKST_DEVLOOP_TEST_COMMAND"'] = { stdout = "", exit_code = 0 },
      ['printf %s "$FKST_GITHUB_REPO"'] = { stdout = "owner/repo", exit_code = 0 },
      ['printf %s "$FKST_GITHUB_BOT_LOGIN"'] = { stdout = "fkst-test-bot", exit_code = 0 },
      ['printf %s "$FKST_GITHUB_WRITE"'] = { stdout = "", exit_code = 0 },
    }
    local function exec(cmd)
      local rendered = type(cmd) == "table" and (cmd.cmd or table.concat(cmd.argv or {}, " ")) or cmd
      return responses[rendered] or { stdout = "", stderr = "unexpected " .. tostring(rendered), exit_code = 1 }
    end
    local cfg = config.devloop_config(core, exec)
    t.eq(cfg.repo, "owner/repo")
    t.eq(cfg.bot_login, "fkst-test-bot")
    t.eq(cfg.write_mode, "dry-run")
    t.eq(cfg.upstream_branch, "dev")
    t.eq(cfg.integration_branch, "dev")
    t.eq(cfg.rollup_merge, "auto")
    t.eq(config.test_command(core, exec), "scripts/run.sh test")
    local local_command = config.local_iteration_test_command(core)
    t.eq(local_command, "scripts/run.sh test-affected")
    t.is_nil(local_command:find("FKST_DEVLOOP_TEST_COMMAND", 1, true))

    t.eq(config.env_present_command(core, "GH_TOKEN"), 'if [ -n "${GH_TOKEN:-}" ]; then printf present; fi')
    responses[config.env_present_command(core, "GH_TOKEN")] = { stdout = "present", exit_code = 0 }
    responses[config.env_present_command(core, "GITHUB_TOKEN")] = { stdout = "", exit_code = 0 }
    t.eq(config.env_present(core, "GH_TOKEN", exec), true)
    t.eq(config.env_present(core, "GITHUB_TOKEN", exec), false)
    t.raises(function()
      devloop_base.read_env_command("GH_TOKEN")
    end)

    responses['printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"'] = { stdout = "main", exit_code = 0 }
    responses['printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"'] = { stdout = "integration/dev", exit_code = 0 }
    responses['printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"'] = { stdout = "manual", exit_code = 0 }
    responses['printf %s "$FKST_DEVLOOP_TEST_COMMAND"'] = { stdout = "cargo build && cargo test", exit_code = 0 }
    responses['printf %s "$FKST_GITHUB_WRITE"'] = { stdout = "1", exit_code = 0 }
    cfg = config.devloop_config(core, exec)
    t.eq(cfg.write_mode, "real")
    t.eq(cfg.upstream_branch, "main")
    t.eq(cfg.integration_branch, "integration/dev")
    t.eq(cfg.rollup_merge, "manual")
    t.eq(config.test_command(core, exec), "cargo build && cargo test")
    t.eq(config.local_iteration_test_command(core, exec), local_command)

    responses['printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"'] = { stdout = "../bad", exit_code = 0 }
    t.raises(function()
      config.branch_config(core, exec)
    end)
    responses['printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"'] = { stdout = "integration/dev", exit_code = 0 }
    responses['printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"'] = { stdout = "sometimes", exit_code = 0 }
    t.raises(function()
      config.devloop_config(core, exec)
    end)
  end,
  test_gh_exec_opts_preserves_argv_without_shell_controls = function()
    local spec = core.gh_exec_opts({ argv = { "gh", "issue", "list" }, timeout = 45 })
    t.eq(spec.argv[1], "gh")
    t.eq(spec.argv[2], "issue")
    t.eq(spec.argv[3], "list")
    t.eq(spec.timeout, 45)
    t.is_nil(spec.cmd)
    t.is_nil(spec.rate_pool)
  end,
  test_github_high_risk_paths_cover_ci_auth_dependency_and_scheduler_surfaces = function()
    local high = github_risk.github_high_risk_paths({
      ".github/workflows/ci.yml",
      "Cargo.lock",
      "scripts/run.sh",
      "packages/github-devloop/core.lua",
    })
    t.eq(#high, 3)
    t.eq(high[1], ".github/workflows/ci.yml")
    t.eq(high[2], "Cargo.lock")
    t.eq(high[3], "scripts/run.sh")
  end,
  test_core_shared_surface_keeps_two_copy_helpers_local = function()
    t.is_nil(core.age_minutes)
    t.is_nil(core.valid_round)
  end,
  test_parse_name_only_paths_trims_deduplicates_and_sorts = function()
    local paths = devloop_base.parse_name_only_paths("  b.lua\r\na.lua\n\n b.lua \r  c.lua  \n")
    t.eq(#paths, 3)
    t.eq(paths[1], "a.lua")
    t.eq(paths[2], "b.lua")
    t.eq(paths[3], "c.lua")
  end,
  test_core_shared_judgment_worktree_reads_runtime_root_and_mkdirs = function()
    local worktree = core.judgment_worktree_path("/tmp/fkst-runtime\n", "review-meta", "dedup/key")
    t.eq(core.mkdir_p_cmd(worktree), "mkdir -p '" .. worktree .. "'")
    t.is_nil(core.mkdir_p_cmd(worktree):find("chmod", 1, true))
    t.mock_command(core.read_runtime_root_cmd(), {
      stdout = "/tmp/fkst-runtime\n",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command(core.mkdir_p_cmd(worktree), {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })

    local actual = devloop_base.judgment_worktree_with_exec(exec_sync, "review-meta", "dedup/key")

    t.eq(actual, worktree)
    local saw_mkdir = false
    for _, call in ipairs(t.command_calls()) do
      if call.rendered == core.mkdir_p_cmd(worktree) then
        saw_mkdir = true
      end
    end
    t.eq(saw_mkdir, true)
  end,
  test_opt_in_detection = function()
    t.eq(devloop_base.is_opted_in({ "fkst-dev:enabled" }), true)
    t.eq(devloop_base.is_opted_in({ "bug" }), false)
    t.eq(devloop_base.is_opted_in({ "fkst-dev:enabled", "fkst-dev:thinking" }), true)
    t.eq(devloop_base.is_opted_in({ "fkst-dev:enabled", "fkst-dev:ready" }), true)
    t.eq(devloop_base.is_opted_in({ "fkst-dev:enabled", "fkst-dev:impl-failed" }), true)
    t.eq(devloop_base.is_opted_in({ "fkst-dev:enabled", "fkst-dev:blocked" }), true)
  end,
  test_proposal_id_round_trip = function()
    local id = base_ids.proposal_id("owner/repo", 42)
    t.eq(id, "github-devloop/issue/owner/repo/42")
    local repo, issue_number = base_ids.parse_proposal_id(id)
    t.eq(repo, "owner/repo")
    t.eq(issue_number, "42")
    t.eq(base_ids.issue_ref_round_trips("owner/repo", 42), true)
    t.is_nil(base_ids.parse_proposal_id("autochrono/issue/owner/repo/42"))
  end,
  test_error_fact_fields_include_available_delivery_context = function()
    local fields = error_facts.error_fact_fields(
      "codex-failed",
      "devloop_ready",
      "implement",
      "codex failed at 2026-06-10T01:02:03Z on abcdef1234567890 in /tmp/fkst-a",
      {
        source_ref = source_ref(),
        attempt = 4,
        terminal = false,
      }
    )

    t.eq(fields[1], "error_class=codex-failed")
    t.eq(fields[2], "fingerprint=" .. error_facts.error_fingerprint(
      "codex-failed",
      "devloop_ready",
      "implement",
      "codex failed at 2027-07-11T09:08:07Z on fedcba0987654321 in /tmp/fkst-b"
    ))
    t.eq(fields[3], "source_ref=external:owner/repo#issue/42")
    t.eq(fields[4], "attempt=4")
    t.eq(fields[5], "terminal=false")
  end,
  test_error_fact_fields_omit_unavailable_delivery_context = function()
    local fields = error_facts.error_fact_fields("codex-failed", "devloop_ready", "implement", "codex failed", {})

    t.eq(#fields, 2)
    t.eq(fields[1], "error_class=codex-failed")
    t.is_true(fields[2]:find("^fingerprint=fp%-") ~= nil)
  end,
  test_log_codex_result_emits_structured_failure_line = function()
    local captured = {}
    local old_log = log
    log = {
      error = function(message)
        table.insert(captured, tostring(message))
      end,
    }

    core.log_codex_result(
      "implement",
      "github-devloop/issue/owner/repo/42",
      "implement",
      { exit_code = 1 },
      nil,
      "codex failed",
      {
        queue = "devloop_ready",
        source_ref = source_ref(),
        terminal = false,
      }
    )
    log = old_log

    t.eq(#captured, 1)
    t.is_true(captured[1]:find("github-devloop dept=implement", 1, true) ~= nil)
    t.is_true(captured[1]:find("tag=CODEX", 1, true) ~= nil)
    t.is_true(captured[1]:find("error_class=codex-failed", 1, true) ~= nil)
    t.is_true(captured[1]:find("fingerprint=", 1, true) ~= nil)
    t.is_true(captured[1]:find("source_ref=external:owner/repo#issue/42", 1, true) ~= nil)
    t.is_true(captured[1]:find("terminal=false", 1, true) ~= nil)
  end,
  test_wrapped_pipeline_failure_logs_delivery_error_fact_and_rethrows = function()
    local captured = {}
    local old_log = log
    log = {
      error = function(message)
        table.insert(captured, tostring(message))
      end,
    }

    local wrapped = core.wrap_pipeline_failure("implement", function(_event)
      error("github-devloop: gh-issue-view-failed: bad sha abcdef1234567890 at 2026-06-10T01:02:03Z /tmp/fkst-a")
    end)
    local ok, err = pcall(function()
      wrapped({
        queue = "devloop_ready",
        attempt = 4,
        terminal = false,
        payload = {
          proposal_id = "github-devloop/issue/owner/repo/42",
          source_ref = source_ref(),
        },
      })
    end)

    log = old_log
    t.eq(ok, false)
    t.is_true(tostring(err):find("gh-issue-view-failed", 1, true) ~= nil)
    t.eq(#captured, 1)
    t.is_true(captured[1]:find("github-devloop dept=implement proposal_id=github-devloop/issue/owner/repo/42 tag=FAILURE", 1, true) ~= nil)
    t.is_true(captured[1]:find("error_class=gh-issue-view-failed", 1, true) ~= nil)
    t.is_true(captured[1]:find("fingerprint=", 1, true) ~= nil)
    t.is_true(captured[1]:find("source_ref=external:owner/repo#issue/42", 1, true) ~= nil)
    t.is_true(captured[1]:find("attempt=4", 1, true) ~= nil)
    t.is_nil(captured[1]:find("terminal=", 1, true))
    t.is_true(captured[1]:find("queue=devloop_ready", 1, true) ~= nil)
  end,
  test_error_class_from_message_prefers_inner_codex_failure = function()
    t.eq(
      core.error_class_from_message("github-devloop: fix codex failed: bad sha abcdef1234567890"),
      "codex-failed"
    )
    t.eq(
      core.error_class_from_message("github-devloop: intake codex failed: timed out"),
      "codex-failed"
    )
  end,
  test_build_proposal = function()
    local proposal = payloads_builders.build_proposal(core, issue())
    t.eq(proposal.schema, "consensus.proposal.v1")
    t.eq(proposal.proposal_id, "github-devloop/issue/owner/repo/42")
    t.eq(proposal.title, "Implement decision recorder")
    t.is_true(#proposal.body < 256)
    t.is_true(proposal.body:find("GitHub issue", 1, true) ~= nil)
    t.is_nil(proposal.body:find("Issue body", 1, true))
    t.is_nil(proposal.content_fetch)
    t.eq(proposal.dedup_key, "github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z")
    t.eq(proposal.source_ref.ref, "owner/repo#issue/42")
    t.eq(v_validate_proposal.validate_proposal(core, proposal), true)
  end,
}
