# gitwizz documentation

The [README](../README.md) covers installation and the command overview. These pages are the reference.

| Page | Covers |
|---|---|
| [install.md](install.md) | requirements, Windows (PowerShell, cmd) and a single-file binary |
| [workflow.md](workflow.md) | `guide`, `doctor`, the repository workspace (`status`, `--refresh`, `--clear-cache`) |
| [gates.md](gates.md) | `evaluate` (and `--explain`), gates and policy as code, CI, `trace`, per-PR test environments |
| [system-context.md](system-context.md) | what gitwizz reads from docwizz, context quality, requirement evidence |
| [ai-review.md](ai-review.md) | the `ai-review` gate, the evidence package, the benchmark that lets it block |
| [planning.md](planning.md) | `plan`: options, configuration, how the search works, performance |
| [providers.md](providers.md) | GitHub, Azure DevOps Server and local branches; `context` as JSON |
| [history/](history/README.md) | `history` and the `gitwizz.history/v2` contract |
| [version-history/](version-history/README.md) | `version-history` and the `gitwizz.version-history/v2` contract |

## Common options

`<pr>` is a GitHub or Azure DevOps PR number, or a local branch name. Common options, where a command takes them:
`-t/--target` (default: `origin/HEAD`, else `main`/`master`, else the current branch; `evaluate` uses the branch the PR
targets), `--provider`, `-s/--strategy`, `-f/--format`, `-o/--output` (format from the extension) and `-r/--repo`.
Typos get suggestions (`--strat` → "did you mean --strategy?"), and common errors come with a hint.

## Principles

- **Deterministic evidence before AI judgement.** Builds, tests and merge simulation decide. A gate that can't run is
  an `error` (verdict *undetermined*), never mistaken for a broken change.
- **AI is advisory** until a [benchmark](ai-review.md#ai-quality-benchmark-when-ai-review-may-block) has validated the model and
  prompt for blocking. It only talks to **self-hosted** models and sees a bounded evidence package, never the
  repository.
- **One result shape for every gate**: status, blocking, findings, evidence, duration, command, tool version.
- **Risk-based checks for PRs.** The PR's risk picks the gate profile and the test suites; full regression belongs
  to release gates.
- **Providers behind adapters.** GitHub (`gh`), Azure DevOps Server (`ado`) and local branches fill one
  provider-neutral model.
- **Reuse, don't reimplement.** Documentation gaps and the system context of a change (callers, data, endpoints,
  external systems, documentation freshness) come from docwizz, tests from your test runner, security findings from
  your scanners (as `command` gates). gitwizz runs them and reads their results.
- **Traceable and auditable.** Policy is read from the target branch's commit, so a PR can't relax the gates it is
  judged by. Every result names its policy source; `--evidence` keeps full logs and the result; AI reviews record
  the hash of their input. Your working tree, index and branches are never touched: merges are simulated with
  `git merge-tree`, commands run in temporary worktrees.
- **Stable output.** Human-readable text and versioned JSON (`gitwizz.evaluation/v1`, `gitwizz.trace/v1`,
  `gitwizz.context/v1`, `gitwizz.system-context/v1`, `gitwizz.evidence/v1`, `gitwizz.benchmark/v1`,
  `gitwizz.health/v1`, [`gitwizz.history/v2`](history/README.md)), plus documented
  [exit codes](../README.md#exit-codes).

## Known limitations

- The workspace's tool fingerprint covers the program each command segment starts, not what that program starts in
  turn: a script in the repository is covered by the commit, but a tool it calls is not. After changing such tools
  underneath, run with `--no-cache` or `gitwizz status --clear-cache`. Another gitwizz build invalidates everything stored.
- `status` (also with `--refresh`) and the repository guide read every open PR's linked work items, one or two provider calls per
  PR. The workspace is never pruned; `gitwizz status --clear-cache` empties it.
- System context: relations come from docwizz's model of the target, so a symbol the PR adds has no callers or
  callees there (the diff shows them). `docwizz diff` scans the target again for every PR, because it takes refs and no
  stored model. Flows are static call chains, so they are marked inferred.
- Hunk overlap assumes the PRs share a merge-base; with very different bases it's an approximation.
- A declared dependency on an *open* PR outside the selected set blocks the PR; one on a merged or closed PR counts as
  met. A PR whose base is neither the target nor a planned PR's branch is blocked ("targets 'release', not 'main'"),
  because merging it would land elsewhere. Branches already in the target are left out with a note; a local target
  behind `origin` gets a warning.
- Beam search is a heuristic. Complete plans always win, but one that only a very wide search reaches can be missed.
  Raise `--beam` when the plan blocks PRs you expected to merge.
- After a REGENERATE step the planning state holds the PR's lockfile, not a regenerated one.
- Synthetic commits are unreferenced objects; `git gc` cleans them up.
- History needs true merge commits; squash- or rebase-merged repositories have nothing to replay.
- `history` finds pull requests through their merge or squash messages, so rebase-merged PRs and custom merge
  messages leave commits `unknown`. A merge commit carries no ranges of its own: changes made while resolving a merge
  conflict aren't attributed. Commit → work item links kept by the provider are read only from Azure DevOps (GitHub
  links issues to pull requests, not to commits), and only for the newest `--commit-lookups` commits.
- `version-history` reads local refs (fetched remote branches included) and git dates: when a GitHub or Azure DevOps
  release was published isn't read, pull requests and work items aren't attached, and a cherry-picked fix is listed
  in each line it was picked to.
- Requirement evidence needs a resolver that answers `gitwizz.requirement-input/v1`; gitwizz doesn't build one. Until
  docwizz ships it ([docwizz#166](https://github.com/mycaravam-crypto/docwizz/issues/166)), leave
  `context.requirements` off or point it at your own read-only command.
- Structural and semantic analysis (members, API deltas, test selection by changed symbol) cover C# only; other
  languages use file and hunk overlap. It is syntax-only: same-named members of different classes look alike, which is
  why only a name new to the whole base becomes a hard dependency.

## Roadmap

Done: the v1, v2 and v0.4 planner work, and the PR quality & merge orchestrator epic (#53): quality gates with
`evaluate`/`explain` (#55), the Azure DevOps Server provider with `context` (#54), requirement-to-test traceability
with risk-based test selection (#57), advisory AI review on a bounded evidence package (#56), the AI quality benchmark
that decides when AI review may block (#59), and ephemeral per-PR test environments (#58). Since then: the guided
PR workflow `guide` (#69), per-phase progress bars on every long-running command, and the persistent repository
workspace with incremental, fingerprinted reuse, `status`, `refresh` and the repository-first `guide` (#71), and
docwizz system context for guided and AI-assisted review (#73), the dependency preflight `doctor` and its checks in
`guide` (#75), the historical evidence export `history` for code-to-requirement tracing (#84), and optional
requirement evidence in the PR system context (#85), and the version history of released tags and open release
branches `version-history` (#95), with Markdown changelog pages and the `gitwizz.version-history/v2` contract.

Known gaps from the epic: the benchmark reports generated-test pass rate, acceptance-criterion coverage and mutation
score as `null` (gitwizz doesn't generate tests yet); Azure DevOps branch policies other than reviewer votes and PR
builds aren't visible through `ado`; change impact is C#-only and doesn't check architecture rules (docwizz's
`architecture` layers do); merges are proposed, not executed.

Open, from [PLAN.md](../PLAN.md): structural analysis beyond C#, and recording real merge outcomes (CI result,
resolution) next to the git-history replay.
