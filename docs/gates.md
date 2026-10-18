# Merge readiness, test selection and test environments

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
| Collect context and linked requirements | the provider loads the PR, reviews, checks and linked work items with their acceptance criteria ([`context`](providers.md#pr-context-as-json)) |
| Calculate change impact | changed files and C# members, API changes, and the PR's **risk** (`low`/`medium`/`high`, with reasons) |
| Read the system context | what docwizz knows around the change, its documentation's freshness, and the context quality ([system context](system-context.md)) |
| Select applicable gates | the profile from `--profile` or the risk; gates whose `paths` don't match are skipped |
| Run deterministic checks and tests | merge simulation, policy, build, test, docwizz, commands, the test environment, and the test suites the change selects ([`trace`](#test-selection-and-traceability-trace)) |
| Run AI-assisted review | an `ai-review` gate, if configured, on the [bounded evidence package](ai-review.md), system context included |
| Aggregate evidence | one result per gate; `--evidence` keeps logs, packages and `evaluation.json` |
| Evaluate the readiness policy | blocking vs advisory gates → `ready`, `blocked` or `undetermined` |
| Explain blockers | `explain`: every blocking decision with its evidence, and the criteria matrix |
| Optimize the merge | [`plan`](planning.md): the merge order and the next merge command. gitwizz proposes the command; it doesn't run it |

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
| `ai-review` | a self-hosted model, advisory ([AI review](ai-review.md)) |
| `system-context` | the [system context](system-context.md) policy; advisory, added automatically when `context.docwizz` requires context |

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
    cache_inputs: [~/.tool-versions, /opt/ci/e2e.env]  # files outside the commit the result depends on (see below)
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

A build, test, command or docwizz gate's result is [reused](workflow.md#repository-workspace-status-refresh-cache)
while its merged state, definition, command, environment and tools are unchanged. Files in the commit are part of the
merged state; `cache_inputs` names files outside it that the command also reads (a shared script, a machine config, a
tool-version file). Their path, mode and content become part of the gate's cache inputs, so a change reruns that gate
only. Relative paths resolve against the repository root, `~/` against the home directory. A missing or unreadable
entry (a directory included) means the result can't be proven reusable: the gate runs every time and nothing is kept.

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
