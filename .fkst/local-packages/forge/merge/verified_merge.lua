local S = {}
local github_adapter = require("forge.github")
local forge_validators = require("forge.gitref")

function S.install(M, shared, ci_gate)
local github = github_adapter.production_handle
local merge_attempt_limit = shared.merge_attempt_limit
local expected_pr_identity = shared.expected_pr_identity

local function is_merged_pr(pr)
  return tostring(pr and pr.state or ""):upper() == "MERGED" and tostring(pr and pr.merged_at or "") ~= ""
end

local function is_match_head_modified_error(stderr)
  return tostring(stderr or ""):find("Head branch was modified", 1, true) ~= nil
end

local function run_verified_pr_merge(request)
  local repo = tostring(request and request.repo or "")
  local pr_number = request and request.pr_number
  local max_attempts = merge_attempt_limit(request)
  for attempt = 1, max_attempts do
    local pr_recheck = github("forge.merge").gh_pr_view_merge(repo, pr_number, 30)
    if pr_recheck.exit_code ~= 0 then
      error("forge.merge: gh pr merge recheck failed: " .. tostring(pr_recheck.stderr))
    end
    local rechecked_pr = M.parse_pr_view_merge(pr_recheck.stdout)
    rechecked_pr.number = pr_number
    local merge_head_sha = request and request.head_sha
    if request and request.accept_current_head == true then
      merge_head_sha = rechecked_pr.head_sha
      if not forge_validators.is_git_sha(merge_head_sha) then
        return false, "invalid-current-head-sha", rechecked_pr
      end
    end
    local expected = expected_pr_identity(request, repo, merge_head_sha)
    local identity_ok, identity_reason = ci_gate.pr_identity_matches(rechecked_pr, expected)
    if not identity_ok then
      return false, identity_reason, rechecked_pr
    end
    if type(request.validate_rechecked_pr) == "function" then
      local validate_ok, validate_reason = request.validate_rechecked_pr(rechecked_pr)
      if not validate_ok then
        return false, validate_reason or "pr-validation-failed", rechecked_pr
      end
    end
    local gate_ok, gate_reason = ci_gate.evaluate_ci_merge_gate(rechecked_pr, {
      repo = repo,
      dept = request.dept or "merge",
      proposal_id = request.proposal_id,
    })
    if not gate_ok then
      return false, gate_reason, rechecked_pr
    end
    if type(request.before_merge) == "function" then
      request.before_merge(rechecked_pr)
    end

    local merge_result = github("forge.merge").gh_pr_merge(repo, pr_number, merge_head_sha, 120)
    if merge_result.exit_code ~= 0 then
      if attempt < max_attempts and is_match_head_modified_error(merge_result.stderr) then
        M.log_line("info", tostring(request.dept or "merge"), tostring(request.proposal_id or "merge"), "MATCH_HEAD_RETRY", {
          "repo=" .. tostring(repo),
          "pr=" .. tostring(pr_number),
          "head_sha=" .. tostring(merge_head_sha),
          "attempt=" .. tostring(attempt),
          "max_attempts=" .. tostring(max_attempts),
          "reason=head-branch-modified",
        })
      else
        error("forge.merge: gh pr merge failed: " .. tostring(merge_result.stderr))
      end
    else
      M.invalidate_entity_after_write(repo, "pr", pr_number)

      local merged_view = github("forge.merge").gh_pr_view_merge(repo, pr_number, 30)
      if merged_view.exit_code ~= 0 then
        error("forge.merge: gh pr post-merge view failed: " .. tostring(merged_view.stderr))
      end
      local merged_pr = M.parse_pr_view_merge(merged_view.stdout)
      merged_pr.number = pr_number
      if not is_merged_pr(merged_pr) then
        return false, "merge-confirmation-pending", merged_pr
      end
      if tostring(merged_pr.head_ref_name or "") ~= tostring(expected.head_branch or "")
        or tostring(merged_pr.head_sha or "") ~= tostring(expected.head_sha or "")
        or tostring(merged_pr.base_ref_name or "") ~= tostring(expected.base_branch or "")
        or not require("forge.merge.shared").is_same_repo_pr_head(merged_pr, repo) then
        return false, "merge-confirmation-mismatch", merged_pr
      end
      return true, "merged", merged_pr
    end
  end
  error("forge.merge: gh pr merge failed: Head branch was modified after bounded retry")
end
rawset(M, "is_merged_pr", is_merged_pr)
rawset(M, "is_match_head_modified_error", is_match_head_modified_error)
rawset(M, "run_verified_pr_merge", run_verified_pr_merge)
return {
  is_merged_pr = is_merged_pr,
  is_match_head_modified_error = is_match_head_modified_error,
  run_verified_pr_merge = run_verified_pr_merge,
}
end

return S
