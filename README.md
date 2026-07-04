# Project Radar

Radar detects public companies whose business trajectory appears to be improving **before** that
improvement becomes obvious to mainstream retail investors. It is a **research assistant, not a
trading bot or recommendation engine**.

> Signals before stories. Evidence before opinions. AI assists. Humans decide.

- Product vision & pipeline: [`docs/radar-full-pipeline-spec.md`](docs/radar-full-pipeline-spec.md)
- Domain & persistence schema: [`docs/radar-schema-spec.md`](docs/radar-schema-spec.md)
- Principles & allowed output language: [`.claude/agents/radar-philosophy.md`](.claude/agents/radar-philosophy.md)

This README documents the **development workflow** — a spec-driven, agent-assisted pipeline that
turns small implementation specs into reviewed, CI-verified pull requests, mostly unattended.

---

## Contents

- [How the development workflow works](#how-the-development-workflow-works)
- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [The driver script: `run-next.ps1`](#the-driver-script-run-nextps1)
- [The agents](#the-agents)
- [The verification gates](#the-verification-gates)
- [The architecture-gated planner](#the-architecture-gated-planner)
- [Key files & directories](#key-files--directories)
- [Worktrees](#worktrees)
- [Day-to-day: common commands](#day-to-day-common-commands)
- [Gotchas](#gotchas)

---

## How the development workflow works

Work is sliced into **small implementation specs** (~1–2 hours each) that live in `docs/next/`.
A single PowerShell driver, `scripts/run-next.ps1`, advances the project one slice at a time:

```
                       ┌─────────────────────────────────────────────────────┐
   docs/next/ empty?   │  PLAN  (architecture-gated planner, interactive)     │
        ──────────────▶│  read decisions ledger → radar-architecture-reviewer │
                       │  HIGH/MEDIUM drift? → cleanup spec : feature spec(s)  │
                       └─────────────────────────────────────────────────────┘
                                            │  writes docs/next/NN-*.md
                                            ▼
   docs/next/ has a spec?  ┌──────────────────────────────────────────────────┐
        ─────────────────▶ │  IMPLEMENT  (headless, unattended)               │
                           │  reset a worktree to main → claude -p "run next" │
                           │  CLAUDE.md orchestrator: plan → branch →          │
                           │  radar-coder ↔ radar-code-reviewer loop →         │
                           │  commit → push → open PR → promote spec to docs/  │
                           └──────────────────────────────────────────────────┘
                                            │  (with -CopilotReview)
                                            ▼
                           ┌──────────────────────────────────────────────────┐
                           │  COPILOT REVIEW  (poll every 3 min, one cycle)    │
                           │  Copilot reviews PR → if comments, headless       │
                           │  fix pass commits to the same branch              │
                           └──────────────────────────────────────────────────┘
                                            ▼
                           ┌──────────────────────────────────────────────────┐
                           │  VERIFY & MERGE                                   │
                           │  CI (GitHub Actions) build+test must be green →   │
                           │  human merges the PR → spec is now in docs/       │
                           └──────────────────────────────────────────────────┘
```

Each implemented slice is verified by **three independent gates** (internal reviewer, Copilot, CI)
and only merges when green. When the spec queue empties, planning resumes — but first it runs an
**architecture audit** and fixes any structural drift before adding new features.

---

## Prerequisites

| Tool | Purpose | Notes |
|---|---|---|
| **.NET 10 SDK** | build/test | pinned in `global.json` |
| **PowerShell** | runs `scripts/*.ps1` | Windows PowerShell 5.1 is fine |
| **git** | version control | repo must be a git repo with an `origin` remote |
| **`claude` CLI** | the agent runtime | must be on `PATH` |
| **`gh` CLI** | PR + CI + Copilot APIs | run `gh auth status`; needs `repo` + `workflow` scopes |
| **GitHub repo** | remote | with **Actions enabled** and **Copilot code review** enabled (paid Copilot tier) for the `-CopilotReview` stage |

---

## Quick start

```powershell
# 1. (once) create N isolated worktrees: radar-claude-1, -2, -3
.\scripts\setup-claude-worktrees.ps1

# 2. advance the project one slice, with Copilot review + auto-fix:
.\scripts\run-next.ps1 -CopilotReview
#    - if docs/next/ has a spec  → implements the lowest-numbered one end-to-end, opens a PR
#    - if docs/next/ is empty     → launches the architecture-gated planner to queue new specs

# 3. review the PR; when all checks are green, merge it. Re-run step 2 for the next slice.
```

Sequential by design: implement one spec, merge its PR, then run the next. (The specs in a batch are
usually dependent, so they must go in order.)

---

## The driver script: `run-next.ps1`

`scripts/run-next.ps1` is the single entry point. On each run it:

1. **Syncs** the main repo to `origin/<default branch>` (fast-forward, only when on that branch and
   clean) so the spec-vs-planner decision uses the latest code.
2. **Decides**:
   - **Pending spec** in `docs/next/` → resets a worktree hard to `origin/main`, then dispatches a
     **headless, unattended** `claude -p "run next: <spec>"` that runs the `CLAUDE.md` orchestrator
     (implement → review loop → commit → push → open PR → promote the spec into `docs/`).
   - **No pending spec** (or `-Plan`) → launches the **interactive** architecture-gated planner
     (see [below](#the-architecture-gated-planner)).
3. **(Optional) Copilot stage** (`-CopilotReview`): after the PR opens, polls for Copilot’s first
   review; if it left inline comments, dispatches one headless fix pass on the same branch. One
   cycle, then exits.

### Parameters

| Parameter | Default | Purpose |
|---|---|---|
| `-CopilotReview` | off | after the PR opens, wait for Copilot’s first review and auto-fix its comments |
| `-CopilotPollSeconds` | `180` | poll interval while waiting for the review |
| `-CopilotTimeoutSeconds` | `1200` | give up waiting after this long |
| `-Plan` | off | force planner mode even if specs are pending |
| `-WorktreeIndex` | `1` | which `radar-claude-N` worktree to drive |
| `-PermissionFlag` | `--dangerously-skip-permissions` | set to `""` to be prompted for tool permissions instead of running fully unattended |
| `-ProjectName` / `-RepoPath` / `-WorktreeBase` / `-DefaultBranch` | derived / `main` | environment overrides |

> **Headless = trusted.** `--dangerously-skip-permissions` lets the unattended run use git/`gh`/
> `dotnet`/file edits without prompts. The safety net is the **merge gate** — nothing reaches `main`
> until a PR passes all checks and is merged. Use `-PermissionFlag ""` to supervise a run.

---

## The agents

Agent definitions live in `.claude/agents/` and are loaded automatically by `claude` in any session
or worktree. They split into **development agents** (drive the build pipeline) and **runtime/product
agents** (judge Radar’s actual outputs at run time).

### Development agents

| Agent | Role | When it runs | Tools |
|---|---|---|---|
| **`radar-work-planner`** | Splits master specs into small implementation specs in `docs/next/`. Architecture-gated: runs in **cleanup mode** (convert drift findings into a fix spec) or **feature mode** (next feature slices). Reads & respects the decisions ledger. | Planner mode (queue empty / `-Plan`) | Read, Write, Edit, Glob, Grep, Bash |
| **`radar-coder`** | Implements exactly one spec with the minimum change; keeps the solution buildable; preserves provenance; never calls AI provider SDKs outside Infrastructure. | `CLAUDE.md` Step 3 (per spec) | Read, Write, Edit, Bash, Glob, Grep |
| **`radar-code-reviewer`** | Reviews the coder’s change for **correctness against the spec**; independently re-runs build/test; returns exactly `APPROVED` or `ISSUES FOUND`. The in-loop gate. | `CLAUDE.md` Step 3 (per spec) | Read, Bash, Glob, Grep |
| **`radar-architecture-reviewer`** | Read-only, ad-hoc audit of the **whole codebase** for cross-slice drift (layering, DI/naming/error-handling consistency, duplication, provenance erosion). Reads the decisions ledger and never re-flags settled decisions. Severity (HIGH/MEDIUM/LOW) drives the planner gate. | Planner gate; or ad-hoc | Read, Grep, Glob, Bash |

### Runtime / product agents

These review **Radar’s product output** (extracted signals and emerging investment theses), not code.
They are invoked by the pipeline at run time once the relevant stages exist.

| Agent | Role | Tools |
|---|---|---|
| **`radar-signal-reviewer`** | Judges AI-extracted signals for evidence quality, materiality, novelty, company-resolution reliability, and hype risk. Returns structured JSON (`APPROVED` / `ISSUES_FOUND` / `REJECTED`). Decides whether a signal may contribute to scoring. | Read, Grep, Glob |
| **`radar-skeptic-reviewer`** | Devil’s advocate. Challenges an emerging company thesis (balance sheet, revenue quality, valuation, hype, governance, evidence weakness, thesis breakers) and returns a structured risk assessment. Stress-tests an opportunity before human investigation. | Read, Grep, Glob, WebSearch, WebFetch |

### Not an agent

- **`.claude/agents/radar-philosophy.md`** is a **reference document** (principles, allowed output
  language), not a runnable agent. It lives alongside the agents but has no frontmatter, so it isn’t
  registered as a subagent — that’s intentional.

> **Tip:** to register a new `.claude/agents/*.md` file as a usable subagent, it must start with a
> `---` frontmatter block containing at least `name` and `description` (and optionally `tools`).

---

## The verification gates

Every implemented slice passes through three independent checks before it can merge:

1. **`radar-code-reviewer`** (in-loop) — correctness vs the spec, re-runs build/test. Same model
   lineage as the coder, so it shares some blind spots.
2. **GitHub Copilot review** (`-CopilotReview`) — an *independent* external reviewer. Auto-triggers
   on PR open; guided by [`.github/copilot-instructions.md`](.github/copilot-instructions.md) (Radar’s
   provenance/layering/no-financial-advice rules). Has caught real bugs the in-loop reviewer approved.
3. **CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) — objective `dotnet build` +
   `dotnet test` on every PR and push to `main`, with `TreatWarningsAsErrors=true`. This is the
   trustworthy gate: it doesn’t depend on an agent self-reporting “tests pass.”

**Merge only when all three are green.** CI is not yet a *required* status check (no branch
protection) — that’s an optional hardening (`gh api` / Settings → Branches).

---

## The architecture-gated planner

Planning is **gated on architectural coherence — converge the trunk before expanding it.** When the
spec queue empties and the planner runs, it:

1. Reads [`docs/architecture-decisions.md`](docs/architecture-decisions.md) — the **decisions
   ledger** of consciously-accepted trade-offs.
2. Runs **`radar-architecture-reviewer`** over the whole codebase.
3. Branches on the findings:
   - **HIGH/MEDIUM drift** not covered by the ledger → **cleanup mode**: the work-planner emits one
     numbered *cleanup* spec; no new features this round.
   - **Only LOW / none** → **feature mode**: the work-planner emits the next 1–3 feature specs.

Two rules keep it from misbehaving:
- **Severity gate** — only HIGH/MEDIUM gate; LOW is informational (so it never loops on cosmetics).
- **Decisions ledger** — recorded decisions are never re-flagged (so it never ping-pongs). To change a
  decision, edit its entry in `docs/architecture-decisions.md`.

> Planner mode runs **interactively** (so you review the verdict and the generated specs, and so the
> session can spawn the reviewer/planner subagents). Run `.\scripts\run-next.ps1` with an empty queue
> to trigger it.

---

## Key files & directories

| Path | What it is |
|---|---|
| `CLAUDE.md` | The **orchestrator** prompt. Defines the per-spec pipeline (Step 0 spec selection → Step 1 plan → Step 2 branch → Step 3 coder/reviewer loop → Step 4 commit/push/PR/promote) plus the Radar hard rules. Loaded automatically by `claude` in the repo. |
| `docs/` | Master/reference specs + **completed** implementation specs (`01-*`…) + the decisions ledger. |
| `docs/next/` | **Pending** implementation specs (the queue). A spec moves `docs/next/ → docs/` when its PR is finalised. |
| `docs/architecture-decisions.md` | The **decisions ledger** (AD-1, AD-2, …). Settled trade-offs the reviewers/planner must respect. |
| `.claude/agents/` | Agent definitions (see [The agents](#the-agents)). |
| `.github/workflows/ci.yml` | CI build/test gate. |
| `.github/copilot-instructions.md` | Repository rules that guide Copilot’s reviews. |
| `scripts/run-next.ps1` | The driver (implement / plan / Copilot stage). |
| `scripts/run-radar.ps1` | Runs the **Worker** for a live measurement from a named JSON profile (`scripts/run-profiles/`, `default` = baseline; experiments overlay a delta and write to `data/experiments/<profile>/`). Not the dev driver — this runs the app. |
| `scripts/setup-claude-worktrees.ps1` | One-time worktree creation. |
| `src/` , `tests/` | The .NET 10 solution. |

---

## Worktrees

Implementation runs happen in **git worktrees** (`radar-claude-1`, `-2`, `-3`) — sibling folders that
share the repo’s history but check out their own branch. This keeps your main checkout untouched
while an unattended run edits files, builds, and commits on a feature branch.

- `setup-claude-worktrees.ps1` creates them on per-worktree base branches (`radar-claude-N-main`).
- `run-next.ps1` resets the chosen worktree **hard to `origin/main`** at the start of every run, so
  each slice begins from a clean, current base. (Uncommitted state in the worktree is discarded — it
  is a scratch area, not for manual edits.)
- Use `-WorktreeIndex` to drive a different worktree.

---

## Day-to-day: common commands

```powershell
# Advance one slice, fully autonomous incl. Copilot auto-fix:
.\scripts\run-next.ps1 -CopilotReview

# Advance one slice, but supervise tool permissions:
.\scripts\run-next.ps1 -CopilotReview -PermissionFlag ""

# Force the architecture-gated planner (even if specs are pending):
.\scripts\run-next.ps1 -Plan

# Drive a different worktree:
.\scripts\run-next.ps1 -CopilotReview -WorktreeIndex 2
```

Ad-hoc architecture audit (any `claude` session in the repo):

```
Run the radar-architecture-reviewer over the codebase.
```

Inspect a PR’s gates with `gh`:

```bash
gh pr checks <N>        # CI status
gh pr view <N> --json reviews   # reviewer + Copilot state
gh pr merge <N> --merge         # merge when green
```

---

## Gotchas

- **Sequential, merge-between.** A spec lives on `main` until its PR merges; re-running before merging
  picks the *same* spec again. Merge, then run the next.
- **`-CopilotReview` needs a paid Copilot tier** with code review enabled on the repo. Without it, the
  stage times out and finishes cleanly (no auto-fix), but the rest of the pipeline is unaffected.
- **CI re-runs on the Copilot fix commit**, which lands *after* `run-next.ps1` exits. So CI may still
  be “pending” when the script finishes — check `gh pr checks <N>` before merging.
- **Planner mode is interactive** — don’t expect it to run headless/in the background; run it in a
  terminal so you can review the verdict and generated specs.
- **Worktrees are scratch.** Don’t do manual work in `radar-claude-N`; `run-next.ps1` hard-resets them.
