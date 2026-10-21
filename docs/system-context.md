# System context from docwizz

A diff alone shows code. [docwizz](https://github.com/mycaravam-crypto/docwizz) knows the system around it: who calls a
changed method, which endpoint reaches it, which data it writes and who else reads that data, which external systems
are involved, which documentation describes it and whether that documentation is still current, and which tests use
it. gitwizz reads this as data and puts the part that matters for the change into the [guide](workflow.md#guided-workflow-guide),
the [AI evidence package](ai-review.md) and `gitwizz context <pr> --system`. You never run docwizz yourself for a
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

## Requirement evidence (optional)

The system context can also say which requirements the changed symbols are linked to, from docwizz's requirement
resolver (fed by [`history`](history/README.md#usage) and your evidence files). gitwizz keeps no
requirement index of its own: it asks one read-only command, validates and bounds the answer, and keeps each link's
provenance. **Off by default, and advisory**: it never changes a gate, the context quality or the verdict.

```yaml
context:
  requirements:
    enabled: false                  # true: ask for requirement evidence of the changed symbols
    command: docwizz requirements query   # the resolver; gets <repository dir> <query file>, prints JSON on stdout
    timeout: 300                    # seconds
    titles: true                    # false: work item titles stay out of the AI evidence package
```

With docwizz, export the history and import it once, then point the command at `docwizz requirements query`
([docwizz#173](https://github.com/mycaravam-crypto/docwizz/issues/173)), which answers from that index without network
access:

```bash
gitwizz history --provider azure-devops -o history.json
docwizz requirements import . history.json work-items/
```

The command gets a query file (`gitwizz.requirement-query/v1`: the target and PR commits and the changed symbols with
their docwizz ids) and must print `gitwizz.requirement-input/v1`:

```json
{
  "schema": "gitwizz.requirement-input/v1",
  "tool": "docwizz 1.9.0",
  "analyzedCommit": "<the target commit it answered for>",
  "links": [
    { "symbol": "<a queried symbol id>", "requirement": "AB#4711", "type": "verified", "title": "Cancel orders",
      "revision": 12, "source": "azure-devops", "evidence": ["commit 1a2b3c names AB#4711"], "freshness": "current" },
    { "symbol": "<a queried symbol id>", "requirement": "AB#4790", "type": "candidate", "confidence": 0.6, "source": "commit-history" }
  ],
  "unavailable": [{ "source": "azure-devops", "reason": "server not reachable" }]
}
```

- **verified** links are independently traceable (the symbol's commits or an explicit evidence record name the
  requirement); **candidate** links are inferred and may be wrong. Both keep their source, work item revision,
  evidence and freshness. A link is never test coverage: traceability still needs executed tests.
- **Status** is `available`; `partial` when a source couldn't be read (an Azure DevOps server offline is named, so an
  empty list never reads as "no requirements"); `unavailable` (no command, not installed, or no system context to ask
  about); or `failed` (non-zero exit, timeout, not JSON, another schema). **Freshness** is `stale` when the answer was
  computed for another commit than the target.
- **Untrusted input.** Links to symbols that weren't asked about, malformed requirement ids and unknown types are
  dropped (counted in `dropped`); titles and evidence are cut to one line of at most 200 characters with control
  characters removed. In the evidence package they are data under a note saying so, within their own share of the
  budget (6 % of `max_context_chars`, at least 2000 characters, verified links first, two evidence entries per link);
  what doesn't fit is listed under `budget.truncated`.
- **Not stored.** Answers aren't kept in the workspace, as work items change without a commit. The answer's
  fingerprint is part of the evidence package, so a stored AI review is reused only for the same requirement evidence.

`gitwizz context <pr> --system` shows the full answer under `requirements` (only when enabled); `guide` shows it in the
system context step; `doctor` reports the command as missing or unset (degraded, never a risk to the verdict).
