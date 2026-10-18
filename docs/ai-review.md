# AI review

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
changed, the selected tests, and the [system context](system-context.md) (`systemContext`: changed symbols,
affected modules, callers, callees, API endpoints, data entities, external systems, architecture, flows, relevant
documents with their freshness, linked tests and whether their suite ran, each with id, source and origin; criteria
list the context they mention under `relatedContext`; with [requirement evidence](system-context.md#requirement-evidence-optional)
enabled, `systemContext.requirements`). Each section has a share of `max_context_chars` (system
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

## AI quality benchmark: when AI review may block

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
[docs/benchmark/negative-rate.json](benchmark/negative-rate.json).

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
