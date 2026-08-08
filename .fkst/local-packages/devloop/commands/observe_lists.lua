local S = {}
local C = {}
local support = require("devloop.commands.support")

local observe_list_timeout = 10

function S.bounded_page_number(page)
  if page == nil then
    return nil
  end
  local n = tonumber(page)
  if n == nil or n ~= math.floor(n) or n < 1 then
    error("github-devloop: invalid list page number")
  end
  return n
end

function S.observe_list_page_key(page)
  local selected_page = S.bounded_page_number(page)
  if selected_page == nil then
    return "paginate"
  end
  return tostring(selected_page)
end

function S.read_coalesce_key_segment(value, fallback)
  local text = tostring(value or "")
  if text == "" then
    return fallback or "all"
  end
  local segment = text:gsub("[^A-Za-z0-9%.%-]", function(char)
    return string.format("_%02X", string.byte(char))
  end)
  return "v-" .. segment
end

function S.observe_list_repo_key(repo)
  local owner, name = tostring(repo or ""):match("^([^/]+)/([^/]+)$")
  if owner ~= nil and name ~= nil then
    return S.read_coalesce_key_segment(owner, "owner") .. "/" .. S.read_coalesce_key_segment(name, "repo")
  end
  return S.read_coalesce_key_segment(repo, "repo")
end

function S.observe_list_label_key(label)
  if label == nil or tostring(label) == "" then
    return "all"
  end
  return S.read_coalesce_key_segment(label, "label")
end

function S.observe_list_read_coalesce(key)
  return {
    key = key,
    ttl_seconds = 30,
  }
end

  function C.gh_issue_list_observe(repo, label, page, include_headers, timeout)
    return support.gh_result(function()
      return support.github().issue_list_observe(repo, label, S.bounded_page_number(page), include_headers, timeout or observe_list_timeout)
    end)
  end

  function C.gh_issue_list_observe_read_coalesce(repo, label, page)
    return S.observe_list_read_coalesce(table.concat({
      "github-devloop",
      "observe-list",
      S.observe_list_repo_key(repo),
      "issues",
      "label",
      S.observe_list_label_key(label),
      "page",
      S.observe_list_page_key(page),
    }, "/"))
  end

  function C.gh_issue_list_observe_opts(repo, label, page, include_headers)
    return {
      run = function(timeout)
        return C.gh_issue_list_observe(repo, label, page, include_headers, timeout)
      end,
      timeout = observe_list_timeout,
      read_coalesce = C.gh_issue_list_observe_read_coalesce(repo, label, page),
    }
  end

  function C.gh_pr_list_observe(repo, page, include_headers, timeout)
    return support.gh_result(function()
      return support.github().pr_list_observe(repo, S.bounded_page_number(page), include_headers, timeout or observe_list_timeout)
    end)
  end

  function C.gh_pr_list_observe_read_coalesce(repo, page)
    return S.observe_list_read_coalesce(table.concat({
      "github-devloop",
      "observe-list",
      S.observe_list_repo_key(repo),
      "prs",
      "page",
      S.observe_list_page_key(page),
    }, "/"))
  end

  function C.gh_pr_list_observe_opts(repo, page, include_headers)
    return {
      run = function(timeout)
        return C.gh_pr_list_observe(repo, page, include_headers, timeout)
      end,
      timeout = observe_list_timeout,
      read_coalesce = C.gh_pr_list_observe_read_coalesce(repo, page),
    }
  end

function S.install(M)
  for _, n in ipairs({"gh_issue_list_observe", "gh_issue_list_observe_opts", "gh_issue_list_observe_read_coalesce", "gh_pr_list_observe", "gh_pr_list_observe_opts", "gh_pr_list_observe_read_coalesce"}) do M[n] = C[n] end
end
C.install = S.install

for k, v in pairs(S) do if C[k] == nil then C[k] = v end end
return C
