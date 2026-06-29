# Task: Strip HTML markup and decode entities in evidence normalization

## Overview

Real RSS/Atom feeds almost always deliver their item body as **HTML**: `<p>`, `<a href=…>`, `<br/>`,
`<ul><li>`, plus character entities like `&amp;`, `&#8217;`, `&nbsp;`. `SyndicationFeed` returns that
markup verbatim in `item.Summary.Text`, the collector copies it into `CollectedEvidence.RawText`, and
`EvidenceNormalizer` — which today only normalizes line endings and whitespace — passes the tags and
entities straight through into the immutable `EvidenceItem.RawText` and its `ContentHash`.

That HTML then pollutes everything downstream:

- **Signal extraction** (`KeywordSignalExtractor`, slices 34–35) matches keywords against text laced
  with tags and undecoded entities, so phrases broken by inline markup (`multi-launch <b>agreement</b>`)
  or entity-encoded punctuation are missed, and tag/attribute text (`href`, `https`, `style`) becomes
  spurious match noise.
- **Report excerpts and evidence titles** rendered into the weekly markdown contain raw `<p>`/`<a>`
  tags, producing an ugly, hard-to-read report — the opposite of the master spec's "trim obvious RSS
  boilerplate".

This slice makes normalization HTML-aware: strip tags and decode entities **before** the existing
whitespace canonicalization, so every consumer of `RawText` sees clean, human-readable plain text. The
master spec explicitly assigns this to normalization ("Trim obvious RSS boilerplate… Remove duplicate
whitespace… MVP normalization should be simple"). It is the highest-value robustness step for an
RSS-first MVP — it fixes both extraction recall and report readability on real feeds at once.

---

## Assignment

Worktree: any
Dependencies: None (builds on merged slices 26–37)
Conflicts with: None. (Slice 39 prefers full RSS content but edits Infrastructure files, not the
normalizer; slice 39 should land after 38 so the richer HTML body is cleaned, but they share no files.)
Estimated time: ~1-2 hours

---

## Project structure changes

```text
src/Radar.Application/Evidence/EvidenceNormalizer.cs                       # MODIFIED: strip HTML + decode entities

tests/Radar.Application.Tests/Evidence/EvidenceNormalizerTests.cs          # MODIFIED/extended
```

No new files, no new interfaces, no domain changes, no DI changes. `IEvidenceNormalizer.Normalize`
keeps its exact signature.

---

## Implementation details

### Order of operations (must be exactly this)

`EvidenceNormalizer.Normalize` currently does: line-ending normalization → per-line trim →
inline-whitespace collapse → blank-run collapse → overall trim → SHA-256 hash. Insert an HTML cleanup
pass **at the very front**, applied to both the title and the body text, before any whitespace work:

1. **Strip HTML/XML tags.** Replace every `<…>` tag with a **single space** (not empty string), so
   block boundaries like `…end of sentence.</p><p>Next sentence…` do not word-join into
   `sentence.Next`. A simple, deterministic scan/regex that matches `<` … next `>` is sufficient — do
   **not** add an HTML-parser package. Also drop the contents of `<script>…</script>` and
   `<style>…</style>` blocks (tag **and** inner text), since their bodies are never human-readable
   evidence; match case-insensitively.
2. **Decode HTML entities** on the tag-stripped text using `System.Net.WebUtility.HtmlDecode` (in the
   BCL — no package reference, honours AD-5). This turns `&amp;`→`&`, `&#8217;`→`’`, `&nbsp;`→a space,
   etc.
3. Hand the result to the **existing** whitespace pipeline (line-ending normalization, trims, collapse).
   `&nbsp;`-decoded spaces and the tag-replacement spaces are then collapsed by the current
   inline-whitespace logic, so no double spaces survive.

**Ordering rationale (call this out in a code comment):** strip tags *first*, then decode entities.
Decoding first would turn source-escaped literal text such as `&lt;script&gt;` into `<script>`, which
the tag-strip would then wrongly delete. Stripping first leaves escaped angle brackets to be decoded
into literal `<`/`>` **text**, preserving them as content. This keeps the transform faithful to what
the source actually meant.

### Determinism and purity

- `EvidenceNormalizer` stays a pure, culture-invariant function: no clock, no randomness, no I/O.
- `WebUtility.HtmlDecode` and the tag scan are deterministic; use `CultureInfo.InvariantCulture` /
  ordinal comparisons consistently (the existing `ToLowerInvariant`/ordinal style).
- Implement the tag scan as a compiled `GeneratedRegex` or a small hand-written char scanner — match
  the file's existing style (the codebase already uses `GeneratedRegex` in `ExtractedSignalMapper`).

### Hashing

The `ContentHash` is still computed over `normalizedTitle + "\n" + normalizedText`, but now those are
the **cleaned** strings. This intentionally changes hashes versus pre-38 output. That is safe: storage
is files-first and regenerated each run (AD-8); there is no persisted corpus to migrate, and immutable
evidence is keyed by this hash going forward. Note this in the PR description.

### Scope guard

Keep it simple per the master spec — do **not** attempt full boilerplate/footer removal, link
extraction, readability heuristics, or markdown conversion. Strip tags, decode entities, collapse
whitespace. Nothing more.

---

## Tests

### `EvidenceNormalizerTests` (extended)

- **Tags removed, words preserved:** body `"<p>Acme <b>raises</b> $50M in a credit facility.</p>"`
  normalizes to `"Acme raises $50M in a credit facility."` (no tags, no double spaces).
- **Block tags do not word-join:** `"<p>First sentence.</p><p>Second sentence.</p>"` →
  `"First sentence. Second sentence."` (a space, not `sentence.Second`).
- **Entities decoded:** `"AT&amp;T &#8211; Q1 &nbsp;update"` → `"AT&T – Q1 update"` (ampersand,
  en-dash, single space).
- **Strip-before-decode ordering:** input containing the source-escaped literal
  `"&lt;script&gt;alert(1)&lt;/script&gt;"` decodes to the literal text `"<script>alert(1)</script>"`
  and is **not** deleted (asserts tags were stripped before entities were decoded).
- **`<script>`/`<style>` contents dropped:** `"<style>.a{color:red}</style>Hello<script>x()</script>"`
  → `"Hello"`.
- **Title cleaned too:** an HTML/entity-laden `title` is stripped+decoded the same way and reflected in
  the `ContentHash`.
- **Plain-text regression:** input with no markup produces the same output as before this slice
  (existing whitespace/blank-run tests still pass unchanged).
- **Hash stability/determinism:** the same input normalizes to the same `ContentHash` across repeated
  calls; two inputs differing only by markup that cleans to identical text produce the **same** hash.

---

## Constraints

- Target .NET 10; C# 14.
- No HTML-parsing or third-party packages — use the BCL (`System.Net.WebUtility`, regex/char scan).
  Application may use BCL only here; no provider SDK leakage (AD-5).
- Pure, deterministic, culture-invariant: no clock, no randomness, no I/O.
- Keep changes scoped to `EvidenceNormalizer` and its tests. Do not touch the RSS reader/collector,
  extraction, scoring, resolution, the report, or DI. Do not add AI.
- `dotnet build Radar.sln -c Release` and `dotnet test Radar.sln -c Release --no-build` green.

---

## Acceptance criteria

- [ ] `EvidenceNormalizer` strips HTML/XML tags (tag → single space; `<script>`/`<style>` bodies
      removed) and decodes HTML entities for both title and body before whitespace canonicalization.
- [ ] Tags are stripped **before** entities are decoded, so source-escaped literal angle brackets
      survive as text (covered by a test).
- [ ] Cleaned text feeds the existing whitespace pipeline and the `ContentHash`; output is deterministic
      and culture-invariant.
- [ ] Plain-text inputs are unchanged from prior behaviour (regression tests pass).
- [ ] New tests cover tag removal, block-boundary spacing, entity decoding, ordering, script/style
      removal, title cleaning, and hash determinism; build/test green.
