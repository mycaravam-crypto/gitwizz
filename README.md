# gitwizz — PR quality & merge orchestrator

`gitwizz` answers two questions about pull requests, with evidence you can reproduce:

1. **Is this PR ready to merge, and if not, why?** `evaluate` runs the repository's quality gates (mergeability,
   review policy, build, tests, traced acceptance criteria, a per-PR test environment, docs, AI review, any command)
   and returns one verdict. `explain` lists every blocking decision with its evidence.
2. **In which order should a set of PRs merge?** `plan` simulates the merges with real git and searches for the
   order with the fewest conflicts.

gitwizz owns orchestration, policy and the merge-readiness decision. It works with GitHub, Azure DevOps Server and
plain local branches. `plan` still works on its own, with no gates configured.

### Principles

- **Deterministic evidence before AI judgement.** Builds, tests and merge simulation decide. A gate that can't run is
  an `error` (verdict *undetermined*), never mistaken for a broken change.
- **AI is advisory** until a [benchmark](#ai-quality-benchmark-when-ai-review-may-block) has validated the model and
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
  `gitwizz.context/v1`, `gitwizz.system-context/v1`, `gitwizz.evidence/v1`, `gitwizz.benchmark/v1`), plus documented
  [exit codes](#exit-codes).

Design rationale: [PLAN.md](PLAN.md) (German).

## Quick start

```bash
dotnet run --project src/Gitwizz -- example   # builds a demo repository and plans it
```

The demo has seven branches covering every planning outcome: independent PRs that can merge in parallel, a lockfile
conflict that only needs a regenerate, a stacked dependency, a member-level overlap in `BillingService.CalculateTax`,
and a PR blocked by a real conflict. It ends with commands to try on the demo yourself.

In your own repository ([install](#install) first):

```bash
gitwizz guide                    # the repository: which PRs are ready, blocked or stale, and what to do next
gitwizz guide 57                 # step by step: context, system context, test impact, tests, verdict, what to do next
gitwizz status                   # the same overview, non-interactive (-f json for scripts)
gitwizz evaluate 57              # is PR 57 ready? (exit 0 ready, 3 blocked, 4 undetermined)
gitwizz explain 57               # every blocking decision, with evidence
gitwizz trace 57 --run           # the tests the change needs, checked against its acceptance criteria
gitwizz --all-open               # merge order for all open PRs (plan is the default command)
```

## Commands

| Command | Answers |
|---|---|
| `guide [<pr>] [--yes]` | What do I do next? The repository overview, or one PR walked from context to verdict |
| `doctor` | Is the environment ready? git, provider login, docwizz, the AI endpoint, Docker and gate tools, and which gates they affect (`-f text\|json`) |
| `status` | Which open PRs are ready, blocked, undetermined, stale or never evaluated? (`-f text\|json`) |
| `refresh [--all-open]` | Re-evaluates what is stale (with `--all-open` also what was never evaluated), reusing the rest |
| `cache status\|clear` | Where the workspace is and what it holds; removes it |
| `evaluate <pr>` | Is it ready? Runs the gates and prints the verdict (`-f pretty\|text\|json`) |
| `explain <pr>` | Why (not)? Blocking decisions, evidence, the criteria matrix (`-f text\|json`) |
| `trace <pr> [--run]` | Which tests does the change need, and which acceptance criteria do they cover? |
| `context <pr> [--system]` | The PR's normalized context from any provider, as JSON; `--system`: the system context docwizz gives for the change |
| `evidence <pr>` | The bounded package an AI review would see, as JSON, without contacting a model |
| `benchmark [--accept]` | How good is the AI review on labelled cases? `--accept` saves the baseline |
| `env down <pr>` | Removes a PR's test environment (idempotent) |
| `plan` (default) | In which order should these PRs merge? |
| `example` | Builds a demo repository and plans it |
| `help`, `version` | All options with examples; the version |

`<pr>` is a GitHub or Azure DevOps PR number, or a local branch name. Common options, where a command takes them:
`-t/--target` (default: `origin/HEAD`, else `main`/`master`, else the current branch; `evaluate` uses the branch the PR
targets), `--provider`, `-s/--strategy`, `-f/--format`, `-o/--output` (format from the extension) and `-r/--repo`.
Typos get suggestions (`--strat` → "did you mean --strategy?"), and common errors come with a hint.

## Install

Requires the .NET 10 SDK and git ≥ 2.38 (`merge-tree --write-tree`; ≥ 2.40 for `--strategy rebase`). Providers need
an authenticated [`gh`](https://cli.github.com) (GitHub) or [`ado`](https://github.com/mycaravam-crypto/ado-devops)
logged in with `ado auth login` (Azure DevOps Server). Gates need the tools they run, e.g. Docker for environments.

Publish to a fixed folder and point a `gitwizz` command at it. Re-run `dotnet publish` after pulling changes.

```bash
# Linux, macOS, Git Bash
dotnet publish src/Gitwizz -c Release -o ~/tools/gitwizz
echo "alias gitwizz='dotnet ~/tools/gitwizz/gitwizz.dll'" >> ~/.bashrc   # zsh: ~/.zshrc
```

<details>
<summary>Windows (PowerShell or cmd), and a faster single-file binary</summary>

PowerShell: `Set-Alias` can't pass arguments, so add a function to `$PROFILE`. If the profile won't load, run
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once.

```powershell
dotnet publish src/Gitwizz -c Release -o "$HOME\tools\gitwizz"
if (!(Test-Path $PROFILE)) { New-Item -Type File -Force $PROFILE }
Add-Content $PROFILE 'function gitwizz { dotnet "$HOME\tools\gitwizz\gitwizz.dll" @args }'
```

cmd (also works from PowerShell): put a `gitwizz.cmd` on your `PATH`. Run this once in PowerShell, then open a new
terminal.

```powershell
dotnet publish src/Gitwizz -c Release -o "$HOME\tools\gitwizz"
New-Item -Type Directory -Force "$HOME\bin" | Out-Null
Set-Content "$HOME\bin\gitwizz.cmd" "@dotnet `"$HOME\tools\gitwizz\gitwizz.dll`" %*" -Encoding ASCII
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (";$userPath;" -notlike "*;$HOME\bin;*") { [Environment]::SetEnvironmentVariable('Path', "$userPath;$HOME\bin", 'User') }
```

Fastest start-up: a precompiled (ReadyToRun) single-file binary in `src/Gitwizz/bin/Release/net10.0/<rid>/publish/`.
ReadyToRun compiles for the target platform, so build the Windows binary on Windows (or drop
`-p:PublishReadyToRun=true`).

```bash
dotnet publish src/Gitwizz -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true
# also: -r win-x64, -r osx-arm64
```

</details>

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
   fix it), or run docwizz again. See [System context](#system-context-from-docwizz).
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

Before it loads anything, the guide checks its environment, with the first checks
[`doctor`](#environment-check-doctor) makes: git, the repository and the provider it will use (`ado auth status` or
`gh auth status`, read-only). It prints them as an *Environment* section. When one of them is unusable, it shows the
fix (for example `ado auth login <server-url>`) and stops with exit code 7 before any PR is loaded. On a terminal it
offers *Retry* (after you fixed it in another shell) or *Exit*; with `--yes` or without a terminal it stops at once.
It never retries on its own. The same applies to `gitwizz guide` without a PR. The dependencies of the configured
gates (docwizz, the AI endpoint, Docker, gate tools) are checked by `doctor`; their check inside the guide follows
(#75, part 3).

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
```

`doctor` checks what gitwizz relies on before anything expensive runs, and says what a missing piece means:

| Dependency | Check | Never |
|---|---|---|
| git, repository | `git --version` (2.38 or later), `rev-parse` | |
| Azure DevOps | `ado --version`, `ado auth status` (asks the server: reachability and token) | prints the token or the user |
| GitHub | `gh --version`, `gh auth status` | |
| `.gitwizz.yml` | read at the target as `evaluate` would | |
| docwizz | `docwizz --version`; `docwizz.yaml` is valid YAML | runs an analysis |
| AI endpoint | the self-hosting policy, then `GET {endpoint}/models` and that `review.model` is served | contacts a refused host; sends repository content |
| Docker | `docker info`, `docker compose version` (for an `environment` gate) | |
| gate and suite tools | `--version` of well-known tools (dotnet, npm, make, ...) | runs a configured command or script: those are `UNKNOWN` |

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
never stored: each run checks again. Options: `--target`, `--provider`, `--profile`, `-f`, `-o`, `--repo`.

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
| Build, test and command gate results | merged-state commit, gate definition, command, environment, the command's tools |
| Test suite runs (`traceability`) | merged-state commit, suite definition, environment, the suite's tools |
| AI review results | evidence-package hash, model, prompt version and hash, endpoint, review settings |
| Latest verdict per PR (`status`, `guide`) | commits, `.gitwizz.yml`, provider state (reviews, checks, description, work items), strategy, profile, the tools of the commands it ran |
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

## Merge readiness: `evaluate` and `explain`

```bash
gitwizz evaluate 57                                               # run the gates, print the verdict
gitwizz evaluate feature/x -f json                                # a local branch, schema gitwizz.evaluation/v1
gitwizz evaluate 57 --profile fast --evidence .gitwizz-evidence   # pick a profile, keep full logs
gitwizz explain 57                                                # why it is (not) ready
```

`evaluate` loads one PR and runs its **quality gates** in order. Every gate reports the same shape: a status, whether
it blocks, findings (message, file, line, rule, evidence), evidence references, duration, command and tool version.

| Status | Meaning | Blocks if the gate is blocking |
|---|---|---|
| `pass` | the check passed | no |
| `warn` | passed with something to look at (e.g. a lockfile to regenerate) | no |
| `fail` | a quality failure: the change needs work | yes |
| `error` | the gate could not run (tool missing, timeout, crash): nothing is known | yes |
| `skipped` | not applicable (no changed file matches its `paths`), or a gate it `needs` didn't pass | only in the second case |

The verdict is `ready`, `blocked` (a blocking gate failed or was left unrun) or `undetermined` (only gate errors).
Advisory gates (`blocking: false`; `ai-review` by default) are reported but never block.

### From PR to verdict

| Step | What gitwizz does |
|---|---|
| Collect context and linked requirements | the provider loads the PR, reviews, checks and linked work items with their acceptance criteria ([`context`](#pr-context-as-json)) |
| Calculate change impact | changed files and C# members, API changes, and the PR's **risk** (`low`/`medium`/`high`, with reasons) |
| Read the system context | what docwizz knows around the change, its documentation's freshness, and the context quality ([system context](#system-context-from-docwizz)) |
| Select applicable gates | the profile from `--profile` or the risk; gates whose `paths` don't match are skipped |
| Run deterministic checks and tests | merge simulation, policy, build, test, docwizz, commands, the test environment, and the test suites the change selects ([`trace`](#test-selection-and-traceability-trace)) |
| Run AI-assisted review | an `ai-review` gate, if configured, on the [bounded evidence package](#ai-review), system context included |
| Aggregate evidence | one result per gate; `--evidence` keeps logs, packages and `evaluation.json` |
| Evaluate the readiness policy | blocking vs advisory gates → `ready`, `blocked` or `undetermined` |
| Explain blockers | `explain`: every blocking decision with its evidence, and the criteria matrix |
| Optimize the merge | [`plan`](#merge-order-plan): the merge order and the next merge command. gitwizz proposes the command; it doesn't run it |

### Gates

Two gates are built in and run without configuration:

- **merge** simulates the merge into the target with the planned strategy. A conflict fails; a conflict only in
  lockfiles or generated files warns (regenerate). It always runs first, and its merged state is the **workspace**
  every other gate runs in.
- **policy** checks draft state, the review decision, failing or pending required checks, branch protection, declared
  dependencies on open PRs, and a PR that targets another branch. Skipped for local branches, which carry no such data.

Gate types to configure:

| Type | Runs |
|---|---|
| `build`, `test` | the command detected from the project (`dotnet`, `npm`, `go`, `cargo`, Maven, Gradle, Python, `make`), or `run:` |
| `docwizz` | `docwizz check . --since $GITWIZZ_TARGET_SHA`: the documentation gaps the change introduces |
| `command` | any command, e.g. a security scanner or linter |
| `traceability` | the test suites the change needs ([trace](#test-selection-and-traceability-trace)) |
| `environment` | a per-PR deployment ([environments](#ephemeral-pr-test-environments)) |
| `ai-review` | a self-hosted model, advisory ([AI review](#ai-review)) |
| `system-context` | the [system context](#system-context-from-docwizz) policy; advisory, added automatically when `context.docwizz` requires context |

Commands run with `sh -c` in a temporary worktree of the merged state and get `GITWIZZ_PR`, `GITWIZZ_TARGET`,
`GITWIZZ_TARGET_SHA`, `GITWIZZ_HEAD_SHA` and `GITWIZZ_RISK`. Exit 0 passes, anything else fails; 126/127 (not
installed) and a timeout are errors. Compiler errors (`file(line,col): error CS1002: …`, `file:line: error …`) and
failed tests (`dotnet test`, pytest, `go test`) become findings with file and line. gitwizz runs these tools; it
doesn't reimplement them.

### Policy as code

Gates live in `.gitwizz.yml`, read **from the target branch's commit**. The report names the source
(`.gitwizz.yml@<commit>`).

```yaml
gates:
  - id: build                    # type defaults to the id when it names one
  - id: test
    needs: [build]               # skipped, and blocking, when build doesn't pass (command gates default to [merge])
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
secrets: [DEPLOY_TOKEN]          # values never appear in output, logs or JSON
```

The profile is `--profile`, else the one for the PR's **risk** (`high`: a `high_paths` file or more than `max_files`
files; `medium`: an API signature, migration or configuration change; else `low`), else `default`, else every gate.
Redefining `merge` or `policy` (e.g. `{ id: policy, blocking: false }`) changes the built-in. Values of `secrets` and of
`GITHUB_TOKEN`, `GH_TOKEN`, `ADO_PAT`, `SYSTEM_ACCESSTOKEN` are masked as `***` everywhere.

`--evidence <dir>` keeps each command gate's full log (`<gate>.log`) and the result (`evaluation.json`), which
references the logs: an audit trail for every verdict.

### In CI

Use the exit code as the merge check and keep the evidence as a build artifact:

```bash
gitwizz evaluate "$PR" -f json -o evaluation.json --evidence gitwizz-evidence
# 0 ready · 3 blocked (the change needs work) · 4 undetermined (fix the pipeline, not the PR)
```

Run `gitwizz doctor -f json` as an earlier step to fail fast on the runner's tooling (exit 6 or 7) before evaluating.

## Test selection and traceability: `trace`

```bash
gitwizz trace 42                                      # which tests the change needs and why; criteria and their tests
gitwizz trace 42 --run -f json                        # also run them on the merged state (schema gitwizz.trace/v1)
gitwizz trace 42 --run --evidence .gitwizz-evidence   # keep each suite's full log
```

A PR's linked work items (Azure DevOps work items, or the GitHub issues it closes) are split into individual
**acceptance criteria**, with ids like `AB#4711.2` or `#57.1`. Test suites are declared in `.gitwizz.yml`:

```yaml
tests:
  - id: billing
    run: dotnet test --filter Category=Billing
    files: ["tests/Billing*"]       # its test files: searched for changed names and requirement ids
  - id: ui
    kind: ui                        # unit, integration, contract, e2e, ui (informational) or manual (never run)
    run: npm run test:ui
    covers: ["src/Web/*"]           # a change to these selects it
  - id: contract
    run: ./scripts/contract-tests.sh
    criteria: ["AB#4711.2"]         # verifies this criterion ("AB#4711" = all of its criteria)
  - id: smoke
    run: ./scripts/smoke.sh
    always: true
  - id: e2e
    run: ./scripts/e2e.sh
    high_risk: true                 # also for every high-risk change (see risk:)
traceability:
  require: false                    # true: an uncovered or failed criterion blocks the merge
gates:
  - id: traceability                # runs the selected suites in the merged workspace
```

A suite is **selected**, with the reason listed, when it always runs, a changed file matches `covers`, its own test
files changed, its test files use a changed C# member or type (`tests/BillingTests.cs:12 uses CalculateTax
(changed)`), it verifies one of the PR's criteria, or the change is high-risk and the suite is `high_risk`. The rest
are reported as not affected. A PR runs what its impact and risk call for; full regression stays with release gates.

A suite **verifies** a criterion when `criteria:` lists it or its test files mention the id (`// AB#4711.1`,
`[Trait("Requirement", "AB#4711.1")]`). Each criterion ends up as:

| Status | Meaning |
|---|---|
| `covered` | a linked suite ran here and passed |
| `failed` | a linked suite ran and failed |
| `unknown` | linked, but not run in this evaluation (e.g. `trace` without `--run`) |
| `uncovered` | no test is linked to it |
| `manual` | only manual suites verify it, or the criterion is marked `[manual]` |

A link alone, including a generated test proposal, is never evidence: only a suite that ran and passed covers a
criterion. The `traceability` gate fails when a selected suite fails; uncovered criteria only warn unless
`traceability.require` is set. `evaluate` includes the whole trace in its JSON, and `explain` prints the criteria
matrix. `trace` exits `3` when a suite it ran didn't pass.

## Ephemeral PR test environments

An `environment` gate deploys the PR's merged state into its own isolated environment, so integration, contract, UI,
smoke and E2E tests run against deployable software, not just a build. The provisioner is Docker Compose (behind an
interface, so others can be added).

```yaml
environment:
  file: docker-compose.test.yml        # in the merged tree (default docker-compose.yml)
  env: { APP_MODE: test, SEED: demo }  # configuration and test data settings
  secrets: [DB_PASSWORD]               # passed through from gitwizz's environment; never printed or stored
  ready:
    - url: http://localhost:${GITWIZZ_PORT_WEB_8080}/health   # must answer 2xx
    - command: ./scripts/db-ready.sh                            # or exit 0
  ready_timeout: 180                   # seconds, for all checks together
  up_timeout: 900
  smoke: ./scripts/smoke.sh            # once ready
  keep: false                          # true: leave it running (remove with gitwizz env down)
gates:
  - id: environment
  - id: e2e
    run: npm run e2e                    # sees GITWIZZ_PORT_WEB_8080, GITWIZZ_ENV_PROJECT, ...
    needs: [environment]
profiles:
  fast: [build, test]                  # no provisioning for fast PR profiles
  full: [build, test, environment, e2e]
```

- **Isolation**: each PR gets its own compose project, `gitwizz-<repo>-<pr>`, so environments of several PRs coexist.
  Publish container ports without a host port (`ports: ["8080"]`): Docker picks a free one, exposed to later gates and
  readiness URLs as `GITWIZZ_PORT_<SERVICE>_<PORT>` (plus `GITWIZZ_ENV_PROJECT`, `GITWIZZ_ENV_HOST`).
- **Lifecycle**: leftovers of an earlier run are removed first; afterwards the environment is always torn down
  (`docker compose down -v --remove-orphans`), also when a gate failed. `gitwizz env down <pr>` does the same by hand.
- **Root causes**: a failed build or start (`deployment failed`), a readiness check that misses its deadline (`not
  ready after 180 s: <check>`) or a failing smoke check fails the gate, with output and service logs as evidence.
  Gates that `need` it are skipped and block with that cause. Docker missing, the daemon unreachable or a secret not
  set is an `error`, not a failure.
- **Secrets** reach only the provisioner and the commands, and are masked in every log, report and JSON.

## System context from docwizz

A diff alone shows code. [docwizz](https://github.com/mycaravam-crypto/docwizz) knows the system around it: who calls a
changed method, which endpoint reaches it, which data it writes and who else reads that data, which external systems
are involved, which documentation describes it and whether that documentation is still current, and which tests use
it. gitwizz reads this as data and puts the part that matters for the change into the [guide](#guided-workflow-guide),
the [AI evidence package](#ai-review) and `gitwizz context <pr> --system`. You never run docwizz yourself for a
review.

docwizz does the analysis; gitwizz selects, keeps provenance and decides trust. docwizz never talks to the AI.

**What gitwizz reads.** Two machine-readable docwizz outputs, never its console report:

| docwizz command | Used for | Reused |
|---|---|---|
| `docwizz scan <target checkout> <file>` | the code model of the target: symbols with file, line and hash, and relations (`calls`, `creates`, `accesses`, `persists`, `implements`, `connects`, `publishes`, `subscribes`, `tests`, `contains`); external systems tagged `detected` or `inferred`. Architecture layers come from the target's `docwizz.yaml` (first matching glob, as docwizz assigns them) | across PRs, until the target's tree or docwizz changes |
| `docwizz diff <target checkout> <target> <merged> --format json` | the changed, added and removed symbols; possibly stale docs (contract changed, doc comment didn't); new gaps, doc quality flags and architecture violations; affected doc pages; test code linked to each changed symbol | per PR, until the target or the merged state changes |

Both run in a temporary checkout of the target, so docwizz uses the target's `docwizz.yaml`, the way gitwizz uses
the target's `.gitwizz.yml`. A changed signature (docwizz: one id removed, one added under the same name) is one
changed symbol whose relations are looked up by its old id; a type whose members are listed is represented by them.

**Selection: the impacted subgraph.** Starting from the changed symbols, gitwizz takes, in this order: their module
and layer; direct callers (also through an interface the type implements, marked `inferred`) and callees; endpoints
that expose them or whose call chain reaches them within three levels (a `flow`, marked `inferred`); the data entities
they read, write or create, who else accesses those entities and who persists them; the external systems of their
types, of direct callees and of the stores behind their data; events they publish and who handles them; new
architecture violations and the decisions docwizz says the change implies. One more dependency hop is taken only for
a high-risk change, or for a neighbour an acceptance criterion or the PR title mentions. At most 8 relations of a kind
per symbol. Nothing else from the model is used: unrelated code, its tests and its external systems stay out.

**Provenance.** Every fact names its source (`docwizz`) and origin: `detected` (found in the code), `inferred`
(derived; may be wrong), and for documents `human-authored`, `generated` or `none`. Facts have ids (`F3`), documents
(`D1`) and tests (`T1`) too, so a finding can cite them.

**Documentation freshness.** gitwizz uses docwizz's signals, not file dates:

| Status | When |
|---|---|
| `current` | the changed symbol's doc comment passes docwizz's contract check (no change to parameters, return type, exceptions or route under an unchanged comment, no contradiction), or a docwizz-generated page this change regenerated |
| `stale` | docwizz reports the doc comment possibly stale (the contract changed, the comment didn't) or contradicting the code; a committed docwizz-generated page (`docs/`, with `docs/.docwizz/`) the change affects but didn't regenerate |
| `missing` | docwizz reports a new documentation gap for the symbol; a generated page that doesn't exist yet |
| `unknown` | docwizz only infers an inconsistency, or its diff couldn't run |

Each document keeps the reason, the affected symbol, the code or document fingerprint and the analyzed commit. A
stale doc comment carries the conflict itself ("may still describe the previous parameters: the current code is
authoritative"). gitwizz never rewrites documentation; the guide names the command (`docwizz generate .`) for you to run.

**Context quality**, shown before any test or AI review runs, always with its reasons:

| Quality | Means |
|---|---|
| `GOOD` | every changed symbol is mapped, owned by a layer (when layers are configured) and its documentation is current |
| `PARTIAL` | something is missing: a symbol without an architecture owner, a source file docwizz doesn't analyze, stale, missing or unverified documentation, or no docwizz diff (symbols then come from the changed lines) |
| `MISSING` | no docwizz analysis: docwizz isn't installed or is disabled, or it failed and nothing earlier is stored |
| `STALE` | docwizz failed now, and the facts are from an earlier analysis of another target commit |

These are different situations with different messages: docwizz found no relationship (context is available, the
guide says it found none), docwizz is not available, docwizz failed, the facts are stale, and context was cut to fit
the AI budget (reported under `budget.truncated`, not as a quality).

**Policy.** Missing context is advisory by default: nothing blocks, and the review continues with what is there.

```yaml
context:
  docwizz:
    enabled: true                                   # false: never run docwizz
    command: docwizz                                # or e.g. "dotnet tools/DocWizz.dll"
    timeout: 600                                    # seconds per docwizz run
    required: false                                 # true: MISSING or STALE context makes the verdict undetermined
    require_architecture_for_changed_symbols: false # true: a changed symbol in no docwizz.yaml layer blocks
```

With either requirement, a blocking `system-context` gate runs in every profile (after `merge`). Without them you can
still list `- id: system-context` under `gates` to see the context quality in every evaluation as an advisory result.

## AI review

An `ai-review` gate asks a **self-hosted**, OpenAI-compatible model (vLLM, llama.cpp server, Ollama, TGI, LocalAI, …)
to review the PR. It is independent of the deterministic gates and **advisory**: it never blocks, even with
`blocking: true`, until a [benchmark](#ai-quality-benchmark-when-ai-review-may-block) has validated the model and
prompt.

```yaml
gates:
  - id: traceability                # optional; list it first so the package includes the selected tests
  - id: review
    type: ai-review
review:
  endpoint: http://llm.internal:8000/v1   # gitwizz calls {endpoint}/chat/completions
  model: qwen2.5-coder-32b
  api_key_env: LLM_KEY              # variable holding the key, if needed (never in the file)
  allowed_hosts: []                 # self-hosted hosts whose names look public
  timeout: 120                      # seconds
  max_tokens: 2000                  # answer length
  max_context_chars: 60000          # evidence package size
  min_confidence: 0.5               # below: downgraded to info
  rules: ["Money is decimal, never double", "Controllers never call repositories directly"]
  rule_files: ["docs/CODING.md"]
  docs: ["docs/adr/*.md"]
```

**No public inference.** Known public AI services (OpenAI, Azure OpenAI, Anthropic, Google, Mistral, Groq, Bedrock,
OpenRouter, …) are refused, even when listed. Other endpoints must be loopback, a private IP, a single-label or
internal host name (`.internal`, `.corp`, `.lan`, `.local`, …), or listed in `allowed_hosts`; anything else is a
configuration error.

**Bounded evidence, not the repository.** The model gets one JSON package (`gitwizz.evidence/v1`; see it with
`gitwizz evidence 42 -o pkg.json`): PR title and description, linked requirements and acceptance criteria, changed
files, changed C# members and API changes, the diff (without lockfiles, generated and binary files), where changed
symbols are used outside the change (`git grep`), the configured rules, the paragraphs of `docs` that mention what
changed, the selected tests, and the [system context](#system-context-from-docwizz) (`systemContext`: changed symbols,
affected modules, callers, callees, API endpoints, data entities, external systems, architecture, flows, relevant
documents with their freshness, linked tests and whether their suite ran, each with id, source and origin; criteria
list the context they mention under `relatedContext`). Each section has a share of `max_context_chars` (system
context: 12 %, at least 1500 characters, most relevant first); what doesn't fit is cut and listed under
`budget.truncated`. The package's sha256 is part of the result, so every review traces back to its input.

**Repository content is data, never instructions.** The system prompt is fixed in code and versioned (`review-v2`).
The package goes in as JSON-encoded data inside `<evidence_package>` tags, with `<` and `>` escaped, so code or
comments can't close the block or pose as the system. Injection attempts are reported as `prompt-injection` findings.
The model never decides the outcome: gitwizz validates every finding and derives the status itself. The prompt tells
the model to cite the system context a finding rests on, to never claim a relationship that no fact shows, to treat
inferred facts and stale or unknown documents as unverified, and to trust current code over contradicting
documentation and report the contradiction.

| Finding | Result |
|---|---|
| names a file the PR doesn't change, quotes nothing, or quotes text that isn't in the diff | rejected (kept as evidence, not as a finding) |
| points to a line outside the changed lines (±3), or confidence below `min_confidence` | downgraded to `info` |
| cites system context (`"context": ["F3"]`) that isn't in the package | downgraded to `info` |
| cites only inferred facts, or only stale, unknown or missing documents (or the whole context is stale) | downgraded to `info` |
| otherwise | kept: `error` fails the gate, `warning` warns |

The result records the model the endpoint reported, prompt version and hash, token usage and the package hash. With
`--evidence <dir>`, the package is stored as `<gate>-package.json`. An unreachable endpoint, an HTTP error or an answer
without a JSON `findings` array is an `error`, not a verdict.

### AI quality benchmark: when AI review may block

`gitwizz benchmark` runs the configured model over **labelled cases** and scores it. Only a committed, passing
baseline for exactly this model and prompt lets an `ai-review` gate with `blocking: true` block.

```bash
gitwizz evidence 42 -o benchmark/rate-sign.json     # then add "expected": [...] by hand: a labelled case
gitwizz benchmark                                   # score every case, compare with the accepted baseline
gitwizz benchmark -f json -o bench.json             # schema gitwizz.benchmark/v1
gitwizz benchmark --accept                          # write the baseline; commit it to promote the gate
gitwizz benchmark --cases bench/ --baseline b.json  # override benchmark.cases / benchmark.baseline
```

A case is an evidence package plus `"expected"`: the findings a good review must report (`file`, new-side `line` ±3,
optionally `rule`); `[]` marks a clean change, where any finding is a false positive. Build cases from past PRs with
known outcomes: human review findings, defects found before or after merge, rule violations, and changes that drew
false alarms. `expected`, `id` and `notes` are stripped before the model sees the package. Example:
[docs/benchmark/negative-rate.json](docs/benchmark/negative-rate.json).

| Metric | Meaning |
|---|---|
| precision | reported findings (kept, not `info`) that match an expected one |
| recall | expected findings that were reported |
| false-positive rate | clean cases with any reported finding |
| evidence rejection rate | findings rejected for missing or invented evidence |
| `generatedTestPassRate`, `acceptanceCriterionCoverage`, `mutationScore` | `null`: not measured yet, rather than made up |

```yaml
benchmark:
  cases: benchmark                              # directory of *.json cases
  baseline: .gitwizz/benchmark-baseline.json
  thresholds:
    min_precision: 0.8
    min_recall: 0.5
    max_false_positive_rate: 0.1
    min_cases: 10
    max_regression: 0.02                         # allowed drop per metric against the baseline
```

**Promotion rule.** A blocking `ai-review` gate blocks only when the **target branch's commit** holds a baseline whose
fingerprint (model, prompt version, prompt hash) matches the current configuration and whose metrics meet the current
thresholds. Otherwise it runs advisory and says why (`ai-promotion` finding). A new model or prompt needs a new
benchmark, and a PR can't promote the gate that judges it.

The benchmark uses only the configured endpoint (same self-hosting rules), at temperature 0 with a fixed seed. It exits
`0` when thresholds are met without regressions, else `3`, and writes nothing but `-o` and, with `--accept`, the
baseline.

## Merge order: `plan`

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
[docs/sample-plan.html](docs/sample-plan.html).

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

### Planning configuration

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

### How planning works

```text
provider ─▶ analyze (files, hunks) ─▶ dependencies ─▶ readiness ─▶ beam search over simulations ─▶ report ─▶ verify
```

1. **Providers** ([Providers.cs](src/Gitwizz/Providers.cs), [AzureDevOps.cs](src/Gitwizz/AzureDevOps.cs)) fill one
   provider-neutral model, so the analysis never knows where a PR came from (see [Providers](#providers)).
2. **Analysis** ([Analyzer.cs](src/Gitwizz/Analyzer.cs)): changed files (with renames and deletes) and `-U0` hunks
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
6. **Simulation** ([Simulator.cs](src/Gitwizz/Simulator.cs)): `State₀ = target HEAD`, then
   `git merge-tree --write-tree State PR`. A clean merge becomes the next synthetic state (two parents for `merge`,
   one for `squash`; `ff-only` requires ancestry). `rebase` **replays each commit** with
   `merge-tree --merge-base=<parent>`, so a conflict in one commit blocks the PR even if the final tree would merge.
   Results are cached by `(state, PR head)`, and commits are only created for states the search keeps.
7. **Planner** ([Planner.cs](src/Gitwizz/Planner.cs)): beam search minimising `Σ marginalCost(PRᵢ | Stateᵢ)`, where

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

8. **Verification** ([Verify.cs](src/Gitwizz/Verify.cs)): `git worktree add --detach` on a plan state, run the
   command, remove the worktree. Only the chosen plan's states are verified, so the test count stays linear. The first
   failure stops verification and names the step ("FAILED after #108").

### Performance

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

## Providers

`--provider` is detected: `local` for branch names; for PR numbers and `--all-open`, `azure-devops` with an Azure
DevOps `origin` and `github` with a GitHub one (`--all-open` also needs `gh`), else `local`.

- **local** treats each branch or ref as a PR. Local branches carry no policy data, so reports say that only structural
  mergeability was checked.
- **github** reads PRs through `gh` (title, body, labels, draft, review decision, reviews, CI rollup, branch protection
  and rulesets, merge queue) and fetches `refs/pull/N/head`. Linked work items are the issues the PR closes.
- **azure-devops** (alias `ado`) reads an on-prem Azure DevOps Server through the `ado` CLI. Detected from an `origin`
  like `https://tfs.company.local/tfs/Coll/Proj/_git/repo` or `dev.azure.com`.

### Azure DevOps Server

All REST work is delegated to [`ado`](https://github.com/mycaravam-crypto/ado-devops): server, project, PAT, API
version (`ADO_API_VERSION`), TLS and proxy come from its configuration (`ado auth login`, `ADO_*` variables), so
gitwizz never handles or logs a credential. Set `GITWIZZ_ADO` to use an `ado` executable that isn't on the `PATH`.

| Data | From |
|---|---|
| PRs, refs, source commit, description, author | `ado pr list --json --status active` |
| review state | reviewer votes: rejected or waiting for author → changes requested; approved → approved |
| checks | the newest build per definition on `refs/pull/N/merge` (`ado build list --json`); none without build access |
| linked work items, acceptance criteria | `ado pr context N` names them, then `ado workitem show <id> --json` reads each in full (for `evaluate`, `trace` and `context`): `Microsoft.VSTS.Common.AcceptanceCriteria`, else an "Acceptance criteria" section in the description |

Other branch policies (minimum reviewer count, comment resolution, …) aren't visible through `ado`; reports say so.
The next step is `ado pr merge N --yes` (`--squash` for squash plans). `ado` errors keep their meaning: not
installed, not logged in (exit 3, with a hint), permission denied, not found.

### PR context as JSON

`gitwizz context 42 --system` returns the system context instead (`gitwizz.system-context/v1`): every selected fact,
document and linked test, unbounded by the AI budget, with the quality and its reasons.

`gitwizz context 42` returns one PR from any provider as one stable document (`gitwizz.context/v1`): the PR (title,
description, author, URL, draft, labels), source and target refs with commits, reviewers and votes, checks, policy
state (review decision, checks, problems, open dependencies), linked work items with their individual acceptance
criteria (GitHub: from the closed issues' "Acceptance criteria" section), changed files, changed C# members and
commits. Lists are sorted, so the same state gives the same document.

## Configuration reference

Everything is optional: without `.gitwizz.yml`, the built-in defaults apply. The file is read from the target branch's
commit. Patterns use `*` and `?` and match the file name, or the whole path when they contain a `/`.

| Keys | Section |
|---|---|
| `gates`, `profiles`, `risk`, `secrets` | [Policy as code](#policy-as-code) |
| `tests`, `traceability` | [Test selection and traceability](#test-selection-and-traceability-trace) |
| `environment` | [Ephemeral PR test environments](#ephemeral-pr-test-environments) |
| `review` | [AI review](#ai-review) |
| `context` | [System context from docwizz](#system-context-from-docwizz) |
| `benchmark` | [AI quality benchmark](#ai-quality-benchmark-when-ai-review-may-block) |
| `regenerators`, `generated`, `ignored`, `costs` | [Planning configuration](#planning-configuration) |

Unknown keys, empty patterns, negative costs, unknown gate types, duplicate gate or test ids and references to unknown
gates or profiles are errors.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | ok: ready, plan made, thresholds met |
| `1` | error, or failed `--verify` |
| `2` | usage error |
| `3` | `evaluate`/`explain`/`guide`: blocked by a failed gate · `trace`: a suite failed · `benchmark`: thresholds missed or regressed |
| `4` | `evaluate`/`explain`/`guide`: undetermined, a blocking gate could not run · `guide`: exited before a verdict |
| `5` | `doctor`: degraded, only advisory checks are affected |
| `6` | `doctor`: verdict at risk, a blocking gate cannot run |
| `7` | `doctor`: cannot start (git, repository, provider or `.gitwizz.yml`) · `guide`: git, the repository or the provider is unusable |

## Known limitations

- The workspace's tool fingerprint covers the program each command segment starts, not what that program starts in
  turn: a script in the repository is covered by the commit, but a tool it calls is not. After changing such tools
  underneath, run with `--no-cache` or `gitwizz cache clear`. Another gitwizz build invalidates everything stored.
- `status`, `refresh` and the repository guide read every open PR's linked work items, one or two provider calls per
  PR. The workspace is never pruned; `gitwizz cache clear` empties it.
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
docwizz system context for guided and AI-assisted review (#73), and the dependency preflight `doctor` (#75, part 1 of 3)
with its git and provider check at the start of `guide` (part 2; the gate dependencies in `guide` follow).

Known gaps from the epic: the benchmark reports generated-test pass rate, acceptance-criterion coverage and mutation
score as `null` (gitwizz doesn't generate tests yet); Azure DevOps branch policies other than reviewer votes and PR
builds aren't visible through `ado`; change impact is C#-only and doesn't check architecture rules (docwizz's
`architecture` layers do); merges are proposed, not executed.

Open, from [PLAN.md](PLAN.md): structural analysis beyond C#, and recording real merge outcomes (CI result,
resolution) next to the git-history replay.

## Contributing

```bash
dotnet build
dotnet test
```

The same commands work on Linux, macOS and Windows; [.github/workflows/test.yml](.github/workflows/test.yml) runs
them on every pull request and on main. [docwizz](https://github.com/mycaravam-crypto/docwizz) checks
this repository's `///` docs ([docwizz.yaml](docwizz.yaml)). On every pull request,
[.github/workflows/docwizz.yml](.github/workflows/docwizz.yml) comments with the documentation gaps the change
*introduces* and fails on a new critical gap; existing gaps don't fail it.

```bash
docwizz analyze .                        # every gap, ranked by how much it needs docs
docwizz check . --since origin/main      # only what your branch introduces, as CI runs it
```
