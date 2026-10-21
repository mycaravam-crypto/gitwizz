# File inspection (first increment)

`gitwizz inspect <path>` shows a read-only history for a tracked file, or a\nlast-change-first overview of all tracked files recursively under a directory at the selected revision.

```bash
gitwizz inspect src/Gitwizz/History.cs
gitwizz inspect src/Gitwizz/History.cs --since v1.0.0 --until HEAD --limit 500
gitwizz inspect src/Gitwizz/History.cs -f json -o inspect.json
```

The report includes author, author date, committer date in JSON, SHA, subject,
change type, diff hunk headers and commit-message references to PRs and work items.
References are **unverified**; the tool does not currently retrieve PR details,
review discussion or verified work item links. A commit's subject is not necessarily
a reliable explanation of the developer's intention.

`--follow` attempts to track renames. History is bounded (200 commits by default,
up to 10,000). Shallow repositories and truncation are indicated. This is not
lineage-aware line-by-line superblame yet: `--line`, `--lines`, HTML output and
provider-enriched reasoning remain follow-up increments.

The operation runs read-only git commands and does not modify the index or working tree.
The output uses `gitwizz.inspect/v1`, independent of `gitwizz.history/v2`.
\n## Directory overview\n\nA directory argument (including `.` for the repository root) lists tracked files\nrecursively, newest touching commit first, with path, committer date, author,\ncommit SHA, subject and unverified PR/work-item mentions. `--limit` bounds\nthe returned rows; `--since` excludes files with no changes in the range.\nUse a file path to drill down to all change hunks. This overview is based on\ntracked files at `--until`, not uncommitted or untracked working-tree content.\n
## Terminal display

In an interactive terminal, `gitwizz inspect` now uses a bordered table for directories and per-commit cards for files. The directory view shows the last commit date, file path, change subject, author, abbreviated SHA, and unverified PR/work-item mentions. The file view shows authored/committed dates, change status, references and changed hunk headers. `-f text` forces plain text; redirected output defaults to plain text. `-f json` and `-o *.json` preserve the existing JSON contracts. The extra visual formatting is presentation-only and does not verify why a change was made.

## Pull request and work-item titles

For GitHub repositories, directory inspection optionally calls `gh pr view` for PR numbers inferred from commit messages and shows the fetched PR title and issues from its `closingIssuesReferences`. The commit-to-PR association is still inferred from the commit subject, **not independently verified**. PR title and closing-issue metadata are provider-sourced when available. When `gh` is unavailable, inaccessible or unauthenticated, the report retains its local commit evidence and adds a warning. Multiple files referencing the same PR share one lookup per invocation. Non-GitHub providers currently show commit-message references only. The local file-history output and its JSON schema are unchanged.

When no PR number is found in the commit subject, GitHub inspection now queries the GitHub commit-to-pull-request API for that commit SHA. Only a unique merged PR is used. Missing or ambiguous associations remain unassigned; PR titles and linked closing issues are still loaded through `gh pr view`. This requires a working authenticated GitHub CLI; the local history remains usable without it.

## Azure DevOps Server evidence

For Azure DevOps Server remotes (`/_git/`) or `--provider azure-devops`, directory inspection uses the existing `ado-devops` CLI (`GITWIZZ_ADO` can override its location):

- `ado commit context <sha>` supplies provider-verified commit/work-item links, including work-item titles via `ado workitem show`.
- `ado pr list --status completed --json --limit 500` associates an exact merge commit or last merge-source commit with a unique completed PR when available.
- `ado pr context <id>` confirms PR information and its linked work items. A PR title/PR-linked work item is shown only if that PR context names the inspected commit or merge commit.

Not every commit is attributable to a PR. In particular, rebase merges, older PRs outside the 500-item listing, and commits whose merge metadata is incomplete may have no confirmed PR even when work-item evidence exists. Missing ADO credentials or permissions are reported as warnings; no association is fabricated. Run `ado auth status` to verify access.

In the interactive directory table, consecutive files with the same last-change minute form a visual group: the German-formatted date (`dd.MM.yyyy HH:mm`) is shown only on its first row and a blank separator appears between different minutes. This changes presentation only; text and JSON output retain their existing contracts.


## Progress feedback

Directory inspection uses GitWizz's standard progress bars on interactive terminals for 20 or more files and for 5 or more PR lookups. Redirected output remains free of progress markup.
