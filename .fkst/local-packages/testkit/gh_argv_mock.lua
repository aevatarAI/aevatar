local M = {}

local function url_encode(value)
  return (tostring(value or ""):gsub("([^%w%-%._~])", function(char)
    return string.format("%%%02X", string.byte(char))
  end))
end

local function repo_owner(repo)
  return tostring(repo or ""):match("^([^/]+)/")
end

local function shell_single_quote(value)
  return "'" .. tostring(value):gsub("'", "'\\''") .. "'"
end

local function is_git_ref_safe(value)
  if type(value) ~= "string" and type(value) ~= "number" then
    return false
  end
  local text = tostring(value)
  if text == "" or #text > 160 then
    return false
  end
  if text:sub(1, 1) == "-" or text:sub(1, 1) == "/" then
    return false
  end
  if text:find("%.%.", 1, true) ~= nil
    or text:find("//", 1, true) ~= nil
    or text:find("@{", 1, true) ~= nil
    or text:sub(-1) == "/"
    or text:sub(-1) == "."
    or text:sub(-5) == ".lock"
    or text:find("[%s~^:?%[%]\\*]") ~= nil then
    return false
  end
  for segment in text:gmatch("[^/]+") do
    if segment == "." or segment == ".." or segment:sub(1, 1) == "." then
      return false
    end
  end
  return text:find("^[%w%._%-%/]+$") ~= nil
end

local function is_git_sha(value)
  return tostring(value or ""):find("^%x+$") ~= nil and #tostring(value or "") <= 64
end

local function positive_pr_number(value)
  local number = tonumber(value)
  return number ~= nil and number >= 1 and number % 1 == 0 and number <= 2147483647
end

local function bounded_page_number(page)
  if page == nil then
    return nil
  end
  local n = tonumber(page)
  if n == nil or n ~= math.floor(n) or n < 1 then
    error("github-devloop: invalid list page number")
  end
  return n
end

local function parent_dir(worktree)
  return tostring(worktree):gsub("/+$", ""):match("^(.*)/[^/]+$") or "."
end

local function strip_simple_shell_quotes(command)
  local stripped = tostring(command or ""):gsub("'([^']*)'", "%1")
  return stripped
end

local function shell_quote_argv(value)
  local text = tostring(value or "")
  if text:find("^[%w_%-%./:=]+$") ~= nil then
    return text
  end
  return "'" .. text:gsub("'", "'\"'\"'") .. "'"
end

local function render_argv(values)
  local parts = {}
  for _, value in ipairs(values or {}) do
    table.insert(parts, shell_quote_argv(value))
  end
  return table.concat(parts, " ")
end

local function call_argv_rendered(call)
  local values = {}
  local program = (call or {}).program
  if program ~= nil and tostring(program) ~= "" then
    table.insert(values, tostring(program))
  end
  for _, arg in ipairs((call or {}).args or {}) do
    table.insert(values, tostring(arg))
  end
  return render_argv(values)
end

local function find_with_token_boundary(haystack, needle)
  local start_index, end_index = tostring(haystack or ""):find(tostring(needle or ""), 1, true)
  if start_index == nil then
    return false
  end
  local next_char = tostring(haystack or ""):sub(end_index + 1, end_index + 1)
  return next_char == "" or next_char:match("%s") ~= nil
end

local function strip_single_quotes_around_tokens(value)
  return tostring(value or ""):gsub("'([^'%s]+)'", "%1")
end

local function append_pr_list_query_order_permutation(patterns, command)
  local prefix, base = tostring(command or ""):match("^(gh api %-%-paginate %-%-slurp '?repos/[^']-/pulls%?state=open&head=[^&']+)&base=([^&']+)&per_page=100'?$")
  if prefix ~= nil then
    table.insert(patterns, prefix .. "&per_page=100&base=" .. base)
  end
end

local function append_render_permutations(patterns, command)
  local text = tostring(command or "")
  if text:match("^gh api %-%-include '") ~= nil
    or text:match("^gh api %-%-method [^ ]+ %-%-include '") ~= nil then
    return
  end
  local unquoted = strip_simple_shell_quotes(text)
  if unquoted ~= text then
    table.insert(patterns, unquoted)
  end
  append_pr_list_query_order_permutation(patterns, text)
  append_pr_list_query_order_permutation(patterns, unquoted)
  if unquoted:find("refs/remotes/origin/", 1, true) ~= nil then
    table.insert(patterns, (unquoted:gsub("refs/remotes/origin/", "refs/remotes/'origin'/'")))
  end
  if unquoted:find("refs/heads/", 1, true) ~= nil then
    table.insert(patterns, (unquoted:gsub("refs/heads/", "refs/heads/'")))
  end
  local first, rest = text:match("^(.-)%s+&&%s+(.*)$")
  if first ~= nil then
    table.insert(patterns, first)
    table.insert(patterns, strip_simple_shell_quotes(first))
    table.insert(patterns, rest)
    table.insert(patterns, strip_simple_shell_quotes(rest))
  end
  for segment in text:gmatch("[^;]+") do
    local trimmed = segment:gsub("^%s+", ""):gsub("%s+$", "")
    if trimmed ~= "" and trimmed ~= text and trimmed:find("%s") ~= nil then
      table.insert(patterns, trimmed)
      table.insert(patterns, strip_simple_shell_quotes(trimmed))
    end
  end
  local json_fields = text:match("^%-%-json ([^'].-)$")
  if json_fields ~= nil then
    table.insert(patterns, "--json " .. shell_single_quote(json_fields))
  end
  local quoted_json_fields = text:match("^%-%-json '([^']+)'$")
  if quoted_json_fields ~= nil then
    table.insert(patterns, "--json " .. quoted_json_fields)
  end
end

local function unique(values)
  local out = {}
  local seen = {}
  for _, value in ipairs(values or {}) do
    if type(value) == "string" and value ~= "" and not seen[value] then
      table.insert(out, value)
      seen[value] = true
    end
  end
  return out
end

function M.argv_rendered(command)
  return strip_simple_shell_quotes(command)
end

function M.call_rendered(call)
  local rendered = tostring((call or {}).rendered or "")
  if rendered ~= "" then
    return rendered
  end
  return call_argv_rendered(call)
end

function M.call_contains(call, needle)
  local rendered = tostring((call or {}).rendered or "")
  local argv_rendered = call_argv_rendered(call)
  local expected = tostring(needle or "")
  local unquoted_rendered = strip_simple_shell_quotes(rendered)
  local unquoted_argv_rendered = strip_simple_shell_quotes(argv_rendered)
  local unquoted_expected = strip_simple_shell_quotes(expected)
  local token_unquoted_expected = strip_single_quotes_around_tokens(expected)
  local expected_ends_with_quoted_token = expected:match("'[^']+'$") ~= nil
  local token_unquoted_match = false
  if token_unquoted_expected ~= expected then
    if expected_ends_with_quoted_token then
      token_unquoted_match = find_with_token_boundary(rendered, token_unquoted_expected)
        or find_with_token_boundary(unquoted_rendered, token_unquoted_expected)
        or find_with_token_boundary(argv_rendered, token_unquoted_expected)
        or find_with_token_boundary(unquoted_argv_rendered, token_unquoted_expected)
    else
      token_unquoted_match = rendered:find(token_unquoted_expected, 1, true) ~= nil
        or unquoted_rendered:find(token_unquoted_expected, 1, true) ~= nil
        or argv_rendered:find(token_unquoted_expected, 1, true) ~= nil
      or unquoted_argv_rendered:find(token_unquoted_expected, 1, true) ~= nil
    end
  end
  local normalized_rendered_match = false
  local normalized_argv_match = false
  if expected_ends_with_quoted_token then
    normalized_rendered_match = find_with_token_boundary(unquoted_rendered, unquoted_expected)
    normalized_argv_match = find_with_token_boundary(unquoted_argv_rendered, unquoted_expected)
  else
    normalized_rendered_match = rendered:find(unquoted_expected, 1, true) ~= nil
      or unquoted_rendered:find(unquoted_expected, 1, true) ~= nil
    normalized_argv_match = argv_rendered:find(unquoted_expected, 1, true) ~= nil
      or unquoted_argv_rendered:find(unquoted_expected, 1, true) ~= nil
  end
  if expected_ends_with_quoted_token then
    return rendered:find(expected, 1, true) ~= nil
      or unquoted_rendered:find(expected, 1, true) ~= nil
      or argv_rendered:find(expected, 1, true) ~= nil
      or unquoted_argv_rendered:find(expected, 1, true) ~= nil
      or normalized_rendered_match
      or normalized_argv_match
      or token_unquoted_match
  end
  return rendered:find(expected, 1, true) ~= nil
    or unquoted_rendered:find(expected, 1, true) ~= nil
    or argv_rendered:find(expected, 1, true) ~= nil
    or unquoted_argv_rendered:find(expected, 1, true) ~= nil
    or normalized_rendered_match
    or normalized_argv_match
    or token_unquoted_match
end

function M.argv_contains(call, values)
  local argv = {}
  if (call or {}).program ~= nil and tostring(call.program) ~= "" then
    table.insert(argv, tostring(call.program))
  end
  for _, arg in ipairs((call or {}).args or {}) do
    table.insert(argv, tostring(arg))
  end
  local offset = 1
  for _, expected in ipairs(values or {}) do
    local found = false
    for index = offset, #argv do
      if argv[index] == tostring(expected) then
        found = true
        offset = index + 1
        break
      end
    end
    if not found then
      return false
    end
  end
  return true
end

function M.argv_value_after(call, flag)
  local selected_flag = tostring(flag or "")
  local args = (call or {}).args or {}
  for index, arg in ipairs(args) do
    if tostring(arg) == selected_flag then
      local value = args[index + 1]
      if value ~= nil then
        return tostring(value)
      end
    end
  end
  local rendered = M.call_rendered(call)
  local single_quoted = rendered:match(selected_flag:gsub("([^%w])", "%%%1") .. "%s+'([^']+)'")
  if single_quoted ~= nil then
    return single_quoted
  end
  return rendered:match(selected_flag:gsub("([^%w])", "%%%1") .. "%s+([^%s]+)")
end

function M.count_calls(t, needle)
  local count = 0
  for _, call in ipairs(t.command_calls()) do
    if M.call_contains(call, needle) then
      count = count + 1
    end
  end
  return count
end

local function append_gh_mock_patterns(patterns, command)
  local text = tostring(command or "")
  if text:find("gh ", 1, true) == nil then
    return
  end
  table.insert(patterns, strip_simple_shell_quotes(text))
  local issue_number, repo, issue_fields = text:match("^gh issue view '([^']+)' %-%-repo '([^']+)' %-%-json ([^ ]+)$")
  if issue_number ~= nil then
    table.insert(patterns, "gh issue view " .. issue_number .. " --repo " .. repo .. " --json '" .. issue_fields .. "'")
    return
  end
  local issue_number_plain, repo_plain, issue_fields_plain = text:match("^gh issue view ([^ ]+) %-%-repo ([^ ]+) %-%-json ([^ ]+)$")
  if issue_number_plain ~= nil then
    table.insert(patterns, "gh issue view " .. issue_number_plain .. " --repo " .. repo_plain .. " --json '" .. issue_fields_plain .. "'")
    return
  end
  local pr_number, pr_repo, pr_fields = text:match("^gh pr view '([^']+)' %-%-repo '([^']+)' %-%-json ([^ ]+)$")
  if pr_number ~= nil then
    table.insert(patterns, "gh pr view " .. pr_number .. " --repo " .. pr_repo .. " --json '" .. pr_fields .. "'")
    return
  end
  local pr_number_plain, pr_repo_plain, pr_fields_plain = text:match("^gh pr view ([^ ]+) %-%-repo ([^ ]+) %-%-json ([^ ]+)$")
  if pr_number_plain ~= nil then
    table.insert(patterns, "gh pr view " .. pr_number_plain .. " --repo " .. pr_repo_plain .. " --json '" .. pr_fields_plain .. "'")
    return
  end
  local edit_number, edit_repo = text:match("^gh issue edit '([^']+)' %-%-repo '([^']+)' ")
  if edit_number ~= nil then
    table.insert(patterns, "gh issue edit " .. edit_number .. " --repo " .. edit_repo)
  end
  local list_repo, list_state, list_limit, list_fields = text:match("^gh issue list %-%-repo '([^']+)' %-%-state ([^ ]+) %-%-limit ([^ ]+) %-%-json ([^ ]+)$")
  if list_repo ~= nil then
    table.insert(patterns, "gh issue list --repo " .. list_repo .. " --state " .. list_state .. " --limit " .. list_limit .. " --json '" .. list_fields .. "'")
    table.insert(patterns, "gh issue list --repo " .. list_repo .. " --state " .. list_state .. " --limit " .. list_limit .. " --json " .. list_fields)
  end
  local search_repo, search_state, search_limit, search_query, search_fields =
    text:match("^gh issue list %-%-repo '([^']+)' %-%-state ([^ ]+) %-%-limit ([^ ]+) %-%-search '([^']+)' %-%-json ([^ ]+)$")
  if search_repo ~= nil then
    table.insert(patterns, "gh issue list --repo " .. search_repo
      .. " --state " .. search_state
      .. " --limit " .. search_limit
      .. " --search " .. shell_single_quote(search_query)
      .. " --json '" .. search_fields .. "'")
    table.insert(patterns, "gh issue list --repo " .. search_repo
      .. " --state " .. search_state
      .. " --limit " .. search_limit
      .. " --search " .. shell_single_quote(search_query)
      .. " --json " .. search_fields)
  end
  local pr_list_repo, pr_list_state, pr_list_limit, pr_list_fields = text:match("^gh pr list %-%-repo '([^']+)' %-%-state ([^ ]+) %-%-limit ([^ ]+) %-%-json ([^ ]+)$")
  if pr_list_repo ~= nil then
    table.insert(patterns, "gh pr list --repo " .. pr_list_repo .. " --state " .. pr_list_state .. " --limit " .. pr_list_limit .. " --json '" .. pr_list_fields .. "'")
  end
  local api_path = text:match("^gh api '([^']+)'$")
  if api_path ~= nil then
    table.insert(patterns, "gh api " .. api_path)
  end
  local comments_path = text:match("^gh api %-%-paginate %-%-slurp '([^']+)'$")
  if comments_path ~= nil then
    table.insert(patterns, "gh api --paginate --slurp " .. comments_path)
  end
  local api_method_simple, api_method_simple_path = text:match("^gh api %-%-method ([^ ]+) ([^ '][^ ]*)$")
  if api_method_simple_path ~= nil then
    table.insert(patterns, "gh api --method " .. api_method_simple .. " " .. shell_single_quote(api_method_simple_path))
  end
  local jq_path, jq_expr = text:match("^gh api '([^']+)' %-%-jq '([^']+)'$")
  if jq_path ~= nil then
    table.insert(patterns, "gh api " .. jq_path .. " --jq " .. jq_expr)
    table.insert(patterns, "gh api " .. jq_path .. " --jq '" .. jq_expr .. "'")
  end
  local method, method_path = text:match("^gh api %-%-method ([^ ]+) '([^']+)'$")
  if method_path ~= nil then
    table.insert(patterns, "gh api --method " .. method .. " " .. method_path)
  end
  local input_method, input_path, input_file = text:match("^gh api %-%-method ([^ ]+) '([^']+)' %-%-input '([^']+)'")
  if input_path ~= nil then
    table.insert(patterns, "gh api --method " .. input_method .. " " .. input_path .. " --input " .. input_file)
  end
  local input_method_prefix, input_path_prefix, input_file_prefix = text:match("^gh api %-%-method ([^ ]+) '([^']+)' %-%-input '([^']*)$")
  if input_path_prefix ~= nil then
    table.insert(patterns, "gh api --method " .. input_method_prefix .. " " .. input_path_prefix .. " --input " .. input_file_prefix)
  end
  local field_method, field_path = text:match("^gh api %-%-method ([^ ]+) '([^']+)' %-f ")
  if field_path ~= nil then
    table.insert(patterns, "gh api --method " .. field_method .. " " .. field_path .. " -f ")
    table.insert(patterns, "gh api --method " .. field_method .. " " .. shell_single_quote(field_path) .. " -f ")
  end
  local field_method_full, field_path_full, fields_tail = text:match("^gh api %-%-method ([^ ]+) '([^']+)' (%-f .*)$")
  if field_path_full ~= nil then
    local unquoted_fields = fields_tail:gsub("'([^']*)'", "%1")
    table.insert(patterns, "gh api --method " .. field_method_full .. " " .. field_path_full .. " " .. unquoted_fields)
    table.insert(patterns, "gh api --method " .. field_method_full .. " " .. shell_single_quote(field_path_full) .. " " .. unquoted_fields)
  end
  local pr_comment_number, pr_comment_repo, pr_comment_file = text:match("^gh pr comment '([^']+)' %-%-repo '([^']+)' %-%-body%-file '([^']+)'")
  if pr_comment_number ~= nil then
    table.insert(patterns, "gh pr comment " .. pr_comment_number .. " --repo " .. pr_comment_repo .. " --body-file " .. pr_comment_file)
  end
  local pr_comment_number_prefix, pr_comment_repo_prefix, pr_comment_file_prefix = text:match("^gh pr comment '([^']+)' %-%-repo '([^']+)' %-%-body%-file '([^']*)$")
  if pr_comment_number_prefix ~= nil then
    table.insert(patterns, "gh pr comment " .. pr_comment_number_prefix .. " --repo " .. pr_comment_repo_prefix .. " --body-file " .. pr_comment_file_prefix)
  end
  local issue_comment_number, issue_comment_repo, issue_comment_file = text:match("^gh issue comment '([^']+)' %-%-repo '([^']+)' %-%-body%-file '([^']+)'")
  if issue_comment_number ~= nil then
    table.insert(patterns, "gh issue comment " .. issue_comment_number .. " --repo " .. issue_comment_repo .. " --body-file " .. issue_comment_file)
  end
  local issue_comment_number_prefix, issue_comment_repo_prefix, issue_comment_file_prefix = text:match("^gh issue comment '([^']+)' %-%-repo '([^']+)' %-%-body%-file '([^']*)$")
  if issue_comment_number_prefix ~= nil then
    table.insert(patterns, "gh issue comment " .. issue_comment_number_prefix .. " --repo " .. issue_comment_repo_prefix .. " --body-file " .. issue_comment_file_prefix)
  end
  local pr_ready_number, pr_ready_repo = text:match("^gh pr ready '([^']+)' %-%-repo '([^']+)'$")
  if pr_ready_number ~= nil then
    table.insert(patterns, "gh pr ready " .. pr_ready_number .. " --repo " .. pr_ready_repo)
  end
  local pr_close_number, pr_close_repo = text:match("^gh pr close '([^']+)' %-%-repo '([^']+)'$")
  if pr_close_number ~= nil then
    table.insert(patterns, "gh pr close " .. pr_close_number .. " --repo " .. pr_close_repo)
  end
  local issue_close_number, issue_close_repo = text:match("^gh issue close '([^']+)' %-%-repo '([^']+)'$")
  if issue_close_number ~= nil then
    table.insert(patterns, "gh issue close " .. issue_close_number .. " --repo " .. issue_close_repo)
  end
  local diff_number, diff_repo = text:match("^gh pr diff '([^']+)' %-%-repo '([^']+)'$")
  if diff_number ~= nil then
    table.insert(patterns, "gh pr diff " .. diff_number .. " --repo " .. diff_repo)
  end
  local diff_name_number, diff_name_repo = text:match("^gh pr diff '([^']+)' %-%-repo '([^']+)' %-%-name%-only$")
  if diff_name_number ~= nil then
    table.insert(patterns, "gh pr diff " .. diff_name_number .. " --repo " .. diff_name_repo .. " --name-only")
  end
end

local function append_git_mock_patterns(patterns, command)
  local text = tostring(command or "")
  if text:find("git ", 1, true) == nil then
    return
  end
  local unquoted = strip_simple_shell_quotes(text)
  table.insert(patterns, unquoted)
  if unquoted:find("refs/remotes/origin/", 1, true) ~= nil then
    table.insert(patterns, (unquoted:gsub("refs/remotes/origin/", "refs/remotes/'origin'/'")))
  end
  if unquoted:find("refs/heads/", 1, true) ~= nil then
    table.insert(patterns, (unquoted:gsub("refs/heads/", "refs/heads/'")))
  end
  local fetch_remote, fetch_ref = text:match("^git fetch '([^']+)' '([^']+)'$")
  if fetch_remote ~= nil then
    table.insert(patterns, "git fetch " .. fetch_remote .. " " .. fetch_ref)
  end
  if text == "git rev-parse --verify FETCH_HEAD^{commit}" then
    table.insert(patterns, "git rev-parse --verify 'FETCH_HEAD^{commit}'")
  elseif text == "git rev-parse --verify 'FETCH_HEAD^{commit}'" then
    table.insert(patterns, "git rev-parse --verify FETCH_HEAD^{commit}")
  end
  local rev_remote, rev_branch = text:match("^git rev%-parse %-%-verify refs/remotes/'([^']+)'/'([^']+)'%^{commit}$")
  if rev_remote ~= nil then
    table.insert(patterns, "git rev-parse --verify refs/remotes/" .. rev_remote .. "/" .. rev_branch .. "^{commit}")
    table.insert(patterns, "git rev-parse --verify 'refs/remotes/" .. rev_remote .. "/" .. rev_branch .. "^{commit}'")
  end
  local quoted_rev_ref = text:match("^git rev%-parse %-%-verify 'refs/remotes/([^']+)%^{commit}'$")
  if quoted_rev_ref ~= nil then
    local remote, branch = quoted_rev_ref:match("^([^/]+)/(.+)$")
    if remote ~= nil and branch ~= nil then
      table.insert(patterns, "git rev-parse --verify refs/remotes/'" .. remote .. "'/'" .. branch .. "'^{commit}")
    end
  end
  local ls_remote, ls_branch = text:match("^git ls%-remote '([^']+)' refs/heads/'([^']+)'$")
  if ls_remote ~= nil then
    table.insert(patterns, "git ls-remote " .. ls_remote .. " refs/heads/" .. ls_branch)
  end
  local worktree, branch = text:match("^git %-C '([^']+)' rev%-parse %-%-abbrev%-ref HEAD$")
  if worktree ~= nil then
    table.insert(patterns, "git -C " .. worktree .. " rev-parse --abbrev-ref HEAD")
  end
  local bare_branch = text:match("^git rev%-parse %-%-abbrev%-ref HEAD$")
  if bare_branch ~= nil then
    table.insert(patterns, "git rev-parse --abbrev-ref HEAD")
  end
end

local function install_command_shim(t)
  if t._gh_argv_mock_shim_installed == true then
    return
  end
  local raw_mock_command = t.mock_command
  t.mock_command = function(command, result)
    local patterns = { command }
    append_render_permutations(patterns, command)
    append_gh_mock_patterns(patterns, command)
    append_git_mock_patterns(patterns, command)
    for _, pattern in ipairs(unique(patterns)) do
      raw_mock_command(pattern, result)
    end
  end
  t._gh_argv_mock_shim_installed = true
end

local function gh_issue_list_intake_command(repo, limit)
  return "gh issue list --repo " .. shell_single_quote(repo)
    .. " --state open --limit " .. tostring(math.floor(tonumber(limit or 100)))
    .. " --json number,title,body,updatedAt,labels,assignees,author"
end

local function gh_issue_list_decompose_children_command(repo, proposal_id)
  return "gh issue list --repo " .. shell_single_quote(repo)
    .. " --state all --limit 100 --search "
    .. shell_single_quote("fkst:github-devloop:decompose-child:v1 " .. tostring(proposal_id))
    .. " --json number,title,state,author,body,url"
end

local function gh_issue_list_recent_closed_command(repo, limit)
  return "gh issue list --repo " .. shell_single_quote(repo)
    .. " --state closed --limit " .. tostring(math.floor(tonumber(limit or 30)))
    .. " --json number,title,closedAt,labels"
end

local function gh_issue_list_board_digest_command(repo)
  return "gh issue list --repo " .. shell_single_quote(repo)
    .. " --state open --limit 100 --json number,title,labels"
end

local function gh_pr_list_board_digest_command(repo)
  return "gh pr list --repo " .. shell_single_quote(repo)
    .. " --state open --limit 100 --json number,title,labels"
end

local function gh_issue_list_observe_command(repo, label, page, include_headers)
  local selected_page = bounded_page_number(page)
  local include = include_headers and "--include " or ""
  local paginate = "--paginate --slurp "
  local page_query = selected_page ~= nil and ("&page=" .. tostring(selected_page)) or ""
  if selected_page ~= nil then
    paginate = ""
  end
  local query = "repos/" .. tostring(repo) .. "/issues?state=open&per_page=100" .. page_query
  if label ~= nil and tostring(label) ~= "" then
    query = "repos/" .. tostring(repo) .. "/issues?state=open&labels="
      .. tostring(label):gsub(":", "%%3A") .. "&per_page=100" .. page_query
  end
  return "gh api " .. include .. paginate .. shell_single_quote(query)
end

local function gh_pr_list_observe_command(repo, page, include_headers)
  local selected_page = bounded_page_number(page)
  local include = include_headers and "--include " or ""
  local paginate = selected_page == nil and "--paginate --slurp " or ""
  local page_query = selected_page ~= nil and ("&page=" .. tostring(page)) or ""
  return "gh api " .. include .. paginate
    .. shell_single_quote("repos/" .. tostring(repo) .. "/pulls?state=open&per_page=100" .. page_query)
end

local function gh_issue_view_command(repo, issue_number, fields)
  return "gh issue view " .. shell_single_quote(issue_number)
    .. " --repo " .. shell_single_quote(repo)
    .. " --json " .. tostring(fields)
end

local function gh_pr_view_command(repo, pr_number, fields)
  return "gh pr view " .. shell_single_quote(pr_number)
    .. " --repo " .. shell_single_quote(repo)
    .. " --json " .. tostring(fields)
end

local function gh_pr_list_head_command(repo, head, base)
  local owner = repo_owner(repo)
  local head_filter = owner ~= nil and (owner .. ":" .. tostring(head)) or tostring(head)
  local query = "repos/" .. tostring(repo)
    .. "/pulls?state=open&head=" .. url_encode(head_filter)
    .. "&per_page=100"
  if base ~= nil then
    query = query .. "&base=" .. url_encode(base)
  end
  return "gh api --paginate --slurp " .. shell_single_quote(query)
end

local function gh_api_paginate(path)
  return "gh api --paginate --slurp " .. shell_single_quote(path)
end

local function gh_api_method_command(method, path, fields, input_file, include_headers)
  local parts = { "gh", "api", "--method", tostring(method) }
  if include_headers then
    table.insert(parts, "--include")
  end
  table.insert(parts, tostring(path))
  for _, field in ipairs(fields or {}) do
    table.insert(parts, "-f")
    table.insert(parts, tostring(field))
  end
  if input_file ~= nil then
    table.insert(parts, "--input")
    table.insert(parts, tostring(input_file))
  end
  return render_argv(parts)
end

local function gh_blocked_by_command(core, repo, issue_number)
  local owner, name = tostring(repo or ""):match("^([^/]+)/([^/]+)$")
  if owner == nil then
    owner = ""
    name = ""
  end
  local query = core.render_github_graphql_query("dependency_blocked_by", {
    owner = owner,
    name = name,
    issue_number = tostring(math.floor(tonumber(issue_number) or 0)),
  })
  return render_argv({ "gh", "api", "graphql", "-f", "query=" .. query })
end

local function install_legacy_command_renderers(core)
  core.gh_issue_list_intake_cmd = core.gh_issue_list_intake_cmd or gh_issue_list_intake_command
  core.gh_issue_list_decompose_children_cmd = core.gh_issue_list_decompose_children_cmd or gh_issue_list_decompose_children_command
  core.gh_issue_list_recent_closed_cmd = core.gh_issue_list_recent_closed_cmd or gh_issue_list_recent_closed_command
  core.gh_issue_list_board_digest_cmd = core.gh_issue_list_board_digest_cmd or gh_issue_list_board_digest_command
  core.gh_pr_list_board_digest_cmd = core.gh_pr_list_board_digest_cmd or gh_pr_list_board_digest_command
  core.gh_issue_list_observe_cmd = core.gh_issue_list_observe_cmd or gh_issue_list_observe_command
  core.gh_pr_list_observe_cmd = core.gh_pr_list_observe_cmd or gh_pr_list_observe_command
  core.gh_issue_list_observe_opts = function(repo, label, page, include_headers)
    local timeout = 10
    return {
      cmd = core.gh_issue_list_observe_cmd(repo, label, page, include_headers),
      run = function(selected_timeout)
        return core.gh_issue_list_observe(repo, label, page, include_headers, selected_timeout or timeout)
      end,
      timeout = timeout,
      read_coalesce = core.gh_issue_list_observe_read_coalesce(repo, label, page),
    }
  end
  core.gh_pr_list_observe_opts = function(repo, page, include_headers)
    local timeout = 10
    return {
      cmd = core.gh_pr_list_observe_cmd(repo, page, include_headers),
      run = function(selected_timeout)
        return core.gh_pr_list_observe(repo, page, include_headers, selected_timeout or timeout)
      end,
      timeout = timeout,
      read_coalesce = core.gh_pr_list_observe_read_coalesce(repo, page),
    }
  end
  core.gh_issue_list_wip_cmd = core.gh_issue_list_wip_cmd or function(repo)
    return "gh issue list --repo " .. shell_single_quote(repo)
      .. " --state open --limit 100 --json number"
  end
  core.gh_dashboard_issue_list_cmd = core.gh_dashboard_issue_list_cmd or function(repo, label)
    return gh_api_paginate("repos/" .. tostring(repo) .. "/issues?state=open&labels=" .. tostring(label):gsub(":", "%%3A") .. "&per_page=100")
  end
  core.gh_dashboard_issue_all_open_cmd = core.gh_dashboard_issue_all_open_cmd or function(repo)
    return gh_api_paginate("repos/" .. tostring(repo) .. "/issues?state=open&per_page=100")
  end
  core.gh_dashboard_label_get_cmd = core.gh_dashboard_label_get_cmd or function(repo, label)
    return "gh api --method GET " .. shell_single_quote("repos/" .. tostring(repo) .. "/labels/" .. tostring(label):gsub(":", "%%3A"))
  end
  core.gh_dashboard_issue_get_cmd = core.gh_dashboard_issue_get_cmd or function(repo, issue_number)
    return "gh api --method GET --include " .. shell_single_quote("repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number))
  end
  core.gh_dashboard_issue_create_cmd = core.gh_dashboard_issue_create_cmd or function(repo, input_file)
    return gh_api_method_command("POST", "repos/" .. tostring(repo) .. "/issues", nil, input_file)
  end
  core.gh_dashboard_issue_update_cmd = core.gh_dashboard_issue_update_cmd or function(repo, issue_number, input_file)
    return gh_api_method_command("PATCH", "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number), nil, input_file)
  end
  core.gh_repo_labels_list_cmd = core.gh_repo_labels_list_cmd or function(repo)
    return gh_api_paginate("repos/" .. tostring(repo) .. "/labels?per_page=100")
  end
  core.gh_repo_label_create_cmd = core.gh_repo_label_create_cmd or function(repo, name, color, description)
    return "gh api --method POST " .. shell_single_quote("repos/" .. tostring(repo) .. "/labels")
      .. " -f " .. shell_single_quote("name=" .. tostring(name))
      .. " -f " .. shell_single_quote("color=" .. tostring(color))
      .. " -f " .. shell_single_quote("description=" .. tostring(description or ""))
  end
  core.gh_repo_label_update_cmd = core.gh_repo_label_update_cmd or function(repo, name, color, description)
    return "gh api --method PATCH " .. shell_single_quote("repos/" .. tostring(repo) .. "/labels/" .. tostring(name):gsub(":", "%%3A"))
      .. " -f " .. shell_single_quote("color=" .. tostring(color))
      .. " -f " .. shell_single_quote("description=" .. tostring(description or ""))
  end

  core.gh_issue_view_intake_judge_cmd = core.gh_issue_view_intake_judge_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,body,createdAt,updatedAt,labels,comments,state,assignees,author")
  end
  core.gh_issue_view_state_cmd = core.gh_issue_view_state_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,createdAt,updatedAt,labels,state,comments,assignees,author")
  end
  core.gh_issue_view_claim_cmd = core.gh_issue_view_claim_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "assignees,author")
  end
  core.gh_issue_view_result_cmd = core.gh_issue_view_result_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "labels,comments")
  end
  core.gh_issue_view_loop_cmd = core.gh_issue_view_loop_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,updatedAt,labels,comments,state")
  end
  core.gh_issue_view_meta_cmd = core.gh_issue_view_meta_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments")
  end
  core.gh_issue_view_implement_cmd = core.gh_issue_view_implement_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,body,labels,comments,state,author")
  end
  core.gh_issue_view_open_pr_cmd = core.gh_issue_view_open_pr_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments,assignees,author")
  end
  core.gh_issue_view_reviewing_cmd = core.gh_issue_view_reviewing_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "labels,comments")
  end
  core.gh_issue_view_review_cmd = core.gh_issue_view_review_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments,assignees,author")
  end
  core.gh_issue_view_decompose_cmd = core.gh_issue_view_decompose_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,body,labels,comments")
  end
  core.gh_issue_view_fix_cmd = core.gh_issue_view_fix_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments")
  end
  core.gh_issue_view_commit_subject_cmd = core.gh_issue_view_commit_subject_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "number,title")
  end
  core.gh_issue_view_review_loop_cmd = core.gh_issue_view_review_loop_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments,assignees,author")
  end
  core.gh_issue_view_merge_cmd = core.gh_issue_view_merge_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,labels,comments,state,assignees")
  end
  core.gh_issue_view_observe_cmd = core.gh_issue_view_observe_cmd or function(repo, number)
    return gh_issue_view_command(repo, number, "title,comments,state,stateReason,assignees,author")
  end

  core.gh_pr_view_origin_cmd = core.gh_pr_view_origin_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,mergedAt,comments,labels,mergeable,mergeStateStatus")
  end
  core.gh_pr_view_observe_cmd = core.gh_pr_view_observe_cmd or core.gh_pr_view_origin_cmd
  core.gh_pr_view_merge_cmd = core.gh_pr_view_merge_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "headRefName,headRefOid,baseRefName,baseRefOid,state,updatedAt,isDraft,mergedAt,comments,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup")
  end
  core.gh_pr_view_fix_cmd = core.gh_pr_view_fix_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "headRefName,headRefOid,baseRefName,state,comments,headRepository,headRepositoryOwner,isCrossRepository")
  end
  core.gh_pr_view_fix_precheck_cmd = core.gh_pr_view_fix_precheck_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "headRefName,headRefOid,baseRefName,state,updatedAt,comments,headRepository,headRepositoryOwner,isCrossRepository")
  end
  core.gh_pr_view_freshness_cmd = core.gh_pr_view_freshness_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "headRefName,headRefOid,baseRefName,state,updatedAt,isDraft,comments,labels,headRepository,headRepositoryOwner,isCrossRepository,mergeable,mergeStateStatus,statusCheckRollup")
  end
  core.gh_pr_view_head_cmd = core.gh_pr_view_head_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "headRefName,baseRefName,state")
  end
  core.gh_pr_view_context_cmd = core.gh_pr_view_context_cmd or function(repo, number)
    return gh_pr_view_command(repo, number, "title,body,headRefName,headRefOid,baseRefName,state,updatedAt,comments,labels")
  end
  core.gh_pr_list_freshness_cmd = core.gh_pr_list_freshness_cmd or function(repo)
    return gh_api_paginate("repos/" .. tostring(repo) .. "/pulls?state=open&per_page=100")
  end
  core.gh_pr_list_recent_merged_cmd = core.gh_pr_list_recent_merged_cmd or function(repo, limit)
    return "gh pr list --repo " .. shell_single_quote(repo)
      .. " --state merged --limit " .. tostring(math.floor(tonumber(limit or 30)))
      .. " --json number,title,mergedAt,headRefOid"
  end
  core.gh_pr_list_merge_queue_cmd = core.gh_pr_list_merge_queue_cmd or function(repo, base)
    return gh_api_paginate("repos/" .. tostring(repo) .. "/pulls?state=open&base=" .. url_encode(base) .. "&per_page=100")
  end
  core.gh_pr_list_head_base_cmd = core.gh_pr_list_head_base_cmd or function(repo, head, base)
    return gh_pr_list_head_command(repo, head, base)
  end
  core.gh_pr_list_head_cmd = core.gh_pr_list_head_cmd or function(repo, head)
    return gh_pr_list_head_command(repo, head, nil)
  end
  core.gh_pr_merge_cmd = core.gh_pr_merge_cmd or function(repo, number, head_sha)
    return "gh pr merge " .. shell_single_quote(number)
      .. " --repo " .. shell_single_quote(repo)
      .. " --merge --match-head-commit " .. shell_single_quote(head_sha)
  end
  core.gh_check_run_rerequest_cmd = core.gh_check_run_rerequest_cmd or function(repo, id)
    return "gh api --method POST " .. shell_single_quote("repos/" .. tostring(repo) .. "/check-runs/" .. tostring(id) .. "/rerequest")
  end
  core.gh_commit_check_runs_cmd = core.gh_commit_check_runs_cmd or function(repo, head_sha)
    return "gh api " .. shell_single_quote("repos/" .. tostring(repo) .. "/commits/" .. tostring(head_sha) .. "/check-runs")
  end
  core.gh_issue_comment_get_cmd = core.gh_issue_comment_get_cmd or function(repo, comment_id)
    return "gh api " .. shell_single_quote("repos/" .. tostring(repo) .. "/issues/comments/" .. tostring(comment_id))
  end
  core.gh_pr_ready_cmd = core.gh_pr_ready_cmd or function(repo, number)
    return "gh pr ready " .. shell_single_quote(number) .. " --repo " .. shell_single_quote(repo)
  end
  core.gh_pr_comment_cmd = core.gh_pr_comment_cmd or function(repo, number, body_file)
    return "gh pr comment " .. shell_single_quote(number)
      .. " --repo " .. shell_single_quote(repo)
      .. " --body-file " .. shell_single_quote(body_file)
  end
  core.gh_issue_comment_cmd = core.gh_issue_comment_cmd or function(repo, number, body_file)
    return "gh issue comment " .. shell_single_quote(number)
      .. " --repo " .. shell_single_quote(repo)
      .. " --body-file " .. shell_single_quote(body_file)
  end
  core.gh_pr_close_cmd = core.gh_pr_close_cmd or function(repo, number)
    return "gh pr close " .. shell_single_quote(number) .. " --repo " .. shell_single_quote(repo)
  end
  core.gh_issue_close_cmd = core.gh_issue_close_cmd or function(repo, number)
    return "gh issue close " .. shell_single_quote(number) .. " --repo " .. shell_single_quote(repo)
  end
  core.gh_pr_diff_cmd = core.gh_pr_diff_cmd or function(repo, number)
    return "gh pr diff " .. shell_single_quote(number) .. " --repo " .. shell_single_quote(repo)
  end
  core.gh_pr_diff_name_only_cmd = core.gh_pr_diff_name_only_cmd or function(repo, number)
    return "gh pr diff " .. shell_single_quote(number) .. " --repo " .. shell_single_quote(repo) .. " --name-only"
  end
  core.gh_blocked_by_cmd = core.gh_blocked_by_cmd or function(repo, issue_number)
    return gh_blocked_by_command(core, repo, issue_number)
  end

  core.git_status_cmd = core.git_status_cmd or function(worktree)
    return "git -C " .. shell_single_quote(worktree) .. " status --porcelain"
  end
  core.git_add_all_cmd = core.git_add_all_cmd or function(worktree)
    return "git -C " .. shell_single_quote(worktree) .. " add -A"
  end
  core.git_commit_cmd = core.git_commit_cmd or function(worktree, message)
    return "git -C " .. shell_single_quote(worktree) .. " commit -m " .. shell_single_quote(message)
  end
  core.git_empty_commit_cmd = core.git_empty_commit_cmd or function(worktree, message)
    return "git -C " .. shell_single_quote(worktree) .. " commit --allow-empty -m " .. shell_single_quote(message)
  end
  core.git_current_branch_cmd = core.git_current_branch_cmd or function(worktree)
    if worktree == nil then
      return "git rev-parse --abbrev-ref HEAD"
    end
    return "git -C " .. shell_single_quote(worktree) .. " rev-parse --abbrev-ref HEAD"
  end
  core.git_head_sha_cmd = core.git_head_sha_cmd or function(worktree)
    return "git -C " .. shell_single_quote(worktree) .. " rev-parse HEAD"
  end
  core.git_base_head_cmd = core.git_base_head_cmd or function(branch)
    return "git rev-parse --verify refs/remotes/origin/" .. shell_single_quote(branch) .. "^{commit}"
  end
  core.git_fetch_branch_cmd = core.git_fetch_branch_cmd or function(remote, branch)
    return "git fetch " .. shell_single_quote(remote) .. " " .. shell_single_quote(branch)
  end
  core.git_fetch_pr_merge_ref_cmd = core.git_fetch_pr_merge_ref_cmd or function(remote, number)
    return "git fetch " .. shell_single_quote(remote) .. " " .. shell_single_quote("refs/pull/" .. tostring(number) .. "/merge")
  end
  core.git_fetch_pr_head_ref_cmd = core.git_fetch_pr_head_ref_cmd or function(remote, number)
    return "git fetch " .. shell_single_quote(remote) .. " " .. shell_single_quote("refs/pull/" .. tostring(number) .. "/head")
  end
  core.git_fetch_head_commit_cmd = core.git_fetch_head_commit_cmd or function()
    return "git rev-parse --verify FETCH_HEAD^{commit}"
  end
  core.git_remote_branch_head_cmd = core.git_remote_branch_head_cmd or function(remote, branch)
    return "git rev-parse --verify refs/remotes/" .. shell_single_quote(remote) .. "/" .. shell_single_quote(branch) .. "^{commit}"
  end
  core.git_ls_remote_branch_cmd = core.git_ls_remote_branch_cmd or function(remote, branch)
    return "git ls-remote " .. shell_single_quote(remote) .. " refs/heads/" .. shell_single_quote(branch)
  end
  core.git_fetch_remote_branch_to_tracking_ref_cmd = core.git_fetch_remote_branch_to_tracking_ref_cmd or function(remote, branch, tracking_ref)
    return "git fetch " .. shell_single_quote(remote) .. " " .. shell_single_quote("refs/heads/" .. tostring(branch) .. ":" .. tostring(tracking_ref))
  end
  core.git_rev_parse_ref_commit_cmd = core.git_rev_parse_ref_commit_cmd or function(ref)
    return "git rev-parse --verify " .. shell_single_quote(tostring(ref) .. "^{commit}")
  end
  core.git_worktree_merge_no_edit_cmd = core.git_worktree_merge_no_edit_cmd or function(worktree, sha)
    return "git -C " .. shell_single_quote(worktree) .. " merge --no-edit " .. shell_single_quote(sha)
  end
  core.git_worktree_add_new_branch_cmd = core.git_worktree_add_new_branch_cmd or function(worktree, branch, base)
    return "mkdir -p " .. shell_single_quote(parent_dir(worktree))
      .. " && git worktree add -b " .. shell_single_quote(branch)
      .. " " .. shell_single_quote(worktree)
      .. " " .. shell_single_quote(base)
  end
  core.git_worktree_add_reset_branch_cmd = core.git_worktree_add_reset_branch_cmd or function(worktree, branch, base)
    return "mkdir -p " .. shell_single_quote(parent_dir(worktree))
      .. " && git worktree add -B " .. shell_single_quote(branch)
      .. " " .. shell_single_quote(worktree)
      .. " " .. shell_single_quote(base)
  end
  core.git_worktree_add_existing_branch_cmd = core.git_worktree_add_existing_branch_cmd or function(worktree, branch)
    return "mkdir -p " .. shell_single_quote(parent_dir(worktree))
      .. " && git worktree add " .. shell_single_quote(worktree)
      .. " " .. shell_single_quote(branch)
  end
  core.git_worktree_add_remote_branch_cmd = core.git_worktree_add_remote_branch_cmd or function(worktree, remote, branch, force)
    return "mkdir -p " .. shell_single_quote(parent_dir(worktree))
      .. " && git worktree add" .. (force and " --force" or "")
      .. " -B " .. shell_single_quote(branch)
      .. " " .. shell_single_quote(worktree)
      .. " refs/remotes/" .. shell_single_quote(remote) .. "/" .. shell_single_quote(branch)
  end
  core.git_worktree_reset_hard_cmd = core.git_worktree_reset_hard_cmd or function(worktree, branch)
    return "git -C " .. shell_single_quote(worktree) .. " reset --hard refs/heads/" .. shell_single_quote(branch)
  end
  core.git_worktree_clean_cmd = core.git_worktree_clean_cmd or function(worktree)
    return "git -C " .. shell_single_quote(worktree) .. " clean -fd"
  end
  core.git_ahead_count_cmd = core.git_ahead_count_cmd or function(upstream, integration)
    return "git rev-list --count refs/remotes/origin/" .. shell_single_quote(upstream) .. "..refs/remotes/origin/" .. shell_single_quote(integration)
  end
  core.git_show_ref_branch_cmd = core.git_show_ref_branch_cmd or function(branch)
    return "git show-ref --verify --quiet refs/heads/" .. shell_single_quote(branch)
  end
  core.git_show_ref_cmd = core.git_show_ref_cmd or function(worktree, branch)
    return "git -C " .. shell_single_quote(worktree) .. " show-ref --verify --quiet refs/heads/" .. shell_single_quote(branch)
  end
  core.git_branch_ahead_count_cmd = core.git_branch_ahead_count_cmd or function(base, branch)
    return "git rev-list --count " .. shell_single_quote(tostring(base) .. "..refs/heads/" .. tostring(branch))
  end
  core.git_branch_head_cmd = core.git_branch_head_cmd or function(branch)
    return "git rev-parse --verify refs/heads/" .. shell_single_quote(branch)
  end
  core.git_push_branch_cmd = core.git_push_branch_cmd or function(branch)
    return "git push origin " .. shell_single_quote(branch)
  end
  core.git_switch_branch_cmd = core.git_switch_branch_cmd or function(worktree, branch)
    return "git -C " .. shell_single_quote(worktree) .. " switch " .. shell_single_quote(branch)
  end
  core.git_rev_parse_branch_cmd = core.git_rev_parse_branch_cmd or function(worktree, branch)
    return "git -C " .. shell_single_quote(worktree) .. " rev-parse --verify refs/heads/" .. shell_single_quote(branch)
  end
  core.git_worktree_list_cmd = core.git_worktree_list_cmd or function()
    return "git worktree list --porcelain"
  end
  core.git_worktree_remove_cmd = core.git_worktree_remove_cmd or function(worktree)
    return "git worktree remove --force " .. shell_single_quote(worktree)
  end
  core.git_worktree_prune_cmd = core.git_worktree_prune_cmd or function()
    return "git worktree prune"
  end
  core.git_worktree_force_clean_cmd = core.git_worktree_force_clean_cmd or function(worktree)
    local quoted = shell_single_quote(worktree)
    return "git worktree remove --force " .. quoted .. " 2>/dev/null; rm -rf " .. quoted .. "; git worktree prune"
  end
end

local function gh_issue_view_entity_command(repo, issue_number)
  return "gh issue view " .. tostring(issue_number)
    .. " --repo " .. tostring(repo)
    .. " --json"
end

local function gh_pr_view_entity_command(repo, pr_number)
  return "gh pr view " .. tostring(pr_number)
    .. " --repo " .. tostring(repo)
    .. " --json"
end

local function gh_entity_updated_at_command(repo, kind, number)
  local path_kind = kind == "pr" and "pulls" or "issues"
  return "gh api " .. "repos/" .. tostring(repo) .. "/" .. path_kind .. "/" .. tostring(number)
    .. " --jq .updated_at // .updatedAt // \"\""
end

function M.install(t, core)
  install_command_shim(t)
  install_legacy_command_renderers(core)
  core.gh_issue_view_entity_cmd = core.gh_issue_view_entity_cmd or gh_issue_view_entity_command
  core.gh_pr_view_entity_cmd = core.gh_pr_view_entity_cmd or gh_pr_view_entity_command
  core.gh_entity_updated_at_cmd = core.gh_entity_updated_at_cmd or gh_entity_updated_at_command
end

return M
