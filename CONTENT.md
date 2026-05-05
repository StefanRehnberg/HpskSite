# Content not in this repo

The HpskSite engine is open-source under AGPL-3.0 (see [LICENSE](LICENSE)). The pistol.nu
content — data, images, written guides — is intentionally **not** committed to this
repository. A fresh clone will be missing the paths listed below.

For the project maintainer, this is invisible: local working trees still contain everything,
and `dotnet publish` picks the files up from disk on deploy. For new contributors, the site
will run but pages that consume the missing content will render with placeholders or empty
states.

To get the content for a development environment, contact the maintainer.

## What's missing

### Data files

| Path | Shape | Notes |
|---|---|---|
| `src/HpskSite/Clubs.json` | JSON array of `{ krets, foreningsnamn, foreningsnummer, email, web }` per club. ~515 entries. | Used by the registration / club-admin flows to seed and validate club identity. |
| `src/HpskSite/Associations.json` | JSON `{ totalCount, extractedAt, urls[] }` of SPSF club URLs from a 2025-12 scrape. | Reference for crawls and migrations; not load-bearing at runtime. |
| `src/HpskSite/access.txt` | Plain-text description of the role / permission model. | Reference document. |

### Image libraries

| Path | What it is |
|---|---|
| `src/HpskSite/wwwroot/images/field-targets/` | The Figurkatalog: ~100 field-shooting target images (`.jpg`), 5 colour variants (blå / grön / gul / orange / svart) of each target type (1/3, 1/4 Höger/rak/Vänster, 1/6, 1/7, 1/8, B45, B65, B100, C15, C20, C25, Ballongmål, Bildäck, Bunkerspringa, …). Database keys live in `FieldTarget` / `FieldTargetVariant`. |
| `src/HpskSite/wwwroot/images/competition-types/` | Branded sport-type icons (`Precision.png`, `Springskytte.png`, `Milsnabb.png`, `MagnumPrecision.png`, `Faltskytte.png`, `Duell.png`, etc.) shown across the UI. |

### Written content

| Path | What it is |
|---|---|
| `src/HpskSite/KnowledgeBase/docs/` | 20 Swedish-language markdown guides for the in-site AI assistant. Each file has a `roles:` frontmatter for access control. The C# controller / service that reads these stays in the repo — only the docs themselves are excluded. |
| `src/HpskSite/Documentation/` | 60+ files: architecture docs, deployment guides, business-rule references, SPSF rule PDFs, class-definition lists. |

### Already gitignored (not new — listed for completeness)

- `appsettings.Production.json` / `appsettings.Development.json` — secrets
- `src/HpskSite/wwwroot/media/` — runtime user uploads (logos, profile pictures, event images, PDFs)
- `Marketing/` — campaign emails and outreach templates
- `src/HpskSite/wwwroot/images/uploads/` — user-uploaded content
- Three specific email-template HTMLs at the repo root
- `bin/`, `obj/`, `node_modules/`, `.vs/` — build artefacts

## Why

The code is open: the engine, the schema, the patterns. Anyone can read it, learn from it,
fork it, or run their own instance under AGPL-3.0. The *curated content* — the work of
collecting Swedish clubs, photographing field targets, writing user guides — is not the
engine and would let a competitor bootstrap from zero without any of that effort. Keeping it
out of the public repo isn't a perfect barrier (past commits still contain it; anyone who
cloned the repo before keeps their copy) but stops the repo being a one-stop shop for that
data going forward.

If you want to use the engine and you have your own data and content, that's exactly what
this licence is for. Fork and run.
