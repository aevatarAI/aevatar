local C = {}
local support = require("devloop.commands.support")
local validators = require("devloop.commands.validators")

function C.gh_repo_labels_list(repo, timeout)
  return support.gh_result(function()
    return support.github().api_paginate_slurp("repos/" .. tostring(repo) .. "/labels?per_page=100", timeout)
  end)
end

function C.gh_repo_label_create(repo, name, color, description, timeout)
  return support.gh_result(function()
    return support.github().label_rest_create(
      repo,
      validators.require_label_name(name),
      validators.require_label_color(color),
      description,
      timeout
    )
  end)
end

function C.gh_repo_label_update(repo, name, color, description, timeout)
  return support.gh_result(function()
    return support.github().label_rest_update(
      repo,
      validators.require_label_name(name),
      validators.require_label_color(color),
      description,
      timeout
    )
  end)
end

return C
