local S = {}

function S.install(M, deps)
local shared = deps or M
local strings = require("contract.strings")
local forge_strings = require("forge.strings")
local max_runtime_id_len = 180
local stale_comment_target_error_class = "stale-comment-target"

local function safe_runtime_segment(value)
  local safe = tostring(value or ""):gsub("[^%w._-]", "_")
  safe = safe:gsub("_+", "_"):gsub("^_+", ""):gsub("_+$", "")
  return safe == "" and "empty" or safe
end

local function comment_runtime_identity(repo, kind, number)
  local id = "comment-" .. safe_runtime_segment(repo)
    .. "-" .. safe_runtime_segment(kind)
    .. "-" .. safe_runtime_segment(number)
  if #id > max_runtime_id_len then
    return id:sub(1, max_runtime_id_len)
  end
  return id
end

local function comment_author_login(comment)
  local raw = nil
  if type(comment) == "table" then
    if comment.author_login ~= nil then
      raw = comment.author_login
    elseif type(comment.author) == "table" and comment.author.login ~= nil then
      raw = comment.author.login
    elseif type(comment.user) == "table" and comment.user.login ~= nil then
      raw = comment.user.login
    end
  end
  return shared.strip_bot_login_suffix(raw)
end

function M._comment_body(comment)
  return forge_strings.comment_body(comment)
end

function M._comment_author_login(comment)
  return comment_author_login(comment)
end

function M.stale_comment_target_error_class()
  return stale_comment_target_error_class
end

local function comment_id(comment)
  if type(comment) ~= "table" then
    return nil
  end
  local id = comment.databaseId or comment.database_id or comment.id
  if id == nil or tostring(id) == "" then
    return nil
  end
  return tostring(id)
end

local function rest_comment_id(comment)
  if type(comment) ~= "table" or comment.id == nil then
    return nil
  end
  local id = tostring(comment.id)
  if id == "" or id:find("^%d+$") == nil then
    return nil
  end
  return id
end

local function append_rest_comments(comments, value)
  if type(value) ~= "table" then
    return
  end
  if value.id ~= nil or value.body ~= nil or value.user ~= nil or value.author ~= nil then
    local id = comment_id(value) or rest_comment_id(value)
    if id ~= nil then
      table.insert(comments, {
        id = id,
        body = forge_strings.comment_body(value),
        author_login = comment_author_login(value),
      })
    end
    return
  end
  for _, item in ipairs(value) do
    append_rest_comments(comments, item)
  end
end

function M.comment_marker(dedup_key)
  return "<!-- fkst:github-proxy:comment:" .. tostring(dedup_key) .. " -->"
end

function M.has_marker(comments_text, dedup_key)
  if comments_text == nil or comments_text == "" then
    return false
  end
  return tostring(comments_text):find(M.comment_marker(dedup_key), 1, true) ~= nil
end

function M.parse_issue_comments(gh_json_stdout)
  local decoded = json.decode(gh_json_stdout or "{}")
  local comments = {}
  if decoded.comments == nil then
    append_rest_comments(comments, decoded)
    return comments
  end
  for _, comment in ipairs(decoded.comments or {}) do
    table.insert(comments, {
      id = comment_id(comment),
      body = forge_strings.comment_body(comment),
      author_login = comment_author_login(comment),
    })
  end
  return comments
end

function M.has_trusted_marker(comments, dedup_key, bot_login)
  if type(comments) ~= "table" then
    return false
  end
  local marker = M.comment_marker(dedup_key)
  for _, comment in ipairs(comments) do
    if comment_author_login(comment) == bot_login and forge_strings.comment_body(comment):find(marker, 1, true) ~= nil then
      return true
    end
  end
  return false
end

function M.has_trusted_comment_fragment(comments, fragment, bot_login)
  if type(comments) ~= "table" or type(fragment) ~= "string" or fragment == "" then
    return false
  end
  for _, comment in ipairs(comments) do
    if comment_author_login(comment) == bot_login and forge_strings.comment_body(comment):find(fragment, 1, true) ~= nil then
      return true
    end
  end
  return false
end

function M.trusted_comment_with_fragment(comments, fragment, bot_login)
  if type(comments) ~= "table" or type(fragment) ~= "string" or fragment == "" then
    return nil
  end
  for _, comment in ipairs(comments) do
    if comment_author_login(comment) == bot_login and forge_strings.comment_body(comment):find(fragment, 1, true) ~= nil then
      return comment
    end
  end
  return nil
end

local function gh_result_stderr(result)
  if type(result) ~= "table" then
    return ""
  end
  return tostring(result.stderr or "")
end

local function is_gh_not_found(result)
  local lower = gh_result_stderr(result):lower()
  if lower:find("404", 1, true) ~= nil and lower:find("not found", 1, true) ~= nil then
    return true
  end
  return lower:find("gh: not found", 1, true) ~= nil
end

local function load_comments(M, target, repo)
  local view = M.gh_exec(function(timeout)
    return target.view_comments(M.github(), repo, target.number, timeout)
  end, 30, target.view_label)
  return M.parse_issue_comments(view.stdout)
end

local function parse_rest_comments(stdout)
  local ok, decoded = pcall(json.decode, stdout or "[]")
  if not ok then
    return {}
  end
  local comments = {}
  append_rest_comments(comments, decoded)
  return comments
end

local function load_rest_comments(M, target, repo)
  local view = M.gh_exec(function(timeout)
    return target.view_comments(M.github(), repo, target.number, timeout)
  end, 30, "GitHub issue comments")
  return parse_rest_comments(view.stdout)
end

local function trusted_rest_comment_with_fragment(M, repo, target, fragment, bot_login)
  local comments = load_rest_comments(M, target, repo)
  return M.trusted_comment_with_fragment(comments, fragment, bot_login)
end

local function confirmed_existing_handoff_comment(M, repo, target, dedup_key, bot_login, handoff)
  if handoff == nil then
    return nil
  end
  return trusted_rest_comment_with_fragment(M, repo, target, M.comment_marker(dedup_key), bot_login)
end

local function marker_attr(marker, name)
  return tostring(marker or ""):match(name .. '="([^"]*)"')
end

local function marker_name(marker)
  return tostring(marker or ""):match("^%s*<!%-%-%s*([^%s>]+)")
end

local function round_marker_family(replace_marker)
  local name = marker_name(replace_marker)
  if name == nil then
    return nil
  end
  local family = name:gsub(":latest:v%d+$", ":")
  if family == name then
    return nil
  end
  return family
end

local function marker_round(body, family)
  if family == nil then
    return nil
  end
  local max_seen = nil
  for marker in tostring(body or ""):gmatch("<!%-%-%s*.-%-%->") do
    local name = marker_name(marker)
    if name ~= nil and name:sub(1, #family) == family then
      local round = tonumber(marker_attr(marker, "round"))
      if round ~= nil and round >= 1 and (max_seen == nil or round > max_seen) then
        max_seen = round
      end
    end
  end
  return max_seen
end

local function stale_round_marker_replace(existing, next_body, replace_marker)
  if existing == nil then
    return false
  end
  local family = round_marker_family(replace_marker)
  local existing_round = marker_round(M._comment_body(existing), family)
  local next_round = marker_round(next_body, family)
  return existing_round ~= nil and next_round ~= nil and existing_round >= next_round
end

function M.gh_pr_comment(repo, pr_number, body_file, timeout)
  return M.github().pr_comment(repo, pr_number, body_file, timeout or 30)
end

function M.gh_pr_comment_cmd(repo, pr_number, body_file)
  return function(timeout)
    return M.gh_pr_comment(repo, pr_number, body_file, timeout)
  end
end

function M.gh_pr_view_comments(repo, pr_number, timeout)
  return M.github().pr_comments(repo, pr_number, timeout or 30)
end

function M.gh_pr_view_comments_cmd(repo, pr_number)
  return function(timeout)
    return M.gh_pr_view_comments(repo, pr_number, timeout)
  end
end

function M.gh_issue_view_comments(repo, issue_number, timeout)
  return M.github().issue_comments(repo, issue_number, timeout or 30)
end

function M.gh_issue_view_comments_cmd(repo, issue_number)
  return function(timeout)
    return M.gh_issue_view_comments(repo, issue_number, timeout)
  end
end

function M.gh_issue_comment(repo, issue_number, body_file, timeout)
  return M.github().issue_comment(repo, issue_number, body_file, timeout or 30)
end

function M.gh_issue_comment_cmd(repo, issue_number, body_file)
  return function(timeout)
    return M.gh_issue_comment(repo, issue_number, body_file, timeout)
  end
end

function M.gh_issue_comment_create(repo, issue_number, body_file, timeout)
  return M.github().issue_comment_create(repo, issue_number, body_file, timeout or 30)
end

function M.gh_issue_comment_create_cmd(repo, issue_number, body_file)
  return function(timeout)
    return M.gh_issue_comment_create(repo, issue_number, body_file, timeout)
  end
end

function M.gh_pr_comment_create(repo, pr_number, body_file, timeout)
  return M.github().pr_comment_create(repo, pr_number, body_file, timeout or 30)
end

function M.gh_comment_edit(repo, comment_id_value, body_file, timeout)
  return M.github().comment_update(repo, comment_id_value, body_file, timeout or 30)
end

function M.gh_comment_edit_cmd(repo, comment_id_value, body_file)
  return function(timeout)
    return M.gh_comment_edit(repo, comment_id_value, body_file, timeout)
  end
end

local function edit_existing_comment(M, repo, target, path, existing, replace_marker, bot_login)
  if existing == nil or existing.id == nil then
    return false, "missing-id"
  end

  local ok, err = M.gh_exec_result(function(timeout)
    return M.gh_comment_edit(repo, existing.id, path, timeout)
  end, 30, "GitHub comment edit")
  if ok then
    return true, nil, existing
  end

  if not is_gh_not_found(err.result) then
    error(err.message)
  end

  log.warn("github-proxy: GitHub comment edit returned 404; re-reading comments before classification")
  local comments = load_comments(M, target, repo)
  local refreshed = M.trusted_comment_with_fragment(comments, replace_marker, bot_login)
  if refreshed == nil or refreshed.id == nil then
    log.warn("github-proxy: GitHub comment edit target is stale: error_class=" .. stale_comment_target_error_class)
    return false, stale_comment_target_error_class
  end

  local refreshed_ok, refreshed_err = M.gh_exec_result(function(timeout)
    return M.gh_comment_edit(repo, refreshed.id, path, timeout)
  end, 30, "GitHub comment edit")
  if refreshed_ok then
    return true, nil, refreshed
  end
  if is_gh_not_found(refreshed_err.result) then
    log.warn("github-proxy: refreshed GitHub comment edit target is stale: error_class=" .. stale_comment_target_error_class)
    return false, stale_comment_target_error_class
  end
  error(refreshed_err.message)
end

local function parse_written_comment(stdout)
  local ok, decoded = pcall(json.decode, stdout or "{}")
  if not ok or type(decoded) ~= "table" then
    return nil
  end
  local id = comment_id(decoded)
  if id == nil then
    return nil
  end
  return {
    id = id,
    body = forge_strings.comment_body(decoded),
    author_login = comment_author_login(decoded),
  }
end

function M.write_comment_request(payload, target)
  local repo = payload.repo
  if repo == nil or repo == "" then
    repo = M.read_env("FKST_GITHUB_REPO")
  end
  if repo == nil or repo == "" then
    log.warn("github-proxy: comment request missing repo")
    return
  end
  if target.number == nil or payload.body == nil or payload.dedup_key == nil then
    log.warn("github-proxy: comment request missing " .. tostring(target.number_field) .. ", body, or dedup_key")
    return
  end

  if M.read_env("FKST_GITHUB_WRITE") ~= "1" then
    log.info("github-proxy dry-run: would comment on " .. repo .. "#" .. tostring(target.number))
    return
  end
  local bot_login = M.assert_trusted_bot_configured()

  local runtime_id = comment_runtime_identity(repo, target.kind, target.number)
  local written_comment = nil
  with_lock("github-proxy/" .. runtime_id, function()
    local comments = load_comments(M, target, repo)
    local replace_marker = payload.replace_marker
    local existing = nil
    if replace_marker ~= nil and tostring(replace_marker) ~= "" then
      existing = M.trusted_comment_with_fragment(comments, tostring(replace_marker), bot_login)
    elseif M.has_trusted_marker(comments, payload.dedup_key, bot_login) then
      log.info("github-proxy: comment marker already present")
      written_comment = confirmed_existing_handoff_comment(M, repo, target, payload.dedup_key, bot_login, payload.handoff)
      return
    end
    local claim_issue_number = target.kind == "issue" and target.number or payload.issue_number
    if claim_issue_number ~= nil
      and not M.verify_issue_claim_before_write(payload, repo, claim_issue_number, target.kind == "pr" and "github_pr_comment" or "github_comment") then
      return
    end

    local body = tostring(payload.body) .. "\n\n" .. M.comment_marker(payload.dedup_key) .. "\n"
    if stale_round_marker_replace(existing, body, replace_marker) then
      log.info("github-proxy: round-marker replacement is stale; keeping newer visible marker")
      return
    end
    body = M.with_github_debug_stamp(body, {
      emitter = "github-proxy.comment",
      target = tostring(target.kind) .. ":" .. tostring(repo) .. "#" .. tostring(target.number),
      dedup_key = payload.dedup_key,
    })
    local path = "/tmp/fkst-github-proxy-" .. runtime_id .. ".md"
    file.write(path, body)
    local edited, edit_status, edited_comment = edit_existing_comment(M, repo, target, path, existing, tostring(replace_marker or ""), bot_login)
    if edited then
      written_comment = edited_comment
      M.invalidate_entity_after_write(repo, target.kind, target.number)
      return
    end
    if edit_status == stale_comment_target_error_class then
      log.warn("github-proxy: creating a fresh comment after stale comment edit target")
    elseif existing ~= nil then
      log.warn("github-proxy: replace marker comment missing id; creating a fresh comment")
    end
    local created = M.gh_exec(function(timeout)
      return target.comment_create(M.github(), repo, target.number, path, timeout)
    end, 30, target.comment_label)
    local written = parse_written_comment(created.stdout)
    if written == nil then
      error("github-proxy: comment create did not return a valid comment id")
    end
    written_comment = written
    M.invalidate_entity_after_write(repo, target.kind, target.number)
  end)
  return written_comment
end

end

return S
