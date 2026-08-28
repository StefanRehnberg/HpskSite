using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using HpskSite.Services;
using HpskSite.Models.Configuration;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers admin-related services for dependency injection
    /// </summary>
    public class AdminServicesComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            // Register AdminAuthorizationService as scoped (required because IMemberManager is scoped)
            builder.Services.AddScoped<AdminAuthorizationService>();

            // Register UnifiedResultsService as scoped for aggregating results from multiple sources
            builder.Services.AddScoped<UnifiedResultsService>();

            // Register MemberActivityService as scoped (static cache still shared, but avoids DI lifetime issues)
            builder.Services.AddScoped<MemberActivityService>();

            // Configure member activity options from appsettings.json
            builder.Services.Configure<MemberActivityOptions>(
                builder.Config.GetSection("MemberActivity"));

            // Register TrainingGroupService as scoped
            builder.Services.AddScoped<TrainingGroupService>();

            // Credits Skyttetrappan levels 1-3 from a Pistolskyttemarke the member already holds
            builder.Services.AddScoped<TrainingBadgeCreditService>();

            // Register DocumentService as scoped (uses IScopeProvider)
            builder.Services.AddScoped<DocumentService>();

            // Register SeriesCalculationService as scoped, plus the per-discipline score sources it
            // dispatches over. A new discipline gets series standings by adding an ISeriesScoreSource
            // here — the calculation strategies themselves are discipline-agnostic.
            builder.Services.AddScoped<SeriesCalculationService>();
            builder.Services.AddScoped<ISeriesScoreSource, PrecisionFamilySeriesScoreSource>();
            builder.Services.AddScoped<ISeriesScoreSource, FaltskytteSeriesScoreSource>();

            // "Is every registered start actually placed?" — same seam, one source per discipline.
            builder.Services.AddScoped<HpskSite.Services.StartListCoverage.StartListCoverageService>();
            builder.Services.AddScoped<HpskSite.Services.StartListCoverage.IStartListCoverageSource, HpskSite.Services.StartListCoverage.FaltskytteStartListCoverageSource>();
            // Precision LAST: it claims the empty/unknown competitionType as a legacy fallback, so
            // every named discipline must get to answer first.
            builder.Services.AddScoped<HpskSite.Services.StartListCoverage.IStartListCoverageSource, HpskSite.Services.StartListCoverage.PrecisionFamilyStartListCoverageSource>();

            // Coverage makes an unplaced shooter visible; cleanup removes a DELETED one from the
            // list instead of leaving them on it with orphaned result rows. Same precision-LAST rule.
            builder.Services.AddScoped<HpskSite.Services.StartListCleanup.StartListCleanupService>();
            builder.Services.AddScoped<HpskSite.Services.StartListCleanup.IStartListCleanupSource, HpskSite.Services.StartListCleanup.FaltskytteStartListCleanupSource>();
            builder.Services.AddScoped<HpskSite.Services.StartListCleanup.IStartListCleanupSource, HpskSite.Services.StartListCleanup.PrecisionFamilyStartListCleanupSource>();

            // Register CompetitionTeamService as scoped
            builder.Services.AddScoped<CompetitionTeamService>();

            // Register BoardRoleService as scoped
            builder.Services.AddScoped<BoardRoleService>();
            // Member-database expansion (see Documentation/MEMBER_DATABASE.md)
            builder.Services.AddScoped<ClubMembershipService>();
            builder.Services.AddScoped<MemberAccessKeyService>();
            builder.Services.AddScoped<ForeningsintygService>();
            builder.Services.AddScoped<MembershipFeeService>();
            // Hard-delete purge of a member's subject-owned rows across all custom DB tables.
            builder.Services.AddScoped<MemberDataPurgeService>();
            builder.Services.AddScoped<MemberMergeService>();

            // Board work: meeting lifecycle (meetings + agenda + attendance/quorum + protokoll + actions).
            // Run create-board-meeting-tables.sql. See BOARD_WORK_PHASE2_MEETINGS.md.
            builder.Services.AddScoped<BoardMeetingService>();

            // Board work: club/region-editable agenda templates per meeting type (typed items).
            // Run add-typed-agenda-items-and-templates.sql.
            builder.Services.AddScoped<BoardMeetingTemplateService>();

            // Board work Phase 3: Årshjul (annual cycle checklist) + Valberedning (nominations).
            builder.Services.AddScoped<BoardGovernanceService>();

            // Tracks each club's electronic acceptance of the Personuppgiftsbiträdesavtal (DPA).
            // Backed by the ClubDpaAcceptance table — run create-club-dpa-acceptance-table.sql.
            builder.Services.AddScoped<DpaAcceptanceService>();

            // Register ClubComparisonService as scoped (snapshot is cached via IMemoryCache)
            builder.Services.AddScoped<ClubComparisonService>();

            // Certification authority + writer for instructor / control roles
            builder.Services.AddScoped<CertificationAuthorizationService>();
            builder.Services.AddScoped<CertificationService>();

            // Klubb- och kretsrekord (manual record entry, IsCurrent + history chain)
            builder.Services.AddScoped<CompetitionRecordsService>();

            // Standardmedaljer ledger (per-discipline klass 3 qualification + pooled Guldmedalj accounting)
            builder.Services.AddScoped<StandardMedalLedgerService>();

            // Standardmedalj proof files (PDF/image) stored under App_Data, served via authorized endpoint
            builder.Services.AddScoped<StandardMedalProofStorage>();

            // Materializes won Standard medals from our own competitions into the ledger on publish
            builder.Services.AddScoped<StandardMedalMaterializationService>();

            // Märken (marksmanship proficiency badges, SHB kap 5) — Phase 1: Pistolskyttemärket.
            // Ledger (badges + yearly Guldfodringar + årtalsmärke derivation) + candidate engine
            // (proposes Guldfodring parts from TrainingScores; never writes).
            builder.Services.AddScoped<MarkenLedgerService>();
            builder.Services.AddScoped<MarkenCandidateService>();
            builder.Services.AddScoped<MarkenCompetitionSeriesSync>();
            // Phase 2: competition-driven discipline märken (Precision/Fält/Milsnabb/NatHelmatch) —
            // harvests hosted results live + merges verified self-reports; evaluates valör + årtalsmärke.
            builder.Services.AddScoped<MarkenCompetitionService>();
            // Phase 3: Stormästarmärket inteckningspoäng entries (career championship merits).
            builder.Services.AddScoped<MarkenStormastarService>();

            // Manual klubb-/kretsmästare entries (auto-compute approach abandoned —
            // many clubs don't run results through pistol.nu)
            builder.Services.AddScoped<CompetitionChampionsService>();

            // Skjutbanedatabas (Shooting Range Database) — Phase 0: ranges + sections + club links +
            // per-club allocations + steward ACL + OSM seed import. Phase 2 adds permits + documents.
            // See SHOOTING_RANGE_DATABASE.md.
            builder.Services.AddScoped<ShootingRangeService>();
            // Range compliance documents (permit/besiktning/buller/lead …) under App_Data, served via
            // an authorized steward-gated endpoint. Mirrors StandardMedalProofStorage.
            builder.Services.AddScoped<RangeDocumentStorage>();

            // Fältskytte member-stats aggregator (powers /user-profile-page dashboard + Resultat tab)
            builder.Services.AddScoped<FaltskytteStatsService>();

            // Cheap "does member X have data in discipline Y" lookups for member-list dots and mini-dashboard tabs
            builder.Services.AddScoped<MemberDataPresenceService>();

            // Särskjutning (shoot-off) entries for tied medal positions in championship competitions
            builder.Services.AddScoped<ShootOffService>();

            // DNS / DNF — tells "no more results are coming" apart from "still shooting".
            // Consumed by the särskjutning gate, which must not award a medal on a tie that a
            // later series could still break.
            builder.Services.AddScoped<ParticipantStatusService>();

            // Fältskytte (Normal/Poäng/Magnumfält) Särskjutning — separate service since Fältskytte
            // uses a different result-entry shape (per-station hits/figures/poängmål)
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteShootOffService>();

            // Standalone Fältskytte station configurations (CRUD + sharing + secrecy gate)
            builder.Services.AddScoped<FaltskytteConfigurationService>();

            // Fältskytte "Projekt" — lightweight containers that group configurations
            // (shared access + archive). Config-access rolls up to project members.
            builder.Services.AddScoped<FaltskytteProjectService>();

            // Precision finals start list pipeline:
            //   QualificationService — ranks shooters and computes the 1/6+min10 cutoff (existed before, now DI-registered)
            //   QualifyingResultsService — snapshot the qualifying leaderboard before finals are built
            //   FinalsStartListBuilder — turn snapshot + per-class config into a finals StartListConfiguration
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsQualificationService>();
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionQualifyingResultsService>();
            builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsStartListBuilder>();

            // Printable Kvitto (receipt) builder — shared by the /kvitto page.
            builder.Services.AddScoped<ReceiptModelBuilder>();

            // Utbildning course catalog (Courses + CourseModules + CoursePrerequisites).
            // Data layer only; access/eligibility gating lives in the controllers.
            // Run create-course-tables.sql. See COURSE_SYSTEM.md.
            builder.Services.AddScoped<CourseService>();

            // Course test engine (Phase 2): versions, prerequisite-gated access, results
            // (online auto-scored + instructor-recorded paper). Reads Märken + certs for eligibility.
            builder.Services.AddScoped<CourseTestService>();

            // In-app functionary messaging (Funktionärs-/stationsmeddelanden) — competition-scoped
            // message store addressed by generic (ScopeType, ScopeKey), delivered over the staff-screen
            // poll. Run create-event-message-tables.sql. Transport/addressing kept pluggable so the
            // later shooter-facing web-push channel can reuse the same rows.
            builder.Services.AddScoped<HpskSite.Services.Messaging.EventMessageService>();

            // Participant (shooter-facing) competition notifications — reuses the EventMessage store
            // (Audience='Shooter') + the web-push pipe. ParticipantAudienceResolver reads registrations
            // via IContentService (unpublished); ParticipantNotificationService composes resolve+post+push.
            // Run add-audience-to-eventmessage.sql. See memory participant-push-notifications.
            builder.Services.AddScoped<HpskSite.Services.Messaging.ParticipantAudienceResolver>();
            builder.Services.AddScoped<HpskSite.Services.Messaging.ParticipantNotificationService>();

            // Competition planning & staffing (Tävlingsplanering) — Phase 1: day-of functionary roster
            // (StaffAssignment) + Phase 1b preparation work-breakdown (WorkArea/WorkItem). Run
            // create-staff-assignment-table.sql + create-work-breakdown-tables.sql. See
            // Documentation/COMPETITION_STAFFING_SYSTEM.md.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffingService>();
            builder.Services.AddScoped<HpskSite.Services.Staffing.WorkBreakdownService>();
            // THE ROLE CATALOG: built-in FunctionaryRoles + arrangör-named StaffRole rows, merged. The one
            // place a role is resolved — never call FunctionaryRoles directly from a surface. Degrades to
            // the built-ins if StaffRole is missing. Run create-staff-role-table.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.RoleCatalogService>();
            // THE DAY AXIS: the days you STAFF (incl. build-up/teardown) — not the days you COMPETE.
            // Seeded from the competition span, owned by the arrangör. Shared by the Bemanning grid and
            // Dagsprogram so they cannot disagree. Run create-staff-day-table.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffDayService>();
            // Guesses which member a free-text roster name was meant to be. Suggestions only — a wrong
            // silent link hands a stranger someone else's shift, invisibly. Run create-staff-role-table.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.PersonMatchService>();
            // THE GRID: roles x days. One builder for the screen AND the printout - the printable sheet
            // used to render its own role-grouped list, so paper looked nothing like the plan on screen.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffingGridService>();
            // Phase 1.5: editable per-club/region planning templates. Run create-staffing-template-table.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffingTemplateService>();
            // Materiel-quantity estimate (general Beställningslista from participant/class/series counts).
            builder.Services.AddScoped<HpskSite.Services.Staffing.MaterielEstimateService>();
            // Phase 3: sourcing scope + member self-sign-up. Run create-staffing-source-scope-table.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffingSignupService>();
            // "Sök funktionärer" mail-out (relay to club/region admins, or direct via push+Brevo).
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffRequestService>();
            // Self-sign-up rework: organiser help-slots + member checkbox sign-up. Run create-staff-help-tables.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffHelpService>();
            // Big-comp staffing: shift/pass model + crew needs + coverage matrix. Run create-staff-pass-tables.sql.
            builder.Services.AddScoped<HpskSite.Services.Staffing.StaffPassService>();
            // Prep documents (sanktion/inbjudan/ritning…) stored under App_Data (survives deploys).
            builder.Services.AddScoped<HpskSite.Services.Staffing.PrepDocumentStorage>();
            // THE PEOPLE LAYER: one row per human across roster + sign-ups + availability + prep ownership.
            // Every people-facing planning surface projects this, so they can't disagree with each other.
            // No table of its own — it composes the services above. See CompetitionPeopleService.
            builder.Services.AddScoped<HpskSite.Services.Staffing.CompetitionPeopleService>();

            // Register BrevoEmailService and named HttpClient
            builder.Services.AddHttpClient("Brevo");
            builder.Services.AddScoped<BrevoEmailService>();

            // Configure document archive options from appsettings.json
            builder.Services.Configure<DocumentArchiveOptions>(
                builder.Config.GetSection("DocumentArchive"));
        }
    }
}
