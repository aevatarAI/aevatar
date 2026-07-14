local entity_lib = require("devloop.entity")
local base = require("tests.devloop_base_helpers")
local t = base.t
local core = base.core
local action_label = base.action_label
local reason_label = base.reason_label
local json_string = base.json_string
local render_comment = base.render_comment
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
local m_builders = require("devloop.markers.builders")
local last_merge_comments = nil

local function json_literal(value)
  return '"' .. json_string(value) .. '"'
end
local function review_result_approve_marker(event)
  return m_builders.review_result_marker(core, event.review_proposal_id, event.proposal_id, "approve", event.review_dedup_key)
end

local function append_merged_pr_merging_fact(comments, pr_state)
  if tostring(pr_state or "OPEN") ~= "MERGED" then
    return comments
  end
  local has_merging = false
  local proposal_id = nil
  local version = nil
  local head_sha = nil
  for _, comment in ipairs(comments or {}) do
    local body = type(comment) == "table" and comment.body or comment
    if tostring(body or ""):find("fkst:github-devloop:merging:v1", 1, true) ~= nil then
      has_merging = true
    end
    for marker in tostring(body or ""):gmatch("<!%-%- fkst:github%-devloop:merge%-ready:v1.-%-%->") do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      if entity_lib.parse_entity_proposal_id(marker_proposal) ~= nil and require("devloop.pr_safety").is_safe_head_sha(marker_head_sha) then
        proposal_id = marker_proposal
        version = marker_version
        head_sha = marker_head_sha
      end
    end
  end
  if has_merging or proposal_id == nil then
    return comments
  end
  local merged = {}
  for _, comment in ipairs(comments or {}) do
    table.insert(merged, comment)
  end
  table.insert(merged, core.state_marker(proposal_id, "merging", version))
  table.insert(merged, m_builders.merging_marker(core, proposal_id, 7, version, head_sha or "def456"))
  return merged
end

local function merge_comments(event, branch, impl_version, include_review_result)
  local version = event.version
  local comments = {
    m_builders.pr_origin_marker(core, event.proposal_id, 42, branch or "devloop-owner-repo-42-01HY", impl_version or version, "dev"),
    core.state_marker(event.proposal_id, "merge-ready", version),
    m_builders.merge_ready_marker(core, event.proposal_id, event.pr_number, version, event.review_proposal_id, event.review_dedup_key, event.reviewed_head_sha),
  }
  if include_review_result ~= false then
    table.insert(comments, review_result_approve_marker(event))
  end
  return comments
end

local function pr_native_comments(event, include_review_result)
  local comments = {
    core.state_marker(event.proposal_id, "merge-ready", event.version),
    m_builders.merge_ready_marker(core, event.proposal_id, event.pr_number, event.version, event.review_proposal_id, event.review_dedup_key, event.reviewed_head_sha),
  }
  if include_review_result ~= false then
    table.insert(comments, review_result_approve_marker(event))
  end
  return comments
end

local function merge_ready_pr_number_from_comments(comments, head)
  for _, comment in ipairs(comments or {}) do
    local body = type(comment) == "table" and comment.body or comment
    for marker in tostring(body or ""):gmatch("<!%-%- fkst:github%-devloop:merge%-ready:v1.-%-%->") do
      local pr_number = tonumber(marker:match('pr="(%d+)"'))
      if pr_number ~= nil then
        return pr_number
      end
    end
  end
  local head_pr_number = tostring(head or ""):match("^devloop%-owner%-repo%-(%d+)$")
  if head_pr_number ~= nil then
    return tonumber(head_pr_number)
  end
  return 7
end

local function mock_pr_origin_for(fields)
  local effective = fields or {}
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = effective.repo or "owner/repo",
    number = effective.number or 7,
    comments = effective.comments,
    head = effective.head or "devloop-owner-repo-42-01HY",
    head_sha = effective.head_sha or "def456",
    state = effective.state or "OPEN",
    base_branch = effective.base_branch or "dev",
    labels = effective.labels or {},
    mergeable = effective.mergeable,
    merge_state = effective.merge_state,
    times = effective.times or 1,
  })
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = effective.repo or "owner/repo",
    number = effective.number or 7,
    comments = effective.comments,
    head = effective.head or "devloop-owner-repo-42-01HY",
    head_sha = effective.head_sha or "def456",
    state = effective.state or "OPEN",
    base_branch = effective.base_branch or "dev",
    labels = effective.labels or {},
    mergeable = effective.mergeable,
    merge_state = effective.merge_state,
  }, entity_read_mocks.pr_origin_selector, effective.times or 1)
end

local function mock_pr_origin(comments, head, head_sha, state, base_branch, times, labels)
  local input_comments = comments
  local cached = base.take_pr_phase_comments()
  local has_state_marker = false
  for _, comment in ipairs(input_comments or {}) do
    if tostring(type(comment) == "table" and comment.body or comment):find("fkst:github-devloop:state:v1", 1, true) ~= nil then
      has_state_marker = true
    end
  end
  if cached == nil and input_comments ~= nil and #input_comments > 0 and not has_state_marker then
    base.set_pending_pr_origin({
      repo = "owner/repo",
      pr_number = 7,
      comments = input_comments,
      head = head or "devloop-owner-repo-42-01HY",
      head_sha = head_sha or "def456",
      state = state or "OPEN",
      base_branch = base_branch or "dev",
      labels = labels or {},
    })
    return
  end
  if input_comments == nil or #input_comments == 0 then
    input_comments = cached or {}
  elseif cached ~= nil then
    local merged = {}
    for _, comment in ipairs(input_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(cached) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  if tostring(state or "OPEN") == "MERGED" and last_merge_comments ~= nil then
    local merged = {}
    for _, comment in ipairs(last_merge_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(input_comments or {}) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  input_comments = append_merged_pr_merging_fact(input_comments, state)
  if tostring(state or "OPEN") ~= "MERGED" then
    last_merge_comments = input_comments
  end
  mock_pr_origin_for({
    repo = "owner/repo",
    number = 7,
    comments = input_comments,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    base_branch = base_branch or "dev",
    labels = labels or {},
    times = times or 1,
  })
end

local function mock_pr_merge(comments, head, head_sha, state, head_repo, cross_repo, mergeable, merge_state, rollup_state, rollup_conclusion, merged_at, is_draft, base_sha)
  -- Merge-gate PR reads are force-fresh authorization checks, not cacheable observe/open entity reads.
  local input_comments = comments
  local cached = base.take_pr_phase_comments()
  if input_comments == nil or #input_comments == 0 then
    input_comments = cached or last_merge_comments or {}
  elseif cached ~= nil then
    local merged = {}
    for _, comment in ipairs(input_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(cached) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  if cached == nil
    and last_merge_comments ~= nil
    and tostring(state or "OPEN") == "OPEN"
    and (comments == nil or #comments == 0) then
    local merged = {}
    for _, comment in ipairs(last_merge_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(input_comments or {}) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  input_comments = append_merged_pr_merging_fact(input_comments, state)
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = "owner/repo",
    number = 7,
    comments = input_comments,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
    merged_at = merged_at,
    is_draft = is_draft,
    base_sha = base_sha or "abc123",
    mergeable = mergeable,
    merge_state = merge_state,
    status_check_rollup_json = '[{"__typename":"CheckRun","completedAt":"2026-06-03T02:04:04Z","conclusion":' .. json_literal(rollup_conclusion or "SUCCESS") .. ',"detailsUrl":"https://example.invalid/checks/test","name":"test","startedAt":"2026-06-03T02:03:04Z","status":' .. json_literal(rollup_state or "COMPLETED") .. ',"workflowName":"test"}]',
    merge_view = true,
    register_merge_views = false,
  })
  entity_read_mocks.mock_pr_view_selector(t, {
    repo = "owner/repo",
    number = merge_ready_pr_number_from_comments(input_comments, head),
    comments = input_comments,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
    merged_at = merged_at,
    is_draft = is_draft,
    base_sha = base_sha or "abc123",
    mergeable = mergeable,
    merge_state = merge_state,
    status_check_rollup_json = '[{"__typename":"CheckRun","completedAt":"2026-06-03T02:04:04Z","conclusion":' .. json_literal(rollup_conclusion or "SUCCESS") .. ',"detailsUrl":"https://example.invalid/checks/test","name":"test","startedAt":"2026-06-03T02:03:04Z","status":' .. json_literal(rollup_state or "COMPLETED") .. ',"workflowName":"test"}]',
  }, entity_read_mocks.pr_merge_selector)
  if tostring(state or "OPEN") ~= "MERGED" then
    last_merge_comments = input_comments
  end
end

local function mock_pr_merge_rollup(comments, rollup_json, head, head_sha, state, head_repo, cross_repo, mergeable, merge_state, merged_at, is_draft, base_sha)
  local input_comments = comments
  local cached = base.take_pr_phase_comments()
  if input_comments == nil or #input_comments == 0 then
    input_comments = cached or last_merge_comments or {}
  elseif cached ~= nil then
    local merged = {}
    for _, comment in ipairs(input_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(cached) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  if tostring(state or "OPEN") == "MERGED" and last_merge_comments ~= nil then
    local merged = {}
    for _, comment in ipairs(last_merge_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(input_comments or {}) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  input_comments = append_merged_pr_merging_fact(input_comments, state)
  if tostring(state or "OPEN") ~= "MERGED" then
    last_merge_comments = input_comments
  end
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = "owner/repo",
    number = 7,
    comments = input_comments,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
    merged_at = merged_at,
    is_draft = is_draft,
    base_sha = base_sha or "abc123",
    mergeable = mergeable,
    merge_state = merge_state,
    status_check_rollup_json = rollup_json,
    merge_view = true,
    register_merge_views = false,
  })
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = input_comments,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
    merged_at = merged_at,
    is_draft = is_draft,
    base_sha = base_sha or "abc123",
    mergeable = mergeable,
    merge_state = merge_state,
    status_check_rollup_json = rollup_json,
  }, "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup")
end

local function mock_merging_comment(exit_code, stderr)
  t.mock_command("gh pr comment '7' --repo 'owner/repo' --body-file", {
    stdout = "commented\n",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_pr_merge_command(exit_code, stderr)
  mock_pr_merge(nil, "devloop-owner-repo-42-01HY", "def456", "OPEN")
  mock_merging_comment()
  t.mock_command("gh pr merge '7' --repo 'owner/repo' --merge --match-head-commit 'def456'", {
    stdout = "merged\n",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_pr_ready(exit_code, stderr)
  t.mock_command("gh pr ready '7' --repo 'owner/repo'", {
    stdout = "ready\n",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_required_check_runs_for(head_sha, conclusion, repo)
  local sha = tostring(head_sha or "def456")
  t.mock_command("gh api 'repos/" .. tostring(repo or "owner/repo") .. "/commits/" .. sha .. "/check-runs'", {
    stdout = '{"total_count":1,"check_runs":[{"name":"test","status":"completed","conclusion":"'
      .. tostring(conclusion or "failure")
      .. '","head_sha":"'
      .. sha
      .. '"}]}\n',
    stderr = "",
    exit_code = 0,
  })
end

local function has_call(needle)
  for _, call in ipairs(t.command_calls()) do
    if gh_argv.call_contains(call, needle) then
      return true
    end
  end
  return false
end

local function mock_issue_close(exit_code, stderr)
  t.mock_command("gh issue close", {
    stdout = "closed\n",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function merge_comments_with_merging(event, branch, impl_version)
  local comments = merge_comments(event, branch, impl_version)
  table.insert(comments, core.state_marker(event.proposal_id, "merging", event.version))
  table.insert(comments, m_builders.merging_marker(core, event.proposal_id, event.pr_number, event.version, event.reviewed_head_sha))
  return comments
end

local function mock_pr_fix(comments, head, head_sha, state, head_repo, cross_repo, times)
  local cached = base.take_pr_phase_comments()
  local with_origin = {
    m_builders.pr_origin_marker(core, "github-devloop/issue/owner/repo/42", 42, head or "devloop-owner-repo-42-01HY", base.reviewing().version, "dev"),
  }
  local input_comments = comments
  if input_comments == nil or #input_comments == 0 then
    input_comments = cached or {}
  end
  for _, comment in ipairs(input_comments or {}) do
    table.insert(with_origin, comment)
  end
  for _, comment in ipairs(cached or {}) do
    table.insert(with_origin, comment)
  end
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = "owner/repo",
    number = 7,
    comments = with_origin,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  })
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = with_origin,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  }, "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository", times or 1)
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = with_origin,
    head = head or "devloop-owner-repo-42-01HY",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  }, "headRefName,headRefOid,baseRefName,state,updatedAt,comments,headRepository,headRepositoryOwner,isCrossRepository", times or 1)
end

local function mock_pr_native_fix(comments, head, head_sha, state, head_repo, cross_repo, times)
  local cached = base.take_pr_phase_comments()
  local input_comments = comments
  if input_comments == nil or #input_comments == 0 then
    input_comments = cached or {}
  elseif cached ~= nil then
    local merged = {}
    for _, comment in ipairs(input_comments) do
      table.insert(merged, comment)
    end
    for _, comment in ipairs(cached) do
      table.insert(merged, comment)
    end
    input_comments = merged
  end
  entity_read_mocks.mock_pr_read_forms(t, {
    repo = "owner/repo",
    number = 7,
    comments = input_comments,
    head = head or "pr-native-branch",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  })
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = input_comments,
    head = head or "pr-native-branch",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  }, "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository", times or 1)
  entity_read_mocks.mock_pr_view_selector(t, {
    comments = input_comments,
    head = head or "pr-native-branch",
    head_sha = head_sha or "def456",
    state = state or "OPEN",
    head_repo = head_repo or "owner/repo",
    cross_repo = cross_repo,
  }, "headRefName,headRefOid,baseRefName,state,updatedAt,comments,headRepository,headRepositoryOwner,isCrossRepository", times or 1)
end

local function mock_pr_origin_sequence(entries)
  for _, entry in ipairs(entries or {}) do
    local cached = base.take_pr_phase_comments()
    local comments = entry.comments
    if comments == nil then
      comments = {
        m_builders.pr_origin_marker(core, "github-devloop/issue/owner/repo/42", "42", entry.head or "devloop-owner-repo-42-01HY", base.reviewing().version, "dev"),
      }
    end
    if cached ~= nil then
      local merged = {}
      for _, comment in ipairs(comments) do
        table.insert(merged, comment)
      end
      for _, comment in ipairs(cached) do
        table.insert(merged, comment)
      end
      comments = merged
    end
    entity_read_mocks.mock_pr_read_forms(t, {
      repo = "owner/repo",
      number = 7,
      comments = comments,
      head = entry.head or "devloop-owner-repo-42-01HY",
      head_sha = entry.head_sha or "def456",
      state = entry.state or "OPEN",
      base_branch = "dev",
      labels = {},
      times = 1,
    })
    entity_read_mocks.mock_pr_view_selector(t, {
      comments = comments,
      head = entry.head or "devloop-owner-repo-42-01HY",
      head_sha = entry.head_sha or "def456",
      state = entry.state or "OPEN",
      base_branch = "dev",
      labels = {},
    }, entity_read_mocks.pr_origin_selector)
  end
end

local function mock_pr_head(head, state)
  entity_read_mocks.mock_pr_view_selector(t, {
    head = head or "devloop-owner-repo-42-01HY",
    base_branch = "dev",
    state = state or "OPEN",
  }, entity_read_mocks.pr_head_selector)
end

local function mock_pr_diff(diff, exit_code, stderr)
  t.mock_command("gh pr diff", {
    stdout = diff or "diff --git a/file.lua b/file.lua\n+return true\n",
    stderr = stderr or "",
    exit_code = exit_code or 0,
  })
end

local function mock_branch_exists(branch, head)
  t.mock_command("show-ref --verify --quiet", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("rev-parse --verify", {
    stdout = (head or "abc123") .. "\n",
    stderr = "",
    exit_code = 0,
  })
end

local function mock_branch_head_descends(descends)
  t.mock_command("merge-base --is-ancestor", {
    stdout = "",
    stderr = "",
    exit_code = descends == false and 1 or 0,
  })
end

local function mock_meta_codex(action, reason, exit_code, blocking_gap)
  local stdout = ""
  if action ~= nil then
    stdout = action_label .. " " .. tostring(action) .. "\n" .. reason_label .. " " .. tostring(reason or "Reason.")
    if action == "fix" then
      stdout = stdout .. "\nBlocking gap: " .. tostring(blocking_gap or "missing retry guard")
    end
  end
  t.mock_command('printf %s "$FKST_RUNTIME_ROOT"', {
    stdout = "/tmp/fkst-packages-test/github-devloop/runtime",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("mkdir -p", {
    stdout = "",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("codex exec", {
    stdout = stdout,
    stderr = "",
    exit_code = exit_code or 0,
  })
end

local function reset_pr_helper_state()
  last_merge_comments = nil
end


return {
  merge_comments = merge_comments,
  pr_native_comments = pr_native_comments,
  review_result_approve_marker = review_result_approve_marker,
  mock_pr_origin = mock_pr_origin,
  mock_pr_origin_for = mock_pr_origin_for,
  mock_pr_merge = mock_pr_merge,
  mock_pr_merge_rollup = mock_pr_merge_rollup,
  mock_merging_comment = mock_merging_comment,
  mock_pr_merge_command = mock_pr_merge_command,
  mock_pr_ready = mock_pr_ready,
  mock_required_check_runs_for = mock_required_check_runs_for,
  has_call = has_call,
  mock_issue_close = mock_issue_close,
  merge_comments_with_merging = merge_comments_with_merging,
  mock_pr_fix = mock_pr_fix,
  mock_pr_native_fix = mock_pr_native_fix,
  mock_pr_origin_sequence = mock_pr_origin_sequence,
  mock_pr_head = mock_pr_head,
  mock_pr_diff = mock_pr_diff,
  mock_branch_exists = mock_branch_exists,
  mock_branch_head_descends = mock_branch_head_descends,
  mock_meta_codex = mock_meta_codex,
  reset_pr_helper_state = reset_pr_helper_state,
}
