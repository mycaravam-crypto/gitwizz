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
