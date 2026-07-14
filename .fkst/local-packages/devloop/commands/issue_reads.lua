local payloads_predicates = require("devloop.payloads.predicates")
local S = {}
local support = require("devloop.commands.support")
local validators = require("devloop.commands.validators")

local issue_view_fields = {
  intake_judge = "title,body,createdAt,updatedAt,labels,comments,state,assignees,author",
  view_state = "title,createdAt,updatedAt,labels,state,comments,assignees,author",
  claim = "assignees,author",
  result = "labels,comments",
  loop = "title,updatedAt,labels,comments,state",
  meta = "title,labels,comments",
  implement = "title,body,labels,comments,state,author",
  open_pr = "title,labels,comments,assignees,author",
  reviewing = "labels,comments",
  review = "title,labels,comments,assignees,author",
  decompose = "title,body,labels,comments",
  fix = "title,labels,comments",
  commit_subject = "number,title",
  review_loop = "title,labels,comments,assignees,author",
  merge = "title,labels,comments,state,assignees",
  observe = "title,comments,state,stateReason,assignees,author",
}

local function issue_fields(fields_key_or_fields)
  return issue_view_fields[tostring(fields_key_or_fields or "")] or validators.validate_fields(fields_key_or_fields, "github-devloop: invalid issue view fields")
end

function S.install(M)
  function M.gh_issue_list_intake(repo, limit, timeout)
    return support.gh_result(function()
      return support.github().issue_list_intake(
        repo,
        validators.bounded_limit(limit, 100, 1, 100, "github-devloop: invalid intake issue list limit"),
        timeout
      )
    end)
  end

  function M.gh_issue_list_decompose_children(repo, proposal_id, timeout)
    return support.gh_result(function()
      return support.github().issue_search(
        repo,
        "fkst:github-devloop:decompose-child:v1 " .. tostring(proposal_id),
        "number,title,state,author,body,url",
        timeout
      )
    end)
  end

  function M.gh_issue_list_recent_closed(repo, limit, timeout)
    return support.gh_result(function()
      return support.github().issue_list_recent_closed(
        repo,
        validators.bounded_limit(limit, 30, 1, 100, "github-devloop: invalid closed issue list limit"),
        timeout
      )
    end)
  end

  function M.gh_issue_list_board_digest(repo, timeout)
    return support.gh_result(function()
      return support.github().issue_list_board_digest(repo, timeout)
    end)
  end

  function M.gh_issue_list_wip(repo, timeout)
    return support.gh_result(function()
      return support.github().issue_list_cli(repo, "open", 100, "number", timeout)
    end)
  end

  function M.gh_issue_view(repo, issue_number, fields_key_or_fields, timeout, run)
    return support.gh_result(function()
      return support.github(run).issue_view(repo, issue_number, issue_fields(fields_key_or_fields), timeout)
    end)
  end

  function M.gh_issue_view_intake_judge(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "intake_judge", timeout)
  end

  function M.gh_issue_view_state(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "view_state", timeout)
  end

  function M.gh_issue_view_claim(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "claim", timeout)
  end

  function M.gh_issue_view_result(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "result", timeout)
  end

  function M.gh_issue_view_loop(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "loop", timeout)
  end

  function M.gh_issue_view_meta(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "meta", timeout)
  end

  function M.gh_issue_view_implement(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "implement", timeout)
  end

  function M.gh_issue_view_open_pr(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "open_pr", timeout)
  end

  function M.gh_issue_view_reviewing(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "reviewing", timeout)
  end

  function M.gh_issue_view_review(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "review", timeout)
  end

  function M.gh_issue_view_decompose(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "decompose", timeout)
  end

  function M.gh_issue_view_fix(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "fix", timeout)
  end

  function M.gh_issue_view_commit_subject(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "commit_subject", timeout)
  end

  function M.gh_issue_view_review_loop(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "review_loop", timeout)
  end

  function M.gh_issue_view_merge(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "merge", timeout)
  end

  function M.gh_issue_view_observe(repo, issue_number, timeout)
    return M.gh_issue_view(repo, issue_number, "observe", timeout)
  end

  function M.gh_issue_comment_get(repo, comment_id, timeout)
    if not payloads_predicates.is_safe_comment_id(M, comment_id) then
      error("github-devloop: invalid comment id")
    end
    return support.gh_result(function()
      return support.github().comment_get(repo, comment_id, timeout)
    end)
  end

  function M.gh_issue_close(repo, issue_number, timeout)
    return support.gh_result(function()
      return support.github().issue_close(repo, issue_number, timeout)
    end)
  end
end

return S
