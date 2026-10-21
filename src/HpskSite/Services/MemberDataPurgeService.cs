using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Hard-delete helper: removes EVERY row across the app's custom DB tables where the given
    /// Umbraco member is the SUBJECT (the row is that member's own data). Used when a member is
    /// hard-deleted so no orphaned personal data lingers in the custom tables.
    ///
    /// CORRECTNESS RULE (see the (table, column) map below):
    ///   A row is deleted ONLY when the member is the OWNER/SUBJECT of it — the table's primary
    ///   member column (MemberId, CandidateMemberId, HolderMemberId, …). ACTOR / audit columns
    ///   (EnteredBy, EnteredByMemberId, CreatedByMemberId, SignedOffByMemberId, ValidatedByMemberId,
    ///   VerifiedByMemberId, AssignedByMemberId, PaidConfirmedByMemberId, RequestedApproverMemberId,
    ///   IssuedByMemberId, CertifiedByMemberId, RevokedByMemberId, ReviewedByMemberId, ApprovedByMemberId,
    ///   AppliedByMemberId, ResponseByMemberId, GrantedByMemberId, EnabledByMemberId, RangeOfficerId, …)
    ///   only record who performed an action on someone else's row and are NEVER used here. Tables that
    ///   reference the member solely through an actor column are deliberately omitted (see _skipped notes).
    ///
    /// This is destructive and irreversible. Every statement is table-existence-guarded so a
    /// not-yet-created table is a silent no-op, and each runs in its own try/catch so a single
    /// failure can't abort the rest of the purge.
    /// </summary>
    public class MemberDataPurgeService
    {
        private readonly IScopeProvider _scopeProvider;

        public MemberDataPurgeService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        // -------------------------------------------------------------------------------------------------
        // Authoritative (table, subject-column) map. Derived by reading every CREATE TABLE in
        // Migrations/*.sql + Scripts/*.sql and confirming the real column name per table.
        //
        // Deliberately SKIPPED (actor-only OR no member column — never the subject's own row):
        //   MembershipFeeCategory        — no member column (ClubId/Year only)
        //   ClubDpaAcceptance            — only AcceptedByMemberId (actor; the row is about the CLUB)
        //   CompetitionTeam              — CreatedBy (actor) + ClubId; a team is not "about" one member
        //   CompetitionTeamMember        — INCLUDED below (MemberId = this member is a team member)
        //   TrainingGroups               — CreatedByMemberId (actor/owner); the group is not the member's data
        //   TrainingMatches              — CreatedByMemberId (actor); the match row is not the member's data
        //   TrainingMatchTeams           — CreatedBy (actor) + ClubId; no subject member column
        //   PrecisionResultEntrySession  — RangeOfficerId only (actor; a live entry-lock, not the shooter's data)
        //   Courses / CourseModules / CoursePrerequisites / CourseTestVersions — no member column
        //   (Training match SERIES scores are stored in the shared TrainingScores table — covered below.)
        // -------------------------------------------------------------------------------------------------
        /// <summary>
        /// Shared with <see cref="MemberMergeService"/>, which moves these same rows to another
        /// member instead of deleting them. One map, two operations — if a new table is added
        /// here, merge picks it up for free. Two copies would drift, and a drifted merge silently
        /// leaves a person's results behind on a member that is about to be deleted.
        /// </summary>
        public static (string Table, string Column)[] SubjectTables => _tables;

        private static readonly (string Table, string Column)[] _tables = new[]
        {
            // ── Membership / keys / föreningsintyg / fees ──
            ("ClubMembership",               "MemberId"),
            ("MemberAccessKey",              "MemberId"),
            ("MemberToken",                  "MemberId"),
            ("MemberTutorialWatch",          "MemberId"),
            ("MemberCertificateIssue",       "MemberId"),
            ("MembershipFeeCharge",          "MemberId"),

            // ── Certifications ──
            ("MemberCertifications",         "MemberId"),
            ("CertificationRequests",        "CandidateMemberId"),   // subject = the member the cert is FOR

            // ── Märken (marksmanship proficiency badges) ──
            ("MemberBadge",                  "MemberId"),
            ("MemberBadgeQualification",     "MemberId"),
            ("MarkenSeries",                 "MemberId"),
            ("MarkenCompetitionResult",      "MemberId"),
            ("MarkenStormastarEntry",        "MemberId"),

            // ── Standardmedaljer ──
            ("StandardMedalAward",           "MemberId"),
            ("StandardMedalGoldApplication", "MemberId"),

            // ── Training (log, groups, matches, handicap stats) ──
            ("TrainingScores",              "MemberId"),
            ("MemberTrainingProgress",      "MemberId"),   // Skyttetrappans framsteg (skalplanen B5)
            ("TrainingGroupMembers",        "MemberId"),
            ("TrainingMatchParticipants",   "MemberId"),
            ("TrainingMatchJoinRequests",   "MemberId"),
            ("ShooterStatistics",           "MemberId"),

            // ── Competition results (one per-discipline table + shoot-off + team membership) ──
            ("PrecisionResultEntry",         "MemberId"),
            ("DuellResultEntry",             "MemberId"),
            ("MagnumPrecisionResultEntry",   "MemberId"),
            ("MilsnabbResultEntry",          "MemberId"),
            ("NationellHelmatchResultEntry", "MemberId"),
            ("SpringskytteResultEntry",      "MemberId"),
            ("CompetitionShootOffEntry",     "MemberId"),
            ("CompetitionTeamMember",        "MemberId"),

            // ── Records / champions (subject = the record/title holder) ──
            ("CompetitionRecords",           "HolderMemberId"),
            ("CompetitionChampions",         "HolderMemberId"),

            // ── Board work (subject = the member who holds the role) ──
            ("BoardRoles",                   "MemberId"),

            // ── Shooting-range activity (subject = the member who shot) ──
            ("RangeActivitySession",         "MemberId"),

            // ── Anmälan och närvaro på händelser och träningar (fas B5) ──
            // GuestOfMemberId är BETALAREN för en gästrad (medlemmen som tog med sin fru), alltså ett
            // subjekt och inte en aktör — vid en sammanslagning ska ansvaret följa med till kontot som
            // blir kvar. SignedUpByMemberId/RecordedByMemberId är aktörer och räknas medvetet inte.
            // ⚠️ ClubTraining.SkjutledareMemberId står INTE här: kartan RADERAR vid gallring, och en
            // träning får inte försvinna för att skjutledarens uppgifter gallras.
            ("ClubEventParticipant",         "MemberId"),
            ("ClubEventParticipant",         "GuestOfMemberId"),

            // ── Lånevapenbokningar (subject = medlemmen som lånar) ──
            ("FirearmBooking",               "MemberId"),

            // ── Tävlingsplanering: funktionärens EGNA uppgifter (anmälan till pass, tillgänglighet) ──
            // Uppdragen i bemanningen och ansvaret i förberedelserna står INTE här — de hör till
            // arrangörens planering och får inte försvinna. De ligger i AssignmentColumns nedan, där
            // gallringen tar bort personuppgifterna men lämnar raden (tillagt 2026-10-21).
            ("StaffAvailability",            "MemberId"),
            ("StaffHelpSignup",              "MemberId"),

            // ── Kursen (fas D): anteckningar och registrerade serier om deltagaren ──
            // AuthorMemberId/RecordedByMemberId är aktörer och räknas inte.
            ("TrainingCourseNote",           "MemberId"),
            ("TrainingCourseSeries",         "MemberId"),

            // ── Auth / devices / push ──
            ("RefreshTokens",                "MemberId"),
            ("DeviceRegistrations",          "MemberId"),
            ("WebPushSubscription",          "MemberId"),

            // ── Ranking / stats ──
            ("RankingSnapshot",              "MemberId"),

            // ── Utbildning (course access / results / reviewer grant — all the member's own) ──
            ("CourseTestAccess",             "MemberId"),
            ("CourseTestResults",            "MemberId"),
            ("CourseReviewers",              "MemberId"),
        };

        /// <summary>
        /// Rader som PEKAR på medlemmen men tillhör någon annan — ett funktionärsuppdrag i en tävlings
        /// bemanning, ansvaret för ett område eller en uppgift i förberedelserna. Vid gallring tas
        /// personuppgifterna bort (medlemmen och de denormaliserade namnen/kontaktuppgifterna) och raden
        /// står kvar, eftersom arrangörens planering inte får tappa en rad för att en medlem gallras. Vid en
        /// sammanslagning flyttas medlemskolumnen till kontot som blir kvar, precis som SubjectTables.
        /// <para>ClearSql är resten av SET-satsen vid gallring (efter att medlemskolumnen satts till NULL).</para>
        /// </summary>
        public static (string Table, string MemberColumn, string ClearSql)[] AssignmentColumns => _assignmentColumns;

        private static readonly (string Table, string MemberColumn, string ClearSql)[] _assignmentColumns = new[]
        {
            ("StaffAssignment", "MemberId",
                "DisplayName = N'Borttagen medlem', Phone = NULL, Email = NULL, OriginalName = NULL, HasAdminAccess = 0"),
            ("WorkArea",        "ResponsibleMemberId", "ResponsibleName = NULL"),
            ("WorkItem",        "AssignedMemberId",    "AssignedName = NULL"),
            // Tävlingsplaneringen (2026-10-21): deltagarraden står kvar så att meddelandena behåller
            // en författare, men tappar namnet och gästadressen. Svar, röster och läsmarkering pekar på
            // deltagaren (PlanningMember.Id), aldrig på medlemmen — de behöver ingen egen rad här.
            ("PlanningMember",  "MemberId",
                "DisplayName = N'Borttagen medlem', GuestEmail = NULL, RemovedAt = ISNULL(RemovedAt, GETDATE())"),
        };

        /// <summary>
        /// Deletes all subject-owned rows for <paramref name="memberId"/> across every mapped table.
        /// Runs in a single auto-completing scope; each table is guarded + isolated so partial
        /// failures are recorded and don't abort the rest.
        /// </summary>
        public MemberPurgeResult PurgeMemberData(int memberId)
        {
            var result = new MemberPurgeResult();

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            foreach (var (table, column) in _tables)
            {
                try
                {
                    // OBJECT_ID guard → a not-yet-created table is a silent no-op (never throws).
                    var deleted = db.Execute(
                        $"IF OBJECT_ID('dbo.{table}','U') IS NOT NULL DELETE FROM [{table}] WHERE [{column}] = @0",
                        memberId);

                    if (deleted > 0)
                    {
                        result.Deleted[table] = deleted;
                        result.TotalRowsDeleted += deleted;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{table}: {ex.Message}");
                }
            }

            foreach (var (table, column, clearSql) in _assignmentColumns)
            {
                try
                {
                    var cleared = db.Execute(
                        $"IF OBJECT_ID('dbo.{table}','U') IS NOT NULL UPDATE [{table}] SET [{column}] = NULL, {clearSql} WHERE [{column}] = @0",
                        memberId);

                    if (cleared > 0)
                    {
                        result.Anonymized[table] = cleared;
                        result.TotalRowsAnonymized += cleared;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{table}: {ex.Message}");
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Outcome of a <see cref="MemberDataPurgeService.PurgeMemberData"/> call.
    /// </summary>
    public class MemberPurgeResult
    {
        /// <summary>Total rows deleted across all tables.</summary>
        public int TotalRowsDeleted;

        /// <summary>Per-table deleted-row counts (only tables that deleted &gt; 0 rows appear).</summary>
        public Dictionary<string, int> Deleted = new();

        /// <summary>Rows kept but stripped of the member's personal data (<see cref="MemberDataPurgeService.AssignmentColumns"/>).</summary>
        public int TotalRowsAnonymized;

        /// <summary>Per-table anonymized-row counts.</summary>
        public Dictionary<string, int> Anonymized = new();

        /// <summary>"Table: message" for any table whose delete threw (the rest still ran).</summary>
        public List<string> Errors = new();
    }
}
