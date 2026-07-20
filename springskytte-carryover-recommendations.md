# Springskytte overhaul → carry-over recommendations (staging)

Staging list for review together. Tick `[x]` the ones we want, then move them into `backlog.md`.
Full technical detail per pattern: `src/HpskSite/Documentation/SPRINGSKYTTE_STAFF_SCREENS_REUSABLE_PATTERNS.md`.
Effort key: **S** ≈ hours, **M** ≈ a day, **L** ≈ multi-day. "Verify" = confirm the current state
of that discipline's screen before doing the work (I haven't audited every screen yet).

---

## Tier 1 — Strong (universal, high value, low risk)

- [ ] **Wake-lock on every staff/board screen.** Opt-in "Håll skärmen vaken", re-acquired on
  visibilitychange. **Apply to:** Precision `/station` + `/skjutledare`, Fältskytte `/station`
  (station entry) + `/patrullista`, and the `/live` board. (Fältskytte Stationschef Tidur already
  has it — copy that pattern.) **Effort: S per screen.** Cheap, pure win; screens that sleep mid-pass
  are a real problem.

- [ ] **Connectivity indicator on data-entry screens.** Green/red wifi badge driven by poll
  success/failure; red = "åtgärder sparas inte". **Apply to:** Precision `/station`, Fältskytte
  `/station`, `/skjutledare`. **Effort: S per screen.** Makes a wifi drop visible instead of a silent
  "Ett fel uppstod".

- [ ] **Club-name shortening in result lists.** Add `clubShort` (ClubNameHelper.Shorten) to the result
  payloads and render it (keep full name for tooltip + same-club highlight). **Apply to:** Precision
  `GetResultsList` + public/admin renderers, Fältskytte `GetFaltskytteResults` + renderers. **Effort:
  M** (a few endpoints + renderers). Public `/resultat/` already truncates precision clubs with a
  title — shortening is nicer on mobile.

## Tier 2 — Consider (good value, more effort or partial fit)

- [ ] **Auto-save on change in scoring entry** (no manual save-per-series/station). Debounced +
  coalesced + read-back verify; refresh the unsaved-changes snapshot after save. **Apply to:**
  Precision `/station` scoring, Fältskytte `/station` result entry. **Effort: M-L per discipline** +
  care. **Caveat:** only safe for idempotent upserts; keep the endpoint field-scoped so parallel roles
  don't clobber (see next). Biggest UX win, but the most work/risk — do per discipline deliberately.

- [ ] **Field-scoped saves for parallel roles.** Where two roles touch the same result row, each writes
  only its own fields (server preserves the rest) via an atomic upsert. **Apply to:** any discipline
  where scoring + another role (timing/verification) run concurrently. **Verify** which disciplines
  actually have concurrent writers before building. **Effort: M.**

- [ ] **Print / "Visa & skriv ut" button on the admin Resultat tab.** Opens the public `/resultat/`
  (and sub-comp `?sub=true`) in a new tab. **Verify:** do Precision/Fältskytte Resultat tabs already
  have this? If not, add it. **Effort: S.**

- [ ] **Penalties/adjustments recordable by any staff role + a safety-violation entry point.** The
  Springskytte "any functionary can log a straff/tidsavdrag from their own screen" model. **Fit is
  discipline-specific:** time penalties are Springskytte's; for Precision/Fältskytte the analogue is a
  DQ/annotation, and they use X-count/hits not time. **Discuss** whether a general "range-officer can
  record a DQ/penalty note from `/skjutledare` or `/station`" is wanted. **Effort: M**, needs a
  data-model decision (there's deliberately no DSQ status today).

## Tier 3 — Probably discipline-specific (listed for completeness)

- [ ] **Per-role, per-weapon-class staff screens** (`?s=` scoping). Precision/Fältskytte already split
  by lane/patrol/skjutlag rather than weapon class, so likely N/A — but worth a glance at whether any
  screen still mixes classes.
- [ ] **Move-a-late-shooter to a free slot + timeline free-slots (paus/efter/dns).** Interval-start
  specific (Springskytte). Precision uses skjutlag, Fältskytte uses patrols — different reschedule
  models. Low carry-over.
- [ ] **Unique-per-weapon-class start-number invariant + duplicate detector.** Springskytte's
  multi-list-per-class model caused the dup bug. **Verify** whether Precision/Fältskytte numbering can
  ever produce ambiguous identifiers; only act if confirmed.
- [ ] **Dual-mode wall/operator screen** (clean display + persisted admin toggle). The `/live` board is
  already display-only and `/skjutledare` is operator-only, so limited need — unless a screen needs to
  be both.
- [ ] **Schedule-slip "körs sent" offset.** Only meaningful for wall-clock interval-start displays.

## Deferred / cross-cutting (already noted, not a per-discipline task)

- **Offline write-queue** — decided NOT to build; if ever reconsidered, needs a client op-id + server
  de-dup to be exactly-once (append endpoints like penalties would otherwise double), and it weakens
  device-swap resilience. Already in `backlog.md` (P3) + memory `offline-write-queue-deferred`.
- **Calculate ↔ publish split + preliminary-by-default** — already the norm in Precision/Fältskytte;
  Springskytte was the outlier we fixed. **No carry-over needed** — just keep new disciplines
  consistent with it.

---

## Gotchas to remember when porting
- `.d-flex` is `display:flex !important` → beats `el.style.display='none'`. Toggle flex via inline style, drop the class.
- Chromeless routed pages use `@model`, not `UmbracoViewPage<T>`.
- Views are runtime-compiled — load each changed page once after deploy; C# needs a full rebuild.
