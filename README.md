# gitwizz — PR Merge Optimizer

`pr-optimizer` finds a low-conflict order for merging a set of pull requests into a target branch.
It doesn't sort PRs by size or file overlap. It **simulates** the merges with real git
(`git merge-tree`) on synthetic commits and searches for the order with the lowest total cost.
Your working tree, index and branches are never touched.

Design rationale: [PLAN.md](PLAN.md) (German).

## Requirements

- .NET 10 SDK
- git ≥ 2.38 (needs `merge-tree --write-tree`)
- `gh` CLI, authenticated, for the GitHub provider

## Quick start

```bash
dotnet run --project src/PrOptimizer -- example   # builds a demo repo and plans it
```

The example repository has seven branches. Between them they show every outcome: independent PRs you can merge
in parallel, a lockfile conflict that only needs a regenerate, a stacked dependency, a member-level overlap in
`BillingService.CalculateTax`, and a PR that is blocked by a real conflict.
It ends with commands to try on the demo repo yourself.

## Build & test

```bash
dotnet build
dotnet test
# fastest: precompiled (ReadyToRun) single-file binary
dotnet publish src/PrOptimizer -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

## Usage

Run inside the repository. `plan` is the default command, so `pr-optimizer --all-open` is enough.

```bash
pr-optimizer --all-open                     # all open GitHub PRs (or all unmerged local branches)
pr-optimizer -a -s squash                   # same, planned for squash merges
pr-optimizer -p 101,102,105 -f json         # specific GitHub PRs as JSON
pr-optimizer -p feature/a,feature/b --verify "dotnet test"
pr-optimizer -a -o plan.html                # shareable HTML report (format from the extension)
pr-optimizer help                           # all options with examples
```

| Option | Default | Meaning |
|---|---|---|
| `-a`, `--all-open` | | All open PRs into the target and PRs stacked on them (GitHub), or all local branches not yet merged (local) |
| `-p`, `--prs` | | Comma-separated PR numbers (GitHub) or branch names (local) |
| `-t`, `--target` | auto | Branch to merge into: `origin/HEAD`, else `main`/`master`, else the current branch |
| `--provider` | auto | `github` for numeric `--prs`, or for `--all-open` with a GitHub `origin` and `gh` installed; else `local` |
| `-s`, `--strategy` | `merge` | `merge`, `squash`, `rebase`, `ff-only` |
| `-b`, `--beam` | `8` | Beam search width; `1` = greedy |
| `--verify` | `none` | Shell command run in a temporary worktree on the final merged state |
| `-f`, `--format` | `pretty` / `text` | `pretty` (default on a terminal), `text` (default when piped), `json`, `html` |
| `-o`, `--output` | stdout | Write the report to a file; the format comes from the extension (`.html`, `.json`, else text) |
| `-r`, `--repo` | cwd | Repository directory |

Typos get suggestions (`--strat` → "did you mean --strategy?"), and common errors come with a hint.
Every report ends with the **next step**, as a ready-to-run command (`gh pr merge 105 --squash` or
`git merge --no-ff docs`). Re-run the tool after each real merge so the plan reflects the new state.

Exit codes: `0` ok, `1` error or failed verification, `2` usage error.

### Terminal UI

On a terminal, `pretty` renders a rich report:

- **summary**: target, strategy, totals, and a breakdown bar of clean / regenerate / blocked PRs
- **merge order**: the plan as a flow (`main ➜ docs ➜ billing ➜ …  ✘ clash`) plus PRs you can merge in parallel right now
- **plan table**: cost bars (green → gold → orange → red), status, and the reason for each step
- **dependency tree** of stacked and explicit dependencies
- **conflict-risk heatmap** between all PRs (above 12 PRs it switches to a "riskiest pairs" bar chart)
- **"why A before B?"** panels comparing both simulated orders, a **verification** panel, and the **next step** command

Every colour comes with an icon or a label (✔ ⟳ ✘ ⇉ `dep`), so the report still reads without colour.
Below 90 columns the layout turns compact. `NO_COLOR` is respected.
For a rendered example, see [docs/sample-plan.html](docs/sample-plan.html).

### Plain text output (`--format text`)

```text
PR MERGE PLAN
Target: main
Strategy: merge

1  docs  docs
   Cost: 0
   Reason: no overlapping changes with pending PRs

2  billing  billing
   Cost: 0.3
   Reason: unlocks refactor; overlaps clash (0.40)

3  refactor  refactor
   Cost: 0.4
   Reason: dependency: billing; overlaps clash (0.40)

4  clash  clash
   BLOCKED
   Reason: conflict after refactor: billing.cs

Currently independent candidates (parallelizable):
    docs

Next: git checkout main && git merge --no-ff docs
Total cost: 10.7
```

## How it works

```text
provider ─▶ analyze (files, hunks) ─▶ dependencies ─▶ readiness ─▶ beam search over simulations ─▶ report ─▶ verify
```

1. **Providers** ([Providers.cs](src/PrOptimizer/Providers.cs)): `local` treats each branch or ref as a PR.
   `github` reads open PRs through `gh` (title, body, labels, draft, review decision, CI rollup)
   and fetches `refs/pull/N/head`.
2. **Analysis** ([Analyzer.cs](src/PrOptimizer/Analyzer.cs)): changed files (with renames and deletes) and
   `-U0` hunks against the merge-base. For C# files, hunks are mapped to the **members** they touch with Roslyn
   (e.g. `Billing.Charge(decimal)`), so two PRs editing different lines of the same method are still flagged.
   A pairwise **conflict weight** in [0,1] combines file overlap, hunk overlap, shared members
   and delete/rename-vs-modify risk.
3. **File classes**: lockfiles and generated files (`package-lock.json`, `*.Designer.cs`, …) aren't ignored.
   If a merge conflicts *only* in such files, it counts as mergeable with a "regenerate after merge" note (cost 0.5 per file).
4. **Dependencies**: explicit (`depends on #N` / `blocked by #N` in the body or labels) and structural
   (PR base is another PR's head branch, or commit ancestry). Cycles are an error.
5. **Readiness**: drafts, `CHANGES_REQUESTED`/`REVIEW_REQUIRED`, and failing/pending CI are hard constraints.
   Such PRs, and every PR that depends on them, are reported as BLOCKED. They aren't scored.
6. **Simulation** ([Simulator.cs](src/PrOptimizer/Simulator.cs)): `State₀ = target HEAD`;
   `git merge-tree --write-tree State PR`. If the merge is clean, `git commit-tree` creates the next synthetic state
   (two parents for `merge`, one for `squash`/`rebase`; `ff-only` requires ancestry).
   Results are cached by `(state, PR head)`, and commits are only created for states the search keeps.
7. **Planner** ([Planner.cs](src/PrOptimizer/Planner.cs)): beam search minimising
   `Σ marginalCost(PRᵢ | Stateᵢ)`, where

   ```text
   marginalCost = Σ conflictWeight(PR, pending PRs) + 0.5·regenerateFiles − 0.1·unblockedDependents
   ```

   A PR that can't merge cleanly at a dead end is BLOCKED (+10). When total costs are equal, cheaper PRs go first.
   The report also lists PRs that are parallelizable right now, and gives "Why A before B?" for overlapping pairs
   whose two orders give different results in simulation.
8. **Verification** ([Verify.cs](src/PrOptimizer/Verify.cs)): `git worktree add --detach` on the final synthetic commit,
   run the command, then remove the worktree.

## Performance

Search cost is kept low in four ways:

- **Result reuse:** if the PR merged last touches none of a candidate's files and shares no history with it, the candidate's result
  carries over from the previous state unchanged. The real merge only runs if that branch of the search survives.
- **Batched merges:** the merges each beam level needs go through `git merge-tree --stdin`, one process per CPU core.
- **In-process commits:** synthetic commits are written directly as loose git objects (SHA-1 or SHA-256), not with one `git commit-tree` process per commit.
- **One `rev-list` per PR** for dependency detection, instead of an ancestry check per pair of PRs.

| Benchmark | v0.1 | now (ReadyToRun) |
|---|---|---|
| 20 PRs | 8.6 s | 0.47 s |
| 60 PRs | 10.2 s | 0.8 s |

Set `PR_OPT_TIMING=1` to print a timing for each phase on stderr.

## Known limitations

- `rebase` is simulated as `squash`, with the same resulting tree. Conflicts in individual commits aren't replayed.
- Hunk overlap assumes the PRs share a merge-base. With very different bases it's an approximation.
- Dependencies on PRs outside the selected set are ignored.
- `--verify` checks only the final state. There are no per-batch or per-step modes yet.
- Synthetic commits are unreferenced objects. `git gc` cleans them up.
- Structural overlap covers C# only. Other languages use file and hunk overlap.

## Roadmap

v2 issues: [#16 semantic dependencies](../../issues/16),
[#17 historical intelligence](../../issues/17), [#18 merge queue / branch protection](../../issues/18).
