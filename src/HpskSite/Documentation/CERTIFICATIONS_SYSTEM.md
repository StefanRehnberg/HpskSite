# Certified Instructor & Control Roles

Complete documentation for the certification-based role system used for SPSF-registered roles.

**Last updated:** 2026-04-29

---

## Overview

The site supports three distinct categories of role:

1. **Elected roles** — Chairperson, Secretary etc. — stored in `BoardRoles`. Pure metadata.
2. **Appointed roles** — `ClubAdmin_{clubId}`, `Skjutledare_{clubId}`, `RegionalAdmin_{regionCode}` — stored as Umbraco member groups. Pure boolean access.
3. **Certified roles** *(this feature)* — Föreningsinstruktör, Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare. The cert is a **personal credential** issued by SPSF and follows the person across club/region moves. The **appointment** to a specific scope (club / region / area) is separate, lives in member groups, and gates authority within that scope.

| Role | Trained by | Authority granter (appointment) | Scope of appointment |
|---|---|---|---|
| Föreningsinstruktör | Kretsinstruktör | Club board (förening) | Single club |
| Kretsinstruktör | Riksinstruktör | Region board (krets) | Single region |
| Riksinstruktör | SPSF | SPSF / site admin | One area (Syd/Vast/Ost/Nord) |
| Vapenkontrollant | Krets/Riks instructor | (n/a — cert IS the authority) | National, follows the person |
| Banläggare | Krets/Riks instructor | (n/a — cert IS the authority) | National, follows the person |

The crucial distinction: holding a Kretsinstruktör cert does NOT make a person the designated Kretsinstruktör for a region. They must also be appointed by that region's board. A person can move regions and bring their cert with them, then be re-appointed.

---

## Data model

### Table `MemberCertifications`

Personal credential, scopeless. The DDL is in `Migrations/create-member-certifications-table.sql` and must be run manually in SSMS per the project's migration convention (the Umbraco composer/plan path is unreliable).

```sql
CREATE TABLE [dbo].[MemberCertifications] (
    [Id]                  INT IDENTITY(1,1) PRIMARY KEY,
    [MemberId]            INT          NOT NULL,
    [CertificationType]   NVARCHAR(50) NOT NULL,    -- enum below
    [CertifiedByMemberId] INT          NULL,
    [CertifiedAt]         DATETIME     NOT NULL,
    [ExpiresAt]           DATETIME     NULL,        -- NULL = never expires
    [CertificateNumber]   NVARCHAR(100) NULL,       -- SPSF reference, informational only
    [IsActive]            BIT          NOT NULL DEFAULT 1,
    [RevokedAt]           DATETIME     NULL,
    [RevokedByMemberId]   INT          NULL,
    [RevokedReason]       NVARCHAR(500) NULL,
    [Notes]               NVARCHAR(MAX) NULL,
    [CreatedAt]           DATETIME     NOT NULL DEFAULT GETDATE()
);
```

`CertificationType` is one of: `Foreningsinstruktor`, `Kretsinstruktor`, `Riksinstruktor`, `Vapenkontrollant`, `Banlaggare`. Identifiers omit the Swedish diacritics so they're awkward-free in member-group names; the display label everywhere uses the proper spelling via `CertificationTypes.DisplayName(...)`.

The cert table is the **source of truth**. Records are not deleted on revoke — `IsActive=0` plus `RevokedAt`/`RevokedByMemberId`/`RevokedReason` is set, preserving audit history.

### Appointments — member groups

| Cert type | Group name | Granter | Auto-managed by service? |
|---|---|---|---|
| Föreningsinstruktör | `Foreningsinstruktor_{clubId}` | Club admin (or higher) | Manual via `Appoint`/`Unappoint` |
| Kretsinstruktör | `Kretsinstruktor_{regionCode}` | Regional admin (or higher) | Manual |
| Riksinstruktör | `Riksinstruktor_{areaCode}` | Site admin | Manual |
| Vapenkontrollant | `Vapenkontrollant` (single global group) | n/a | Auto-added on cert grant, removed on revoke |
| Banläggare | `Banlaggare` (single global group) | n/a | Auto-added on cert grant, removed on revoke |

`CertificationService` is the **single writer**. Appointment requires an active matching cert; on revoke, all appointment groups for that cert type are removed from the member.

Existing role-based authorization continues to work via `IMemberService.GetAllRoles()` — no caller has to learn the new schema.

### Region area property

The Riksinstruktör scope is one of four areas (`Syd`, `Vast`, `Ost`, `Nord`). Each region (`regionalPage` doctype) needs an `area` Textstring property mapping it to its national area. `AdminAuthorizationService.GetAreaForRegion(regionCode)` reads this from the published content cache.

**Operator step:** add `area` property to the `regionalPage` doctype in Umbraco backoffice (dropdown values: `Syd`, `Vast`, `Ost`, `Nord`) and backfill on every region node.

---

## Authority hierarchy

`Services/CertificationAuthorizationService.cs` enforces who can grant a given cert:

| Granted cert | Required authority |
|---|---|
| `Riksinstruktor` | Site admin only |
| `Kretsinstruktor` | Active Riksinstruktör appointed to the area covering the candidate's primary club's region (resolved via `regionalFederation` → `area`) |
| `Foreningsinstruktor` | Active Krets or Riks instructor (any region/area) |
| `Vapenkontrollant`, `Banlaggare` | Active Krets or Riks instructor |
| (any) | Site admin always passes |

The "Certifierad av" dropdown in the grant modal is server-filtered to members who pass `CanGrantAsync`.

Appointment authority uses the existing scope-admin checks — `IsClubAdminForClub`, `IsRegionalAdminForRegion`, `IsCurrentUserAdminAsync`.

---

## Services

### `Services/CertificationService.cs`

Single writer for the cert table + group reconciliation.

- `GrantAsync(GrantCertificationRequest, actingMemberId, isSiteAdmin)` — validates auth, inserts row, adds global Vapen/Banläggare group when applicable.
- `RevokeAsync(certId, revokedBy, reason)` — sets `IsActive=0`, removes both global groups (for appointmentless types) and any appointment groups of the same type.
- `AppointAsync(memberId, certType, scopeId)` — adds the appointment member group; refuses unless an active cert exists.
- `UnappointAsync(memberId, certType, scopeId)` — removes the appointment group.
- `UpdateMetaAsync(certId, certNumber, notes, expiresAt)` — small-edit path for filling in cert numbers later.
- `GetForMemberAsync(memberId)` / `GetActiveByTypeAsync(type)` / `GetActiveForMembersAsync(memberIds, type)` / `HasActiveCertAsync(memberId, type)` — read helpers.

### `Services/CertificationAuthorizationService.cs`

- `CanGrantAsync(grantorMemberId, certType, candidateMemberId)` — implements the matrix above.
- `GetAuthorizedGrantorsAsync(certType, candidateMemberId)` — list of member IDs that may grant; powers the modal dropdown.

Both services are registered as scoped in `Composers/AdminServicesComposer.cs`.

### `Services/AdminAuthorizationService.cs` additions

- `GetAreaForRegion(regionCode)` — reads `area` property from the regionalPage content node.
- `IsRiksinstruktorForArea(areaCode)` — checks `Riksinstruktor_{areaCode}` group on the current user; site admins pass.

---

## Controller

`Controllers/CertificationController.cs` is a Surface controller. All endpoints return `{success, data?, message?}` JSON.

### Read endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET ListForMember(memberId)` | Self / site admin / scope admin | Used by the profile card |
| `GET ListForClub(clubId)` | `IsClubAdminForClub` | Powers the club admin Certifieringar tab |
| `GET ListForRegion(regionCode)` | `IsRegionalAdminForRegion` | Powers the regional admin Certifieringar tab |
| `GET ListForArea(areaCode)` | Site admin | Powers the site admin Riksinstruktörer tab |
| `GET PublicListForClub(clubId)` | Logged-in member | Members-tier panel on `Club.cshtml` (names only — no cert numbers) |
| `GET PublicListForRegion(regionCode)` | Logged-in member | Members-tier panel on `RegionalPage.cshtml` |
| `GET GetGrantorsFor(certType, candidateMemberId)` | Logged-in member | Modal dropdown |

### Write endpoints

All POST + `[ValidateAntiForgeryToken]`. Body is JSON.

| Endpoint | Body |
|---|---|
| `POST Grant` | `GrantCertificationRequest` |
| `POST Revoke` | `{certId, reason}` |
| `POST Appoint` | `{memberId, certificationType, scopeId}` |
| `POST Unappoint` | `{memberId, certificationType, scopeId}` |
| `POST UpdateMeta` | `{certId, certificateNumber, notes, expiresAt}` |

`Grant`/`Revoke`/`UpdateMeta` use `CertificationAuthorizationService.CanGrantAsync` to gate. `Appoint`/`Unappoint` use the existing scope-admin checks.

---

## UI surfaces

### Member profile (`Views/UserProfile.cshtml`)

A "Mina certifieringar" card on the Profil tab shows the current member's active certs (type, certified date, expiry, cert number, status). Read-only — members cannot edit their own certs. Rendered via `Views/Partials/MemberCertificationsCard.cshtml`.

### Club admin panel (`Views/Partials/ClubAdminPanel.cshtml`)

New tab **Certifieringar** between Dokument and Statistik. Renders `Views/Partials/CertificationManagementPanel.cshtml` with `CertScopeType=Club` and `CertScopeId=clubId`. Three sub-cards:

- **Föreningsinstruktörer** — list of currently appointed for this club, with grant button (visible if user can grant) + revoke + unappoint actions.
- **Vapenkontrollanter** — list of club-resident certified persons.
- **Banläggare** — same.

### Regional admin panel (`Views/Partials/RegionalAdminPanel.cshtml`)

New tab **Certifieringar** between Roller and Statistik. Same panel partial, with `CertScopeType=Region`. Shows:

- **Kretsinstruktörer** — current region's appointed list with grant/revoke/unappoint.
- **Föreningsinstruktörer i kretsen** — read-only directory grouped by club.
- **Vapenkontrollanter / Banläggare i kretsen** — read-only directory.

### Site admin (`Views/AdminPage.cshtml`)

New tab **Riksinstruktörer** between Kretsar and Statistik. Renders `Views/Partials/RiksinstruktorAdminPanel.cshtml` — a per-area card layout with target counts (`Syd`/`Vast`/`Ost` = 2, `Nord` = 3) and a single grant flow that issues the cert and appoints in one action.

### Members-tier (logged-in only)

- **Club page** (`Views/Club.cshtml` Medlemmar tab) — `Views/Partials/ClubInstructorsPublicPanel.cshtml` renders three columns of names: Föreningsinstruktörer / Vapenkontrollanter / Banläggare. Names only — no cert numbers or admin info.
- **Region page** (`Views/RegionalPage.cshtml` Om Kretsen tab) — `Views/Partials/RegionInstructorsPublicPanel.cshtml` renders the appointed Kretsinstruktör names.

Both gated to `isLoggedIn`; anonymous visitors don't see the panels.

---

## Statistik integration

### Club Statistik (`Views/Partials/ClubStatistics.cshtml`, `Controllers/ClubStatisticsController.cs`)

- New nudge `noForeningsinstruktor` (red) — "Klubben saknar Föreningsinstruktör. Detta är ett krav från SPSF — kontakta kretsinstruktör för att utbilda en medlem." Button switches to the Certifieringar tab.
- New summary line below the cards: "Instruktörer i klubben: N F · M V · K B" (F=Föreningsinstruktör, V=Vapenkontrollant, B=Banläggare).
- Counts appointed Föreningsinstruktörer regardless of which club they're a member of (an admin assigned at one club but a member elsewhere still counts).

### Regional Statistik (`Views/Partials/RegionalStatistics.cshtml`, `Controllers/RegionalStatisticsController.cs`)

- New nudge block **"Saknar Föreningsinstruktör (SPSF-krav)"** at the top of the nudges list. Uses the same UI pattern as the existing "utan klubbadmin" / "utan Skjutledare" lists.
- New full-width chart **"Föreningsinstruktörer per klubb"** — horizontal bar; clubs with 0 are highlighted red.
- New nudge: "Mindre än 2 Kretsinstruktörer i kretsen" when the region has fewer than the standard two.
- Instructor summary line: "N Krets · M Förenings · K Vapen · L Bana".

---

## Authorization summary

| Action | Required |
|---|---|
| View own profile certs | Self (always) |
| View club's instructor list (admin) | `IsClubAdminForClub` |
| View region's instructor list (admin) | `IsRegionalAdminForRegion` |
| View members-tier panels | Logged in |
| Grant Riksinstruktör cert | Site admin |
| Grant Kretsinstruktör cert | Active Riksinstruktör for the candidate's area, OR site admin |
| Grant Förenings/Vapen/Ban cert | Any active Krets or Riks instructor, OR site admin |
| Appoint Föreningsinstruktör | `IsClubAdminForClub` for the club |
| Appoint Kretsinstruktör | `IsRegionalAdminForRegion` |
| Appoint Riksinstruktör | Site admin |
| Revoke a cert | Anyone with grant authority for that type |

Site admins bypass every check — `IsCurrentUserAdminAsync()` returns true short-circuits the hierarchy.

---

## Operator deployment checklist

1. Run `Migrations/create-member-certifications-table.sql` in SSMS against the Umbraco database.
2. Add `area` Textstring property (dropdown: `Syd`/`Vast`/`Ost`/`Nord`) to the `regionalPage` doctype in Umbraco backoffice.
3. Backfill `area` on every existing region node.
4. Full publish per `PRODUCTION_DEPLOYMENT_GUIDE.md`.
5. Bootstrap the cert chain from a site-admin account:
   - Open `/admin` → **Riksinstruktörer** tab → grant the first Riksinstruktörer per area.
   - Those Riks can then grant Kretsinstruktör certs through the regional admin panel of any region in their area.
   - Those Krets can then grant Förenings/Vapen/Banläggare certs through any club admin panel.

---

## Future work

- **`CertificationExpirySweeper` (`IHostedService`)** — daily job that flips `IsActive=0` on certs whose `ExpiresAt` is in the past, removes appointment groups, and emails the holder + scope admin. Plus a 6-month-ahead warning email pass without revoking. Not required for v1; expiry simply doesn't auto-revoke until this lands. The plan in `~/.claude/plans/can-you-suggest-a-greedy-comet.md` documents the expected behavior.
- **Public/anonymous visibility on club pages** — currently logged-in only per the user's choice. Could be relaxed to anonymous if desired.
- **SPSF registry sync** — the `CertificateNumber` column is treated as informational only. If SPSF ever exposes an authoritative API, a sync job could be added.

---

## File index

### Added
- `Migrations/create-member-certifications-table.sql`
- `Models/MemberCertification.cs` — POCO + `CertificationType` enum + helpers
- `Services/CertificationService.cs`
- `Services/CertificationAuthorizationService.cs`
- `Controllers/CertificationController.cs`
- `Views/Partials/CertificationManagementPanel.cshtml` — shared admin panel for Club + Region
- `Views/Partials/RiksinstruktorAdminPanel.cshtml` — site-admin per-area panel
- `Views/Partials/MemberCertificationsCard.cshtml` — profile card
- `Views/Partials/ClubInstructorsPublicPanel.cshtml` — members-tier club panel
- `Views/Partials/RegionInstructorsPublicPanel.cshtml` — members-tier region panel

### Edited
- `Composers/AdminServicesComposer.cs`
- `Services/AdminAuthorizationService.cs` — area helpers
- `Views/Partials/ClubAdminPanel.cshtml` — Certifieringar tab
- `Views/Partials/RegionalAdminPanel.cshtml` — Certifieringar tab
- `Views/AdminPage.cshtml` — Riksinstruktörer tab
- `Views/UserProfile.cshtml` — profile card include
- `Views/Club.cshtml` — members-tier panel include
- `Views/RegionalPage.cshtml` — members-tier panel include + `regionCode` ViewBag
- `Controllers/ClubStatisticsController.cs` — `noForeningsinstruktor` nudge + instructor counts
- `Views/Partials/ClubStatistics.cshtml` — danger nudge + summary line
- `Controllers/RegionalStatisticsController.cs` — `clubsWithoutForeningsinstruktor`, `foreningsinstruktorPerClub`, instructor totals, krets-below-minimum flag
- `Views/Partials/RegionalStatistics.cshtml` — new nudge block, horizontal bar chart, instructor summary, krets-below-minimum warning
