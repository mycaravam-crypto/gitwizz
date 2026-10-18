# Guided workflow, environment check and workspace

## Guided workflow: `guide`

```bash
gitwizz guide 57                         # interactive on a terminal
gitwizz guide 57 --yes                   # no questions: run the selected tests and the gates, print the verdict
gitwizz guide 57 --evidence .gitwizz-evidence --profile full
```

`guide` takes a developer from "I have a PR" to an evidence-based verdict without knowing which command comes next.
It shows six steps:

1. **PR context**: the PR, source → target, provider, risk, linked work items and how many acceptance criteria they have.
2. **System context**: what docwizz knows about the system around the change: changed symbols and their modules and
   layers, callers and callees, API endpoints, data entities, external systems, the freshness of the relevant
   documentation, linked tests, which acceptance criteria name which parts of the system, and how much of it an AI
   review would get. It ends with the context quality (GOOD, PARTIAL, MISSING or STALE) and its reasons. Unless it is
   GOOD, you choose: continue, show the affected system graph, show what is missing or stale (with the commands that
   fix it), or run docwizz again. See [System context](system-context.md).
3. **Test impact**: whether it merges, the selected test suites, and acceptance criteria without a verifying test, all
   before anything expensive runs.
4. **Tests**: run the selected suites on the merged state, show the traceability details first, or skip.
5. **Merge readiness**: the same gate evaluation as `evaluate`. Suites the guide already ran on the same merged state
   are not run again, and the verdict is READY, BLOCKED or UNDETERMINED.
6. **What next?**: for BLOCKED, the blocking reasons and a concrete next action ("Add or link a test for AB#8234.3",
   "Fix the failing test suite contract", "Bring main into feature/x and resolve the conflicts in …"). For
   UNDETERMINED, which gate could not run, kept apart from quality failures. For READY, the merge plan for the target
   with this PR's position and the next merge command.

The menus also offer the full explanation, a summary of the AI evidence package, and saving `evaluation.json` and
`evidence.json` as an evidence bundle.

Before it loads anything, the guide checks its environment with the first checks [`doctor`](#environment-check-doctor)
makes: git, the repository and the provider it will use (`ado auth status` or `gh auth status`, read-only). It prints
them as an *Environment* section. When one of them is unusable, it shows the fix (for example `ado auth login
<server-url>`) and stops with exit code 7 before any PR is loaded. On a terminal it offers *Retry* (after you fixed it
in another shell) or *Exit*; with `--yes` or without a terminal it stops at once. The same applies to `gitwizz guide`
without a PR.

After the PR context, once the policy is read from the target, the guide checks what the gates selected for this PR
rely on: docwizz, the AI endpoint (its model list only, never repository content, never a host the self-hosting policy
refuses), Docker for an `environment` gate, and the version of well-known gate tools (scripts are never run). Under
*Gate dependencies* it lists them, the gates that can't run and what that means: an advisory gate (AI review) leaves
the verdict alone (`DEGRADED`), a blocking one means the verdict can be no better than UNDETERMINED
(`VERDICT_AT_RISK`), with the same rule as `evaluate`. When a gate is affected, it asks: *Continue without …* (or
*Continue anyway*), *Retry the checks*, *Show configuration* (the `.gitwizz.yml` keys involved, hosts only, and the
fixes) or *Exit*. With `--yes` or without a terminal it says what it found and continues. A missing docwizz that only
the system context needs is left to step 2. Nothing is checked twice in one run except on *Retry*, and no result is
stored: every run checks again. An unavailable tool never makes a PR BLOCKED.

It only orchestrates the other commands' logic (one implementation of loading, analysis, traceability, gates, evidence
and planning). It never merges, never changes the PR and never writes tests. It asks only when stdin and stdout are
terminals: with `--yes`, or when piped, it runs the recommended steps and never waits for input. It keeps no state.
After fixing code or tests, rerun `gitwizz guide 57` and it recomputes everything. The exit code matches `evaluate`'s,
and is 4 when you exit before a verdict, 7 when the environment check stops it. Options: `--target`, `--provider`,
`--profile`, `--evidence`, `--yes`, `--repo`, `--no-cache`.

Without a PR, `gitwizz guide` starts from the repository. It shows the target, the workspace and every open PR as
ready, blocked, undetermined, stale (with the reason) or never evaluated, then recommends the next action. A stale
verdict comes first, then unevaluated PRs, stale repository facts, blockers, and finally the merge plan for what is
ready. Choosing a PR continues in the PR guide above, and that PR's new verdict is stored. Without a terminal it prints
the summary and the recommended command, and runs nothing.

## Environment check: `doctor`

```bash
gitwizz doctor                 # dependency health and what it means for the verdict
gitwizz doctor -f json         # schema gitwizz.health/v1, for scripts and CI
gitwizz doctor --profile full  # only the gates of one profile
gitwizz doctor --working-tree # validate local .gitwizz.yml before committing
```

`doctor` checks what gitwizz relies on before anything expensive runs, and says what a missing piece means:

| Dependency | Check | Never |
|---|---|---|
| git, repository | `git --version` (2.38 or later), `rev-parse` | |
| Azure DevOps | `ado --version`, `ado auth status` (asks the server: reachability and token) | prints the token or the user |
| GitHub | `gh --version`, `gh auth status` | |
| `.gitwizz.yml` | read at the target as `evaluate` would; with `--working-tree`, read the local file | |
| docwizz | `docwizz --version`; `docwizz.yaml` is valid YAML | runs an analysis |
| AI endpoint | the self-hosting policy, then `GET {endpoint}/models` and that `review.model` is served | contacts a refused host; sends repository content |
| Docker | `docker info`, `docker compose version` (for an `environment` gate) | |
| gate and suite tools | `--version` of well-known tools (dotnet, npm, make, docwizz, powerwizz-cli, ...) | runs a configured command or script: those are `UNKNOWN` |

Each dependency is `AVAILABLE`, `UNAVAILABLE`, `MISCONFIGURED` or `UNKNOWN`, with the gates that use it and the exact
fix. The gates it affects, and the best verdict an evaluation can still reach, follow from the same rules as
`evaluate`: with every affected gate erroring and the rest passing. Overall:

| Overall | Meaning | Exit |
|---|---|---|
| `OK` | everything is available | `0` |
| `DEGRADED` | only advisory gates or the system context are affected; a PR can still become READY | `5` |
| `VERDICT_AT_RISK` | a blocking gate cannot run: the verdict can be no better than UNDETERMINED | `6` |
| `CANNOT_START` | git, the repository, the provider or `.gitwizz.yml` is unusable | `7` |

An unavailable dependency is never a quality failure: it can make a verdict UNDETERMINED, never BLOCKED. Without a PR
the risk level is unknown, so every profile the risk levels map to is considered (or only `--profile`). Results are
never stored: each run checks again. By default `doctor` uses the policy committed on the target branch, even if a local `.gitwizz.yml` exists.
Use `--working-tree` to validate new or uncommitted policy edits (including `context.docwizz.command`) without changing PR evaluation policy.
When `.gitwizz.yml` has uncommitted changes, text output highlights which policy `doctor` is checking and suggests `doctor --working-tree` if needed.
Options: `--target`, `--provider`, `--profile`, `--working-tree`, `-f`, `-o`, `--repo`.

`guide` runs the same checks: the first three rows (git, the repository, the provider) before it loads a PR, stopping
with the fix when one of them is unusable, and the rest for the gates selected for that PR after its context; see
[Guided workflow](#guided-workflow-guide).

## Repository workspace: `status`, `refresh`, `cache`

```bash
gitwizz status                # open PRs as the workspace sees them, and the next suggested action
gitwizz refresh               # re-evaluate PRs whose verdict is stale; bring repository facts up to date
gitwizz refresh --all-open    # also evaluate open PRs never evaluated
gitwizz cache status          # where the workspace is and what it holds
gitwizz cache clear           # remove it (only a directory gitwizz created)
```

gitwizz keeps what it computed in a workspace under the repository's git directory (`.git/gitwizz/`, shared by
worktrees), never in tracked files. `GITWIZZ_WORKSPACE=<dir>` moves it, e.g. to a CI cache. It holds:

| Artifact | Reused while these are unchanged |
|---|---|
| PR analysis (changed files, hunks, members, API changes) | target commit, PR head, gitwizz build |
| Conflict history (`plan`) | target commit, depth, gitwizz build |
| Policy and test topology | `.gitwizz.yml` at the target, gitwizz build |
| Build, test and command gate results | merged-state commit, gate definition, command, environment, the command's tools, the gate's `cache_inputs` files |
| Test suite runs (`traceability`) | merged-state commit, suite definition, environment, the suite's tools |
| AI review results | evidence-package hash, model, prompt version and hash, endpoint, review settings |
| Latest verdict per PR (`status`, `guide`) | commits, `.gitwizz.yml`, provider state (reviews, checks, description, work items), strategy, profile, the tools of the commands it ran, the `cache_inputs` files of its gates |
| docwizz system model of the target (`repo/docwizz/`), shared by every PR | the target's tree, docwizz version, gitwizz build |
| docwizz diff per PR (`docwizz-diff.json`) | the target's tree, the merged state's tree, docwizz version, gitwizz build |

Every entry stores its inputs, the time it was computed and a fingerprint of those inputs. It is reused only when the
fingerprint of the current inputs matches, never because it is recent. When it doesn't match, `status` and `guide` say
it is stale and why ("new commits on the PR", "the target branch moved", "`.gitwizz.yml` changed", "a tool
changed"…), and nothing stale is shown as current. A tool counts as the program each command segment starts: shell
builtins are fixed; a relative path is part of the commit; well-known tools (dotnet, npm, go, …) by their `--version`;
anything else by its location, size and modification time.

Some results are never stored:
- a gate that errored (a missing tool or a timeout says nothing about the change);
- results that depend on a provisioned test environment (variables beyond `GITWIZZ_*`);
- merge and policy gates, which are cheap and read live provider state.

With `--evidence`, gates run again so every log is written. A stored value also carries a digest, so a corrupt,
truncated or edited entry reads as missing and is recomputed: it can never turn into a READY. Delete the workspace at
any time; the next run recomputes it. `--no-cache` neither reads nor writes it, and CI works the same with an empty
one.

Incremental in practice:
- A new commit on one PR invalidates that PR's analysis and verdict only.
- A moved target invalidates baseline-dependent data, keeping gate results whose merged state is unchanged.
- An edited test command reruns that suite only.
- Another AI model or prompt reruns the AI review only.
- Rerunning `guide` with nothing changed reuses every gate result.

The one repository-wide index is docwizz's code model of the target, which gitwizz stores and reuses for every PR
until the target's code or docwizz changes; `status` lists it as *docwizz system model* when docwizz is installed, and
`refresh` brings it up to date.
