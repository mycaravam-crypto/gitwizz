# gitwizz

A PR quality and merge orchestrator. It answers two questions about pull requests, with evidence you can reproduce:

1. **Is this PR ready to merge, and if not, why?** `evaluate` runs the repository's quality gates (merge simulation,
   review policy, build, tests, traced acceptance criteria, a per-PR test environment, docs, AI review, any command)
   and returns one verdict: *ready*, *blocked* or *undetermined* (a gate couldn't run, so nothing is known).
2. **In which order should a set of PRs merge?** `plan` simulates the merges with real git and searches for the order
   with the fewest conflicts.

It works with GitHub, Azure DevOps Server and plain local branches, and never touches your working tree: merges are
simulated with `git merge-tree`, commands run in temporary worktrees. Policy is read from the target branch, so a PR
can't relax the gates it is judged by. AI review only talks to self-hosted models and stays advisory until a
benchmark validates it. Design rationale: [PLAN.md](PLAN.md) (German).

## Install

Needs the .NET 10 SDK and git ≥ 2.38 (≥ 2.40 for `--strategy rebase`). Providers need an authenticated
[`gh`](https://cli.github.com) (GitHub) or [`ado`](https://github.com/mycaravam-crypto/ado-devops) (Azure DevOps
Server, `ado auth login`). Gates need the tools they run, e.g. Docker for test environments; `gitwizz doctor` checks
all of it.

```bash
# Linux, macOS, Git Bash
dotnet publish src/Gitwizz -c Release -o ~/tools/gitwizz
echo "alias gitwizz='dotnet ~/tools/gitwizz/gitwizz.dll'" >> ~/.bashrc   # zsh: ~/.zshrc
```

```powershell
# Windows PowerShell
dotnet publish src/Gitwizz -c Release -o "$HOME\tools\gitwizz"
if (!(Test-Path $PROFILE)) { New-Item -Type File -Force $PROFILE }
Add-Content $PROFILE 'function gitwizz { dotnet "$HOME\tools\gitwizz\gitwizz.dll" @args }'
```

Re-run `dotnet publish` after pulling. A `gitwizz.cmd` for cmd and a faster single-file binary:
[docs/install.md](docs/install.md).

## Quick start

```bash
dotnet run --project src/Gitwizz -- example   # builds a demo repository with seven branches and plans it
```

In your own repository:

```bash
gitwizz guide                    # which PRs are ready, blocked or stale, and what to do next
gitwizz guide 57                 # step by step: context, system context, test impact, tests, verdict, next step
gitwizz evaluate 57              # is PR 57 ready? (exit 0 ready, 3 blocked, 4 undetermined)
gitwizz explain 57               # every blocking decision, with evidence
gitwizz --all-open               # merge order for all open PRs (plan is the default command)
```

## Commands

| Command | Answers |
|---|---|
| `guide [<pr>] [--yes]` | What do I do next? The repository overview, or one PR walked from context to verdict |
| `doctor` | Is the environment ready? git, provider login, docwizz, AI endpoint, Docker, gate tools, and which gates they affect |
| `status` | Which open PRs are ready, blocked, undetermined, stale or never evaluated? |
| `refresh [--all-open]` | Re-evaluates what is stale, reusing the rest |
| `cache status\|clear` | The workspace of stored results (under `.git/gitwizz/`) |
| `evaluate <pr>` | Is it ready? Runs the gates and prints the verdict (`-f pretty\|text\|json`, `--evidence <dir>` keeps logs) |
| `explain <pr>` | Why (not)? Blocking decisions, evidence, the acceptance criteria matrix |
| `trace <pr> [--run]` | Which tests does the change need, and which acceptance criteria do they cover? |
| `context <pr> [--system]` | The PR's normalized context as JSON (open, merged or closed); `--system`: the system context from docwizz |
| `evidence <pr>` | The bounded package an AI review would see, without contacting a model |
| `benchmark [--accept]` | How good is the AI review on labelled cases? `--accept` saves the baseline that lets it block |
| `history export` | Which commits changed which lines, with their PRs and work items ([`gitwizz.history/v2`](docs/history/README.md)) |
| `version-history list\|export` | Released tags and open `release/*` branches with the PRs each shipped, as a changelog ([`gitwizz.version-history/v2`](docs/version-history/README.md)) |
| `env down <pr>` | Removes a PR's test environment |
| `plan` (default) | In which order should these PRs merge? (`-a` all open, `-p 101,102`, `-s squash`, `--verify "cmd"`, `-o plan.html`) |
| `example` | Builds a demo repository and plans it |
| `help`, `version` | All options with examples; the version |

`<pr>` is a GitHub or Azure DevOps PR number, or a local branch name. Common options: `-t/--target`, `--provider`
(`local`, `github`, `azure-devops`; detected from `origin`), `--profile`, `-f/--format`, `-o/--output`, `-r/--repo`,
`--no-cache`. Every report ends with the next step as a ready-to-run command; gitwizz proposes merges, it never runs
them.

The full reference is in [docs/](docs/README.md): [guide, doctor and the workspace](docs/workflow.md),
[gates and tests](docs/gates.md), [system context](docs/system-context.md), [AI review](docs/ai-review.md),
[merge planning](docs/planning.md), [providers](docs/providers.md), and the
[history](docs/history/README.md) and [version history](docs/version-history/README.md) contracts.

## Configuration

Everything is optional: without `.gitwizz.yml`, `plan` works as is and the built-in `merge` and `policy` gates run.
The file is read from the target branch's commit. A starting point:

```yaml
gates:
  - id: build                    # build/test detect dotnet, npm, go, cargo, Maven, Gradle, Python, make; or set run:
  - id: test
    needs: [build]
  - id: docs
    type: docwizz                # the documentation gaps the change introduces
    blocking: false              # advisory
  - id: e2e
    type: command                # any command; also traceability, environment, ai-review, system-context
    run: ./scripts/e2e.sh
    paths: ["web/*"]
profiles:
  fast: [build]
  default: [build, test, docs]
  full: [build, test, docs, e2e]
risk:
  high_paths: ["src/Billing/*"]
  profiles: { low: fast, medium: default, high: full }
secrets: [DEPLOY_TOKEN]          # masked in every output, log and JSON
```

| Keys | Reference |
|---|---|
| `gates`, `profiles`, `risk`, `secrets` | [Policy as code](docs/gates.md#policy-as-code) |
| `tests`, `traceability` | [Test selection and traceability](docs/gates.md#test-selection-and-traceability-trace) |
| `environment` | [Ephemeral PR test environments](docs/gates.md#ephemeral-pr-test-environments) |
| `review`, `benchmark` | [AI review](docs/ai-review.md) |
| `context` | [System context from docwizz](docs/system-context.md) |
| `regenerators`, `generated`, `ignored`, `costs` | [Planning configuration](docs/planning.md#planning-configuration) |

Patterns use `*` and `?` and match the file name, or the whole path when they contain a `/`. Unknown keys, unknown
gate types and duplicate ids are errors; `gitwizz doctor --working-tree` validates a local edit before you commit it.

In CI, use the exit code as the merge check and keep the evidence:

```bash
gitwizz doctor -f json        # optional: fail fast on missing runner tools (exit 6 or 7)
gitwizz evaluate "$PR" -f json -o evaluation.json --evidence gitwizz-evidence
```

A rendered merge plan: [docs/sample-plan.html](docs/sample-plan.html).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | ok: ready, plan made, thresholds met |
| `1` | error, or failed `--verify` |
| `2` | usage error |
| `3` | `evaluate`/`explain`/`guide`: blocked · `trace`: a suite failed · `benchmark`: thresholds missed |
| `4` | `evaluate`/`explain`/`guide`: undetermined, a blocking gate could not run · `guide`: exited before a verdict |
| `5` | `doctor`: degraded, only advisory checks are affected |
| `6` | `doctor`: verdict at risk, a blocking gate cannot run |
| `7` | `doctor`/`guide`: cannot start (git, repository, provider or `.gitwizz.yml`) |

## Development

```bash
dotnet build
dotnet test
docwizz check . --since origin/main      # the documentation gaps your branch introduces, as CI runs it
```

All three run on every pull request; [docwizz](https://github.com/mycaravam-crypto/docwizz) checks the `///` docs
against [docwizz.yaml](docwizz.yaml). Every merge to `main` is released as the next
SemVer tag; a PR label picks the bump: `release:patch` (default), `release:minor`, `release:major` or `release:none`.
Principles, known limitations and the roadmap: [docs/README.md](docs/README.md).
