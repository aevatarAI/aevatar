local S = {}
local support = require("devloop.commands.support")
local validators = require("devloop.commands.validators")

function S.install(M)
  function M.gh_pr_list_board_digest(repo, timeout)
    return support.gh_result(function()
      return support.github().pr_list_board_digest(repo, timeout)
    end)
  end

  function M.gh_pr_list_freshness(repo, timeout)
    return support.gh_result(function()
      return support.github().pr_list(repo, timeout)
    end)
  end

  function M.gh_pr_list_merge_queue(repo, base, timeout)
    return support.gh_result(function()
      return support.github().pr_list_merge_queue(repo, validators.require_safe_branch(M, "merge queue base branch", base), timeout)
    end)
  end

  function M.gh_pr_list_recent_merged(repo, limit, timeout)
    return support.gh_result(function()
      return support.github().pr_list_recent_merged(
        repo,
        validators.bounded_limit(limit, 30, 1, 100, "github-devloop: invalid recent merged PR list limit"),
        timeout
      )
    end)
  end

  function M.gh_pr_view_origin(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_cli_view(
        repo,
        pr_number,
        "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,mergedAt,comments,labels,mergeable,mergeStateStatus",
        timeout
      )
    end)
  end

  function M.gh_pr_view_observe(repo, pr_number, timeout)
    return M.gh_pr_view_origin(repo, pr_number, timeout)
  end

  function M.gh_pr_view_fix(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_cli_view(repo, pr_number, "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository", timeout)
    end)
  end

  function M.gh_pr_view_fix_precheck(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_cli_view(repo, pr_number, "headRefName,headRefOid,baseRefName,state,updatedAt,comments,headRepository,headRepositoryOwner,isCrossRepository", timeout)
    end)
  end

  function M.gh_pr_view_freshness(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_cli_view(repo, pr_number, "headRefName,headRefOid,baseRefName,state,updatedAt,isDraft,comments,labels,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup", timeout)
    end)
  end

  function M.gh_pr_list_head_base(repo, head, base, timeout)
    return support.gh_result(function()
      return support.github().pr_list_head(
        repo,
        validators.require_safe_branch(M, "PR head branch", head),
        validators.require_safe_branch(M, "PR base branch", base),
        timeout
      )
    end)
  end

  function M.gh_pr_list_head(repo, head, timeout)
    return support.gh_result(function()
      return support.github().pr_list_head(repo, validators.require_safe_branch(M, "PR head branch", head), nil, timeout)
    end)
  end

  function M.gh_pr_create(repo, head, base, title, body_file, timeout)
    return support.gh_result(function()
      return support.github().pr_create(
        repo,
        validators.require_safe_branch(M, "PR head branch", head),
        validators.require_safe_branch(M, "PR base branch", base),
        title,
        body_file,
        timeout
      )
    end)
  end

  function M.gh_pr_create_body(repo, head, base, title, body, timeout)
    return support.gh_result(function()
      return support.github().pr_create_body(
        repo,
        validators.require_safe_branch(M, "PR head branch", head),
        validators.require_safe_branch(M, "PR base branch", base),
        title,
        body,
        timeout
      )
    end)
  end

  function M.gh_pr_ready(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_ready(repo, pr_number, timeout)
    end)
  end

  function M.gh_issue_comment(repo, issue_number, body_file, timeout)
    return support.gh_result(function()
      return support.github().issue_comment(repo, issue_number, body_file, timeout)
    end)
  end

  function M.gh_pr_comment(repo, pr_number, body_file, timeout)
    return support.gh_result(function()
      return support.github().pr_comment(repo, pr_number, body_file, timeout)
    end)
  end

  function M.gh_pr_close(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_close(repo, pr_number, timeout)
    end)
  end

  function M.gh_pr_diff(repo, pr_number, timeout, run)
    return support.gh_result(function()
      return support.github(run).pr_diff(repo, pr_number, timeout)
    end)
  end

  function M.gh_pr_diff_name_only(repo, pr_number, timeout, run)
    return support.gh_result(function()
      return support.github(run).pr_diff_name_only(repo, pr_number, timeout)
    end)
  end

  function M.gh_pr_view_head(repo, pr_number, timeout)
    return support.gh_result(function()
      return support.github().pr_cli_view(repo, pr_number, "headRefName,baseRefName,state", timeout)
    end)
  end

  function M.gh_pr_view_context(repo, pr_number, timeout, run)
    return support.gh_result(function()
      return support.github(run).pr_cli_view(repo, pr_number, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels", timeout)
    end)
  end
end

return S
