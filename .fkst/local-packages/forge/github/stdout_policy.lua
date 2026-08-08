local M = {}

local valid_content_shapes = {
  issue_view = true,
  pr_view = true,
  issue_comments = true,
  pr_comments = true,
  issue_list = true,
  pr_list = true,
  reviews = true,
  graphql = true,
}

local valid_kinds = {
  content_json = true,
  trusted_metadata_json = true,
  plain_text = true,
  write_response = true,
  no_stdout = true,
}

function M.content_json(shape)
  if valid_content_shapes[tostring(shape or "")] ~= true then
    error("forge.github.stdout_policy: unknown content JSON shape")
  end
  return { kind = "content_json", shape = shape }
end

function M.trusted_metadata_json()
  return { kind = "trusted_metadata_json" }
end

function M.plain_text()
  return { kind = "plain_text" }
end

function M.write_response()
  return { kind = "write_response" }
end

function M.no_stdout()
  return { kind = "no_stdout" }
end

function M.validate(policy)
  if type(policy) ~= "table" or valid_kinds[policy.kind] ~= true then
    error("forge.github.stdout_policy: missing or unknown stdout policy")
  end
  if policy.kind == "content_json" and valid_content_shapes[tostring(policy.shape or "")] ~= true then
    error("forge.github.stdout_policy: unknown content JSON shape")
  end
  return policy
end

function M.is_content_json(policy)
  M.validate(policy)
  return policy.kind == "content_json"
end

return M
