local C = {}
local support = require("devloop.commands.support")
local validators = require("devloop.commands.validators")

function C.gh_dashboard_issue_list(repo, label, timeout)
    local selected_label = validators.require_dashboard_label(label)
    return support.gh_result(function()
      return support.github().api_paginate_slurp(
        "repos/" .. tostring(repo) .. "/issues?state=open&labels=" .. selected_label:gsub(":", "%%3A") .. "&per_page=100",
        timeout
      )
    end)
  end

function C.gh_dashboard_issue_all_open(repo, timeout)
    return support.gh_result(function()
      return support.github().api_paginate_slurp("repos/" .. tostring(repo) .. "/issues?state=open&per_page=100", timeout)
    end)
  end

function C.gh_dashboard_issue_add_label(repo, issue_number, label, timeout)
    local selected_label = validators.require_dashboard_label(label)
    return support.gh_result(function()
      return support.github().api_method(
        "POST",
        "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number) .. "/labels",
        { "labels[]=" .. selected_label },
        nil,
        nil,
        timeout
      )
    end)
  end

function C.gh_dashboard_label_get(repo, label, timeout)
    local selected_label = validators.require_dashboard_label(label)
    return support.gh_result(function()
      return support.github().api_method("GET", "repos/" .. tostring(repo) .. "/labels/" .. selected_label:gsub(":", "%%3A"), nil, nil, nil, timeout)
    end)
  end

function C.gh_dashboard_label_create(repo, label, timeout)
    local selected_label = validators.require_dashboard_label(label)
    return support.gh_result(function()
      return support.github().api_method("POST", "repos/" .. tostring(repo) .. "/labels", {
        "name=" .. selected_label,
        "color=ededed",
        "description=fkst observability dashboard singleton",
      }, nil, nil, timeout)
    end)
  end

function C.gh_dashboard_issue_create(repo, input_file, timeout)
    return support.gh_result(function()
      return support.github().api_method("POST", "repos/" .. tostring(repo) .. "/issues", nil, input_file, nil, timeout)
    end)
  end

function C.gh_dashboard_issue_get(repo, issue_number, timeout)
    return support.gh_result(function()
      return support.github().api_method("GET", "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number), nil, nil, true, timeout)
    end)
  end

function C.gh_dashboard_issue_update(repo, issue_number, input_file, timeout)
    return support.gh_result(function()
      return support.github().api_method("PATCH", "repos/" .. tostring(repo) .. "/issues/" .. tostring(issue_number), nil, input_file, nil, timeout)
    end)
  end

return C
