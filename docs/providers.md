# Providers

`--provider` is detected: `local` for branch names; for PR numbers and `--all-open`, `azure-devops` with an Azure
DevOps `origin` and `github` with a GitHub one (`--all-open` also needs `gh`), else `local`.

- **local** treats each branch or ref as a PR. Local branches carry no policy data, so reports say that only structural
  mergeability was checked.
- **github** reads PRs through `gh` (title, body, labels, draft, review decision, reviews, CI rollup, branch protection
  and rulesets, merge queue) and fetches `refs/pull/N/head`. Linked work items are the issues the PR closes.
- **azure-devops** (alias `ado`) reads an on-prem Azure DevOps Server through the `ado` CLI. Detected from an `origin`
  like `https://tfs.company.local/tfs/Coll/Proj/_git/repo` or `dev.azure.com`.

## Azure DevOps Server

All REST work is delegated to [`ado`](https://github.com/mycaravam-crypto/ado-devops): server, project, PAT, API
version (`ADO_API_VERSION`), TLS and proxy come from its configuration (`ado auth login`, `ADO_*` variables), so
gitwizz never handles or logs a credential. Set `GITWIZZ_ADO` to use an `ado` executable that isn't on the `PATH`.

| Data | From |
|---|---|
| PRs, refs, source commit, description, author | `ado pr list --json --status active` |
| review state | reviewer votes: rejected or waiting for author → changes requested; approved → approved |
| checks | the newest build per definition on `refs/pull/N/merge` (`ado build list --json`); none without build access |
| completed PRs for `history export` | `ado pr context N`: status, target, commits and linked work items of each PR the history names |
| commit → work item links for `history export` | `ado commit context <full sha>` (`ado.commit-context/v1`): the work items the server links to each exported commit (`--commit-lookups`, default 500) |
| linked work items, acceptance criteria | `ado pr context N` names them, then `ado workitem show <id> --json` reads each in full (for `evaluate`, `trace` and `context`): `Microsoft.VSTS.Common.AcceptanceCriteria`, else an "Acceptance criteria" section in the description |

Other branch policies (minimum reviewer count, comment resolution, …) aren't visible through `ado`; reports say so.
The next step is `ado pr merge N --yes` (`--squash` for squash plans). `ado` errors keep their meaning: not
installed, not logged in (exit 3, with a hint), permission denied, not found.

## PR context as JSON

`gitwizz context 42 --system` returns the system context instead (`gitwizz.system-context/v1`): every selected fact,
document and linked test, unbounded by the AI budget, with the quality and its reasons.

`gitwizz context 42` returns one PR from any provider as one stable document (`gitwizz.context/v1`): the PR (title,
description, author, URL, draft, labels), source and target refs with commits, reviewers and votes, checks, policy
state (review decision, checks, problems, open dependencies), linked work items with their individual acceptance
criteria (GitHub: from the closed issues' "Acceptance criteria" section), changed files, changed C# members and
commits. Lists are sorted, so the same state gives the same document.

It works for merged and closed pull requests too: one the open list doesn't have is looked up on its own (`gh pr view
N`, `ado pr context N`), and `pr.state` says `open`, `merged` or `closed` (GitHub closed, Azure DevOps abandoned),
with `pr.closedAt` and `pr.mergeCommit`. A merged PR is measured against the target as it was when it was merged (Azure
DevOps' `lastMergeTargetCommit`, else the merge commit's first parent), not today's target, which already contains it:
its files, members and commits are what it brought, for merge, squash and rebase merges alike, and `target.sha` is
that commit. When its source commit is gone (the branch was deleted and the server no longer serves it), a merged PR is
read from its merge commit and `warnings` says so; without either, the lists are empty and `warnings` says why. Reviews,
votes and checks are their final state; work items are their current state, not the state at the merge.
`--system` works for a merged PR as well (docwizz on the merged state). The commands that merge-simulate or gate a PR
(`evaluate`, `explain`, `trace`, `evidence`, `guide <pr>`) keep needing an open one and say which state it is in.
