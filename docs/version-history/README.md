# Hybrid Version History (MVP)

The local, read-only `gitwizz version-history` command reports release snapshots from SemVer tags and active maintenance/release lines from local branches.

## Usage

```bash
gitwizz version-history list
gitwizz version-history export -f json -o versions.json
gitwizz version-history export --branch-pattern "release/*" --limit 500
gitwizz version-history list --repo /path/to/repository
```

Default output is text for `list` and JSON for `export`. Both support `--format text|json`. The branch pattern is a literal prefix followed by `*` (default `release/*`); the part of the branch name after the prefix is its version.

## Semantics

- Tags matching `vMAJOR.MINOR.PATCH` (optionally with a SemVer prerelease suffix) represent **released** snapshots. Lightweight and annotated tags both resolve to commits.
- Local `refs/heads/release/*` branches represent **unreleased** moving versions. The suffix is their version identifier and may identify a minor maintenance line.
- Each entry contains the HEAD/tag commit SHA, its committer date, and commits reachable from that SHA but not reachable from the selected baseline tag (exclusive baseline).
- The baseline is the first tagged commit met walking back from the entry in `git rev-list --topo-order` order (the highest version wins when one commit carries several tags), rather than the highest version number: maintenance lines diverge, so `v1.1.0` on main is measured from `v1.0.0` even when `v1.0.1` exists on `release/1.0`. A tag never uses tags on its own commit as its baseline; a release branch whose head is tagged has no unreleased changes.
- The initial release without a reachable older tag includes history from the root, subject to the configured commit limit.
- Output ordering is descending SemVer precedence for tags (a prerelease sorts below its release, numeric identifiers compare as numbers; equal precedence falls back to the tag name), followed by branch names in ordinal order. Tags that do not point at a commit are skipped.
- Incomplete ranges are flagged `truncated` or `shallow`; no absence of evidence is interpreted as an absence of changes.
- A tag's commit timestamp is **not** the publication timestamp of a GitHub release.
- The format `gitwizz.version-history/v1` is independent of `gitwizz.history/v2`. No GitHub API access or write operation is needed to generate it.

The MVP uses locally available refs: fetch tags/branches before running it if a clone lacks remote history. Deleted release branches cannot be recovered as branches; their tags still appear. This MVP does not yet perform PR/work-item enrichment, cherry-pick equivalence detection or Markdown release-note generation.

## Verification

```bash
dotnet test gitwizz.slnx --filter FullyQualifiedName~VersionHistoryTests
dotnet test gitwizz.slnx
```

Tests exercise tag intervals, diverged maintenance lines, a tagged branch head, annotated tags shadowed by a same-named branch, prerelease ordering, text output, the CLI options, missing refs, deterministic JSON and truncation.
