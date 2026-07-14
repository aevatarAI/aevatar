local S = {}

function S.install(M)
local sagas = {
  ["fork-and-block"] = {
    id = "fork-and-block",
    description = "Create a self-owned fork issue, then make the original issue blocked by that fork.",
    steps = {
      {
        id = "create-fork",
        effect = "github.issue.create",
        request_queue = "github_issue_create_request",
        post_conditions = {
          {
            id = "fork-created-parent-ledger",
            kind = "trusted-comment-marker",
            durable_source = "parent_issue_comments",
            required_body_fragments = {
              "fkst:github-proxy:issue-created:v1",
              'dedup="',
              'issue="',
            },
            issue_number_attr = "issue",
          },
        },
      },
      {
        id = "block-original",
        effect = "github.issue.blocked_by",
        request_queue = "github_issue_blocked_by_request",
        post_conditions = {
          {
            id = "peer-blocked-by-edge",
            kind = "github-add-blocked-by-edge",
            blocked_field = "issueId",
            blocking_field = "blockingIssueId",
            forbidden_fields = { "blockedIssueId" },
            verification_source = "blockedBy",
          },
          {
            id = "blocked-by-marker-visible",
            kind = "trusted-comment-marker",
            durable_source = "blocked_issue_comments",
            required_body_fragments = {
              "fkst:github-proxy:blocked-by:v1",
              'dedup="',
              'blocked="',
              'blocking="',
            },
          },
        },
      },
    },
  },
  ["issue-create-parent-ledger"] = {
    id = "issue-create-parent-ledger",
    description = "Record issue-create intent and created issue facts on the parent entity.",
    steps = {
      {
        id = "record-create-intent",
        effect = "github.parent.comment",
        request_queue = "github_issue_create_request",
        post_conditions = {
          {
            id = "parent-intent-marker-visible",
            kind = "trusted-comment-marker",
            durable_source = "parent_comments",
            required_body_fragments = {
              "fkst:github-proxy:issue-create-intent:v1",
              'dedup="',
            },
          },
        },
      },
      {
        id = "record-created-issue",
        effect = "github.parent.comment",
        request_queue = "github_issue_create_request",
        post_conditions = {
          {
            id = "parent-created-marker-visible",
            kind = "trusted-comment-marker",
            durable_source = "parent_comments",
            required_body_fragments = {
              "fkst:github-proxy:issue-created:v1",
              'dedup="',
              'issue="',
            },
            issue_number_attr = "issue",
          },
        },
      },
    },
  },
}

local function copy_table(value)
  local out = {}
  for key, item in pairs(value or {}) do
    if type(item) == "table" then
      out[key] = copy_table(item)
    else
      out[key] = item
    end
  end
  return out
end

function M.external_effect_saga(id)
  local saga = sagas[tostring(id or "")]
  if saga == nil then
    return nil
  end
  return copy_table(saga)
end

end

return S
