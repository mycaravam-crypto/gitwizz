# gitwizz — PR Merge Optimizer

`pr-optimizer` finds a low-conflict order for merging a set of pull requests into a target branch.
It doesn't sort PRs by size or file overlap. It **simulates** the merges with real git
(`git merge-tree`) on synthetic commits and searches for the order with the lowest total cost.
Your working tree, index and branches are never touched.

Design rationale: [PLAN.md](PLAN.md) (German).

## Requirements

- .NET 10 SDK
- git ≥ 2.38 (needs `merge-tree --write-tree`); ≥ 2.40 for `--strategy rebase` (`merge-tree --merge-base`)
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
| `-s`, `--strategy` | auto | `merge`, `squash`, `rebase`, `ff-only`. Default: the merge queue's method, `squash` if the branch requires linear history, else `merge` |
| `-b`, `--beam` | `8` | Beam search width; `1` = greedy |
| `--history` | `200` | Learn per-file conflict rates from this many past merges into the target; `0` = off |
| `--verify` | `none` | Shell command run in a temporary worktree on merged plan states |
| `--verify-at` | `final` | Which states: `final`, `critical` (after each high-risk step, cost ≥ 1, plus the final one), or `step` (after every step) |
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
Note: local branches: only structural mergeability is checked (no reviews, checks or branch protection)

1  docs  docs
   CLEAN
   Cost: 0
   Reason: no overlapping changes with pending PRs

2  billing  billing
   CLEAN
   Cost: 0.3
   Reason: unlocks refactor; overlaps clash (0.40)

3  refactor  refactor
   CLEAN
   Cost: 0.4
   Reason: dependency: billing; overlaps clash (0.40)

4  clash  clash
   BLOCKED
   Reason: conflict after refactor: billing.cs

Currently independent candidates (parallelizable):
    docs

Next: git checkout main && git merge --no-ff docs
Total cost: 0.7
```

## Configuration

Optional. Put a `.gitwizz.yml` at the repository root. Every key is optional, and anything you leave out keeps the
built-in default, so a repository without the file behaves exactly as described below.

```yaml
regenerators:            # files to regenerate instead of resolving by hand, and the command that does it
  - match: package-lock.json
    command: npm install --package-lock-only
  - match: "*.csproj"
    command: dotnet restore
generated:               # more generated files (regenerable, like *.Designer.cs)
  - "*.pb.go"
ignored:                 # left out of overlap scoring; real merge conflicts in them still block
  - "*.snap"
costs:
  regeneration: 0.5      # per file that needs regenerating
  conflict: 1.0          # a pending PR that would really conflict after this one
  dependency_unblock: 0.1
```

Patterns use `*` and `?`. They match the file name, or the whole path when they contain a `/`.
A regenerator's command appears in the plan ("regenerate after merge: package-lock.json (npm install --package-lock-only)").
Unknown keys, empty patterns and negative costs are errors.

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
   Roslyn also records each PR's **API delta** (declared names with the argument counts they accept, before vs after)
   and the names its new lines call or reference. That gives **semantic risks**: B calls `GetUser(id)` but A changes
   it to `GetUser(id, tenant)`, B calls an overload only A adds, or both PRs add migrations to the same folder.
   A pairwise **conflict weight** in [0,1] combines file overlap, hunk overlap, shared members
   lineage clashes, and 0.3 per semantic risk. A rename is one file **lineage**, found by its old and its new path.
   Delete or rename against an edit, rename against delete, and renames to different names add 0.4 per file.
   Identical deletes or renames don't.
   **History**: the target's last 200 merge commits are replayed with `git merge-tree` (batched) to see which files
   really conflicted. Each file gets a rate, `conflicts / (merges that brought it in + 1)`, and a shared file with
   rate r weighs `(1 + 2r)`× more. Step reasons name conflict-prone files ("billing.cs (33 %)").
3. **File classes**: lockfiles and generated files (`package-lock.json`, `*.Designer.cs`, …) aren't ignored.
   A merge that conflicts *only* in such files is planned as **REGENERATE REQUIRED** (cost 0.5 per file), never as clean.
   merge-tree's conflicted tree is never committed. The next planning state takes the PR's version of those files
   (written through a throwaway index), so later PRs never merge against conflict markers.
4. **Dependencies**: explicit (`depends on #N` / `blocked by #N` in the body or labels), structural
   (PR base is another PR's head branch, or commit ancestry), and semantic: B uses a name that A introduces
   and that appears nowhere at the base (`git grep`), e.g. a new `TenantId` type ("dependency: A (uses TenantId)").
   Cycles from explicit or structural edges are an error. A semantic edge that would close a cycle is dropped.
5. **Readiness** is three separate questions, and the report never mixes them up:
   - *structure*: does the tree merge? Only the simulation answers this.
   - *policy*: drafts, `CHANGES_REQUESTED`/`REVIEW_REQUIRED`, failing or pending checks, and GitHub's `BLOCKED`
     merge state (protection rules the other fields don't name). These are hard constraints: such PRs, and every PR
     that depends on them, are **POLICY BLOCKED** and aren't scored.
   - *GitHub*: for a policy-ready PR, GitHub's merge state is added to the step reason, e.g. "branch is behind its
     base, update it before merging", "reports conflicts with its base branch", or "auto-merge is already enabled".
   Local branches carry no policy data, so the plan says that only structural mergeability was checked.
   **Branch policy** (GitHub): required checks are read from branch protection and rulesets. When the target names
   required checks, only those gate a PR, and a required check that hasn't reported yet counts as pending.
   Required reviews come through GitHub's review decision. With a **merge queue**, the tool only plans: the next step
   is `gh pr merge N` (which enqueues), and the strategy defaults to the queue's merge method. A `merge` plan on a
   branch that requires linear history gets a warning.
6. **Simulation** ([Simulator.cs](src/PrOptimizer/Simulator.cs)): `State₀ = target HEAD`;
   `git merge-tree --write-tree State PR`. If the merge is clean, `git commit-tree` creates the next synthetic state
   (two parents for `merge`, one for `squash`; `ff-only` requires ancestry). `rebase` **replays each commit** like
   `git rebase`: `merge-tree --merge-base=<parent>` per commit, so a conflict in one commit blocks the PR even if
   the final tree would merge cleanly. A PR that is a single commit on its merge-base is replayed as one batched merge,
   since the result is identical.
   Results are cached by `(state, PR head)`, and commits are only created for states the search keeps.
7. **Planner** ([Planner.cs](src/PrOptimizer/Planner.cs)): beam search minimising
   `Σ marginalCost(PRᵢ | Stateᵢ)`, where

   ```text
   marginalCost = Σ risk(PR, pending PRs) + 0.5·regenerateFiles − 0.1·unblockedDependents
   ```

   `risk` is **state-aware**: the simulation outranks the static graph. Each overlapping pending PR is judged by its
   simulated outcome after this PR. A real conflict costs 1 (exact merge on the current search state), a forced
   regenerate 0.5, and a clean merge keeps half the static weight as residual risk. Pairwise outcomes from the target
   are computed once up front, and only pairs that conflict there get the per-state simulation.
   The static weights still drive the heatmap, the explanations and the batching.
   Plans are compared **lexicographically**: first the number of unmerged PRs, then total cost, then cheaper PRs first,
   then name. A plan that merges every PR always beats a cheaper one that leaves a PR BLOCKED. Finished plans are kept
   outside the beam, so the search can't prune the best one.
   The report also lists PRs that are parallelizable right now, and gives "Why A before B?" for overlapping pairs
   whose two orders give different results in simulation.
8. **Verification** ([Verify.cs](src/PrOptimizer/Verify.cs)): `git worktree add --detach` on a synthetic plan state,
   run the command, then remove the worktree. `--verify-at` chooses the states (`final`, `critical`, `step`). Only the
   chosen plan's states are verified, never search candidates, so the test count stays linear. The first failure stops
   verification and names the step ("FAILED after #108").

## Performance

Search cost is kept low in four ways:

- **Result reuse:** if the PR merged last touches none of a candidate's files and shares no history with it, the candidate's result
  carries over from the previous state unchanged. The real merge only runs if that branch of the search survives.
- **Batched merges:** the merges each beam level needs go through `git merge-tree --stdin`, one process per CPU core.
- **In-process commits:** synthetic commits are written directly as loose git objects (SHA-1 or SHA-256), not with one `git commit-tree` process per commit.
- **One `rev-list` per PR** for dependency detection, instead of an ancestry check per pair of PRs.
- **State-aware cost only where it matters:** pairwise outcomes are batched once. Only conflicting pairs are simulated
  again on each search state (about +35% on a dense 60-PR set: 2.5 s → 3.4 s).

| Benchmark | v0.1 | now (ReadyToRun) |
|---|---|---|
| 20 PRs | 8.6 s | 0.47 s |
| 60 PRs | 10.2 s | 0.8 s |

Set `PR_OPT_TIMING=1` to print a timing for each phase on stderr.

## Known limitations

- Hunk overlap assumes the PRs share a merge-base. With very different bases it's an approximation.
- Dependencies on PRs outside the selected set are ignored.
- After a REGENERATE step the planning state holds the PR's lockfile, not a regenerated one. Later lockfile edits
  are judged against that version.
- Synthetic commits are unreferenced objects. `git gc` cleans them up.
- History needs true merge commits. Squash- or rebase-merged repositories have nothing to replay, so the history is empty there.
- Structural overlap and semantic analysis cover C# only. Other languages use file and hunk overlap.
- Semantic analysis is syntax-only. Names aren't resolved to types, so same-named members of different classes
  look alike. That's why only a name that is new to the whole base becomes a hard dependency.

## Roadmap

All planned v1 and v2 issues are done. Ideas from [PLAN.md](PLAN.md) that are still open: structural analysis beyond C#, and recording real merge outcomes (CI result, resolution) next to the git-history replay.
