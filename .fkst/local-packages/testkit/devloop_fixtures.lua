local M = {}

local transition_version = require("contract.transition_version")
local gh_argv = require("testkit.gh_argv_mock")
local testing = require("testkit.testing")
local run_fake = testing.run_fake
local run_fake_expecting_failure = testing.run_fake_expecting_failure
local gh_fake = require("forge.github_fake")
local git_fake = require("forge.git_fake")
local mocks_factory = require("testkit.devloop_fixtures.mocks")
local author_policy = require("testkit.github_author_policy")

local function nonce()
  return tostring({}):gsub("[^%w._-]", "_")
end

local function has_value(values, expected)
  for _, value in ipairs(values or {}) do
    if value == expected then
      return true
    end
  end
  return false
end

function M.new(deps)
  deps = deps or {}
  local t = deps.t or fkst.test
  local core = deps.core or error("testkit.devloop_fixtures: deps.core is required")
  local entity_read_mocks = deps.entity_read_mocks
    or error("testkit.devloop_fixtures: deps.entity_read_mocks is required")
  local devloop_base = deps.devloop_base or error("testkit.devloop_fixtures: deps.devloop_base is required")
  local payloads_builders = deps.payloads_builders
    or error("testkit.devloop_fixtures: deps.payloads_builders is required")
  local conv_reconcile = deps.conv_reconcile
    or error("testkit.devloop_fixtures: deps.conv_reconcile is required")
  local m_builders = deps.m_builders or error("testkit.devloop_fixtures: deps.m_builders is required")
  local pr_safety = deps.pr_safety or error("testkit.devloop_fixtures: deps.pr_safety is required")
  local consensus_result_department = deps.consensus_result_department
  local decompose_queue = deps.decompose_queue or "devloop_decompose"
  local runtime_package_name = deps.runtime_package_name or "github-devloop"
  local mock_merge_pr_diff_name_only = deps.mock_merge_pr_diff_name_only == true

  gh_argv.install(t, core)
  devloop_base.configure_trusted_bot_login("fkst-test-bot")

  local ctx = {
    t = t,
    core = core,
    entity_read_mocks = entity_read_mocks,
    m_builders = m_builders,
    pr_safety = pr_safety,
    has_value = has_value,
    default_pr_origin_times = deps.default_pr_origin_times,
    pr_origin_view_times_enabled = deps.pr_origin_view_times_enabled == true,
    pending_result_issue = nil,
    pending_result_read_failure = nil,
    pr_phase_comments = nil,
    pending_pr_origin = nil,
  }

  local function runtime_root(name)
    return "/tmp/fkst-packages-test/" .. runtime_package_name .. "/" .. tostring(now()) .. "/" .. nonce() .. "/" .. name
  end

  local function opts(name, extra)
    local root = runtime_root(name)
    local result = {
      env = {
        FKST_RUNTIME_ROOT = root,
        FKST_RUNTIME_LOG_DIR = root .. "/logs",
        FKST_CANDIDATE_PREFIX = "candidate",
        FKST_CANDIDATE_FROM_SEP = "-from-",
        FKST_DEVLOOP_UPSTREAM_BRANCH = "dev",
        FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      },
    }
    for key, value in pairs((extra and extra.env) or extra or {}) do
      result.env[key] = value
    end
    return result
  end

  local function source_ref()
    return {
      kind = "external",
      ref = "owner/repo#issue/42",
    }
  end

  local function pr_source_ref()
    return {
      kind = "external",
      ref = "owner/repo#pr/7",
    }
  end

  local function issue(extra)
    local value = {
      schema = "github-proxy.v1",
      type = "issue",
      repo = "owner/repo",
      number = 42,
      title = "Implement decision recorder",
      url = "https://github.example/owner/repo/issues/42",
      state = "OPEN",
      updated_at = "2026-06-03T01:02:03Z",
      labels = { "fkst-dev:enabled" },
      dedup_key = "owner/repo#issue#42@2026-06-03T01:02:03Z",
      source_ref = source_ref(),
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    if value.decision == "reject" and value.blocking_gap == nil then value.blocking_gap = "missing regression guard" end
    return value
  end

  local function reached(extra)
    local value = {
      schema = "consensus.consensus_reached.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      decision = "approve",
      body = "All angles approve.",
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = source_ref(),
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function unresolved(extra)
    local value = {
      schema = "consensus.consensus_converge.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = source_ref(),
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function reconcile(extra)
    local value = conv_reconcile.build_devloop_reconcile_payload(unresolved({
      dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/loop/3",
    }), 3, "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z", "no-semantic-progress")
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function ready(extra)
    local value = {
      schema = "github-devloop.ready.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      dedup_key = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      source_ref = source_ref(),
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function reviewing(extra)
    local value = {
      schema = "github-devloop.reviewing.v1",
      proposal_id = "github-devloop/issue/owner/repo/42",
      pr_number = 7,
      version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z",
      dedup_key = "reviewing/github-devloop/issue/owner/repo/42/ready-consensus-github-devloop-issue-owner-repo-42-2026-06-03T01-02-03Z/7",
      source_ref = pr_source_ref(),
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function review_reached(extra)
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, version, "def456")
    local value = {
      schema = "consensus.consensus_reached.v1",
      proposal_id = proposal_id,
      decision = "approve",
      body = "Review consensus approves the diff.",
      dedup_key = "consensus:" .. proposal_id .. "/review",
      source_ref = {
        kind = "external",
        ref = "owner/repo#pr/7",
      },
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function review_unresolved(extra)
    local version = "ready/consensus-github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z"
    local proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, version, "def456")
    local value = {
      schema = "consensus.consensus_converge.v1",
      proposal_id = proposal_id,
      dedup_key = "consensus:" .. proposal_id .. "/review",
      source_ref = {
        kind = "external",
        ref = "owner/repo#pr/7",
      },
    }
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function fixing(extra)
    local event = review_reached({ decision = "reject", body = "Review consensus rejects the diff." })
    local review_version = reviewing().version
    local value = payloads_builders.build_devloop_fixing_payload({
      proposal_id = "github-devloop/issue/owner/repo/42",
      impl_version = core.fix_version_from_review_version(review_version),
    }, 7, {
      review_proposal_id = event.proposal_id,
      review_dedup_key = event.dedup_key,
      reviewed_head_sha = "def456",
      blocking_gap = "missing regression guard",
    }, pr_source_ref())
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function pr_link_marker_for_fix(fix, branch, impl_version)
    return m_builders.pr_link_marker(fix.proposal_id, fix.pr_number, branch, impl_version or fix.version, "dev")
  end

  local function review_meta_event(extra)
    local proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, reviewing().version, "def456")
    local unresolved_event = review_unresolved({
      dedup_key = transition_version.review_loop_at("consensus:" .. proposal_id .. "/review", 2),
    })
    local value = payloads_builders.build_devloop_review_meta_payload(unresolved_event, "github-devloop/issue/owner/repo/42", reviewing().version, 7, 3)
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function review_reconcile(extra)
    local proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, reviewing().version, "def456")
    local event = review_unresolved({
      dedup_key = transition_version.review_loop_at("consensus:" .. proposal_id .. "/review", 3),
      round = 3,
    })
    local value = conv_reconcile.build_devloop_review_reconcile_payload(event, 3, "github-devloop/issue/owner/repo/42", reviewing().version, "def456", "no-semantic-progress")
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function fix_reconcile(extra)
    local issue_version = core.next_fix_version(core.next_fix_version(core.next_fix_version(reviewing().version)))
    local value = conv_reconcile.build_devloop_fix_reconcile_payload({
      proposal_id = "github-devloop/issue/owner/repo/42",
      review_proposal_id = devloop_base.pr_review_proposal_id("owner/repo", 7, issue_version, "def456"),
      review_dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, issue_version, "def456") .. "/review",
      reviewed_head_sha = "def456",
      pr_number = 7,
      source_ref = pr_source_ref(),
    }, issue_version)
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function decompose_event(extra)
    local value = payloads_builders.build_devloop_decompose_payload(fix_reconcile())
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local function merge_ready(extra)
    local event = review_reached()
    local value = payloads_builders.build_devloop_merge_ready_payload("github-devloop/issue/owner/repo/42",
      7,
      reviewing().version,
      {
        review_proposal_id = event.proposal_id,
        review_dedup_key = event.dedup_key,
        reviewed_head_sha = "def456",
      },
      pr_source_ref()
    )
    for key, field in pairs(extra or {}) do
      value[key] = field
    end
    return value
  end

  local mocks = mocks_factory.new(ctx, {
    reviewing = reviewing,
    pr_link_marker_for_fix = pr_link_marker_for_fix,
  })

  local function mock_branch_config_env()
    t.mock_command('printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"', {
      stdout = "dev",
      stderr = "",
      exit_code = 0,
    })
    t.mock_command('printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"', {
      stdout = "",
      stderr = "",
      exit_code = 0,
    })
  end

  local function install_author_policy_env(run_opts)
    return author_policy.mock_env(t, run_opts, {
      configure_trusted_bot_login = devloop_base.configure_trusted_bot_login,
      times = 8,
    })
  end

  local function run_department(path, event, run_opts)
    install_author_policy_env(run_opts)
    return t.run_department(path, event, run_opts)
  end

  local function run_observe(payload, run_opts)
    return run_department("departments/observe_issue/main.lua", {
      queue = "github-proxy.github_entity_changed",
      payload = payload,
    }, run_opts)
  end

  local function build_result_dept()
    if consensus_result_department == nil then
      error("testkit.devloop_fixtures: deps.consensus_result_department is required for run_result")
    end
    local model = gh_fake.model({
      issues = {
        ["owner/repo#issue/42"] = ctx.pending_result_issue or mocks.mock_result_issue_value(),
      },
    })
    local dept = consensus_result_department.make_department({
      github = gh_fake.new(model),
      git = git_fake.new(git_fake.model({})),
    })
    dept.model = model
    return dept, model
  end

  local function run_result(payload, run_opts)
    if ctx.pending_result_read_failure ~= nil then
      ctx.pending_result_read_failure = nil
      return run_department("departments/consensus_result/main.lua", {
        queue = "consensus.consensus_reached",
        payload = payload,
      }, run_opts)
    end

    local dept, model = build_result_dept()
    local result = run_fake(dept, {
      queue = "consensus.consensus_reached",
      payload = payload,
    })
    result.exit_code = 0
    result.model = model
    return result
  end

  local function run_result_expecting_failure(payload, _run_opts)
    local dept, model = build_result_dept()
    local result = run_fake_expecting_failure(dept, {
      queue = "consensus.consensus_reached",
      payload = payload,
    })
    result.exit_code = 1
    result.model = model
    return result
  end

  local function mark_result_read_failure()
    ctx.pending_result_read_failure = true
  end

  local function run_loop(payload, run_opts)
    return run_department("departments/loop/main.lua", {
      queue = "consensus.consensus_converge",
      payload = payload,
    }, run_opts)
  end

  local function run_reconcile(payload, run_opts)
    return run_department("departments/reconcile/main.lua", {
      queue = "devloop_reconcile",
      payload = payload,
    }, run_opts)
  end

  local function run_review_reconcile(payload, run_opts)
    local cached = mocks.take_pr_phase_comments()
    if cached ~= nil then
      local comments = { m_builders.pr_origin_marker(payload.proposal_id, "42", "devloop-owner-repo-42-01HY", payload.issue_version, "dev") }
      for _, comment in ipairs(cached) do
        table.insert(comments, comment)
      end
      entity_read_mocks.mock_default_pr_read(t, comments)
    end
    return run_department("departments/reconcile/main.lua", {
      queue = "devloop_review_reconcile",
      payload = payload,
    }, run_opts)
  end

  local function run_fix_reconcile(payload, run_opts)
    local cached = mocks.take_pr_phase_comments()
    if cached ~= nil then
      local comments = { m_builders.pr_origin_marker(payload.proposal_id, "42", "devloop-owner-repo-42-01HY", payload.issue_version, "dev") }
      for _, comment in ipairs(cached) do
        table.insert(comments, comment)
      end
      entity_read_mocks.mock_default_pr_read(t, comments)
    end
    return run_department("departments/reconcile/main.lua", {
      queue = "devloop_fix_reconcile",
      payload = payload,
    }, run_opts)
  end

  local function run_decompose(payload, run_opts)
    mocks.mock_pr_origin_from_cached(payload, payload and payload.head_sha or "def456")
    return run_department("departments/decompose/main.lua", {
      queue = decompose_queue,
      payload = payload,
    }, run_opts)
  end

  local function run_implement(payload, run_opts, queue, event_extra)
    mock_branch_config_env()
    local event = {
      queue = queue or "devloop_ready",
      payload = payload,
    }
    for key, value in pairs(event_extra or {}) do
      event[key] = value
    end
    return run_department("departments/implement/main.lua", {
      queue = event.queue,
      payload = event.payload,
      attempt = event.attempt,
      terminal = event.terminal,
      ts = event.ts,
    }, run_opts)
  end

  local function run_observe_pr(payload, run_opts, now_seconds)
    mock_branch_config_env()
    mocks.mock_pr_origin_from_cached({
      proposal_id = "github-devloop/issue/owner/repo/42",
      version = reviewing().version,
    }, "def456")
    return run_department("departments/observe_pr/main.lua", {
      queue = "github-proxy.github_entity_changed",
      payload = payload,
      now_seconds = now_seconds,
    }, run_opts)
  end

  local function run_review_pr(payload, run_opts)
    mocks.mock_pr_origin_from_cached(payload, payload and (payload.head_sha or payload.reviewed_head_sha) or "def456")
    return run_department("departments/review_pr/main.lua", {
      queue = "devloop_reviewing",
      payload = payload,
    }, run_opts)
  end

  local function run_review_result(payload, run_opts)
    mock_branch_config_env()
    local _, _, _, head_sha = devloop_base.parse_pr_review_proposal_id(payload.proposal_id)
    mocks.mock_pr_origin_from_cached({ proposal_id = "github-devloop/issue/owner/repo/42", version = reviewing().version }, head_sha)
    return run_department("departments/review_result/main.lua", {
      queue = "consensus.consensus_reached",
      payload = payload,
    }, run_opts)
  end

  local function run_fix(payload, run_opts)
    mock_branch_config_env()
    local cached = mocks.take_pr_phase_comments()
    local pending = mocks.take_pending_pr_origin()
    if cached ~= nil or pending ~= nil then
      local comments = {}
      local head = pending and pending.head or "devloop-owner-repo-42-01HY"
      local base_branch = pending and pending.base_branch or "dev"
      local state = pending and pending.state or "OPEN"
      for _, comment in ipairs(pending and pending.comments or { m_builders.pr_origin_marker(payload.proposal_id, "42", head, payload.version, base_branch) }) do
        table.insert(comments, comment)
      end
      for _, comment in ipairs(cached or {}) do
        table.insert(comments, comment)
      end
      entity_read_mocks.mock_pr_read_forms(t, { comments = comments, head = head, head_sha = payload.reviewed_head_sha or pending and pending.head_sha or "def456", state = state, base_branch = base_branch, labels = pending and pending.labels or {} })
      entity_read_mocks.mock_pr_view_selector(t, {
        comments = comments,
        head = head,
        head_sha = payload.reviewed_head_sha or pending and pending.head_sha or "def456",
        state = state,
        base_branch = base_branch,
        labels = pending and pending.labels or {},
      }, "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository")
    end
    return run_department("departments/fix/main.lua", {
      queue = "devloop_fixing",
      payload = payload,
    }, run_opts)
  end

  local function run_review_loop(payload, run_opts)
    mock_branch_config_env()
    local _, _, _, head_sha = devloop_base.parse_pr_review_proposal_id(payload.proposal_id)
    mocks.mock_pr_origin_from_cached({ proposal_id = "github-devloop/issue/owner/repo/42", version = reviewing().version }, head_sha)
    return run_department("departments/review_loop/main.lua", {
      queue = "consensus.consensus_converge",
      payload = payload,
    }, run_opts)
  end

  local function run_review_meta(payload, run_opts)
    mocks.mock_pr_origin_from_cached(payload, "def456")
    return run_department("departments/review_meta/main.lua", {
      queue = "devloop_review_meta",
      payload = payload,
    }, run_opts)
  end

  local function run_merge(payload, run_opts)
    mock_branch_config_env()
    t.mock_command("gh api --paginate --slurp 'repos/owner/repo/pulls?state=open&base=dev&per_page=100'", {
      stdout = string.format('[{"number":%d,"state":"open","base":{"ref":"dev"}}]\n', tonumber(payload and payload.pr_number) or 7),
      stderr = "",
      exit_code = 0,
    })
    if mock_merge_pr_diff_name_only then
      local skip_default_risk_mock = type(run_opts) == "table"
        and type(run_opts.env) == "table"
        and run_opts.env.FKST_TEST_SKIP_DEFAULT_RISK_MOCK == "1"
      for _ = 1, skip_default_risk_mock and 0 or 2 do
        t.mock_command("gh pr diff '" .. tostring(tonumber(payload and payload.pr_number) or 7) .. "' --repo 'owner/repo' --name-only", {
          stdout = "file.lua\n",
          stderr = "",
          exit_code = 0,
        })
      end
    end
    return run_department("departments/merge/main.lua", {
      queue = "devloop_merge_ready",
      payload = payload,
    }, run_opts)
  end

  return {
    t = t,
    core = core,
    action_label = deps.action_label or "⟦FKST:ACTION⟧",
    reason_label = deps.reason_label or "⟦FKST:REASON⟧",
    has_value = has_value,
    opts = opts,
    source_ref = source_ref,
    pr_source_ref = pr_source_ref,
    issue = issue,
    reached = reached,
    unresolved = unresolved,
    reconcile = reconcile,
    ready = ready,
    reviewing = reviewing,
    review_reached = review_reached,
    review_unresolved = review_unresolved,
    fixing = fixing,
    pr_link_marker_for_fix = pr_link_marker_for_fix,
    review_meta_event = review_meta_event,
    review_reconcile = review_reconcile,
    fix_reconcile = fix_reconcile,
    decompose_event = decompose_event,
    merge_ready = merge_ready,
    run_observe = run_observe,
    run_department = run_department,
    mock_author_policy_configure = devloop_base.configure_trusted_bot_login,
    run_result = run_result,
    run_result_expecting_failure = run_result_expecting_failure,
    mark_result_read_failure = mark_result_read_failure,
    run_loop = run_loop,
    run_reconcile = run_reconcile,
    run_review_reconcile = run_review_reconcile,
    run_fix_reconcile = run_fix_reconcile,
    run_decompose = run_decompose,
    run_implement = run_implement,
    run_observe_pr = run_observe_pr,
    run_review_pr = run_review_pr,
    run_review_result = run_review_result,
    run_fix = run_fix,
    run_review_loop = run_review_loop,
    run_review_meta = run_review_meta,
    run_merge = run_merge,
    json_string = mocks.json_string,
    encode_json_string = mocks.json_string,
    render_comment = mocks.render_comment,
    default_marker_version = mocks.default_marker_version,
    mock_issue_state = mocks.mock_issue_state,
    state_from_labels = mocks.state_from_labels,
    with_default_state_marker = mocks.with_default_state_marker,
    set_pr_phase_comments = mocks.set_pr_phase_comments,
    take_pr_phase_comments = mocks.take_pr_phase_comments,
    set_pending_pr_origin = mocks.set_pending_pr_origin,
    take_pending_pr_origin = mocks.take_pending_pr_origin,
    mock_issue_body = mocks.mock_issue_body,
    mock_issue_result = mocks.mock_issue_result,
    mock_issue_loop = mocks.mock_issue_loop,
    mock_issue_reconcile = mocks.mock_issue_reconcile,
    mock_issue_implement = mocks.mock_issue_implement,
    mock_issue_implement_raw = mocks.mock_issue_implement_raw,
    mock_issue_reviewing = mocks.mock_issue_reviewing,
    mock_issue_review = mocks.mock_issue_review,
    mock_issue_decompose = mocks.mock_issue_decompose,
    mock_issue_fix = mocks.mock_issue_fix,
    mock_issue_fix_for_event = mocks.mock_issue_fix_for_event,
    mock_issue_review_meta = mocks.mock_issue_review_meta,
    mock_issue_merge = mocks.mock_issue_merge,
    argv_rendered = gh_argv.argv_rendered,
  }
end

return M
