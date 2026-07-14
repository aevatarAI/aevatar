local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local requests_labels = require("devloop.requests.labels")
local core = require("core")
local forks = require("devloop.forks")

local M = {}

local duplicate_label_name = "fkst:duplicate-fork"

local function duplicate_comment(repo, issue_number, ready, origin, canonical_number)
  local body = "Duplicate fork for " .. tostring(origin.repo) .. "#" .. tostring(origin.issue_number)
    .. "; canonical fork is #" .. tostring(canonical_number)
    .. "\n\n<!-- fkst:github-devloop:duplicate-fork:v1 original_issue=\""
    .. tostring(origin.issue_number)
    .. "\" canonical=\""
    .. tostring(canonical_number)
    .. "\" -->"
  return entity_lib.build_entity_comment_request({
    kind = "issue",
    repo = repo,
    number = issue_number,
  }, body, base_ids.dedup_key({
    "implement",
    "duplicate-fork",
    tostring(origin.repo),
    tostring(origin.issue_number),
    tostring(issue_number),
    tostring(canonical_number),
  }), ready.source_ref)
end

local function duplicate_label(repo, issue_number, ready, origin, canonical_number)
  return requests_labels.build_label_request(core,
    repo,
    issue_number,
    { duplicate_label_name },
    {},
    base_ids.dedup_key({
      "implement",
      "duplicate-fork",
      "label",
      tostring(origin.repo),
      tostring(origin.issue_number),
      tostring(issue_number),
      tostring(canonical_number),
    }),
    ready.source_ref
  )
end

function M.check(repo, issue_number, ready, origin, original, managed)
  if type(origin) ~= "table" or type(original) ~= "table" then
    return false
  end
  local canonical = forks.trusted_issue_created_number(
    core,
    original.comments,
    forks.fork_issue_dedup_key(origin.repo, origin.issue_number),
    m_claims.claim_owner(),
    managed
  )
  if canonical == nil or tonumber(canonical) == tonumber(issue_number) then
    return false
  end

  core.log_cas_decision("implement", ready.proposal_id, {
    state = "ready",
    version = ready.dedup_key,
  }, "ready", "duplicate-fork", "skip-stale(noncanonical-fork)", "canonical fork is #" .. tostring(canonical))
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request",
    duplicate_comment(repo, issue_number, ready, origin, canonical))
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_label_request",
    duplicate_label(repo, issue_number, ready, origin, canonical))
  if devloop_base.read_env("FKST_GITHUB_WRITE") == "1" then
    local closed = core.gh_issue_close(repo, issue_number, 30)
    if type(closed) ~= "table" or closed.exit_code ~= 0 then
      error("github-devloop: duplicate-fork-close-failed: duplicate fork close failed: " .. tostring(closed and closed.stderr or "missing result"))
    end
  end
  return true
end

return M
