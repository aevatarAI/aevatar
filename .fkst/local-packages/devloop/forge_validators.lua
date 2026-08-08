local gitref = require("forge.gitref")

local S = {}

S.is_git_sha = gitref.is_git_sha
S.is_git_ref_safe = gitref.is_git_ref_safe
S.is_positive_pr_number = gitref.is_positive_pr_number
S.require_safe_branch = gitref.require_safe_branch
S.require_safe_remote = gitref.require_safe_remote
S.require_safe_sha = gitref.require_safe_sha
S.require_positive_pr_number = gitref.require_positive_pr_number

return S
