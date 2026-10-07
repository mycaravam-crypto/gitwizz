# gitwizz — PR Quality & Merge Orchestrator

`pr-optimizer` answers two questions with reproducible evidence:

- **Is this pull request ready to merge, and if not, why?** `evaluate` runs the repository's quality gates
  (mergeability, review policy, build, tests, docwizz, any command) and gives one verdict; `explain` lists every
  blocking decision with its evidence. See [Merge readiness](#merge-readiness-evaluate-and-explain).
- **In which order should a set of pull requests merge?** `plan` (below).

`plan` finds a low-conflict order for merging a set of pull requests into a target branch.
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

## Install as a command

Publish the DLL to a fixed folder, then point a shell alias at it.

```bash
dotnet publish src/PrOptimizer -c Release -o ~/tools/pr-optimizer   # creates ~/tools/pr-optimizer/pr-optimizer.dll
```

**Bash / zsh**: add the alias to your startup file so every new shell has it.

```bash
echo "alias pr-optimizer='dotnet ~/tools/pr-optimizer/pr-optimizer.dll'" >> ~/.bashrc   # zsh: ~/.zshrc
source ~/.bashrc
```

**PowerShell**: `Set-Alias` can't pass arguments, so use a function. `$PROFILE` runs at every start.

```powershell
dotnet publish src/PrOptimizer -c Release -o $HOME\tools\pr-optimizer
if (!(Test-Path $PROFILE)) { New-Item -Type File -Force $PROFILE }
Add-Content $PROFILE 'function pr-optimizer { dotnet "$HOME\tools\pr-optimizer\pr-optimizer.dll" @args }'
. $PROFILE
```

If PowerShell refuses to load the profile, allow local scripts once: `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

Then run `pr-optimizer --all-open` in any repository. After pulling changes, re-run the `dotnet publish` line to update.

## Documentation gaps

[docwizz](https://github.com/mycaravam-crypto/docwizz) checks this repository's `///` docs, configured in
[docwizz.yaml](docwizz.yaml). On every pull request, [.github/workflows/docwizz.yml](.github/workflows/docwizz.yml)
posts one comment with the documentation gaps the change *introduces*, and fails the job on a new critical gap.
Existing gaps don't fail it. To see them all locally:

```bash
docwizz analyze .                        # every gap, ranked by how much it needs docs
docwizz check . --since origin/main      # only what your branch introduces, as CI runs it
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

Exit codes: `0` ok, `1` error or failed verification, `2` usage error. `evaluate` and `explain` add `3` (not ready: a
blocking gate failed) and `4` (undetermined: a blocking gate could not run).

### Terminal UI

On a terminal, `pretty` renders a rich report:

- **summary**: target, strategy, totals, and a breakdown bar of clean / regenerate / blocked PRs
- **merge order**: the plan as a flow (`main ➜ docs ➜ billing ➜ …  ✘ clash`) plus PRs you can merge in parallel right now
- **plan table**: cost bars (green → gold → orange → red), status (**CLEAN**, **REGENERATE**, **BLOCKED**,
  **POLICY BLOCKED**), and the reason for each step
- **dependency tree** of stacked and explicit dependencies
- **conflict-risk heatmap** between all PRs, as low / medium / high. Pair weights are heuristics, not probabilities,
  so the tool never shows them as percentages. The only percentages come from measured merge history (above 12 PRs it switches to a "riskiest pairs" bar chart)
- **"why A before B?"** panels comparing both simulated orders, a **verification** panel, and the **next step** command

Every colour comes with an icon or a label (✔ ⟳ ✘ ⚑ ⇉ `dep`), so the report still reads without colour.
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

## Merge readiness: evaluate and explain

```bash
pr-optimizer evaluate 57                       # GitHub PR 57: run its gates, print the verdict
pr-optimizer evaluate feature/x -f json        # a local branch, stable JSON (schema gitwizz.evaluation/v1)
pr-optimizer explain 57                        # why it is (not) ready, every blocking decision with evidence
pr-optimizer evaluate 57 --profile fast --evidence .gitwizz-evidence   # pick a profile, keep full logs
```

`evaluate` loads one pull request (GitHub number or local branch; without `--target` it is judged against the branch it
targets), then runs **quality gates** in order. Each gate produces the same result: a status, whether it is blocking,
findings (message, file, line, rule, evidence), evidence references, duration, command and tool version.

| Status | Meaning | Blocks the merge if the gate is blocking |
|---|---|---|
| `pass` | the check passed | no |
| `warn` | passed with something to look at (e.g. a lockfile to regenerate) | no |
| `fail` | a quality failure: the change needs work | yes |
| `error` | the gate could not run (tool missing, timeout, crash): nothing is known | yes |
| `skipped` | not applicable (no changed file matches its `paths`), or a gate it `needs` didn't pass | only in the second case |

One blocking gate that fails, errors or is left unrun makes the PR **not ready**. Advisory (`blocking: false`) gates stay
visible but never block. The verdict is `ready`, `blocked` (a gate failed) or `undetermined` (only gate errors), so a
broken tool is never mistaken for a broken change.

Built-in gates, active without configuration:

- **merge**: simulates the merge into the target with the planned strategy (`git merge-tree`, nothing is touched).
  A conflict fails; a conflict only in lockfiles/generated files warns (regenerate). Always runs first; its merged
  state is the **workspace** the command gates run in.
- **policy**: draft, review decision, failing or pending required checks, branch protection, declared dependencies on
  open PRs, and a PR that targets another branch. Skipped for local branches (they carry no such data).

Gate types to configure: `build` and `test` (command detected from the project: `dotnet`, `npm`, `go`, `cargo`, Maven,
Gradle, Python, `make`, or set `run:`), `docwizz` (`docwizz check . --since $GITWIZZ_TARGET_SHA`, the documentation
gaps the change introduces), and `command` (any command). Commands run with `sh -c` in a temporary worktree of the
merged state and get `GITWIZZ_PR`, `GITWIZZ_TARGET`, `GITWIZZ_TARGET_SHA`, `GITWIZZ_HEAD_SHA` and `GITWIZZ_RISK`.
Exit code 0 passes, any other fails; exit 126/127 (not installed) and a timeout are errors. Compiler errors
(`file(line,col): error CS1002: …`, `file:line: error …`) and failed tests (`dotnet test`, pytest, `go test`) become
findings with file and line. gitwizz runs these tools; it doesn't reimplement them.

### Policy as code

Gates live in `.gitwizz.yml` and are read **from the target branch's commit**, not from the working tree or the PR:
the rules are versioned with the repository, and a pull request can't relax the gates it is judged by. The report names
the source (`.gitwizz.yml@<commit>`).

```yaml
gates:
  - id: build                    # type defaults to the id when it names one (build, test, docwizz, merge, policy)
  - id: test
    needs: [build]               # skipped, and blocking, when build doesn't pass (default for command gates: [merge])
    timeout: 1800                # seconds (default 1800)
  - id: docs
    type: docwizz
    blocking: false              # advisory
  - id: e2e
    type: command
    run: ./scripts/e2e.sh
    paths: ["web/*"]             # only when the PR changes a matching file
profiles:                        # which gates each profile runs, in order
  fast: [build]
  default: [build, test, docs]
  full: [build, test, docs, e2e]
risk:
  high_paths: ["src/Billing/*"]  # touching one of these makes a PR high risk
  max_files: 25                  # so does changing more files than this
  profiles: { low: fast, medium: default, high: full }
secrets: [DEPLOY_TOKEN]          # values of these variables never appear in output, logs or JSON
```

The profile is `--profile`, else the one for the PR's **risk** (`high`: a `high_paths` file or more than `max_files`
files; `medium`: an API signature, migration or configuration change; else `low`), else `default`, else every gate.
Redefining `merge` or `policy` (e.g. `{ id: policy, blocking: false }`) changes the built-in. Values of `secrets` and of
`GITHUB_TOKEN`, `GH_TOKEN`, `ADO_PAT`, `SYSTEM_ACCESSTOKEN` are masked as `***` everywhere.

`--evidence <dir>` keeps each command gate's full log (`<gate>.log`) and the result (`evaluation.json`) for the
audit trail; the JSON references the logs.

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
Unknown keys, empty patterns and negative costs are errors, as are unknown gate types, duplicate gate ids and
references to unknown gates or profiles. The `gates`, `profiles`, `risk` and `secrets` keys are described in
[Policy as code](#policy-as-code).

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
   whose two orders give different results in simulation:

   ```text
   Why flip before billing?
     both modify: billing.cs
     flip -> billing = clean
     billing -> flip = conflict
     reason: after flip, billing merges cleanly; after billing, flip conflicts
   ```

   Each order's outcome is `clean`, `regenerate` or `conflict`, with any shared member listed. The reason is a semantic
   risk when there is one ("legacy uses GetUser(1 args), changed by api"). A policy-blocked PR is explained against the
   planned PR it overlaps most (`policy blocked`).
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
- v0.4 adds correctness work: state-aware cost, per-commit rebase, marker-free regenerate states. On the dense
  synthetic set that costs about 10–15% (60 PRs: 3.2 s → 3.6 s), with identical plans.
- **State-aware cost only where it matters:** pairwise outcomes are batched once. Only conflicting pairs are simulated
  again on each search state (about +35% on a dense 60-PR set: 2.5 s → 3.4 s).

| Benchmark | v0.1 | now (ReadyToRun) |
|---|---|---|
| 20 PRs | 8.6 s | 0.47 s |
| 60 PRs | 10.2 s | 0.8 s |

Set `PR_OPT_TIMING=1` to print a timing for each phase on stderr.

## Known limitations

- Hunk overlap assumes the PRs share a merge-base. With very different bases it's an approximation.
- A declared dependency on an *open* PR outside the selected set blocks the PR. One on a merged or closed PR counts as met.
  A PR whose base is neither the target nor a planned PR's branch is blocked ("targets 'release', not 'main'"),
  because `gh pr merge` would merge it elsewhere. Branches already in the target are left out with a note. A local
  target behind `origin` gets a warning.
- Beam search is a heuristic. Among the plans it finds, complete ones always win, but a complete order that only a
  very wide search would reach can be missed. Raise `--beam` when the plan blocks PRs you expected to merge.
- After a REGENERATE step the planning state holds the PR's lockfile, not a regenerated one. Later lockfile edits
  are judged against that version.
- Synthetic commits are unreferenced objects. `git gc` cleans them up.
- History needs true merge commits. Squash- or rebase-merged repositories have nothing to replay, so the history is empty there.
- Structural overlap and semantic analysis cover C# only. Other languages use file and hunk overlap.
- Semantic analysis is syntax-only. Names aren't resolved to types, so same-named members of different classes
  look alike. That's why only a name that is new to the whole base becomes a hard dependency.

## Roadmap

All planned v1, v2 and v0.4 issues are done. The PR quality & merge orchestrator epic (#53) adds quality gates and
`evaluate`/`explain` (#55). Ideas from [PLAN.md](PLAN.md) that are still open: structural analysis beyond C#, and recording real merge outcomes (CI result, resolution) next to the git-history replay.
