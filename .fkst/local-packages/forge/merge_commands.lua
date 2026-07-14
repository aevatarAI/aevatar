local M = {}

local merge_issue_fields = "title,labels,comments,state,assignees"
local merge_pr_fields = "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup"

function M.install(core)
  local github = require("forge.github").new(function() end)
  local git = require("forge.git").new(function() end)

  function core.gh_pr_list_merge_queue_cmd(repo, base)
    return github.pr_list_merge_queue_cmd(repo, base)
  end

  function core.gh_issue_view_merge_cmd(repo, issue_number)
    return github.issue_view_cmd(repo, issue_number, merge_issue_fields)
  end

  function core.gh_pr_view_merge_cmd(repo, pr_number)
    return github.pr_cli_view_cmd(repo, pr_number, merge_pr_fields)
  end

  function core.gh_pr_merge_cmd(repo, pr_number, head_sha)
    return github.pr_merge_cmd(repo, pr_number, head_sha)
  end

  function core.git_fetch_pr_merge_ref_cmd(remote, pr_number)
    return git.fetch_pr_merge_ref_cmd(remote, pr_number)
  end

  function core.git_worktree_merge_no_edit_cmd(worktree, sha)
    return git.merge_no_edit_cmd(worktree, sha)
  end
end

return M
