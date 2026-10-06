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

## Build & test

```bash
dotnet build
dotnet test
# single-file binary
dotnet publish src/PrOptimizer -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## Usage

Run inside the repository:

```bash
# GitHub: all open PRs into main (plus PRs stacked on them)
pr-optimizer plan --target main --all-open

# GitHub: specific PRs, squash merges, JSON output
pr-optimizer plan --target main --prs 101,102,105 --strategy squash --format json

# Local branches as "PRs", verify the final merged state
pr-optimizer plan --target main --prs feature-a,feature-b --verify "dotnet test"
```

| Option | Default | Meaning |
|---|---|---|
| `--target` | `main` | Branch to merge into |
| `--prs` | | Comma-separated PR numbers (GitHub) or branch names (local) |
| `--all-open` | | All open PRs into `--target` and PRs stacked on them (GitHub) |
| `--provider` | auto | `github` if `--all-open` or all `--prs` are numeric, else `local` |
| `--strategy` | `merge` | `merge`, `squash`, `rebase`, `ff-only` |
| `--beam` | `8` | Beam search width; `1` = greedy |
| `--verify` | `none` | Shell command run in a temporary worktree on the final merged state |
| `--format` | `text` | `text` or `json` |
| `--repo` | cwd | Repository directory |

Exit codes: `0` ok, `1` error or failed verification, `2` usage error.

### Example output

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
