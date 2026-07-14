return [=[
return all({
  require_reached("pr-open", {
    domain = "github-devloop-pr",
    lineage = {
      proposal_id = true,
      issue_number = true,
      impl_version = true,
      branch = true,
      base_branch = true,
    },
  }),
})
]=]
