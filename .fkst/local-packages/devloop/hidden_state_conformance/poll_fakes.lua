local payloads_board = require("devloop.payloads.board")
local S = {}
local context_bundle = require("devloop.context_bundle")
local config = require("devloop.config")
local devloop_base = require("devloop.base")
local git_mechanics = require("devloop.git_mechanics")
local git_commands = require("devloop.commands.git_ops")
local pr_commands = require("devloop.commands.prs")

function S.with(core, opts, fn)
  local head_sha = opts.head_sha
  local base_branch = opts.base_branch
  local previous_children = core.gh_issue_list_decompose_children
  local previous_branch_config = config.branch_config
  local previous_bot_login = devloop_base.configured_trusted_bot_login()
  local previous_repo_ref_store_lock = git_mechanics.with_repo_ref_store_lock
  local previous_git_is_ancestor = core.git.is_ancestor
  local previous_git_fetch_branch = core.git.fetch_branch
  local previous_git_fetch_head_commit = core.git.fetch_head_commit
  local previous_git_remote_branch_head = core.git.remote_branch_head
  local previous_git_fetch_pr_head_ref = git_commands.git_fetch_pr_head_ref
  local previous_git_fetch_head_commit_command = git_commands.git_fetch_head_commit
  local previous_pr_list_promotions = pr_commands.gh_pr_list_promotions
  local previous_context_fetch_ref_from_bundle = context_bundle.context_fetch_ref_from_bundle
  local previous_context_fetch_from_bundle = context_bundle.context_fetch_from_bundle
  local previous_board_digest_block = payloads_board.board_digest_block

  if type(previous_children) == "function" then
    core.gh_issue_list_decompose_children = function()
      return { exit_code = 0, stdout = "[]", stderr = "" }
    end
  end
  devloop_base.configure_trusted_bot_login(core._test_bot_login or "fkst-test-bot")
  git_mechanics.with_repo_ref_store_lock = function(_, locked_fn)
    return locked_fn()
  end
  config.branch_config = function(_core)
    return { integration = base_branch, upstream = "dev" }
  end
  core.git.remote_branch_head = function()
    return { exit_code = 0, stdout = head_sha, stderr = "" }
  end
  core.git.is_ancestor = function(ancestor_sha, descendant_sha)
    local matched = tostring(ancestor_sha or "") == tostring(head_sha)
      and tostring(descendant_sha or "") == tostring(head_sha)
    return { exit_code = matched and 0 or 1, stdout = "", stderr = "" }
  end
  core.git.fetch_branch = function()
    return { exit_code = 0, stdout = "", stderr = "" }
  end
  core.git.fetch_head_commit = function()
    return { exit_code = 0, stdout = head_sha, stderr = "" }
  end
  git_commands.git_fetch_pr_head_ref = function()
    return { exit_code = 0, stdout = "", stderr = "" }
  end
  git_commands.git_fetch_head_commit = function()
    return { exit_code = 0, stdout = head_sha, stderr = "" }
  end
  pr_commands.gh_pr_list_promotions = function(repo, integration, upstream)
    return {
      exit_code = 0,
      stderr = "",
      stdout = '[[{"number":99,"state":"closed","merged_at":"2026-06-03T02:03:04Z"'
        .. ',"head":{"ref":"' .. tostring(integration) .. '","sha":"' .. tostring(head_sha)
        .. '","repo":{"full_name":"' .. tostring(repo) .. '"}},"base":{"ref":"'
        .. tostring(upstream) .. '"}}]]',
    }
  end
  context_bundle.context_fetch_ref_from_bundle = function(_core, args)
    return "runtime-cache:hidden-state-conformance/" .. tostring(args and args.version or "fixture")
  end
  context_bundle.context_fetch_from_bundle = function(_core, args)
    return "Hidden-state conformance fixture context for " .. tostring(args and args.version or "fixture")
  end
  payloads_board.board_digest_block = function()
    return "Hidden-state conformance board fixture."
  end

  local ok, first, second = pcall(fn)
  if type(previous_children) == "function" then
    core.gh_issue_list_decompose_children = previous_children
  end
  devloop_base.configure_trusted_bot_login(previous_bot_login)
  git_mechanics.with_repo_ref_store_lock = previous_repo_ref_store_lock
  config.branch_config = previous_branch_config
  core.git.is_ancestor = previous_git_is_ancestor
  core.git.fetch_branch = previous_git_fetch_branch
  core.git.fetch_head_commit = previous_git_fetch_head_commit
  core.git.remote_branch_head = previous_git_remote_branch_head
  git_commands.git_fetch_pr_head_ref = previous_git_fetch_pr_head_ref
  git_commands.git_fetch_head_commit = previous_git_fetch_head_commit_command
  pr_commands.gh_pr_list_promotions = previous_pr_list_promotions
  context_bundle.context_fetch_ref_from_bundle = previous_context_fetch_ref_from_bundle
  context_bundle.context_fetch_from_bundle = previous_context_fetch_from_bundle
  payloads_board.board_digest_block = previous_board_digest_block
  if not ok then
    error(first)
  end
  return first, second
end

return S
