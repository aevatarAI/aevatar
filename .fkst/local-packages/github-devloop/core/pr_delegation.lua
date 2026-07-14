local git_mechanics = require("devloop.git_mechanics")
local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local requests_labels = require("devloop.requests.labels")
local parsers_pr = require("devloop.parsers.pr")
local m_facts = require("devloop.markers.facts")
local S = {}
local config = require("devloop.config")

function S.install(M)
local gate = require("devloop.gate")
local m_builders = require("devloop.markers.builders")
local child_start_visible_gate = nil

local function load_child_start_visible_gate()
  if child_start_visible_gate == nil then
    child_start_visible_gate = gate.load_gate("child_start_visible")
  end
  return child_start_visible_gate
end

local function issue_fields(issue, impl_version)
  if type(issue) ~= "table" then
    error("github-devloop: invalid delegation issue")
  end
  local repo = issue.repo
  local issue_number = issue.number or issue.issue_number
  local proposal_id = issue.proposal_id or base_ids.proposal_id(repo, issue_number)
  if base_ids.parse_proposal_id(proposal_id) == nil then
    error("github-devloop: invalid delegation issue proposal")
  end
  if not strings.is_bounded_string(impl_version, M._max_dedup_len) then
    error("github-devloop: invalid delegation implementation version")
  end
  return repo, issue_number, proposal_id
end

local function delegation_key(proposal_id, impl_version, generation)
  local value = "g" .. tostring(generation or 1)
  value = value:gsub(":", "-")
  if not strings.is_path_safe_key(value, M._max_dedup_len) then
    error("github-devloop: invalid delegation generation")
  end
  return value
end

local function branch_for(repo, issue_number, impl_version)
  return devloop_base.implement_branch(repo, issue_number, M.implementation_base_version(impl_version))
end

local function parse_open_prs_for_branch(stdout, branch, base_branch)
  local found = {}
  for _, pr in ipairs(parsers_pr.parse_pr_list_head_base(M, stdout)) do
    if tostring(pr.head_ref_name or "") == tostring(branch)
      and (base_branch == nil or tostring(pr.base_ref_name or "") == tostring(base_branch))
      and tostring(pr.state or ""):lower() ~= "closed" then
      table.insert(found, pr)
    end
  end
  table.sort(found, function(a, b) return tonumber(a.number or 0) < tonumber(b.number or 0) end)
  return found
end

local function find_pr(repo, branch, base_branch)
  local listed = M.gh_pr_list_head_base(repo, branch, base_branch, 30)
  if listed.exit_code ~= 0 then
    error("github-devloop: pr-delegation PR list failed: " .. tostring(listed.stderr))
  end
  local prs = parse_open_prs_for_branch(listed.stdout, branch, base_branch)
  if #prs == 0 then
    return nil
  end
  return prs[1]
end

local function create_pr(repo, issue_number, branch, base_branch, title, body)
  local effective_title = tostring(title or "")
  if effective_title == "" then
    effective_title = "github-devloop implementation for #" .. tostring(issue_number)
  end
  if #effective_title > M._max_pr_title_len then
    effective_title = base_ids.truncate_utf8(effective_title, M._max_pr_title_len)
  end
  local created = M.gh_pr_create_body(repo, branch, base_branch, effective_title, body, 60)
  if created.exit_code ~= 0 then
    error("github-devloop: pr-delegation PR create failed: " .. tostring(created.stderr))
  end
end

local function require_head_sha(branch, expected_head)
  local head = tostring(expected_head or "")
  if require("devloop.pr_safety").is_safe_head_sha(head) then
    return head
  end
  local fetched = git_mechanics.current_branch_head_sha(M.git, branch)
  if not require("devloop.pr_safety").is_safe_head_sha(fetched) then
    error("github-devloop: pr-delegation branch head is missing")
  end
  return fetched
end

local function build_pr_open_comment_request(repo, pr_number, pr_proposal_id, issue_proposal_id, issue_number, impl_version, branch, base_branch, head_sha, source_ref, delegation)
  if not require("devloop.pr_safety").is_safe_head_sha(head_sha) then
    error("github-devloop: invalid pr-delegation head sha")
  end
  local body = "github-devloop PR child open"
    .. "\n\n" .. m_builders.pr_origin_marker(M, issue_proposal_id, issue_number, branch, impl_version, base_branch)
    .. "\n" .. m_builders.pr_link_marker(M, issue_proposal_id, pr_number, branch, impl_version, base_branch)
    .. "\n" .. M.state_marker(issue_proposal_id, "pr-open", impl_version)
  local request = entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = pr_number,
  }, body, base_ids.dedup_key({
    "pr-delegation",
    "pr-open",
    tostring(issue_proposal_id),
    tostring(delegation),
  }), source_ref)
  request.handoff = {
    kind = "github-devloop.pr_open",
    proposal_id = issue_proposal_id,
    pr_number = pr_number,
    version = impl_version,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  return request
end

local function build_issue_delegation_comment_request(repo, issue_number, issue_proposal_id, pr_proposal_id, pr_number, impl_version, delegation, source_ref)
  local marker = m_builders.pr_delegation_marker(M, issue_proposal_id, pr_proposal_id, pr_number, impl_version, delegation)
  return entity_lib.build_entity_comment_request({
    kind = "issue",
    repo = repo,
    number = issue_number,
  }, "github-devloop delegated implementation to PR #" .. tostring(pr_number)
    .. "\n\n" .. marker, base_ids.dedup_key({
    "pr-delegation",
    "issue",
    tostring(issue_proposal_id),
    tostring(impl_version),
    tostring(pr_number),
    tostring(delegation),
  }), source_ref)
end

local function build_parent_awaiting_comment(repo, issue_number, ready, child)
  local body = "github-devloop delegated implementation to PR #" .. tostring(child.pr_number)
    .. "\n\n" .. M.state_marker(ready.proposal_id, "awaiting-pr", ready.dedup_key)
    .. "\n" .. m_builders.pr_delegation_marker(M, 
      ready.proposal_id,
      child.pr_proposal_id,
      child.pr_number,
      ready.dedup_key,
      child.delegation_generation
    )
  return entity_lib.build_entity_comment_request({
    kind = "issue",
    repo = repo,
    number = issue_number,
  }, body, base_ids.dedup_key({
    "awaiting-pr",
    tostring(ready.proposal_id),
    tostring(ready.dedup_key),
    tostring(child.pr_number),
    tostring(child.delegation_generation),
  }), ready.source_ref)
end

local function build_parent_awaiting_label(repo, issue_number, ready, child)
  return requests_labels.build_state_label_request(M, repo, issue_number, "awaiting-pr", base_ids.dedup_key({
    "awaiting-pr",
    "label",
    tostring(ready.proposal_id),
    tostring(ready.dedup_key),
    tostring(child.pr_number),
    tostring(child.delegation_generation),
  }), ready.source_ref)
end

local function existing_delegation(issue, issue_proposal_id, delegation)
  local fact = m_facts.pr_delegation_fact(M, issue and issue.comments, issue_proposal_id, nil, delegation)
  if fact == nil then
    return nil
  end
  return {
    number = fact.pr_number,
    pr_number = fact.pr_number,
    version = fact.version,
    delegation = fact.delegation,
    pr_proposal_id = fact.pr_proposal_id,
    head_ref_name = issue.branch or (issue.implementation and issue.implementation.branch),
    base_ref_name = issue.base_branch or (issue.implementation and issue.implementation.base_branch),
  }
end

local function child_start_facts(comments)
  local origin = m_facts.pr_origin_fact(M, comments)
  local origin_fields = nil
  local pr_open_reached = false
  if origin ~= nil then
    origin_fields = {
      proposal_id = origin.proposal_id,
      issue_number = origin.issue_number,
      impl_version = origin.impl_version,
      branch = origin.branch,
      base_branch = origin.base_branch,
    }
    pr_open_reached = M.reached(comments, origin.proposal_id, "pr-open", { domain = "github-devloop-pr" })
  end
  return gate.facts({
    reached = function(milestone, opts)
      local domain = opts and (opts.domain or opts.milestone_domain)
      if tostring(milestone or "") ~= "pr-open" or (domain ~= nil and tostring(domain) ~= "github-devloop-pr") then
        return false
      end
      if pr_open_reached or origin_fields ~= nil then
        return true
      end
      return false
    end,
    lineage_equals = function(field, expected)
      if origin_fields == nil then
        return false
      end
      return tostring(origin_fields[field] or "") == tostring(expected)
    end,
  })
end

local function child_start_bindings(issue_proposal_id, issue_number, impl_version, branch, base_branch)
  return {
    proposal_id = issue_proposal_id,
    issue_number = issue_number,
    impl_version = impl_version,
    branch = branch,
    base_branch = base_branch,
  }
end

function M.build_pr_delegation_open_comment_request(repo, pr_number, issue_proposal_id, pr_proposal_id, issue_number, impl_version, branch, base_branch, head_sha, source_ref, delegation)
  return build_pr_open_comment_request(repo, pr_number, pr_proposal_id, issue_proposal_id, issue_number, impl_version, branch, base_branch, head_sha, source_ref, delegation)
end

function M.build_parent_awaiting_pr_comment_request(repo, issue_number, ready, child)
  return build_parent_awaiting_comment(repo, issue_number, ready, child)
end

function M.build_parent_awaiting_pr_label_request(repo, issue_number, ready, child)
  return build_parent_awaiting_label(repo, issue_number, ready, child)
end

local function child_from_pr(issue, impl_version, generation, pr, repo, issue_number, issue_proposal_id, branch, base_branch, delegation, child_start_visible)
  if pr == nil then
    return nil
  end
  if not require("devloop.pr_safety").is_safe_pr_number(pr.number) then
    error("github-devloop: pr-delegation adopted invalid PR")
  end
  local pr_number = tonumber(pr.number)
  local pr_source_ref = entity_lib.pr_source_ref(repo, pr_number)
  local pr_proposal_id = entity_lib.pr_proposal_id(repo, pr_number)
  local head_sha = pr.head_sha or issue.head_sha or (issue.implementation and issue.implementation.head_sha)
  local effects = {}
  if child_start_visible == nil then
    child_start_visible = gate.holds(
      load_child_start_visible_gate(),
      child_start_facts(issue.pr_comments or {}),
      child_start_bindings(issue_proposal_id, issue_number, impl_version, branch, base_branch)
    )
  end
  if not child_start_visible then
    table.insert(effects, {
      queue = "github-proxy.github_pr_comment_request",
      payload = build_pr_open_comment_request(repo, pr_number, pr_proposal_id, issue_proposal_id, issue_number, impl_version, branch, base_branch, head_sha, pr_source_ref, delegation),
    })
  end
  local delegation_fact = existing_delegation(issue, issue_proposal_id, delegation)
  local issue_delegation_visible = delegation_fact ~= nil
    and tonumber(delegation_fact.pr_number) == pr_number
    and tostring(delegation_fact.version or "") == tostring(impl_version)
    and tostring(delegation_fact.delegation or "") == tostring(delegation)
  if not issue_delegation_visible then
    table.insert(effects, {
      queue = "github-proxy.github_issue_comment_request",
      payload = build_issue_delegation_comment_request(repo, issue_number, issue_proposal_id, pr_proposal_id, pr_number, impl_version, delegation, entity_lib.issue_source_ref(repo, issue_number)),
    })
  end
  return {
    issue_proposal_id = issue_proposal_id,
    pr_proposal_id = pr_proposal_id,
    pr_number = pr_number,
    pr_source_ref = pr_source_ref,
    branch = branch,
    base_branch = base_branch,
    head_sha = head_sha,
    delegation_generation = delegation,
    child_start_visible = child_start_visible,
    issue_delegation_visible = issue_delegation_visible,
    ready_for_parent_awaiting_pr = child_start_visible and issue_delegation_visible,
    effects = effects,
  }
end

function M.adopt_existing_pr_child(issue, impl_version, generation)
  local repo, issue_number, issue_proposal_id = issue_fields(issue, impl_version)
  local base_branch = issue.base_branch or (issue.implementation and issue.implementation.base_branch) or config.branch_config(M).integration
  local branch = issue.branch or (issue.implementation and issue.implementation.branch) or branch_for(repo, issue_number, impl_version)
  local delegation = delegation_key(issue_proposal_id, impl_version, generation or 1)
  local pr = find_pr(repo, branch, base_branch)
  return child_from_pr(issue, impl_version, generation, pr, repo, issue_number, issue_proposal_id, branch, base_branch, delegation)
end

function M.ensure_pr_child(issue, impl_version, generation)
  local repo, issue_number, issue_proposal_id = issue_fields(issue, impl_version)
  local base_branch = issue.base_branch or (issue.implementation and issue.implementation.base_branch) or config.branch_config(M).integration
  local branch = issue.branch or (issue.implementation and issue.implementation.branch) or branch_for(repo, issue_number, impl_version)
  local delegation = delegation_key(issue_proposal_id, impl_version, generation or 1)
  local pr = existing_delegation(issue, issue_proposal_id, delegation)
  if pr == nil then
    pr = find_pr(repo, branch, base_branch)
  end
  if pr == nil then
    local head_sha = require_head_sha(branch, issue.head_sha or (issue.implementation and issue.implementation.head_sha))
    local body = "github-devloop implementation PR for issue #" .. tostring(issue_number)
    create_pr(repo, issue_number, branch, base_branch, issue.title, body)
    pr = find_pr(repo, branch, base_branch)
    if pr == nil then
      error("github-devloop: pr-delegation PR create did not yield an adoptable branch PR")
    end
    if pr.head_sha ~= nil and tostring(pr.head_sha):lower() ~= tostring(head_sha):lower() then
      error("github-devloop: pr-delegation created PR head mismatch")
    end
  end
  local child_start_visible = gate.holds(
    load_child_start_visible_gate(),
    child_start_facts(issue.pr_comments or {}),
    child_start_bindings(issue_proposal_id, issue_number, impl_version, branch, base_branch)
  )
  return child_from_pr(issue, impl_version, generation, pr, repo, issue_number, issue_proposal_id, branch, base_branch, delegation, child_start_visible)
end
end

return S
