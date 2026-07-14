local core = require("core")
local saga = require("workflow.saga")

local spec = {
  consumes = { "github_pr_comment_request" },
  published_seam = { "github_pr_comment_request" },
  produces = { "github_comment_written" },
  published_seam = { "github_pr_comment_request" },
  stall_window = "30s",
}

local function done(_event)
  return false
end

local function has_required_fields(payload)
  return payload.pr_number ~= nil and payload.body ~= nil and payload.dedup_key ~= nil
end

local function log_outbound(payload, repo, write_env)
  if repo == nil or repo == "" or not has_required_fields(payload) then
    return
  end

  local mode = write_env == "1" and "real" or "dry-run"
  local fields = {
    "mode=" .. mode,
    "repo=" .. tostring(repo),
    "pr=" .. tostring(payload.pr_number),
    "dedup_key=" .. tostring(payload.dedup_key),
  }
  if mode == "dry-run" then
    table.insert(fields, "reason=FKST_GITHUB_WRITE!=1")
  end
  core.log_line("info", "github_pr_comment", "OUTBOUND", fields)
end

local function act(event)
  local payload = event.payload or {}
  local written, repo = core.write_with_outbound_log(payload, {
    kind = "pr",
    number = payload.pr_number,
    number_field = "pr_number",
    view_comments = function(github, repo, number, timeout)
      return github.pr_comments(repo, number, timeout)
    end,
    comment_create = function(github, repo, number, body_file, timeout)
      return github.pr_comment_create(repo, number, body_file, timeout)
    end,
    view_label = "GitHub PR REST comments",
    comment_label = "GitHub PR comment",
  }, log_outbound)
  if written ~= nil and written.id ~= nil then
    raise("github_comment_written", {
      schema = "github-proxy.comment-written.v1",
      repo = repo,
      target = "pr",
      pr_number = payload.pr_number,
      issue_number = payload.issue_number,
      comment_id = written.id,
      dedup_key = tostring(payload.dedup_key) .. "/written/" .. tostring(written.id),
      request_dedup_key = payload.dedup_key,
      handoff = payload.handoff,
      source_ref = payload.source_ref,
    })
  end
end

return saga.department(spec, {
  done = done,
  act = act,
  wrap = core.wrap_pipeline_failure,
  name = "github_pr_comment",
})
