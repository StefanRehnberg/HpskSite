using System.Text.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Finds and merges duplicate members (see Documentation/MEMBER_DATABASE.md §8).
    ///
    /// WHY THIS EXISTS
    /// A club imports its old member database and half the people already registered
    /// themselves — under a different email address, so neither personnummer nor email
    /// matched and the import created a second account. The self-registered account owns the
    /// LOGIN and the history; the imported one owns the good field data (pistolskyttekort,
    /// address, phone). Neither is disposable on its own.
    ///
    /// THE CENTRAL IDEA
    /// A merge is not a delete — it is a MOVE. Every table where a member is the subject of a
    /// row is already enumerated, once, in <see cref="MemberDataPurgeService.SubjectTables"/>.
    /// This service walks that same map with UPDATE instead of DELETE. A second, hand-written
    /// table list would drift from it, and a drifted merge quietly leaves a person's results
    /// behind on an account that is about to be deleted.
    ///
    /// WHAT IS DELIBERATELY NOT AUTOMATIC
    /// Which member survives, and which value wins for a field where both have one, are the
    /// admin's calls — this service only supplies the evidence and executes the decision.
    /// </summary>
    public class MemberMergeService
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<MemberMergeService> _logger;

        /// <summary>
        /// Properties that describe the PERSON and are worth carrying across a merge.
        /// Deliberately excludes session/consent plumbing (tokens, tutorial flags, last-active
        /// stamps, training-wizard progress): those belong to the account that generated them and
        /// copying them would misreport the survivor's own activity.
        /// </summary>
        public static readonly (string Alias, string Label)[] MergeableFields = new[]
        {
            ("firstName",              "Förnamn"),
            ("lastName",               "Efternamn"),
            ("personNumber",           "Personnummer"),
            ("birthDate",              "Födelsedatum"),
            ("gender",                 "Kön"),
            ("shooterIdNumber",        "Pistolskyttekortnummer"),
            ("phoneNumber",            "Mobiltelefon"),
            ("landlinePhone",          "Fast telefon"),
            ("address",                "Adress"),
            ("coAddress",              "c/o-adress"),
            ("postalCode",             "Postnummer"),
            ("city",                   "Ort"),
            ("memberSince",            "Medlem sedan"),
            ("emergencyContactName",   "Anhörig, namn"),
            ("emergencyContactPhone",  "Anhörig, telefon"),
            ("guardian1Name",          "Vårdnadshavare 1, namn"),
            ("guardian1Email",         "Vårdnadshavare 1, e-post"),
            ("guardian1Mobile",        "Vårdnadshavare 1, mobil"),
            ("guardian2Name",          "Vårdnadshavare 2, namn"),
            ("guardian2Email",         "Vårdnadshavare 2, e-post"),
            ("guardian2Mobile",        "Vårdnadshavare 2, mobil"),
            ("precisionShooterClass",       "Skytteklass precision"),
            ("duellShooterClass",           "Skytteklass duell"),
            ("magnumPrecisionShooterClass", "Skytteklass magnum"),
            ("milsnabbShooterClass",        "Skytteklass milsnabb"),
            ("nationellHelmatchShooterClass", "Skytteklass nationell helmatch"),
            ("standardpistolShooterClass",  "Skytteklass standardpistol"),
            ("sportpistolShooterClass",     "Skytteklass sportpistol"),
            ("profilePictureUrl",      "Profilbild"),
            ("trainingNotes",          "Träningsanteckningar"),
        };

        private const string ClubMemberTypeAlias = "hpskClub";

        public MemberMergeService(
            IMemberService memberService,
            IContentService contentService,
            IScopeProvider scopeProvider,
            ILogger<MemberMergeService> logger)
        {
            _memberService = memberService;
            _contentService = contentService;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        // =====================================================================
        // 1. Finding candidates
        // =====================================================================

        /// <summary>
        /// Duplicate candidates for one club. The club's own roster is compared against itself
        /// AND against club-less members — the self-registered account that never picked a club
        /// is invisible in the club's list, which is exactly where this duplicate hides. Members
        /// of OTHER clubs are never returned: a club admin has no business being shown them, and
        /// a cross-club merge would move a stranger's history.
        /// </summary>
        public List<DuplicateCandidate> FindCandidates(int clubId)
        {
            var all = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                .ToList();

            var roster = new List<IMember>();
            var clubless = new List<IMember>();
            foreach (var m in all)
            {
                if (IsInClub(m, clubId)) roster.Add(m);
                else if (HasNoClub(m)) clubless.Add(m);
            }

            // Only roster members seed a pair. Two club-less strangers who happen to share a name
            // are not this club's problem, and pairing them here would leak both to the admin.
            var pairs = new Dictionary<(int, int), DuplicateCandidate>();
            var haystack = roster.Concat(clubless).ToList();

            var byName = new Dictionary<string, List<IMember>>(StringComparer.OrdinalIgnoreCase);
            var byPnr = new Dictionary<string, List<IMember>>(StringComparer.Ordinal);
            var byShooterId = new Dictionary<string, List<IMember>>(StringComparer.OrdinalIgnoreCase);
            var byPhone = new Dictionary<string, List<IMember>>(StringComparer.Ordinal);

            foreach (var m in haystack)
            {
                Bucket(byName, NormalizeName(m), m);
                Bucket(byPnr, NormalizePnr(Prop(m, "personNumber")), m);
                Bucket(byShooterId, (Prop(m, "shooterIdNumber") ?? "").Trim(), m);
                Bucket(byPhone, NormalizePhone(Prop(m, "phoneNumber")), m);
            }

            foreach (var seed in roster)
            {
                foreach (var other in Neighbours(seed, byName, byPnr, byShooterId, byPhone))
                {
                    if (other.Id == seed.Id) continue;
                    // Both club-less can't happen (seed is roster), but two roster members must
                    // still only be paired once — key on the ordered id pair.
                    var key = seed.Id < other.Id ? (seed.Id, other.Id) : (other.Id, seed.Id);
                    if (pairs.ContainsKey(key)) continue;

                    var scored = Score(seed, other, clubId);
                    if (scored != null) pairs[key] = scored;
                }
            }

            return pairs.Values
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.A.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        private static IEnumerable<IMember> Neighbours(
            IMember seed,
            Dictionary<string, List<IMember>> byName,
            Dictionary<string, List<IMember>> byPnr,
            Dictionary<string, List<IMember>> byShooterId,
            Dictionary<string, List<IMember>> byPhone)
        {
            var seen = new HashSet<int>();
            foreach (var bucket in new[]
                     {
                         Lookup(byName, NormalizeName(seed)),
                         Lookup(byPnr, NormalizePnr(Prop(seed, "personNumber"))),
                         Lookup(byShooterId, (Prop(seed, "shooterIdNumber") ?? "").Trim()),
                         Lookup(byPhone, NormalizePhone(Prop(seed, "phoneNumber"))),
                     })
            {
                foreach (var m in bucket)
                {
                    if (seen.Add(m.Id)) yield return m;
                }
            }
        }

        private static List<IMember> Lookup(Dictionary<string, List<IMember>> index, string key)
            => key.Length > 0 && index.TryGetValue(key, out var list) ? list : new List<IMember>();

        private static void Bucket(Dictionary<string, List<IMember>> index, string key, IMember m)
        {
            if (key.Length == 0) return;
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<IMember>();
                index[key] = list;
            }
            list.Add(m);
        }

        /// <summary>
        /// Scores one pair. Returns null below the "worth showing" line, so a shared surname or a
        /// shared empty field can never surface as a candidate.
        ///
        /// Personnummer and pistolskyttekort are IDENTIFIERS — equal means same person, full stop.
        /// Everything else is circumstantial and needs the name to agree as well; "same phone" on
        /// its own is a household, not a duplicate.
        /// </summary>
        private static DuplicateCandidate? Score(IMember a, IMember b, int clubId)
        {
            var reasons = new List<string>();
            int score = 0;

            var pnrA = NormalizePnr(Prop(a, "personNumber"));
            var pnrB = NormalizePnr(Prop(b, "personNumber"));
            if (pnrA.Length >= 10 && pnrA == pnrB)
            {
                score = Math.Max(score, 100);
                reasons.Add("samma personnummer");
            }

            var sidA = (Prop(a, "shooterIdNumber") ?? "").Trim();
            var sidB = (Prop(b, "shooterIdNumber") ?? "").Trim();
            if (sidA.Length > 0 && string.Equals(sidA, sidB, StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 95);
                reasons.Add("samma pistolskyttekortnummer");
            }

            bool sameName = NormalizeName(a).Length > 0 && NormalizeName(a) == NormalizeName(b);
            if (sameName)
            {
                var bdA = NormalizeDate(Prop(a, "birthDate"));
                var bdB = NormalizeDate(Prop(b, "birthDate"));
                if (bdA.Length > 0 && bdA == bdB)
                {
                    score = Math.Max(score, 85);
                    reasons.Add("samma namn och födelsedatum");
                }

                var phA = NormalizePhone(Prop(a, "phoneNumber"));
                var phB = NormalizePhone(Prop(b, "phoneNumber"));
                if (phA.Length >= 7 && phA == phB)
                {
                    score = Math.Max(score, 80);
                    reasons.Add("samma namn och telefonnummer");
                }

                var adA = NormalizeText(Prop(a, "address"));
                var adB = NormalizeText(Prop(b, "address"));
                var pcA = NormalizePhone(Prop(a, "postalCode"));
                var pcB = NormalizePhone(Prop(b, "postalCode"));
                if (adA.Length > 0 && adA == adB && pcA.Length > 0 && pcA == pcB)
                {
                    score = Math.Max(score, 70);
                    reasons.Add("samma namn och adress");
                }

                if (score == 0)
                {
                    score = 40;
                    reasons.Add("samma namn");
                }
            }

            if (score == 0) return null;

            return new DuplicateCandidate
            {
                Score = score,
                Reasons = reasons,
                A = Describe(a, clubId),
                B = Describe(b, clubId)
            };
        }

        // =====================================================================
        // 2. Comparing two members field by field
        // =====================================================================

        /// <summary>
        /// Side-by-side comparison driving the merge form. The suggested survivor is the account
        /// with a LOGIN HISTORY — the self-registered one. It owns the password the member knows,
        /// their push subscriptions and their results; the imported record is a data-rich shell.
        /// Keeping the shell and deleting the login would lock the member out of their own account.
        /// Ties fall back to the older account, which has had longer to be linked from elsewhere.
        /// </summary>
        public MergeComparison Compare(int memberAId, int memberBId, int clubId)
        {
            var a = _memberService.GetById(memberAId) ?? throw new InvalidOperationException($"Medlem {memberAId} finns inte.");
            var b = _memberService.GetById(memberBId) ?? throw new InvalidOperationException($"Medlem {memberBId} finns inte.");

            var (survivor, loser) = PickSurvivor(a, b);

            var comparison = new MergeComparison
            {
                SuggestedSurvivorId = survivor.Id,
                SuggestedReason = SurvivorReason(survivor, loser),
                Survivor = Describe(survivor, clubId),
                Loser = Describe(loser, clubId)
            };

            foreach (var (alias, label) in MergeableFields)
            {
                var sv = ReadForMerge(survivor, alias);
                var lv = ReadForMerge(loser, alias);
                if (sv.Length == 0 && lv.Length == 0) continue;

                comparison.Fields.Add(new MergeField
                {
                    Alias = alias,
                    Label = label,
                    SurvivorValue = sv,
                    LoserValue = lv,
                    // The common case is one side empty: take the value, nothing to decide.
                    // A real disagreement is rare and is the only thing the admin must look at.
                    Conflict = sv.Length > 0 && lv.Length > 0 &&
                               !string.Equals(sv, lv, StringComparison.OrdinalIgnoreCase),
                    TakeFromLoser = sv.Length == 0 && lv.Length > 0
                });
            }

            comparison.Counts = CountSubjectRows(loser.Id);
            return comparison;
        }

        private (IMember Survivor, IMember Loser) PickSurvivor(IMember a, IMember b)
        {
            var la = a.LastLoginDate;
            var lb = b.LastLoginDate;
            if (la.HasValue && !lb.HasValue) return (a, b);
            if (lb.HasValue && !la.HasValue) return (b, a);
            if (la.HasValue && lb.HasValue) return la >= lb ? (a, b) : (b, a);
            return a.CreateDate <= b.CreateDate ? (a, b) : (b, a);
        }

        private static string SurvivorReason(IMember survivor, IMember loser)
        {
            if (survivor.LastLoginDate.HasValue && !loser.LastLoginDate.HasValue)
            {
                return "har loggat in — den posten äger inloggningen medlemmen känner till";
            }
            if (survivor.LastLoginDate.HasValue && loser.LastLoginDate.HasValue)
            {
                return "har loggat in senast";
            }
            return "är det äldsta kontot — ingen av dem har loggat in";
        }

        // =====================================================================
        // 3. Executing the merge
        // =====================================================================

        /// <summary>
        /// Copies the chosen field values onto the survivor, moves every subject-owned row and
        /// registration from the loser, unions club memberships and roles, records the whole
        /// thing in MemberMerge, and deletes the loser.
        ///
        /// ORDER MATTERS: the snapshot is taken BEFORE anything moves (it is the only record of
        /// what the loser was), and the loser is deleted LAST, after every move has been logged.
        /// A crash halfway leaves both members alive and the log row absent — recoverable by
        /// running the merge again — rather than a deleted member whose rows never arrived.
        /// </summary>
        public MergeResult Merge(MergeRequest request)
        {
            var survivor = _memberService.GetById(request.SurvivorMemberId)
                ?? throw new InvalidOperationException("Medlemmen som ska behållas finns inte.");
            var loser = _memberService.GetById(request.LoserMemberId)
                ?? throw new InvalidOperationException("Medlemmen som ska tas bort finns inte.");

            if (survivor.Id == loser.Id)
            {
                throw new InvalidOperationException("Det är samma medlem.");
            }

            var result = new MergeResult
            {
                SurvivorMemberId = survivor.Id,
                LoserMemberId = loser.Id,
                LoserName = loser.Name ?? "",
                LoserEmail = loser.Email ?? ""
            };

            var snapshot = Snapshot(loser);

            // ---- Fields ----
            foreach (var (alias, _) in MergeableFields)
            {
                if (!request.TakeFromLoser.Contains(alias)) continue;
                var value = ReadForMerge(loser, alias);
                if (value.Length == 0) continue;
                if (!survivor.HasProperty(alias)) continue;

                survivor.SetValue(alias, value);
                result.FieldsTaken.Add(alias);
            }

            // Affiliations are a union, never a choice: the survivor must end up in every club
            // either account belonged to, or the merge silently removes someone from a roster.
            UnionAffiliations(survivor, loser);

            // The display name follows the names actually stored after the merge; leaving the old
            // one would show "Kent Öberg" in one list and the merged name in another.
            var display = $"{ReadForMerge(survivor, "firstName")} {ReadForMerge(survivor, "lastName")}".Trim();
            if (display.Length > 0) survivor.Name = display;

            _memberService.Save(survivor);

            // ---- Roles ----
            foreach (var role in _memberService.GetAllRoles(loser.Id))
            {
                var current = _memberService.GetAllRoles(survivor.Id);
                if (current.Contains(role, StringComparer.OrdinalIgnoreCase)) continue;
                _memberService.AssignRole(survivor.Id, role);
                result.RolesTaken.Add(role);
            }

            // ---- Club memberships (union per club, then the generic move) ----
            MergeClubMemberships(survivor.Id, loser.Id, result);

            // ---- Every other subject-owned table ----
            MoveSubjectRows(survivor.Id, loser.Id, result);

            // ---- Competition registrations (Umbraco nodes, not rows) ----
            MoveRegistrations(survivor.Id, loser.Id, result);

            // ---- Log BEFORE the delete: the snapshot is the only copy that will exist ----
            WriteLog(request, result, snapshot);

            _memberService.Delete(loser);

            _logger.LogInformation(
                "[MemberMerge] Club {ClubId}: member {Loser} merged into {Survivor}. " +
                "{Fields} fields, {Rows} rows moved, {Conflicts} conflicts, {Regs} registrations.",
                request.ClubId, loser.Id, survivor.Id, result.FieldsTaken.Count,
                result.RowsMoved.Values.Sum(), result.Conflicts.Values.Sum(), result.RegistrationsMoved);

            return result;
        }

        /// <summary>
        /// ClubMembership is unioned, not moved: the unique index is (MemberId, ClubId), so a club
        /// both accounts belonged to would collide. For a shared club the survivor's row absorbs
        /// the loser's empty columns and the earliest MemberSince (the member's real history with
        /// that club), then the loser's row goes. Clubs only the loser had are moved as they are.
        /// </summary>
        private void MergeClubMemberships(int survivorId, int loserId, MergeResult result)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var shared = db.Fetch<int>(
                @"SELECT l.ClubId FROM ClubMembership l
                  WHERE l.MemberId = @0 AND EXISTS (SELECT 1 FROM ClubMembership s WHERE s.MemberId = @1 AND s.ClubId = l.ClubId)",
                loserId, survivorId);

            foreach (var clubId in shared)
            {
                db.Execute(
                    @"UPDATE s SET
                        s.MembershipType   = COALESCE(NULLIF(s.MembershipType, ''),   l.MembershipType),
                        s.MembershipStatus = COALESCE(NULLIF(s.MembershipStatus, ''), l.MembershipStatus),
                        s.MemberSince      = CASE
                                                WHEN s.MemberSince IS NULL THEN l.MemberSince
                                                WHEN l.MemberSince IS NULL THEN s.MemberSince
                                                WHEN l.MemberSince < s.MemberSince THEN l.MemberSince
                                                ELSE s.MemberSince END,
                        s.EndReason        = COALESCE(NULLIF(s.EndReason, ''),  l.EndReason),
                        s.Federations      = COALESCE(NULLIF(s.Federations, ''), l.Federations),
                        s.MemberNotes      = COALESCE(NULLIF(s.MemberNotes, ''), l.MemberNotes),
                        s.HouseholdId      = COALESCE(NULLIF(s.HouseholdId, ''), l.HouseholdId),
                        s.BackgroundCheckApproved = CASE WHEN s.BackgroundCheckApproved = 1 THEN 1 ELSE l.BackgroundCheckApproved END,
                        s.BackgroundCheckDate     = COALESCE(s.BackgroundCheckDate, l.BackgroundCheckDate),
                        s.RegisteredInMap         = CASE WHEN s.RegisteredInMap = 1 THEN 1 ELSE l.RegisteredInMap END
                      FROM ClubMembership s
                      JOIN ClubMembership l ON l.ClubId = s.ClubId
                      WHERE s.MemberId = @0 AND l.MemberId = @1 AND s.ClubId = @2",
                    survivorId, loserId, clubId);

                db.Execute("DELETE FROM ClubMembership WHERE MemberId = @0 AND ClubId = @1", loserId, clubId);
                result.ClubMembershipsUnioned++;
            }

            var moved = db.Execute("UPDATE ClubMembership SET MemberId = @0 WHERE MemberId = @1", survivorId, loserId);
            if (moved > 0) result.RowsMoved["ClubMembership"] = moved;
        }

        /// <summary>
        /// Moves every row the loser is the SUBJECT of, using the purge service's map.
        ///
        /// Unique indexes are the hazard: both accounts entered the same competition, hold the
        /// same badge, subscribed the same device. A bulk UPDATE would throw 2627 and roll the
        /// whole table back, so a failed bulk falls back to row-by-row and each straggler is
        /// counted as a conflict rather than aborting the merge. Conflicting rows stay on the
        /// loser and go with it — their content is in the snapshot, and the report names them, so
        /// nothing disappears without being written down.
        /// </summary>
        private void MoveSubjectRows(int survivorId, int loserId, MergeResult result)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            foreach (var (table, column) in MemberDataPurgeService.SubjectTables)
            {
                if (string.Equals(table, "ClubMembership", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var exists = db.ExecuteScalar<int>($"SELECT CASE WHEN OBJECT_ID('dbo.{table}','U') IS NULL THEN 0 ELSE 1 END");
                    if (exists == 0) continue;

                    int moved;
                    try
                    {
                        moved = db.Execute($"UPDATE [{table}] SET [{column}] = @0 WHERE [{column}] = @1", survivorId, loserId);
                    }
                    catch (Exception)
                    {
                        moved = MoveRowByRow(db, table, column, survivorId, loserId, result);
                    }

                    if (moved > 0) result.RowsMoved[table] = moved;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{table}: {ex.Message}");
                    _logger.LogError(ex, "[MemberMerge] Failed moving {Table} from {Loser} to {Survivor}",
                        table, loserId, survivorId);
                }
            }
        }

        private static int MoveRowByRow(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            string table, string column, int survivorId, int loserId, MergeResult result)
        {
            // Only tables with an Id primary key can be walked individually; every custom table
            // in the map has one. A table without it stays a conflict rather than a wrong guess.
            List<int> ids;
            try
            {
                ids = db.Fetch<int>($"SELECT Id FROM [{table}] WHERE [{column}] = @0", loserId);
            }
            catch (Exception)
            {
                result.Conflicts[table] = -1; // unknown count — flagged for a human, never silent
                return 0;
            }

            int moved = 0, conflicts = 0;
            foreach (var id in ids)
            {
                try
                {
                    moved += db.Execute($"UPDATE [{table}] SET [{column}] = @0 WHERE Id = @1", survivorId, id);
                }
                catch (Exception)
                {
                    conflicts++;
                }
            }
            if (conflicts > 0) result.Conflicts[table] = conflicts;
            return moved;
        }

        /// <summary>
        /// Competition registrations are Umbraco content nodes carrying a memberId property, not
        /// rows — and they are saved unpublished, so they are re-saved, never published.
        ///
        /// LEFT(..., 20) är inte kosmetik. TRY_CONVERT(INT, x) returnerar INTE NULL när x är längre
        /// än 4000 tecken — den kastar 8152 "String or binary data would be truncated", och TRY_
        /// sväljer bara misslyckade konverteringar, aldrig trunkeringsfel. Optimeraren får dessutom
        /// utvärdera villkoret FÖRE filtret på pt.Alias, så ETT enda överstort textValue var som
        /// helst i umbracoPropertyData (en RTE-text, en block list) slog ut hela sammanslagningen
        /// — i prod, aldrig i dev där ingen rad var så lång. Att korta strängen först gör
        /// konverteringen omöjlig att spränga. Vanligaste vägen är ändå intValue, som jämförs direkt.
        /// </summary>
        private void MoveRegistrations(int survivorId, int loserId, MergeResult result)
        {
            List<int> nodeIds;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                nodeIds = scope.Database.Fetch<int>(
                    @"SELECT n.id
                      FROM umbracoNode n
                      JOIN umbracoContent c         ON c.nodeId = n.id
                      JOIN cmsContentType ct        ON ct.nodeId = c.contentTypeId AND ct.alias = 'competitionRegistration'
                      JOIN umbracoContentVersion cv ON cv.nodeId = n.id AND cv.[current] = 1
                      JOIN umbracoPropertyData pd   ON pd.versionId = cv.id
                      JOIN cmsPropertyType pt       ON pt.id = pd.propertyTypeId AND pt.Alias = 'memberId'
                      WHERE n.trashed = 0
                        AND (pd.intValue = @0
                             OR (pd.intValue IS NULL
                                 AND TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20)) = @0))",
                    loserId);
            }

            foreach (var nodeId in nodeIds)
            {
                try
                {
                    var node = _contentService.GetById(nodeId);
                    if (node == null) continue;
                    node.SetValue("memberId", survivorId);
                    _contentService.Save(node);
                    result.RegistrationsMoved++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Anmälan {nodeId}: {ex.Message}");
                }
            }
        }

        private void WriteLog(MergeRequest request, MergeResult result, string snapshot)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                @"INSERT INTO MemberMerge
                    (SurvivorMemberId, LoserMemberId, LoserName, LoserEmail, ClubId, MergedByMemberId, LoserSnapshot, MoveReport)
                  VALUES (@0, @1, @2, @3, @4, @5, @6, @7)",
                result.SurvivorMemberId,
                result.LoserMemberId,
                Truncate(result.LoserName, 255),
                Truncate(result.LoserEmail, 255),
                request.ClubId,
                request.MergedByMemberId,
                snapshot,
                JsonSerializer.Serialize(new
                {
                    fields = result.FieldsTaken,
                    roles = result.RolesTaken,
                    rows = result.RowsMoved,
                    conflicts = result.Conflicts,
                    registrations = result.RegistrationsMoved,
                    errors = result.Errors
                }));
        }

        /// <summary>
        /// Emails that belonged to a member deleted by a merge, mapped to the survivor. The import
        /// indexes these so a file still carrying the old address lands on the right person
        /// instead of recreating the duplicate that was just merged away.
        /// </summary>
        public Dictionary<string, int> GetRetiredEmails()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                // The existence check is a SEPARATE statement on purpose: NPoco only skips its
                // auto-generated "SELECT * FROM <T>" when the SQL STARTS with SELECT. Prefixing
                // this with IF OBJECT_ID(...) made it query a table named after the POCO.
                var exists = scope.Database.ExecuteScalar<int>(
                    "SELECT CASE WHEN OBJECT_ID('dbo.MemberMerge','U') IS NULL THEN 0 ELSE 1 END");
                if (exists == 0) return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                var rows = scope.Database.Fetch<RetiredEmailRow>(
                    @"SELECT LoserEmail AS Email, SurvivorMemberId AS MemberId
                      FROM MemberMerge WHERE LoserEmail IS NOT NULL AND LoserEmail <> ''
                      ORDER BY Id DESC");

                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    // Newest wins: a survivor that was itself later merged away points onward.
                    map.TryAdd((row.Email ?? "").Trim(), row.MemberId);
                }
                return map;
            }
            catch (Exception ex)
            {
                // A missing table must never break the import — it just means no retired emails.
                _logger.LogWarning(ex, "[MemberMerge] Could not read retired emails");
                return new Dictionary<string, int>();
            }
        }

        private class RetiredEmailRow
        {
            public string? Email { get; set; }
            public int MemberId { get; set; }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private Dictionary<string, int> CountSubjectRows(int memberId)
        {
            var counts = new Dictionary<string, int>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            foreach (var (table, column) in MemberDataPurgeService.SubjectTables)
            {
                try
                {
                    var exists = db.ExecuteScalar<int>($"SELECT CASE WHEN OBJECT_ID('dbo.{table}','U') IS NULL THEN 0 ELSE 1 END");
                    if (exists == 0) continue;
                    var n = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM [{table}] WHERE [{column}] = @0", memberId);
                    if (n > 0) counts[table] = n;
                }
                catch
                {
                    // A count is decoration on the confirmation screen — never worth failing over.
                }
            }
            return counts;
        }

        private string Snapshot(IMember member)
        {
            var props = new Dictionary<string, string>();
            foreach (var p in member.Properties)
            {
                var v = p.GetValue()?.ToString() ?? "";
                if (v.Length > 0) props[p.Alias] = v;
            }

            List<ClubMembershipRow> memberships;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                memberships = scope.Database.Fetch<ClubMembershipRow>(
                    "SELECT ClubId, MembershipType, MembershipStatus, MemberSince FROM ClubMembership WHERE MemberId = @0",
                    member.Id);
            }

            return JsonSerializer.Serialize(new
            {
                id = member.Id,
                key = member.Key,
                name = member.Name,
                email = member.Email,
                username = member.Username,
                created = member.CreateDate,
                lastLogin = member.LastLoginDate,
                roles = _memberService.GetAllRoles(member.Id).ToList(),
                properties = props,
                clubMemberships = memberships
            });
        }

        private class ClubMembershipRow
        {
            public int ClubId { get; set; }
            public string? MembershipType { get; set; }
            public string? MembershipStatus { get; set; }
            public DateTime? MemberSince { get; set; }
        }

        private static void UnionAffiliations(IMember survivor, IMember loser)
        {
            var ids = new List<string>();
            void Collect(IMember m)
            {
                var primary = Prop(m, "primaryClubId");
                if (!string.IsNullOrWhiteSpace(primary)) ids.Add(primary.Trim());
                var csv = Prop(m, "memberClubIDs") ?? "";
                ids.AddRange(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            Collect(survivor);
            Collect(loser);

            var survivorPrimary = Prop(survivor, "primaryClubId");
            if (string.IsNullOrWhiteSpace(survivorPrimary))
            {
                var loserPrimary = Prop(loser, "primaryClubId");
                if (!string.IsNullOrWhiteSpace(loserPrimary) && survivor.HasProperty("primaryClubId"))
                {
                    survivor.SetValue("primaryClubId", loserPrimary.Trim());
                    survivorPrimary = loserPrimary.Trim();
                }
            }

            if (!survivor.HasProperty("memberClubIDs")) return;

            var secondary = ids
                .Where(id => id.Length > 0 && id != (survivorPrimary ?? "").Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            survivor.SetValue("memberClubIDs", string.Join(",", secondary));
        }

        public static bool IsInClub(IMember member, int clubId)
        {
            var id = clubId.ToString();
            if (Prop(member, "primaryClubId")?.Trim() == id) return true;
            var csv = Prop(member, "memberClubIDs") ?? "";
            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(id);
        }

        private static bool HasNoClub(IMember member)
        {
            if (!string.IsNullOrWhiteSpace(Prop(member, "primaryClubId"))) return false;
            var csv = Prop(member, "memberClubIDs") ?? "";
            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0;
        }

        private static MemberSummary Describe(IMember m, int clubId) => new()
        {
            Id = m.Id,
            Name = m.Name ?? "",
            Email = m.Email ?? "",
            PersonNumber = Prop(m, "personNumber") ?? "",
            ShooterIdNumber = Prop(m, "shooterIdNumber") ?? "",
            PhoneNumber = Prop(m, "phoneNumber") ?? "",
            Created = m.CreateDate,
            LastLogin = m.LastLoginDate,
            InClub = IsInClub(m, clubId),
            FilledFields = MergeableFields.Count(f => ReadForMerge(m, f.Alias).Length > 0)
        };

        /// <summary>
        /// Property read that never throws. Umbraco property editors can hand back types that
        /// blow up on a naive cast (see the FlexibleDropdown trap), and a merge screen must not
        /// 500 because one member has an odd stored value.
        /// </summary>
        private static string? Prop(IMember member, string alias)
        {
            try
            {
                if (!member.HasProperty(alias)) return null;
                return member.GetValue(alias)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadForMerge(IMember member, string alias) => (Prop(member, alias) ?? "").Trim();

        /// <summary>
        /// Lowercased and whitespace-collapsed — but NEVER stripped of diacritics. Folding å/ä/ö
        /// onto a/a/o would make Öberg and Oberg the same person, and those are different people.
        /// </summary>
        private static string NormalizeName(IMember m)
        {
            var name = $"{Prop(m, "firstName")} {Prop(m, "lastName")}".Trim();
            if (name.Length == 0) name = m.Name ?? "";
            return NormalizeText(name);
        }

        private static string NormalizeText(string? value)
            => string.Join(" ", (value ?? "").Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private static string NormalizePnr(string? value)
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            // 12 digits and 10 digits are the same person written two ways.
            return digits.Length == 12 ? digits.Substring(2) : digits;
        }

        /// <summary>Digits only, with a Swedish +46 prefix folded back to a leading 0.</summary>
        private static string NormalizePhone(string? value)
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            if (digits.StartsWith("46") && digits.Length >= 10) digits = "0" + digits.Substring(2);
            return digits;
        }

        private static string NormalizeDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return DateTime.TryParse(value, out var dt) ? dt.ToString("yyyy-MM-dd") : value.Trim();
        }

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value.Substring(0, max);
    }

    // =========================================================================
    // Contracts
    // =========================================================================

    public class MemberSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string PersonNumber { get; set; } = "";
        public string ShooterIdNumber { get; set; } = "";
        public string PhoneNumber { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime? LastLogin { get; set; }
        /// <summary>False for a club-less member surfaced as a candidate but not on the roster.</summary>
        public bool InClub { get; set; }
        public int FilledFields { get; set; }
    }

    public class DuplicateCandidate
    {
        public int Score { get; set; }
        public List<string> Reasons { get; set; } = new();
        public MemberSummary A { get; set; } = new();
        public MemberSummary B { get; set; } = new();
    }

    public class MergeField
    {
        public string Alias { get; set; } = "";
        public string Label { get; set; } = "";
        public string SurvivorValue { get; set; } = "";
        public string LoserValue { get; set; } = "";
        /// <summary>Both sides have a value and they differ — the only rows needing a decision.</summary>
        public bool Conflict { get; set; }
        /// <summary>Pre-ticked default: take the loser's value (survivor's is empty).</summary>
        public bool TakeFromLoser { get; set; }
    }

    public class MergeComparison
    {
        public int SuggestedSurvivorId { get; set; }
        public string SuggestedReason { get; set; } = "";
        public MemberSummary Survivor { get; set; } = new();
        public MemberSummary Loser { get; set; } = new();
        public List<MergeField> Fields { get; set; } = new();
        /// <summary>Per-table row counts the loser owns — what the merge is about to move.</summary>
        public Dictionary<string, int> Counts { get; set; } = new();
    }

    public class MergeRequest
    {
        public int ClubId { get; set; }
        public int SurvivorMemberId { get; set; }
        public int LoserMemberId { get; set; }
        public int? MergedByMemberId { get; set; }
        public HashSet<string> TakeFromLoser { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class MergeResult
    {
        public int SurvivorMemberId { get; set; }
        public int LoserMemberId { get; set; }
        public string LoserName { get; set; } = "";
        public string LoserEmail { get; set; } = "";
        public List<string> FieldsTaken { get; set; } = new();
        public List<string> RolesTaken { get; set; } = new();
        public Dictionary<string, int> RowsMoved { get; set; } = new();
        /// <summary>Rows the survivor already had an equivalent of; -1 means "count unknown".</summary>
        public Dictionary<string, int> Conflicts { get; set; } = new();
        public int ClubMembershipsUnioned { get; set; }
        public int RegistrationsMoved { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}
