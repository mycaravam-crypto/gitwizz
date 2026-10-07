# PR Merge Optimizer

## 1. Ziel

Der PR Merge Optimizer ermittelt eine möglichst konfliktarme und technisch sinnvolle Reihenfolge zum Zusammenführen mehrerer Pull Requests in einen Ziel-Branch.

Das Tool optimiert nicht einfach nach PR-Größe oder Dateioverlappung, sondern simuliert die Auswirkungen möglicher Merge-Reihenfolgen.

Zielgrößen:

* Minimierung erwarteter Merge-Konflikte
* Minimierung zukünftiger Rebase-/Resolve-Arbeit
* Einhaltung expliziter und erkannter Dependencies
* Priorisierung bereits merge-fähiger PRs
* Minimierung unnötiger CI-/Rebuild-Kosten
* Erkennung von PRs, deren Reihenfolge kritisch ist

Das Ergebnis ist ein begründeter Merge-Plan, keine bloße Sortierung.

---

# 2. Grundmodell

Das System betrachtet drei voneinander getrennte Modelle.

## 2.1 Dependency Graph

Explizite oder erkannte Abhängigkeiten:

```text
PR-101 ─────> PR-105
PR-108 ─────> PR-101
```

Dieser Graph muss azyklisch sein.

Quellen:

* PR base/head Beziehungen
* Stack-PR-Strukturen
* Commit-Ancestry
* Referenzen in PR-Beschreibungen
* `depends-on` Labels
* erkannte API-/Typ-Abhängigkeiten
* Migration-Abhängigkeiten

---

## 2.2 Conflict Graph

Der Konfliktgraph ist ein gewichteter, ungerichteter Graph:

```text
         0.85
PR-101 ──────── PR-108
   │             │
 0.10          0.42
   │             │
PR-105 ──────────┘
       0.05
```

Die Kante beschreibt nicht nur "gleiche Datei", sondern ein Konfliktrisiko.

Beispiel:

```text
ConflictWeight(A,B) =
    fileOverlap
  + hunkOverlap
  + renameRisk
  + deleteRisk
  + generatedFileRisk
  + lockfileRisk
  + historicalConflictRate
```

---

## 2.3 Readiness / Policy Model

Ein PR kann trotz geringer Konfliktwahrscheinlichkeit nicht merge-fähig sein.

Beispielsweise:

```text
READY
├── not draft
├── required approvals satisfied
├── CI successful
├── branch protection satisfied
├── mergeable
└── dependencies satisfied
```

Diese Bedingungen sind Constraints und sollten nicht Bestandteil eines normalen Konflikt-Scores sein.

---

# 3. PR Representation

Jeder PR wird intern normalisiert:

```text
PullRequest
├── Id
├── Provider
├── HeadSha
├── BaseSha
├── BaseBranch
├── MergeStrategy
├── Files
├── Hunks
├── Additions
├── Deletions
├── Renames
├── Deletes
├── Age
├── Divergence
├── Dependencies
├── CIStatus
├── ApprovalStatus
├── MergeableState
└── Metadata
```

Wichtig:

`Age` ist nur ein schwacher Indikator.

Deutlich relevanter ist:

```text
Target changes since PR base
```

insbesondere wenn diese Änderungen dieselben Regionen betreffen wie der PR.

---

# 4. Analyse-Pipeline

```text
                    Provider
                       │
                       ▼
                PR / Commit Data
                       │
                       ▼
              Repository Analyzer
                       │
          ┌────────────┼─────────────┐
          ▼            ▼             ▼
     Dependencies   Conflicts     Readiness
          │            │             │
          └────────────┼─────────────┘
                       ▼
                 Plan Engine
                       │
                       ▼
                Merge Simulation
                       │
                       ▼
                  Verification
                       │
                       ▼
                    Report
```

---

# 5. Dependency Analysis

Dependencies sollten in verschiedene Kategorien aufgeteilt werden.

## Explicit

Beispiel:

```text
PR-108 depends-on PR-101
```

## Structural

PR B basiert direkt oder indirekt auf PR A.

```text
main
 └── A
      └── B
           └── C
```

Hier sollte das Tool erkennen:

```text
A → B → C
```

## Semantic

Beispiel:

PR A:

```csharp
UserService.GetUser(id)
```

PR B:

```csharp
UserService.GetUser(id, tenant)
```

Beide PRs können jeweils für sich kompilieren, aber eine Reihenfolge ist trotzdem sinnvoll.

Semantic Dependency sollte in Version 2 über AST/Symbolanalyse ergänzt werden.

---

# 6. Conflict Analysis

Analyseebenen:

### Level 1 – File overlap

```text
A.files ∩ B.files
```

Sehr billig und als erster Filter geeignet.

### Level 2 – Hunk overlap

Analyse von:

```bash
git diff -U0
```

und Bestimmung sich überschneidender Regionen.

### Level 3 – structural overlap

Für unterstützte Sprachen:

```text
class
method
property
interface
enum
configuration key
```

Beispiel:

```text
A modifies UserService.Login()
B modifies UserService.Login()
```

ist wesentlich aussagekräftiger als:

```text
A and B both modify UserService.cs
```

### Level 4 – semantic risk

Optional:

```text
API signature changes
database schema changes
configuration changes
dependency changes
contract changes
generated artifacts
```

---

# 7. Special File Classes

Dateien sollten klassifiziert werden.

```text
NORMAL
GENERATED
LOCKFILE
MIGRATION
CONFIGURATION
BINARY
SUBMODULE
```

Beispiele:

```text
package-lock.json
pnpm-lock.yaml
yarn.lock
*.Designer.cs
*.g.cs
database migrations
```

Diese Dateien sollten nicht pauschal ignoriert werden.

Stattdessen:

```text
LOCKFILE
    ↓
regenerate after merge
```

Das ist wesentlich sinnvoller als einen echten Konflikt einfach aus dem Modell zu entfernen.

---

# 8. Merge Simulation

Die Simulation arbeitet auf einem synthetischen Commit-Graph.

Initial:

```text
S0 = target HEAD
```

Für jeden Kandidaten:

```text
git merge-tree --write-tree S0 PR_HEAD
```

liefert den simulierten Merge-Tree ohne Working Tree oder Index anzufassen. Git verwendet dabei dieselben grundlegenden Merge-Mechanismen wie ein normaler Merge.

Bei erfolgreichem Merge:

```text
S0 + PR_A
      │
      ▼
     S1
```

Ein synthetischer Commit repräsentiert anschließend den Zustand:

```text
S1 = synthetic merge commit
```

Danach:

```text
S1 + PR_B
      │
      ▼
     S2
```

Dadurch bleibt die Merge-Historie für die weitere Simulation erhalten.

---

# 9. Merge Strategy

Die Simulation muss die tatsächliche Zielstrategie kennen.

Unterstützte Modi:

```text
--strategy merge
--strategy squash
--strategy rebase
--strategy ff-only
```

Denn dieselben PRs können je nach Merge-Strategie unterschiedliche Ergebnisse liefern.

Für GitHub sollte zusätzlich berücksichtigt werden:

```text
merge queue
branch protection
required checks
required reviews
```

GitHub berechnet die Mergebarkeit eines PRs serverseitig und unterstützt inzwischen auch asynchrone Merge-/Merge-Queue-Abläufe.

Das Tool sollte daher bei konfigurierter Merge Queue primär einen Plan liefern und nicht versuchen, deren Logik zu ersetzen.

---

# 10. Optimierungsalgorithmus

Statt eines statischen PR-Scores wird ein zustandsabhängiger Score verwendet.

```text
Cost(PR | CurrentState)
```

Beispiel:

```text
Cost =
    conflictCost
  + dependencyPenalty
  + stalenessCost
  + semanticRisk
  + verificationCost
  - unblockBonus
  - readinessBonus
```

Ein PR mit hoher allgemeiner Konfliktzahl kann deshalb trotzdem als nächstes optimal sein, wenn er gerade besonders günstig mergebar ist.

---

# 11. Candidate Selection

Algorithmus:

```text
state = target

while unmerged PRs remain:

    candidates = PRs satisfying dependencies and readiness

    for each candidate:

        result = simulateMerge(state, candidate)

        if merge fails:
            calculate conflict cost
        else:
            calculate marginal cost

    choose candidate with lowest expected total cost

    state = resulting synthetic commit
```

---

# 12. Lookahead

Eine reine Greedy-Strategie kann lokale Optima erzeugen.

Beispiel:

```text
A is cheap now
B is slightly more expensive now
C depends on B
```

A zuerst kann dazu führen:

```text
A → B → conflict → C
```

während:

```text
B → C → A
```

insgesamt günstiger wäre.

Daher sollte das Tool optional eine begrenzte Lookahead-Suche verwenden.

Empfehlung:

```text
Greedy
    +
Beam Search
    +
Local Swap Optimization
```

Beispiel:

```text
beam width = 8
lookahead = 3
```

Für kleine Mengen kann später eine vollständige Suche verwendet werden.

---

# 13. Simulation Cache

Ein wichtiger Performance-Baustein:

```text
Cache Key:

CurrentStateSha + PRHeadSha + MergeStrategy
```

Resultat:

```text
SimulationResult
├── mergeable
├── treeSha
├── conflictFiles
├── conflictHunks
├── conflictTypes
└── syntheticCommit
```

Dadurch müssen identische Simulationen nicht erneut ausgeführt werden.

---

# 14. Parallelization

Disjunkte PRs sollten nicht einfach als "Batch 1" bezeichnet werden.

Besser:

```text
Currently independent candidates:
    PR-101
    PR-105
    PR-112
```

Diese PRs sind **parallelisierbar**, sofern keine Policy-/Dependency-Abhängigkeit besteht.

Nach jedem tatsächlich erfolgten Merge muss die Situation allerdings neu bewertet werden.

---

# 15. Semantic Verification

`merge-tree` beweist nur:

```text
Git can construct the merged tree
```

nicht:

```text
The software still works
```

Deshalb:

```text
Phase 1
    structural merge validation

Phase 2
    optional build

Phase 3
    optional unit tests

Phase 4
    optional integration tests
```

Für echte Tests wird ein isolierter temporärer Worktree benötigt.

Git unterstützt dafür dedizierte linked worktrees und detached Worktrees, die sich für temporäre Testzustände eignen.

Beispiel:

```text
synthetic commit
      │
      ▼
temporary worktree
      │
      ├── restore dependencies
      ├── build
      ├── test
      └── destroy
```

Tests sollten nicht nach jedem einzelnen PR zwingend laufen.

Konfigurierbare Modi:

```text
--verify none
--verify final
--verify batch
--verify critical
```

---

# 16. Historical Intelligence

Eine spätere Ausbaustufe sollte vergangene Merge-Erfahrungen erfassen:

```text
PR type
files
overlap
merge strategy
actual conflict
resolution
CI result
```

Damit kann das Tool empirische Werte aufbauen:

```text
UserService.cs
+ AuthController.cs
+ database migration
```

hat beispielsweise historisch eine hohe Konfliktrate.

Das macht aus:

```text
heuristic tool
```

langfristig:

```text
repository-specific optimizer
```

---

# 17. Recommended Architecture

```text
gitwizz/
├── cmd/
│   └── root.go
│
├── internal/
│   ├── model/
│   │   ├── pullrequest.go
│   │   ├── dependency.go
│   │   ├── conflict.go
│   │   └── simulation.go
│   │
│   ├── provider/
│   │   ├── provider.go
│   │   └── github/
│   │
│   ├── git/
│   │   ├── repository.go
│   │   ├── diff.go
│   │   ├── merge.go
│   │   ├── commit.go
│   │   └── worktree.go
│   │
│   ├── analyzer/
│   │   ├── files.go
│   │   ├── hunks.go
│   │   ├── dependencies.go
│   │   ├── conflicts.go
│   │   └── semantic.go
│   │
│   ├── optimizer/
│   │   ├── planner.go
│   │   ├── greedy.go
│   │   ├── beamsearch.go
│   │   └── cache.go
│   │
│   ├── verification/
│   │   ├── build.go
│   │   └── tests.go
│   │
│   ├── policy/
│   │   └── rules.go
│   │
│   └── reporter/
│       ├── tui.go
│       └── json.go
│
└── main.go
```

---

# 18. Technology Recommendation

Für dieses Projekt würde ich **C#/.NET 10** bevorzugen.

Gründe:

* sehr gute CLI-Unterstützung
* einfache parallele Simulation
* sehr gute JSON-Unterstützung
* gute AST-/Compiler-Integrationen
* schnelle Entwicklung
* Single-file deployment möglich
* passt gut zu bestehenden .NET-Codebasen

.NET 10 ist aktuell die LTS-Version und wird bis November 2028 unterstützt.

Für Git würde ich zunächst **Git als externen Prozess** verwenden und nicht LibGit2Sharp.

Grund:

```text
Tool
  ↓
git merge-tree
git diff
git rev-list
git merge-base
git commit-tree
git worktree
```

Damit entspricht die Simulation möglichst direkt dem Git-Verhalten.

---

# 19. CLI

Beispiel:

```bash
gitwizz plan \
  --target main \
  --prs 101,102,105,108,112 \
  --strategy squash \
  --lookahead 3
```

Automatisch:

```bash
gitwizz plan \
  --target main \
  --all-open \
  --provider github
```

Verification:

```bash
gitwizz plan \
  --target main \
  --all-open \
  --verify "dotnet test"
```

Maschinenlesbar:

```bash
gitwizz plan \
  --target main \
  --all-open \
  --format json
```

---

# 20. Output

```text
PR MERGE PLAN
Target: main
Strategy: squash

1  #105  docs update
   Cost: 0
   Reason: no overlapping changes

2  #102  auth session fix
   Cost: 0.08
   Reason: independent from #105

3  #101  billing service
   Cost: 0.31
   Reason: unlocks #108

4  #108  database refactor
   Cost: 0.74
   Dependency: #101

5  #112  dependency update
   BLOCKED
   Reason: lockfile conflict after #108
```

Zusätzlich:

```text
Why #101 before #108?

#108 modifies:
    BillingService.cs
    BillingRepository.cs

#101 modifies:
    BillingService.cs

Simulation:

101 → 108  = clean
108 → 101  = conflict

Recommended ordering:
101 → 108
```

Das ist wesentlich wertvoller als lediglich:

```text
#101 score = 82.4
```

---

# 21. Final Optimization Objective

Das eigentliche Ziel des Systems sollte lauten:

```text
Find sequence S minimizing:

Σ marginalMergeCost(PR_i | State_i)

subject to:

- dependency constraints
- readiness constraints
- merge strategy
- repository policies
```

mit:

```text
State_0 = target HEAD

State_i = merge(State_(i-1), PR_i)
```
