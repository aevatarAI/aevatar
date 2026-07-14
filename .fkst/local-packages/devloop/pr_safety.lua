local C = {}
local forge_validators = require("devloop.forge_validators")

function C.is_safe_branch(branch)
  return forge_validators.is_git_ref_safe(branch)
end

function C.is_devloop_issue_branch(branch)
  return type(branch) == "string"
    and forge_validators.is_git_ref_safe(branch)
    and branch:find("^devloop/issue/[^/]+/.+/.+") ~= nil
end

function C.is_safe_head_sha(head_sha)
  return forge_validators.is_git_sha(head_sha)
end

function C.is_safe_pr_number(pr_number)
  return forge_validators.is_positive_pr_number(pr_number)
end


return C
