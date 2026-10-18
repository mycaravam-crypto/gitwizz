# Merge order: `plan`

`plan` doesn't sort PRs by size or file overlap. It **simulates** the merges on synthetic commits and searches for the
order with the lowest total cost.

```bash
gitwizz --all-open                      # all open PRs (or all unmerged local branches)
gitwizz -a -s squash                    # same, planned for squash merges
gitwizz -p 101,102,105 -f json          # specific PRs as JSON
gitwizz -p feature/a,feature/b --verify "dotnet test"
gitwizz -a -o plan.html                 # shareable HTML report
```

| Option | Default | Meaning |
|---|---|---|
| `-a`, `--all-open` | | All open PRs into the target and PRs stacked on them, or all unmerged local branches |
| `-p`, `--prs` | | Comma-separated PR numbers or branch names |
| `-s`, `--strategy` | auto | `merge`, `squash`, `rebase`, `ff-only`. Default: the merge queue's method, `squash` if the branch requires linear history, else `merge` |
| `-b`, `--beam` | `8` | Beam search width; `1` = greedy |
| `--history` | `200` | Learn per-file conflict rates from this many past merges into the target; `0` = off |
| `--verify` | `none` | Shell command run in a temporary worktree on merged plan states |
| `--verify-at` | `final` | `final`, `critical` (after each step with cost ≥ 1, plus the final one), or `step` |
| `-f`, `--format` | `pretty` / `text` | `pretty` (terminal), `text` (piped), `json`, `html` |

Every report ends with the **next step** as a ready-to-run command (`gh pr merge 105 --squash`, `ado pr merge 105
--yes`, `git merge --no-ff docs`). Re-run after each real merge so the plan reflects the new state.

On a terminal, `pretty` shows a summary with a clean / regenerate / blocked bar, the merge order as a flow
(`main ➜ docs ➜ billing ➜ …  ✘ clash`) with the PRs you can merge in parallel now, a plan table with cost bars and
status (**CLEAN**, **REGENERATE**, **BLOCKED**, **POLICY BLOCKED**), the dependency tree, a conflict-risk heatmap (low /
medium / high; heuristic weights are never shown as percentages, only measured history is; above 12 PRs a "riskiest
pairs" chart), "why A before B?" panels, verification, and the next step. Every colour comes with an icon or label,
the layout turns compact below 90 columns, and `NO_COLOR` is respected. Rendered example:
[docs/sample-plan.html](sample-plan.html).

<details>
<summary>Plain text output (<code>--format text</code>)</summary>

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
   Reason: unlocks refactor; overlaps clash (medium)

3  refactor  refactor
   CLEAN
   Cost: 0.4
   Reason: dependency: billing; overlaps clash (medium)

4  clash  clash
   BLOCKED
   Reason: conflict after refactor: billing.cs

Currently independent candidates (parallelizable):
    docs

Next: git checkout main && git merge --no-ff docs
Total cost: 0.7
```

</details>

## Planning configuration

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

A regenerator's command appears in the plan ("regenerate after merge: package-lock.json (npm install
--package-lock-only)").

## How planning works

```text
provider ─▶ analyze (files, hunks) ─▶ dependencies ─▶ readiness ─▶ beam search over simulations ─▶ report ─▶ verify
```

1. **Providers** ([Providers.cs](../src/Gitwizz/Providers.cs), [AzureDevOps.cs](../src/Gitwizz/AzureDevOps.cs)) fill one
   provider-neutral model, so the analysis never knows where a PR came from (see [Providers](providers.md)).
2. **Analysis** ([Analyzer.cs](../src/Gitwizz/Analyzer.cs)): changed files (with renames and deletes) and `-U0` hunks
   against the merge-base. For C#, Roslyn maps hunks to the **members** they touch (`Billing.Charge(decimal)`), so two
   PRs editing different lines of one method are still flagged. It also records each PR's **API delta** (declared
   names with the argument counts they accept, before vs after) and the names its new lines use. That gives **semantic
   risks**: B calls `GetUser(id)` but A changes it to `GetUser(id, tenant)`, B calls an overload only A adds, or both
   add migrations to the same folder. A pairwise **conflict weight** in [0,1] combines file overlap, hunk overlap,
   shared members, lineage clashes, and 0.3 per semantic risk. A rename is one file **lineage**, found by its old and
   new path; delete or rename against an edit, rename against delete, and renames to different names add 0.4 per file.
   **History**: the target's last 200 merge commits are replayed with `git merge-tree` (batched) to see which files
   really conflicted. Each file gets a rate `conflicts / (merges that brought it in + 1)`, and a shared file with rate
   r weighs `(1 + 2r)`× more. Reasons name conflict-prone files ("billing.cs (33 %)").
3. **File classes**: a merge that conflicts *only* in lockfiles or generated files (`package-lock.json`,
   `*.Designer.cs`, …) is **REGENERATE REQUIRED** (cost 0.5 per file), never clean. The conflicted tree is never
   committed; the next planning state takes the PR's version of those files, so later PRs never merge against
   conflict markers.
4. **Dependencies**: explicit (`depends on #N` / `blocked by #N` in the body or labels), structural (PR base is another
   PR's head branch, or commit ancestry), and semantic: B uses a name that A introduces and that appears nowhere at
   the base, e.g. a new `TenantId` type ("dependency: A (uses TenantId)"). Explicit or structural cycles are an error;
   a semantic edge that would close a cycle is dropped.
5. **Readiness** keeps three questions apart. *Structure*: does the tree merge? Only the simulation answers this.
   *Policy*: drafts, `CHANGES_REQUESTED`/`REVIEW_REQUIRED`, failing or pending checks, GitHub's `BLOCKED` merge state;
   such PRs and everything depending on them are **POLICY BLOCKED** and not scored. *GitHub state*: for a
   policy-ready PR, the merge state is added to the reason ("branch is behind its base, update it before merging").
   Required checks come from branch protection and rulesets; when the target names some, only those gate a PR, and
   one that hasn't reported counts as pending. With a **merge queue**, the next step is `gh pr merge N` (enqueue) and
   the strategy defaults to the queue's method. A `merge` plan on a linear-history branch gets a warning.
6. **Simulation** ([Simulator.cs](../src/Gitwizz/Simulator.cs)): `State₀ = target HEAD`, then
   `git merge-tree --write-tree State PR`. A clean merge becomes the next synthetic state (two parents for `merge`,
   one for `squash`; `ff-only` requires ancestry). `rebase` **replays each commit** with
   `merge-tree --merge-base=<parent>`, so a conflict in one commit blocks the PR even if the final tree would merge.
   Results are cached by `(state, PR head)`, and commits are only created for states the search keeps.
7. **Planner** ([Planner.cs](../src/Gitwizz/Planner.cs)): beam search minimising `Σ marginalCost(PRᵢ | Stateᵢ)`, where

   ```text
   marginalCost = Σ risk(PR, pending PRs) + 0.5·regenerateFiles − 0.1·unblockedDependents
   ```

   `risk` is **state-aware**: each overlapping pending PR is judged by its simulated outcome after this PR (real
   conflict 1, forced regenerate 0.5, clean merge half the static weight). Plans compare first by unmerged PRs, then
   total cost, then cheaper PRs first, then name, so a plan that merges every PR always beats a cheaper one that leaves
   one blocked. Finished plans are kept outside the beam. For overlapping pairs whose two orders simulate differently,
   the report explains the choice:

   ```text
   Why flip before billing?
     both modify: billing.cs
     flip -> billing = clean
     billing -> flip = conflict
     reason: after flip, billing merges cleanly; after billing, flip conflicts
   ```

8. **Verification** ([Verify.cs](../src/Gitwizz/Verify.cs)): `git worktree add --detach` on a plan state, run the
   command, remove the worktree. Only the chosen plan's states are verified, so the test count stays linear. The first
   failure stops verification and names the step ("FAILED after #108").

## Performance

| Benchmark | v0.1 | now (ReadyToRun) |
|---|---|---|
| 20 PRs | 8.6 s | 0.47 s |
| 60 PRs | 10.2 s | 0.8 s |

- **Result reuse:** if the PR merged last shares no files or history with a candidate, the candidate's result carries
  over unchanged; the real merge runs only if that branch of the search survives.
- **Batched merges:** each beam level's merges go through `git merge-tree --stdin`, one process per CPU core.
- **In-process commits:** synthetic commits are written directly as loose objects (SHA-1 or SHA-256).
- **One `rev-list` per PR** for dependency detection, instead of an ancestry check per pair.
- **State-aware cost only where it matters:** pairwise outcomes are computed once; only pairs that conflict there are
  simulated again per search state (about +35% on a dense 60-PR set: 2.5 s → 3.4 s, with identical plans).

On a terminal, every command shows a progress bar per phase on stderr (analysis, learning from history, the beam
search, verification, quality gates, test suites, benchmark cases), cleared when the result is printed; nothing is
drawn when stderr is redirected. Set `GITWIZZ_TIMING=1` to print a timing for each phase on stderr.
