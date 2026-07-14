local devloop_base = require("devloop.base")
local payloads_builders = require("devloop.payloads.builders")
local conv_reconcile = require("devloop.convergence.reconcile")
local t = fkst.test
local core = require("core")
local gh_argv = require("testkit.gh_argv_mock")
gh_argv.install(t, core)
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local testing = require("testkit.testing")
local run_fake = testing.run_fake
local run_fake_expecting_failure = testing.run_fake_expecting_failure
local gh_fake = require("forge.github_fake")
local git_fake = require("forge.git_fake")
local m_builders = require("devloop.markers.builders")
local action_label = "⟦FKST:ACTION⟧"
local reason_label = "⟦FKST:REASON⟧"

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

local function runtime_root(name)
  return "/tmp/fkst-packages-test/github-devloop/" .. tostring(now()) .. "/" .. nonce() .. "/" .. name
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
    },
  }
  for key, value in pairs((extra and extra.env) or extra or {}) do
    result.env[key] = value
  end
  return result
end

local render_comment, take_pr_phase_comments
local json_string, take_pending_pr_origin
local mock_pr_origin_from_cached
local mock_result_issue_value
local pending_result_issue = nil
local pending_result_read_failure = nil

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
  local value = conv_reconcile.build_devloop_reconcile_payload(core, unresolved({
    dedup_key = "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z/loop/3",
  }), 3, "consensus:github-devloop/issue/owner/repo/42/2026-06-03T01-02-03Z")
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
  local value = {
    schema = "github-devloop.fixing.v1",
    proposal_id = "github-devloop/issue/owner/repo/42",
    pr_number = 7,
    version = core.fix_version_from_review_version(review_version),
    review_proposal_id = event.proposal_id,
    review_dedup_key = event.dedup_key,
    reviewed_head_sha = "def456", blocking_gap = "missing regression guard",
    dedup_key = "fixing/github-devloop/issue/owner/repo/42/v1",
    source_ref = pr_source_ref(),
  }
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function pr_link_marker_for_fix(fix, branch, impl_version)
  return m_builders.pr_link_marker(core, fix.proposal_id, fix.pr_number, branch, impl_version or fix.version, "dev")
end

local function review_meta_event(extra)
  local unresolved_event = review_unresolved({
    dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, reviewing().version, "def456") .. "/review/loop/2",
  })
  local value = payloads_builders.build_devloop_review_meta_payload(core, unresolved_event, "github-devloop/issue/owner/repo/42", reviewing().version, 7, 3)
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function review_reconcile(extra)
  local event = review_unresolved({
    dedup_key = "consensus:" .. devloop_base.pr_review_proposal_id("owner/repo", 7, reviewing().version, "def456") .. "/review/loop/3",
    round = 3,
  })
  local value = conv_reconcile.build_devloop_review_reconcile_payload(core, event, 3, "github-devloop/issue/owner/repo/42", reviewing().version, "def456")
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function fix_reconcile(extra)
  local issue_version = core.next_fix_version(core.next_fix_version(core.next_fix_version(reviewing().version)))
  local value = conv_reconcile.build_devloop_fix_reconcile_payload(core, {
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
  local value = payloads_builders.build_devloop_decompose_payload(core, fix_reconcile())
  for key, field in pairs(extra or {}) do
    value[key] = field
  end
  return value
end

local function merge_ready(extra)
  local event = review_reached()
  local value = payloads_builders.build_devloop_merge_ready_payload(core,
    "github-devloop/issue/owner/repo/42",
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

local function run_observe(payload, run_opts)
  return t.run_department("departments/observe_issue/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = payload,
  }, run_opts)
end

local function build_result_dept()
  local result_dept = require("departments.consensus_result.main")
  local model = gh_fake.model({
    issues = {
      ["owner/repo#issue/42"] = pending_result_issue or mock_result_issue_value(),
    },
  })
  local dept = result_dept.make_department({
    github = gh_fake.new(model),
    git = git_fake.new(git_fake.model({})),
  })
  dept.model = model
  return dept, model
end

local function run_result(payload, run_opts)
  if pending_result_read_failure ~= nil then
    pending_result_read_failure = nil
    return t.run_department("departments/consensus_result/main.lua", {
      queue = "consensus.consensus_reached",
      payload = payload,
    }, run_opts)
  end

  local dept, model = build_result_dept()
  local result = run_fake(dept, {
    queue = "consensus.consensus_reached",
    payload = payload,
  })
  -- run_fake re-raises on pipeline error, so a successful run never carries a
  -- failure here; expected-failure (retry) cases use run_result_expecting_failure.
  result.exit_code = 0
  result.model = model
  return result
end

-- For consensus_result tests that assert the dept errors to drive durable retry
-- (e.g. "thinking state marker not yet visible") — the expected failure is made
-- explicit (#710 Finding 2) instead of relying on a swallowed run_fake error.
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
  pending_result_read_failure = true
end

local function run_loop(payload, run_opts)
  return t.run_department("departments/loop/main.lua", {
    queue = "consensus.consensus_converge",
    payload = payload,
  }, run_opts)
end

local function run_reconcile(payload, run_opts)
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_reconcile",
    payload = payload,
  }, run_opts)
end

local function run_review_reconcile(payload, run_opts)
  local cached = take_pr_phase_comments()
  if cached ~= nil then
    local comments = { m_builders.pr_origin_marker(core, payload.proposal_id, "42", "devloop-owner-repo-42-01HY", payload.issue_version, "dev") }
    for _, comment in ipairs(cached) do
      table.insert(comments, comment)
    end
    entity_read_mocks.mock_default_pr_read(t, comments)
  end
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_review_reconcile",
    payload = payload,
  }, run_opts)
end

local function run_fix_reconcile(payload, run_opts)
  local cached = take_pr_phase_comments()
  if cached ~= nil then
    local comments = { m_builders.pr_origin_marker(core, payload.proposal_id, "42", "devloop-owner-repo-42-01HY", payload.issue_version, "dev") }
    for _, comment in ipairs(cached) do
      table.insert(comments, comment)
    end
    entity_read_mocks.mock_default_pr_read(t, comments)
  end
  return t.run_department("departments/reconcile/main.lua", {
    queue = "devloop_fix_reconcile",
    payload = payload,
  }, run_opts)
end

local function run_decompose(payload, run_opts)
  mock_pr_origin_from_cached(payload, payload and payload.head_sha or "def456")
  return t.run_department("departments/decompose/main.lua", {
    queue = "devloop_decompose",
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
  return t.run_department("departments/implement/main.lua", {
    queue = event.queue,
    payload = event.payload,
    attempt = event.attempt,
    terminal = event.terminal,
    ts = event.ts,
  }, run_opts)
end

local function run_observe_pr(payload, run_opts)
  mock_branch_config_env()
  mock_pr_origin_from_cached({
    proposal_id = "github-devloop/issue/owner/repo/42",
    version = reviewing().version,
  }, "def456")
  return t.run_department("departments/observe_pr/main.lua", {
    queue = "github-proxy.github_entity_changed",
    payload = payload,
  }, run_opts)
end

local function run_review_pr(payload, run_opts)
  mock_pr_origin_from_cached(payload, payload and (payload.head_sha or payload.reviewed_head_sha) or "def456")
  return t.run_department("departments/review_pr/main.lua", {
    queue = "devloop_reviewing",
    payload = payload,
  }, run_opts)
end

local function run_review_result(payload, run_opts)
  mock_branch_config_env()
  local _, _, _, head_sha = devloop_base.parse_pr_review_proposal_id(payload.proposal_id)
  mock_pr_origin_from_cached({ proposal_id = "github-devloop/issue/owner/repo/42", version = reviewing().version }, head_sha)
  return t.run_department("departments/review_result/main.lua", {
    queue = "consensus.consensus_reached",
    payload = payload,
  }, run_opts)
end

local function run_fix(payload, run_opts)
  mock_branch_config_env()
  local cached = take_pr_phase_comments()
  local pending = take_pending_pr_origin()
  if cached ~= nil or pending ~= nil then
    local comments = {}
    local head = pending and pending.head or "devloop-owner-repo-42-01HY"
    local base_branch = pending and pending.base_branch or "dev"
    local state = pending and pending.state or "OPEN"
    for _, comment in ipairs(pending and pending.comments or { m_builders.pr_origin_marker(core, payload.proposal_id, "42", head, payload.version, base_branch) }) do
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
  return t.run_department("departments/fix/main.lua", {
    queue = "devloop_fixing",
    payload = payload,
  }, run_opts)
end

local function run_review_loop(payload, run_opts)
  mock_branch_config_env()
  local _, _, _, head_sha = devloop_base.parse_pr_review_proposal_id(payload.proposal_id)
  mock_pr_origin_from_cached({ proposal_id = "github-devloop/issue/owner/repo/42", version = reviewing().version }, head_sha)
  return t.run_department("departments/review_loop/main.lua", {
    queue = "consensus.consensus_converge",
    payload = payload,
  }, run_opts)
end

local function run_review_meta(payload, run_opts)
  mock_pr_origin_from_cached(payload, "def456")
  return t.run_department("departments/review_meta/main.lua", {
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
  return t.run_department("departments/merge/main.lua", {
    queue = "devloop_merge_ready",
    payload = payload,
  }, run_opts)
end

json_string = function(value)
  return tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

render_comment = function(comment)
  local body, author, created_at = comment, "fkst-test-bot", "2026-06-03T01:00:00Z"
  if type(comment) == "table" then
    body = comment.body
    author = comment.author_login or author
    created_at = comment.created_at or created_at
  end
  local id = type(comment) == "table" and comment.id or nil
  local id_field = id ~= nil and tostring(id) ~= "" and string.format('"id":"%s",', json_string(id)) or ""
  return string.format(
    '{%s"body":"%s","author":{"login":"%s"},"createdAt":"%s"}',
    id_field,
    json_string(body or ""),
    json_string(author),
    json_string(created_at)
  )
end

local default_marker_version = "2026-06-02T00-00-00Z"
local pr_phase_comments = nil
local pending_pr_origin = nil

local function encode_assignees_json(assignees)
  local rendered = {}
  for _, assignee in ipairs(assignees or { "fkst-test-bot" }) do
    table.insert(rendered, string.format('{"login":"%s"}', json_string(assignee)))
  end
  return table.concat(rendered, ",")
end

local function mock_issue_state(labels, state, comments, assignees, author_login)
  local selected_comments = {}
  if comments ~= nil then
    for _, comment in ipairs(comments) do
      table.insert(selected_comments, comment)
    end
  else
    local state_marker = nil
    for _, label in ipairs(labels or {}) do
      if label == "fkst-dev:thinking" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "thinking", default_marker_version)
      elseif label == "fkst-dev:ready" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "ready", default_marker_version)
      elseif label == "fkst-dev:implementing" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "implementing", default_marker_version)
      elseif label == "fkst-dev:pr-open" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "pr-open", default_marker_version)
      elseif label == "fkst-dev:reviewing" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "reviewing", default_marker_version)
      elseif label == "fkst-dev:merge-ready" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "merge-ready", default_marker_version)
      elseif label == "fkst-dev:fixing" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "fixing", default_marker_version)
      elseif label == "fkst-dev:impl-failed" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "impl-failed", default_marker_version)
      elseif label == "fkst-dev:blocked" then
        state_marker = core.state_marker("github-devloop/issue/owner/repo/42", "blocked", default_marker_version)
      end
    end
    if state_marker ~= nil then
      table.insert(selected_comments, state_marker)
    end
  end
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:enabled" }, selected_comments, { state = state or "OPEN", assignees = assignees, author_login = author_login })
  entity_read_mocks.mock_issue_read_forms(t, {
    labels = labels or { "fkst-dev:enabled" },
    comments = selected_comments,
    state = state or "OPEN",
    assignees = assignees,
    author_login = author_login,
  })
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:enabled" }, comments = selected_comments, state = state or "OPEN", assignees = assignees, author_login = author_login }, "title,body,comments,labels,state,updatedAt,assignees")
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:enabled" }, comments = selected_comments, state = state or "OPEN", assignees = assignees, author_login = author_login }, "title,body,comments,labels,state,createdAt,updatedAt,assignees,author")
end

local function state_from_labels(labels)
  for _, label in ipairs(labels or {}) do
    if label == "fkst-dev:thinking" then
      return "thinking"
    end
    if label == "fkst-dev:ready" then
      return "ready"
    end
    if label == "fkst-dev:implementing" then
      return "implementing"
    end
    if label == "fkst-dev:pr-open" then
      return "pr-open"
    end
    if label == "fkst-dev:reviewing" then
      return "reviewing"
    end
    if label == "fkst-dev:merge-ready" then
      return "merge-ready"
    end
    if label == "fkst-dev:merging" then
      return "merging"
    end
    if label == "fkst-dev:merged" then
      return "merged"
    end
    if label == "fkst-dev:fixing" then
      return "fixing"
    end
    if label == "fkst-dev:impl-failed" then
      return "impl-failed"
    end
    if label == "fkst-dev:blocked" then
      return "blocked"
    end
  end
  return nil
end

local function with_default_state_marker(labels, comments)
  local rendered = {}
  local has_explicit_state_marker = false
  for _, comment in ipairs(comments or {}) do
    local body = comment
    if type(comment) == "table" then
      body = comment.body
    end
    if tostring(body or ""):find("fkst:github-devloop:state:v1", 1, true) ~= nil then
      has_explicit_state_marker = true
    end
    table.insert(rendered, comment)
  end
  local state = state_from_labels(labels)
  if state ~= nil and not has_explicit_state_marker then
    table.insert(rendered, core.state_marker("github-devloop/issue/owner/repo/42", state, default_marker_version))
  end
  return rendered
end

local function set_pr_phase_comments(labels, comments)
  pr_phase_comments = with_default_state_marker(labels, comments)
end

take_pr_phase_comments = function()
  local comments = pr_phase_comments
  pr_phase_comments = nil
  return comments
end

local function set_pending_pr_origin(value)
  pending_pr_origin = value
end

local function latest_fix_head_sha(comments)
  local found = nil
  for _, comment in ipairs(comments or {}) do
    local body = comment
    if type(comment) == "table" then
      body = comment.body
    end
    local head = tostring(body or ""):match('fkst:github%-devloop:fix:v1[^<]*new_head_sha="([^"]+)"')
    if head ~= nil and require("devloop.pr_safety").is_safe_head_sha(head) then
      found = head
    end
  end
  return found
end

take_pending_pr_origin = function()
  local value = pending_pr_origin
  pending_pr_origin = nil
  return value
end

mock_pr_origin_from_cached = function(payload, head_sha)
  local cached = take_pr_phase_comments()
  local pending = take_pending_pr_origin()
  if cached == nil and pending == nil then
    return
  end
  local repo = pending and pending.repo or "owner/repo"
  local pr_number = pending and pending.pr_number or 7
  local head = pending and pending.head or "devloop-owner-repo-42-01HY"
  local base_branch = pending and pending.base_branch or "dev"
  local state = pending and pending.state or "OPEN"
  local effective_head_sha = latest_fix_head_sha(cached) or (pending and pending.head_sha) or head_sha or "def456"
  local comments = {}
  if pending ~= nil then
    for _, comment in ipairs(pending.comments or {}) do
      table.insert(comments, comment)
    end
  elseif cached ~= nil then
    table.insert(comments, m_builders.pr_origin_marker(core, payload.proposal_id, "42", head, payload.version or reviewing().version, base_branch))
  end
  for _, comment in ipairs(cached or {}) do
    table.insert(comments, comment)
  end
  local times = pending and pending.times or 3
  local fields = { repo = repo, number = pr_number, comments = comments, head = head, head_sha = effective_head_sha, state = state, base_branch = base_branch, labels = pending and pending.labels or {} }
  fields.times = times
  entity_read_mocks.mock_pr_read_forms(t, fields)
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = repo,
    number = pr_number,
    comments = comments,
    head = head,
    head_sha = effective_head_sha,
    state = state,
    base_branch = base_branch,
    labels = pending and pending.labels or {},
  }, entity_read_mocks.pr_origin_selector, times)
  return repo, pr_number
end

mock_result_issue_value = function(labels, comments, extra)
  local fields = extra or {}
  local gh_labels = {}
  for _, label in ipairs(labels or { "fkst-dev:thinking" }) do
    table.insert(gh_labels, { name = label })
  end
  local gh_comments = {}
  for _, comment in ipairs(comments or with_default_state_marker(labels or { "fkst-dev:thinking" })) do
    if type(comment) == "table" then
      table.insert(gh_comments, comment)
    else
      table.insert(gh_comments, {
        body = tostring(comment),
        author = { login = fields.comment_author_login or fields.author_login or "fkst-test-bot" },
        createdAt = "2026-06-03T01:00:00Z",
      })
    end
  end
  local gh_assignees = {}
  for _, assignee in ipairs(fields.assignees or {}) do
    table.insert(gh_assignees, { login = assignee })
  end
  return {
    number = fields.number or 42,
    title = fields.title or "Implement decision recorder",
    body = fields.body or "",
    url = fields.url or "https://github.example/owner/repo/issues/42",
    updatedAt = fields.updated_at or "2026-06-03T01:02:03Z",
    state = fields.state or "OPEN",
    labels = gh_labels,
    comments = gh_comments,
    assignees = gh_assignees,
    author = { login = fields.author_login or "fkst-test-bot" },
  }
end

local function mock_issue_body(body)
  entity_read_mocks.mock_issue_view_raw_selector(t, {}, "body", {
    stdout = string.format('{"body":"%s"}\n', json_string(body or "Issue body")),
  })
end

local function mock_issue_result(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:thinking" }, comments)
  local fields = {}
  for key, value in pairs(extra or {}) do
    fields[key] = value
  end
  local selected = with_default_state_marker(labels or { "fkst-dev:thinking" }, comments)
  pending_result_issue = mock_result_issue_value(labels or { "fkst-dev:thinking" }, selected, fields)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:thinking" }, selected, fields)
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "labels,comments")
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "assignees,author")
end

local function mock_issue_loop(labels, comments, extra)
  local fields = extra or {}
  local selected = with_default_state_marker(labels or { "fkst-dev:thinking" }, comments)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:thinking" }, selected, fields)
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:thinking" }, comments = selected, title = fields.title, updated_at = fields.updated_at, state = fields.state, assignees = fields.assignees }, "title,updatedAt,labels,comments,state")
end

local function mock_issue_reconcile(labels, comments, extra)
  mock_issue_loop(labels or { "fkst-dev:thinking" }, comments, extra)
end

local function mock_issue_commit_subject_title(fields)
  if fields.commit_title_error ~= nil then
    entity_read_mocks.mock_issue_view_raw_selector(t, {}, "number,title", { stderr = tostring(fields.commit_title_error), exit_code = 1 })
    return
  end
  entity_read_mocks.mock_issue_view_raw_selector(t, {}, "number,title", {
    stdout = string.format('{"number":42,"title":"%s"}\n', json_string(fields.commit_title or fields.title or "Implement decision recorder")),
  })
end

local function mock_issue_title_labels_comments(labels, comments, extra, default_label, include_default_marker, selector)
  local rendered_labels = {}
  local selected_labels = labels or { default_label }
  for _, label in ipairs(selected_labels) do
    table.insert(rendered_labels, string.format('{"name":"%s"}', json_string(label)))
  end
  local rendered_comments = {}
  local selected_comments = comments or {}
  if include_default_marker then
    selected_comments = with_default_state_marker(selected_labels, selected_comments)
  end
  for _, comment in ipairs(selected_comments) do
    table.insert(rendered_comments, render_comment(comment))
  end
  local fields = extra or {}
  local needs_implement_rechecks = has_value(selected_labels, "fkst-dev:ready")
    or has_value(selected_labels, "fkst-dev:implementing")
    or has_value(selected_labels, "fkst-dev:impl-failed")
  local view_count = include_default_marker and needs_implement_rechecks and 5 or 1
  fields.times = view_count
  entity_read_mocks.mock_issue_read_with_defaults(t, selected_labels, selected_comments, fields)
  entity_read_mocks.mock_issue_view_selector(t, {
    repo = fields.repo,
    number = fields.number,
    labels = selected_labels,
    comments = selected_comments,
    title = fields.title,
    body = fields.body,
    state = fields.state,
    assignees = fields.assignees,
    author_login = fields.author_login,
  }, selector or "title,labels,comments", view_count)
  mock_issue_commit_subject_title(fields)
end

local function mock_issue_implement(labels, comments, extra)
  mock_issue_title_labels_comments(labels, comments, extra, "fkst-dev:ready", true, "title,body,labels,comments,state,author")
end

local function mock_issue_implement_raw(labels, comments, extra)
  mock_issue_title_labels_comments(labels or {}, comments, extra, nil, false, "title,body,labels,comments,state,author")
end

local function mock_issue_reviewing(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:pr-open" }, comments)
  local fields = extra or {}
  local selected = with_default_state_marker(labels or { "fkst-dev:pr-open" }, comments)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:pr-open" }, selected, fields)
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:pr-open" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "labels,comments")
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:pr-open" }, comments = selected, assignees = fields.assignees, author_login = fields.author_login }, "assignees,author")
end

local function mock_issue_review(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:reviewing" }, comments)
  local fields = extra or {}
  local selected = with_default_state_marker(labels or { "fkst-dev:reviewing" }, comments)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:reviewing" }, selected, fields)
  entity_read_mocks.mock_issue_view_selector(t, { repo = fields.repo, number = fields.number, labels = labels or { "fkst-dev:reviewing" }, comments = selected, title = fields.title, assignees = fields.assignees, author_login = fields.author_login }, "title,labels,comments,assignees,author")
end

local function mock_issue_decompose(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:blocked" }, comments)
  local fields = extra or {}
  local selected = with_default_state_marker(labels or { "fkst-dev:blocked" }, comments)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:blocked" }, selected, { title = fields.title, body = fields.body or "Body from GitHub" })
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:blocked" }, comments = selected, title = fields.title, body = fields.body or "Body from GitHub" }, "title,body,labels,comments")
end

local function mock_issue_fix(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:fixing" }, comments)
  mock_issue_title_labels_comments(labels, comments, extra, "fkst-dev:fixing", true)
end

local function mock_issue_fix_for_event(fix, labels, comments, branch, impl_version, extra)
  local with_link = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(with_link, comment)
  end
  table.insert(with_link, pr_link_marker_for_fix(fix, branch, impl_version))
  set_pr_phase_comments(labels or { "fkst-dev:fixing" }, comments)
  mock_issue_fix(labels, with_link, extra)
end

local function mock_issue_review_meta(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:review-meta" }, comments)
  mock_issue_fix(labels or { "fkst-dev:review-meta" }, comments, extra)
end

local function mock_issue_merge(labels, comments, extra)
  set_pr_phase_comments(labels or { "fkst-dev:merge-ready" }, comments)
  local fields = extra or {}
  local selected = with_default_state_marker(labels or { "fkst-dev:merge-ready" }, comments)
  entity_read_mocks.mock_issue_read_with_defaults(t, labels or { "fkst-dev:merge-ready" }, selected, { title = fields.title, state = fields.state, assignees = fields.assignees or { "fkst-test-bot" } })
  entity_read_mocks.mock_issue_view_selector(t, { labels = labels or { "fkst-dev:merge-ready" }, comments = selected, title = fields.title, state = fields.state, assignees = fields.assignees or { "fkst-test-bot" } }, "title,labels,comments,state,assignees")
end

return {
  t = t,
  core = core,
  action_label = action_label,
  reason_label = reason_label,
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
  json_string = json_string,
  encode_json_string = json_string,
  render_comment = render_comment,
  default_marker_version = default_marker_version,
  mock_issue_state = mock_issue_state,
  state_from_labels = state_from_labels,
  with_default_state_marker = with_default_state_marker,
  set_pr_phase_comments = set_pr_phase_comments,
  take_pr_phase_comments = take_pr_phase_comments,
  set_pending_pr_origin = set_pending_pr_origin,
  take_pending_pr_origin = take_pending_pr_origin,
  mock_issue_body = mock_issue_body,
  mock_issue_result = mock_issue_result,
  mock_issue_loop = mock_issue_loop,
  mock_issue_reconcile = mock_issue_reconcile,
  mock_issue_implement = mock_issue_implement,
  mock_issue_implement_raw = mock_issue_implement_raw,
  mock_issue_reviewing = mock_issue_reviewing,
  mock_issue_review = mock_issue_review,
  mock_issue_decompose = mock_issue_decompose,
  mock_issue_fix = mock_issue_fix,
  mock_issue_fix_for_event = mock_issue_fix_for_event,
  mock_issue_review_meta = mock_issue_review_meta,
  mock_issue_merge = mock_issue_merge,
  argv_rendered = gh_argv.argv_rendered,
}
