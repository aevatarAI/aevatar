local parsers_pr = require("devloop.parsers.pr")
local core = require("core")
local config = require("devloop.config")
local m_facts = require("devloop.markers.facts")

local H = {}

local function issue_from_current(repo, issue_number, ready, current, fact)
  return {
    repo = repo,
    number = issue_number,
    title = current and current.title,
    proposal_id = ready.proposal_id,
    source_ref = ready.source_ref,
    branch = fact.branch,
    base_branch = fact.base_branch,
    head_sha = fact.head_sha,
    comments = current and current.comments or {},
  }
end

local function copy_fact(fact)
  local copied = {}
  for key, value in pairs(fact or {}) do
    copied[key] = value
  end
  return copied
end

local function find_linked_pr(repo, pr_number)
  if pr_number == nil then
    return nil
  end
  local view = core.gh_pr_view_observe(repo, pr_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: pr-child handoff PR view failed: " .. tostring(view.stderr))
  end
  local current_pr = parsers_pr.parse_pr_view_origin(core, view.stdout)
  if type(current_pr.comments) ~= "table" then
    error("github-devloop: pr-child handoff PR view malformed")
  end
  return current_pr
end

local function emit_effects(dept, proposal_id, effects)
  for _, effect in ipairs(effects or {}) do
    core.log_raise(dept, proposal_id, effect.queue, effect.payload)
  end
end

function H.raise_awaiting_pr_from_fact(dept, repo, issue_number, ready, current, fact, reason)
  if config.write_mode(core) ~= "real" then
    core.log_line("info", dept, ready.proposal_id, "OUTBOUND", {
      "mode=dry-run",
      "queue=github-proxy.github_pr_comment_request",
      "repo=" .. tostring(repo),
      "issue=" .. tostring(issue_number),
      "branch=" .. tostring(fact and fact.branch or ""),
      "reason=would create/adopt delegated PR child requires FKST_GITHUB_WRITE=1",
    })
    return
  end
  local handoff_fact = copy_fact(fact)
  local current_link = m_facts.pr_link_fact(core, current and current.comments, ready.proposal_id)
  local linked_pr_number = nil
  local existing_delegation = m_facts.pr_delegation_fact(core, current and current.comments, ready.proposal_id, ready.dedup_key)
  if existing_delegation ~= nil then
    linked_pr_number = existing_delegation.pr_number
  elseif current_link ~= nil then
    linked_pr_number = current_link.pr_number
  end
  local current_pr = nil
  if linked_pr_number ~= nil then
    current_pr = find_linked_pr(repo, linked_pr_number)
    if current_pr ~= nil then
      handoff_fact.head_sha = handoff_fact.head_sha or current_pr.head_sha
      handoff_fact.branch = handoff_fact.branch or current_pr.head_ref_name
      handoff_fact.base_branch = handoff_fact.base_branch or current_pr.base_ref_name
    end
  end
  local issue = issue_from_current(repo, issue_number, ready, current, handoff_fact)
  if current_pr ~= nil then
    issue.pr_comments = current_pr.comments
  end
  local child = core.ensure_pr_child(issue, ready.dedup_key, core.implementation_retry_attempt(ready.dedup_key) or 1)
  core.log_cas_decision(dept, ready.proposal_id, {
    state = "implementing",
    version = ready.dedup_key,
  }, "implementing", "awaiting-pr", child.ready_for_parent_awaiting_pr and "applied(progress-derived)" or "retry-pending(child-start-not-visible)", reason)
  emit_effects(dept, ready.proposal_id, child.effects)
  if not child.ready_for_parent_awaiting_pr then
    return
  end

  local comment_request = core.build_parent_awaiting_pr_comment_request(repo, issue_number, ready, child)
  local label_request = core.build_parent_awaiting_pr_label_request(repo, issue_number, ready, child)
  local add_labels, remove_labels = core.state_label_changes("awaiting-pr")
  core.log_apply(dept, ready.proposal_id, "awaiting-pr", ready.dedup_key, { add = add_labels, remove = remove_labels }, {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  })
  core.log_raise(dept, ready.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise(dept, ready.proposal_id, "github-proxy.github_issue_label_request", label_request)
end

return H
