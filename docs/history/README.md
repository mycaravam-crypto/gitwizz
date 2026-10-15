# `gitwizz.history/v2` — the history export contract

`gitwizz history export` writes one JSON document: which commits changed which lines, which pull request each came
in with, and which work items are linked to them, each with where that came from and how certain it is. This page is
the contract for that document. gitwizz is its producer and owns it; consumers (DocWizz's
`requirements import`, scripts) read the document as specified here and don't rename or reinterpret its fields.

| File | What it is |
|---|---|
| [`gitwizz.history.v2.schema.json`](gitwizz.history.v2.schema.json) | JSON Schema (draft 2020-12) for every document gitwizz writes |
| [`example.json`](example.json) | a complete export of a scripted repository: a merge PR, a squash PR, a rename, a binary file, a zero-line deletion, a commit two PRs merge, and verified, unavailable and denied provider answers |

Both are checked by `tests/Gitwizz.Tests/HistoryContractTests.cs`: the example is regenerated from the scripted
repository on every test run and must match byte for byte, and every export (all providers, truncated, shallow) must
validate against the schema. After an intended change, run the tests with `GITWIZZ_UPDATE_GOLDEN=1` and review the diff
of `example.json`.

## Versioning and compatibility

- The `schema` field names the contract: `gitwizz.history/v2`. A consumer reads only the versions it knows and fails
  with an explicit error on any other value. It never guesses the meaning of an unknown version.
- **Within v2**, gitwizz may add properties and add values to the open-ended text fields (`problem`, `evidence`,
  `sources.*`, `warnings`). Consumers ignore properties they don't know. The schema file is updated with the producer,
  so it stays strict (`additionalProperties: false`) about what gitwizz really writes.
- **A breaking change** bumps the version: removing or renaming a field, changing a field's type or meaning, or adding
  a value to one of the enumerations below (`status`, `resolution`, `kind`, ...). An unknown enumeration value would
  otherwise be misread, for example as "verified".
- gitwizz writes one version at a time. Consumers that also read older versions convert them explicitly.

### Migration from `gitwizz.history/v1`

v1 was never a stable contract between gitwizz and DocWizz: DocWizz's importer read a different document shape under
the same schema id (`changes[].hunks[]`, `pullRequests[]` per commit, `workItems: {status, items}` per commit,
`range.headSha`). v2 is the one shape both sides use. Compared with what gitwizz wrote as v1:

| v1 | v2 |
|---|---|
| no provider links per commit; `sources.providerCommitLinks` said `unavailable` | `commits[].providerLinks` `{status, items, problem}`: what the provider links to the commit itself |
| `pullRequests[].workItemsStatus`: `verified`, `unavailable`, `unknown` | adds `denied` (the provider refused access) |
| `range.truncated` | `range.truncated` and `range.shallow` |
| local and GitHub `sources.providerCommitLinks` started with `unavailable:` | they start with `unknown:` (not asked), as `providerLinks.status` does |
| links: `workItem`, `source`, `kind`, `evidence` | adds `uri` (where the provider's link can be checked) when the provider names one, and kind `commit-link` |

Everything else (files, ranges, message links, pull request attribution) has the same fields and meaning.

## The document

```text
schema, provider, repository
range        { since, sinceSha, until, untilSha, limit, commits, truncated, shallow }
sources      { commitMessages, providerCommitLinks, pullRequests, pullRequestWorkItems }
commits[]    { sha, parents[], subject, committedAt, kind, files[], workItems[], providerLinks, pullRequest }
pullRequests[] { id, title, url, target, status, strategy, mergeCommit, commits[], workItems[], workItemsStatus, problem }
warnings[]
```

Every property listed is always present. A value that isn't known is `null`, never left out. The only exception is
`uri` on a link, which is present only when the provider named one.

### Top level

| Field | Meaning |
|---|---|
| `provider` | `local` (git only), `github` (through `gh`) or `azure-devops` (through `ado`) |
| `repository` | the `origin` URL without user, password, query or fragment; `null` without an origin |
| `range.since` / `range.until` | the refs as given; `sinceSha` / `untilSha` are the commits they resolved to (`sinceSha` is `null` without `--since`) |
| `range.limit` / `range.commits` | the commit limit and the number of commits exported |
| `range.truncated` | `true` when the range held more than `limit` commits: the oldest are missing |
| `range.shallow` | `true` when the clone is shallow: commits beyond its boundary are missing, and a merge's merged-branch commits may be too |
| `sources` | in words, what each kind of evidence was read from and how complete it is |
| `warnings` | everything that limits the evidence, in words (truncation, shallow clone, unconfirmed PRs, failed lookups) |

### `commits[]`

Newest first in `git rev-list --topo-order` order, at most `limit`.

| Field | Meaning |
|---|---|
| `sha`, `parents` | full commit ids; `parents` in git's order (the first parent is the mainline of a merge) |
| `subject`, `committedAt` | the first line of the message; the committer date in one form whatever the git version: `yyyy-MM-ddTHH:mm:ss+HH:mm`, UTC as `+00:00`, never `Z` |
| `kind` | `commit`, or `merge` (two or more parents). A merge has no `files`: its changes are its parents'. Changes made while resolving a merge conflict are therefore not attributed |
| `files[]` | what changed against the first parent (`git diff -U0 -M -C`), in git's diff order |
| `workItems[]` | work items the commit's **own message names**, `source: commit-message`. These are mentions: nobody verified them, and the provider may link other items or none |
| `providerLinks` | work items the **provider links to the commit**, see below |
| `pullRequest` | which pull request the commit came in with, see below |

#### `files[]`

| Field | Meaning |
|---|---|
| `path` | the path after the commit |
| `oldPath` | the path before, for `renamed` and `copied`; otherwise `null` |
| `status` | `added`, `modified`, `deleted`, `renamed` or `copied` |
| `similarity` | git's similarity index (0–100) for renames and copies; otherwise `null` |
| `granularity` | `lines`: `ranges` say which lines changed. `file`: only the file is known to have changed (binary, a rename or copy without content change, a mode change). `ranges` is then empty, and a consumer **must not** map the change to a symbol or method body |
| `ranges[]` | `{oldStart, oldCount, newStart, newCount}` from the `@@` hunk headers. A pure deletion has `newCount: 0` with `newStart` the line before the gap. A pure insertion has `oldCount: 0` with `oldStart` the line before. An added file has `oldStart: 0, oldCount: 0`, a deleted one `newStart: 0, newCount: 0` |

#### Links: `workItems[]` and `providerLinks.items[]`

| Field | Meaning |
|---|---|
| `workItem` | `AB#123` (Azure Boards) or `#123` (a GitHub issue) |
| `source` | `commit-message` (the message names it) or `provider` (the provider keeps the link) |
| `kind` | from a message: `ab` (`AB#123`), `closes` (a GitHub closing keyword such as `fixes #12`), `mention` (a plain `#123`; with Azure DevOps it becomes `AB#123`, with GitHub and local histories it is dropped, because it may name a PR). From the provider: `commit-link` (linked to the commit itself) or `pr-link` (linked to the pull request) |
| `evidence` | the matched text, or which provider answer it came from (`ado commit context: commit-workitem`, `ado pr context`, `closingIssuesReferences`) |
| `uri` | where the link can be checked (the work item's web URL), when the provider names one |

A link appears once per `workItem` and `kind`, sorted by `workItem` (ordinal), then `kind`.

#### `providerLinks`

| `status` | `items` | Meaning |
|---|---|---|
| `verified` | a list, possibly empty | the provider answered for this exact commit (full SHA) and repository. The list is complete: `[]` means "verified: no linked work items" |
| `unavailable` | `null` | the answer couldn't be read: lookup failed or timed out, the commit isn't on the server, the server didn't report links, or the answer was for another commit or repository. `problem` says which |
| `denied` | `null` | the provider refused access. `problem` says so |
| `unknown` | `null` | not asked: the provider keeps no commit links (`local`, `github`), or the commit is beyond `--commit-lookups`. `problem` says which |

Only `verified` is evidence that a link exists or doesn't. `unavailable`, `denied` and `unknown` must never be read as
"no links", and a message mention is never upgraded to a provider link (the schema rejects both).

Azure DevOps answers come from `ado commit context <full sha>` (`ado.commit-context/v1`), at most `--commit-lookups`
commits (default 500, newest first), 4 at a time, one minute each. When `ado` is missing or not logged in, the
remaining commits are `unavailable` with that reason, without asking again. The answers are never cached, so links
changed on the server show up in the next export.

GitHub closing-issue links retain the issue URL in `workItems[].uri`. The repository in that URL is part of the
identity: `#123` in two repositories must not be treated as the same issue. This is additive within
`gitwizz.history/v2`; links without a URL (including existing ADO exports) retain their previous representation.
GitHub export requires a `gh` version exposing `gh pr view --json closingIssuesReferences` (validated with 2.102.0).
Older versions report unavailable PR metadata rather than a verified empty list. Issue detail exports are a separate
step, for example `gh issue view 123 --repo owner/repo --json number,url,title,body,labels,state,updatedAt`.

#### `pullRequest` (per commit)

| `resolution` | `id` | Meaning |
|---|---|---|
| `exact` | the PR | the provider confirms it: it is the PR's merge commit, or a commit of a branch whose merge the provider confirms |
| `inferred` | the PR | only the commit messages say so (the provider couldn't confirm it) |
| `ambiguous` | `null` | more than one PR merged the commit; `candidates` lists them (sorted) |
| `unknown` | `null` | nothing says which PR: a rebase merge, a custom merge message or a direct push |

`basis` lists the evidence used: `merge-message` (`Merge pull request #12 …`, `Merged PR 12: …`), `squash-subject`
(`… (#12)` on the first-parent line), `merged-branch` (the commit is in a merged branch), `provider` (confirmed).

### `pullRequests[]`

Each completed pull request the range names, sorted by number.

| Field | Meaning |
|---|---|
| `status` | `confirmed` (the provider says it is completed and agrees on the merge commit), `unconfirmed` (it couldn't be asked, doesn't know it, or disagrees: `problem` says why), `local` (no provider) |
| `strategy` | `merge` (a merge commit) or `squash` (one commit with a PR marker). Rebase merges aren't found: their commits are `unknown` |
| `mergeCommit`, `commits` | the commit that completed it, and the commits it owns (the merge commit and what its merge brought in, newest first) |
| `workItems[]`, `workItemsStatus` | the work items the provider links to the **pull request**: `verified` (the list is complete, `[]` is a verified "none"), `unavailable`, `denied` (with `problem`), or `unknown` (no provider). The list is empty unless verified |

A PR's work items are **candidates** for the PR's commits. They are not links of any one commit, and they don't show
that a changed method implements them or that a test covers them. Commit links (`providerLinks`) and PR links are
kept apart so a consumer can weigh them separately.

## Determinism

The same commits and the same provider answers give the same document byte for byte. Lists are sorted as described
above, there are no timestamps of the export itself, and parallel lookups don't change the order. The git part is
cached in the workspace under its inputs (gitwizz build, schema, both commit ids, limit, shallow); provider answers are
never cached.

## Security

The export is read-only. It contains no credentials: the repository URL drops user info, query and fragment, and
error messages taken from tools have user info removed from any URL. Author and committer names and e-mail addresses
aren't exported. gitwizz never sees the Azure DevOps PAT (it stays in `ado`'s configuration).

## Importing into DocWizz

```bash
gitwizz history export --since v1.4.0 --provider azure-devops -o history.json
docwizz requirements import . history.json work-items/
```

This needs a DocWizz with native `gitwizz.history/v2` import
([docwizz#172](https://github.com/mycaravam-crypto/docwizz/issues/172)). Older DocWizz versions stop with
`history: unknown schema gitwizz.history/v2; expected gitwizz.history/v1`, which is intended: they read a different
document shape and would misread this one.

Smoke test for a consumer, without a server:

```bash
dotnet test --filter HistoryContractTests     # in gitwizz: regenerates and checks docs/history/example.json
docwizz requirements import . docs/history/example.json work-items/
```

Then check what the importer made of the example: the linked work item `AB#4712` of the squash commit is the only
confirmed commit link; the `AB#100` and `AB#12` mentions are not confirmed; the denied and unavailable commits have no
links, not empty ones; and `AB#4711` is a candidate for PR 7's commits only.
