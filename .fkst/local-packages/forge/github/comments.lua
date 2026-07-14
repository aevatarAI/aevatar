local M = {}

local function issue_comments_argv(repo, issue_number)
  return {
    "gh",
    "api",
    "--paginate",
    "--slurp",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) .. "/comments?per_page=100",
  }
end

local function issue_comment_create_argv(repo, issue_number, body_file)
  return {
    "gh",
    "api",
    "--method",
    "POST",
    "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) .. "/comments",
    "--field",
    "body=@" .. tostring(body_file),
  }
end

local function comment_update_argv(repo, comment_id, body_file)
  if comment_id == nil or tostring(comment_id) == "" then
    error("forge.github.comments: invalid comment id")
  end
  return {
    "gh",
    "api",
    "--method",
    "PATCH",
    "repos/" .. tostring(repo) .. "/issues/comments/" .. tostring(comment_id),
    "--field",
    "body=@" .. tostring(body_file),
  }
end

local function comment_get_argv(repo, comment_id)
  if comment_id == nil or tostring(comment_id) == "" then
    error("forge.github.comments: invalid comment id")
  end
  return {
    "gh",
    "api",
    "--method",
    "GET",
    "repos/" .. tostring(repo) .. "/issues/comments/" .. tostring(comment_id),
  }
end

local function cli_comment_argv(kind, repo, number, body_file)
  return {
    "gh",
    kind,
    "comment",
    tostring(number),
    "--repo",
    tostring(repo),
    "--body-file",
    tostring(body_file),
  }
end

function M.install(handle)
  function handle.issue_comments(repo, issue_number, timeout)
    return handle._exec(issue_comments_argv(repo, issue_number), timeout, "gh issue comments")
  end

  function handle.pr_comments(repo, pr_number, timeout)
    return handle._exec(issue_comments_argv(repo, pr_number), timeout, "gh PR comments")
  end

  function handle.issue_comment_create(repo, issue_number, body_file, timeout)
    return handle._exec(issue_comment_create_argv(repo, issue_number, body_file), timeout, "gh issue comment")
  end

  function handle.pr_comment_create(repo, pr_number, body_file, timeout)
    return handle._exec(issue_comment_create_argv(repo, pr_number, body_file), timeout, "gh pr comment")
  end

  function handle.comment_update(repo, comment_id, body_file, timeout)
    return handle._exec(comment_update_argv(repo, comment_id, body_file), timeout, "gh comment edit")
  end

  function handle.comment_get(repo, comment_id, timeout)
    return handle._exec(comment_get_argv(repo, comment_id), timeout, "gh comment get")
  end

  function handle.issue_comment(repo, issue_number, body_file, timeout)
    return handle._exec(cli_comment_argv("issue", repo, issue_number, body_file), timeout, "gh issue comment")
  end

  function handle.pr_comment(repo, pr_number, body_file, timeout)
    return handle._exec(cli_comment_argv("pr", repo, pr_number, body_file), timeout, "gh pr comment")
  end
end

return M
