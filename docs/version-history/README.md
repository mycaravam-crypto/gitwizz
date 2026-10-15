# `gitwizz.version-history/v2` — the version history contract

`gitwizz version-history` lists every released version (SemVer tags) and every version still in progress (release
branches, local or only on a remote), each with the commits it adds to the releases it builds on; with `--target`, only
the history of that one branch. It writes the same history three ways:

| Format | For | Command |
|---|---|---|
| Markdown | a changelog page for people and for the project documentation | `gitwizz version-history export -o versions.md` |
| JSON (`gitwizz.version-history/v2`) | tools: docwizz, release notes, dashboards | `gitwizz version-history export -o versions.json` |
| text | a quick look in the terminal | `gitwizz version-history list` |

Read-only and offline: it reads local refs and commits and nothing else, so no provider, token or network is needed.
Fetch first (`git fetch --tags origin`) in a fresh or partial clone.

- [gitwizz.version-history.v2.schema.json](gitwizz.version-history.v2.schema.json): the JSON Schema (draft 2020-12)
- [example.json](example.json) and [example.md](example.md): the export of the scripted repository below, kept
  byte-for-byte by the tests (`VersionHistoryContractTests`)

## Usage

```bash
gitwizz version-history list                                       # text
gitwizz version-history export -o versions.json                    # JSON: the format follows the extension
gitwizz version-history export -o docs/architecture/versions.md    # Markdown changelog page
gitwizz version-history export -f markdown > CHANGELOG.md
gitwizz version-history export --target main -o docs/architecture/versions.md   # main's history: unreleased first
gitwizz version-history list --branch-pattern "maint/*" --limit 500
gitwizz version-history list --repo /path/to/repository
```

| Option | Default | Meaning |
|---|---|---|
| `-f, --format` | from the `-o` extension (`.md`/`.markdown`, `.json`, `.txt`), else `text` for `list` and `json` for `export` | `text`, `json` or `markdown` (`md`) |
| `-o, --output` | stdout | the file to write |
| `-t, --target` | none | the branch the history is about: its unreleased changes first, then only the releases it contains; no release branches |
| `--branch-pattern` | `release/*` | a literal prefix ending in `*`; the branch name after it is the version. With `--target` no release branches are listed |
| `--limit` | 200 (max 10000) | at most this many commits per version |
| `-r, --repo` | `.` | the repository |

## Which versions

- **Released**: tags `vMAJOR.MINOR.PATCH` or `MAJOR.MINOR.PATCH`, optionally with a SemVer prerelease
  (`v2.0.0-rc.1`), lightweight or annotated. Tags that don't parse, or that point at a tree or blob, are skipped.
  `v1.0.0` and `1.0.0` are one version: it is reported once, from the `v` tag, with a warning naming the other.
- **Unreleased**: branches whose name starts with the prefix (`release/1.0` → version `1.0`): local branches
  (`source: branch`) and branches only a remote has (`source: remote-branch`, `refs/remotes/origin/release/2.1`). A
  local branch wins over the same branch on a remote; among remotes, `origin` first, then by name.
- **Order**: unreleased versions first, newest first (`2.10` after `2.9`: numeric parts compare as numbers), then
  released versions by SemVer precedence, highest first (a prerelease sorts below its release; `rc.10` above `rc.2`).
  Equal precedence falls back to the name, so the order never depends on how git lists refs.

## A target branch

`--target main` writes the history of one branch, the usual changelog of a repository:

- **Unreleased first**: an entry for the branch itself (`version: main`, `source: target`, `status: unreleased`)
  with the commits it has since the releases it contains, the same bases rule as below.
- **Only its releases**: tags the branch contains. A patch released only on `release/1.0` (`v1.0.1`) is not a
  release of main until `release/1.0` is merged back; then it is.
- **Nothing it doesn't contain**: no release branches (they are versions in progress, not part of the branch yet) and
  no tags of other branches. A repository that keeps several apps on their own branches gets one history per app:
  `--target app-a` and `--target app-b` never list each other's tags or release branches. Tags made before an app's
  branch was cut from another one are part of its history too, since the branch contains them.
- **A release branch as the target** is its own history: its changes since the releases it contains, then those
  releases.
- The branch is the local one, else `origin/<name>` (a CI checkout often has no local branch), else any ref or commit
  git resolves (`HEAD`, a tag); an unknown name stops with "unknown branch or ref".
- The document names it in `target` (`name`, `reference`, `sha`); without `--target`, `target` is null and nothing
  else changes.

## Which commits: bases

A version's **bases** are the releases it builds on: the tagged commits it contains that no other tagged commit it
contains contains. Its **changes** are the commits none of its bases contain (`git rev-list <version> ^<base>...`),
newest first in topological order. So:

- **Diverged maintenance lines**: `v1.1.0` on main builds on `v1.0.0`, even when `v1.0.1` exists on `release/1.0`.
  The highest version number is not the base.
- **Merge-backs**: after `release/1.0` (`v1.0.1`) is merged into main, `v2.0.0` builds on both `v1.1.0` and
  `v1.0.1`. The backport is listed once, under `v1.0.1`, not again under `v2.0.0`.
- **Prereleases**: a release (no prerelease part) builds on releases only, so `v2.0.0` lists everything since the
  1.x releases, including what `v2.0.0-rc.1` already had. A prerelease builds on any tag.
- **Same commit**: a tag never builds on tags on its own commit. A release branch counts every tag, its own head's
  too: a branch whose head is tagged has no unreleased changes.
- **First release**: no bases, so it lists the history up to `--limit`.

## Changes and their categories

Each commit is classified by its [Conventional Commits](https://www.conventionalcommits.org) header
`type(scope)!: description`:

| `category` | From |
|---|---|
| `feature` | `feat` |
| `fix` | `fix` |
| `performance` | `perf` |
| `other` | any other type (`docs`, `chore`, `refactor`, ...) or a subject that isn't a header |
| `merge` | a commit with more than one parent |

`breaking` is true for a `!` before the colon or a `BREAKING CHANGE:` (or `BREAKING-CHANGE:`) footer. `type` is
the header's type in lower case, `scope` its scope, `description` the text after the colon; without a header, `type`
and `scope` are null and `description` is the subject.

## The Markdown page

One `##` section per version: `## 2.0.0 (2026-09-01)` for a release (the annotated tag's date, else the commit's), `##
1.0 (unreleased)` for a branch. Below it the reference, the commit, the bases and the number of changes, then the
changes in this order: **⚠ Breaking changes**, **Features**, **Fixes**, **Performance**, **Other changes**. Merge
commits are counted, not listed. Features, fixes and performance show `**scope:** description`; other changes keep
their whole subject (`docs: ...`). When the repository's `origin` is an http(s) URL, commits link to
`<origin without .git>/commit/<sha>` (GitHub, Azure DevOps and GitLab all resolve it). Commit text is escaped, so a
message can't add emphasis, links, HTML, tables, headings or nested lists. The page carries no generation time, so it
only changes when the history does: commit it, and regenerate it in CI. See [example.md](example.md).

### In the project documentation (docwizz)

docwizz regenerates `docs/*.md` and leaves `docs/architecture/` to you, so write the page there and link it from your
architecture pages, or write `CHANGELOG.md` at the repository root:

```bash
git fetch --tags origin
gitwizz version-history export -o docs/architecture/versions.md
gitwizz version-history export -o docs/architecture/versions.json   # for tools, and for a native docwizz import
```

The JSON is the contract a native import (like `docwizz load import` for powerwizz reports) would read: it carries
everything the page shows, with SHAs, dates and categories as data.

## Fields

| Field | What it says |
|---|---|
| `schema` | `gitwizz.version-history/v2` |
| `repository` | `origin` URL without user, password, query or fragment; `null` without an origin |
| `branchPattern` | the `--branch-pattern` used (`release/*`) |
| `target` | `name`, `reference`, `sha` of the `--target` branch; `null` without one |
| `shallow` | the clone is shallow: commits beyond its boundary are missing |
| `versions[]` | one entry per version, in the order above |
| `versions[].version` | the tag without a leading `v`, or the branch name after the prefix |
| `versions[].status` | `released` (tag) or `unreleased` (branch) |
| `versions[].source` | `tag`, `branch`, `remote-branch`, or `target` for the `--target` branch's own entry; with `--target` only `target` and `tag` |
| `versions[].reference` | the full ref: `refs/tags/v2.0.0`, `refs/heads/release/1.0`, `refs/remotes/origin/release/2.1`; a `--target` given as another ref or commit (`HEAD`) as given |
| `versions[].commitSha`, `committedAt` | the commit the ref points at, and its committer date |
| `versions[].taggedAt` | an annotated tag's tagger date; `null` for lightweight tags and branches |
| `versions[].bases[]` | `version`, `reference`, `sha` of each base, highest version first; empty for a first release |
| `versions[].changes[]` | `sha`, `subject`, `committedAt`, `kind` (`commit`/`merge`), `category`, `type`, `scope`, `description`, `breaking` |
| `versions[].truncated` | more than `--limit` commits: `changes` is incomplete |
| `versions[].warnings`, `warnings` | what limits the version or the whole export (truncation, shallow clone, duplicate tags) |

Dates are ISO 8601 with offset (`2026-09-01T20:00:00+00:00`). Nothing is left out because it is unknown: a list is
empty only when there is nothing in it, and `truncated` or `shallow` say when the history is incomplete.

## Compatibility

The schema version changes when a field is removed, renamed, retyped or changes meaning; adding an optional field
does not: `target`, and `target` as a `source`, came that way, and documents without them stay valid. v2 replaced v1 (`baseVersion`/`baseSha`: a single base, so merged-back backports were listed twice; local
branches only; no categories) and is not read as v1.

## Limits

- Local refs only: what isn't fetched isn't there. Deleted release branches can't be recovered; their tags remain.
- A tag's dates are git's. When a GitHub or Azure DevOps release was published is not read.
- Pull requests and work items aren't attached; `gitwizz history export` maps commits to them.
- Cherry-picks are different commits: a fix cherry-picked to `release/1.0` and also on main is listed in both lines.

## Verification

```bash
dotnet test gitwizz.slnx --filter "FullyQualifiedName~VersionHistory"
```

`VersionHistoryTests` covers tag ranges, diverged and merged-back maintenance lines, prereleases, tagged branch
heads, remote-only branches, annotated and same-named tags, duplicate versions, classification, the Markdown page,
the text output, the CLI and truncation, and validates every export against the schema. `VersionHistoryContractTests`
rebuilds the example repository (fixed identity and clock) and compares the export with `example.json` and
`example.md`; `GITWIZZ_UPDATE_GOLDEN=1` rewrites them.
