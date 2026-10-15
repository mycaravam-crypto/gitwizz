# Hybrid Version History (MVP)

The local, read-only `gitwizz version-history` command reports release snapshots from SemVer tags and active maintenance/release lines from local branches.

## Usage

```bash
gitwizz version-history list
gitwizz version-history export -f json -o versions.json
gitwizz version-history export --branch-pattern "release/*" --limit 500
gitwizz version-history list --repo /path/to/repository
```

Default output is text for `list` and JSON for `export`. Both support `--format text|json`. The branch pattern is a literal prefix followed by `*` (default `release/*`).

## Semantics

- Tags matching `vMAJOR.MINOR.PATCH` (optionally with a SemVer prerelease suffix) represent **released** snapshots. Lightweight and annotated tags both resolve to commits.
- Local `refs/heads/release/*` branches represent **unreleased** moving versions. The suffix is their version identifier and may identify a minor maintenance line.
- Each entry contains the HEAD/tag commit SHA, its committer date, and commits reachable from that SHA but not reachable from the selected baseline tag (exclusive baseline).
- The baseline is a reachable tag selected by topological traversal proximity, rather than comparing version numbers across diverged maintenance lines.
- The initial release without a reachable older tag includes history from the root, subject to the configured commit limit.
- Output ordering is descending SemVer for tags, followed by branch names in ordinal order.
- Incomplete ranges are flagged `truncated` or `shallow`; no absence of evidence is interpreted as an absence of changes.
- A tag's commit timestamp is **not** the publication timestamp of a GitHub release.
- The format `gitwizz.version-history/v1` is independent of `gitwizz.history/v2`. No GitHub API access or write operation is needed to generate it.

The MVP uses locally available refs: fetch tags/branches before running it if a clone lacks remote history. Deleted release branches cannot be recovered as branches; their tags still appear. This MVP does not yet perform PR/work-item enrichment, cherry-pick equivalence detection or Markdown release-note generation.

## Verification

```bash
dotnet test gitwizz.slnx --filter FullyQualifiedName~VersionHistoryTests
dotnet test gitwizz.slnx
```

Tests exercise tag intervals, a release branch diverging from main, missing refs, deterministic JSON and truncation.
