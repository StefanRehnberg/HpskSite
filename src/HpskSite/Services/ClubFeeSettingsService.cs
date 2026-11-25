using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Skrivvägen för klubbens avgiftsinställningar utöver grundavgiften: tillägg/avdrag (<see cref="MembershipFeePart"/>),
    /// klubbens tillval (<see cref="ClubFeeOption"/>) och vilka tillval en medlem har (<see cref="ClubMembershipOption"/>).
    ///
    /// <para>⚠️ Behörigheten prövas av anroparen (<c>MembershipFeeAccess.CanWriteAsync(clubId)</c>). Tjänsten prövar att varje
    /// id hör till KLUBBEN i anropet — en skrivrätt i klubb A får aldrig röra klubb B:s rader. Efter en ändring av delar eller
    /// tillval ska anroparen rensa klubbens oskickade avgifter (<c>MembershipFeeService.ClearUnsentClubCharges</c>) så att de räknas om.</para>
    /// </summary>
    public class ClubFeeSettingsService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly MembershipTypeCatalog _types;

        public ClubFeeSettingsService(IScopeProvider scopeProvider, MembershipTypeCatalog types)
        {
            _scopeProvider = scopeProvider;
            _types = types;
        }

        // ── Läsa ──────────────────────────────────────────────────────

        public List<ClubFeeOption> GetOptions(int clubId, bool includeInactive = true)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<ClubFeeOption>(
                "SELECT * FROM ClubFeeOption WHERE ClubId = @0 " + (includeInactive ? "" : "AND IsActive = 1 ") + "ORDER BY SortOrder, Name",
                clubId);
        }

        // ── Tillägg/avdrag ────────────────────────────────────────────

        /// <summary>Sparar (id 0 = ny) ett tillägg/avdrag med sitt typurval. Returnerar (id, fel).</summary>
        public (int Id, string? Error) SavePart(int clubId, int year, int id, string? label, decimal amount,
            string? condition, int? optionId, IEnumerable<string>? typeKeys)
        {
            var validKeys = _types.Types(clubId).Select(t => t.Key).ToList();
            var keys = ClubFeeSettingsRules.NormalizeTypeKeys(typeKeys, validKeys);

            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var optionIds = db.Fetch<int>("SELECT Id FROM ClubFeeOption WHERE ClubId = @0", clubId).ToHashSet();

            // Ett typurval med okänd nyckel ska avvisas, inte tyst tappas: jämför före normaliseringen.
            var requested = (typeKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            if (requested.Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
                return (0, "Okänd medlemstyp i urvalet.");

            var error = ClubFeeSettingsRules.ValidatePart(label, amount, condition, optionId, keys, optionIds, validKeys);
            if (error is not null) return (0, error);

            MembershipFeePart part;
            if (id > 0)
            {
                part = db.SingleOrDefault<MembershipFeePart>("SELECT * FROM MembershipFeePart WHERE Id = @0 AND ClubId = @1 AND Year = @2", id, clubId, year);
                if (part is null) return (0, "Tillägget hittades inte.");
            }
            else
            {
                part = new MembershipFeePart
                {
                    ClubId = clubId,
                    Year = year,
                    CreatedDate = DateTime.UtcNow,
                    SortOrder = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM MembershipFeePart WHERE ClubId = @0 AND Year = @1", clubId, year) + 1 ?? 0
                };
            }

            part.Label = label!.Trim();
            part.Amount = amount;
            part.Condition = condition!;
            part.OptionId = condition == MembershipFeePartCondition.Option ? optionId : null;
            if (part.Id > 0) db.Update(part); else db.Insert(part);

            db.Execute("DELETE FROM MembershipFeePartType WHERE PartId = @0", part.Id);
            foreach (var k in keys) db.Insert(new MembershipFeePartType { PartId = part.Id, TypeKey = k });

            scope.Complete();
            return (part.Id, null);
        }

        /// <summary>Tar bort ett tillägg. Returnerar false när det inte hör till klubben och året.</summary>
        public bool DeletePart(int clubId, int year, int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var owned = db.ExecuteScalar<int>("SELECT COUNT(*) FROM MembershipFeePart WHERE Id = @0 AND ClubId = @1 AND Year = @2", id, clubId, year);
            if (owned == 0) return false;
            db.Execute("DELETE FROM MembershipFeePartType WHERE PartId = @0", id);
            db.Execute("DELETE FROM MembershipFeePart WHERE Id = @0", id);
            scope.Complete();
            return true;
        }

        // ── Tillval ───────────────────────────────────────────────────

        public (int Id, string? Error) SaveOption(int clubId, int id, string? name, string? systemKey, bool isActive)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var others = db.Fetch<string>("SELECT Name FROM ClubFeeOption WHERE ClubId = @0 AND Id <> @1", clubId, id);
            var error = ClubFeeSettingsRules.ValidateOptionName(name, others);
            if (error is not null) return (0, error);

            ClubFeeOption opt;
            if (id > 0)
            {
                opt = db.SingleOrDefault<ClubFeeOption>("SELECT * FROM ClubFeeOption WHERE Id = @0 AND ClubId = @1", id, clubId);
                if (opt is null) return (0, "Tillvalet hittades inte.");
            }
            else
            {
                opt = new ClubFeeOption
                {
                    ClubId = clubId,
                    CreatedDate = DateTime.UtcNow,
                    SortOrder = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM ClubFeeOption WHERE ClubId = @0", clubId) + 1 ?? 0
                };
                opt.SystemKey = string.IsNullOrWhiteSpace(systemKey) ? null : systemKey.Trim();
            }
            opt.Name = name!.Trim();
            opt.IsActive = isActive;
            if (opt.Id > 0) db.Update(opt); else db.Insert(opt);
            scope.Complete();
            return (opt.Id, null);
        }

        /// <summary>
        /// Raderar ett tillval som ingen använder. Används det av en medlem eller ett tillägg får kassören veta det
        /// (och kan i stället dölja tillvalet) — ett tillägg som tyst tappar sitt villkor skulle ändra beloppen.
        /// </summary>
        public (bool Deleted, string? Error) DeleteOption(int clubId, int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM ClubFeeOption WHERE Id = @0 AND ClubId = @1", id, clubId) == 0)
                return (false, "Tillvalet hittades inte.");
            var members = db.ExecuteScalar<int>("SELECT COUNT(*) FROM ClubMembershipOption WHERE OptionId = @0", id);
            var parts = db.ExecuteScalar<int>("SELECT COUNT(*) FROM MembershipFeePart WHERE OptionId = @0", id);
            if (members > 0 || parts > 0)
                return (false, $"Tillvalet används av {members} medlemmar och {parts} tillägg/avdrag. Dölj det i stället, eller ta bort kopplingarna först.");
            db.Execute("DELETE FROM ClubFeeOption WHERE Id = @0", id);
            scope.Complete();
            return (true, null);
        }

        // ── Medlemstyper ──────────────────────────────────────────────

        /// <summary>Klubbens typer med antal aktiva medlemmar per typ (för listan och för "Flytta medlemmar").</summary>
        public List<(MembershipTypeEntry Type, int Members)> GetTypesWithCounts(int clubId)
        {
            var types = _types.Types(clubId);
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<string>(
                "SELECT MembershipType FROM ClubMembership WHERE ClubId = @0 AND MembershipStatus NOT IN (N'Utträdd', N'Avliden') AND MembershipType IS NOT NULL", clubId);
            return types.Select(t => (t, rows.Count(r => MembershipTypeKeys.Same(r, t.Key)))).ToList();
        }

        /// <summary>
        /// Byter namn på / döljer en typ (key finns) eller skapar en egen typ (key tom → ny nyckel x-…).
        /// Familj kan varken döpas om eller döljas. Namnet är unikt per klubb.
        /// </summary>
        public (string? Key, string? Error) SaveType(int clubId, string? key, string? name, bool isActive)
        {
            var current = _types.Types(clubId);
            var isNew = string.IsNullOrWhiteSpace(key);
            MembershipTypeEntry? existing = null;
            if (!isNew)
            {
                existing = current.FirstOrDefault(t => MembershipTypeKeys.Same(t.Key, key));
                if (existing is null) return (null, "Medlemstypen hittades inte.");
                var editError = ClubFeeSettingsRules.ValidateTypeEdit(existing.Key, name, existing.Name, isActive);
                if (editError is not null) return (null, editError);
            }
            var nameError = ClubFeeSettingsRules.ValidateTypeName(name,
                current.Where(t => existing is null || !MembershipTypeKeys.Same(t.Key, existing.Key)).Select(t => t.Name));
            if (nameError is not null) return (null, nameError);

            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            ClubMembershipType? row = null;
            if (existing is not null)
                row = db.SingleOrDefault<ClubMembershipType>("SELECT * FROM ClubMembershipType WHERE ClubId = @0 AND TypeKey = @1", clubId, existing.Key);

            if (row is null)
            {
                row = new ClubMembershipType
                {
                    ClubId = clubId,
                    TypeKey = existing?.Key ?? MembershipTypeKeys.CustomPrefix + Guid.NewGuid().ToString("N")[..12],
                    CreatedUtc = DateTime.UtcNow
                };
                row.Name = name!.Trim();
                row.IsActive = isActive;
                row.UpdatedUtc = DateTime.UtcNow;
                db.Insert(row);
            }
            else
            {
                row.Name = name!.Trim();
                row.IsActive = isActive;
                row.UpdatedUtc = DateTime.UtcNow;
                db.Update(row);
            }
            scope.Complete();
            return (row.TypeKey, null);
        }

        /// <summary>Raderar en EGEN typ som ingen medlem och inget tillägg använder. Standardtyper kan bara döljas.</summary>
        public (bool Deleted, string? Error) DeleteType(int clubId, string key)
        {
            if (!MembershipTypeKeys.IsCustom(key)) return (false, "Bara egna medlemstyper kan tas bort. Dölj en standardtyp i stället.");
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var row = db.SingleOrDefault<ClubMembershipType>("SELECT * FROM ClubMembershipType WHERE ClubId = @0 AND TypeKey = @1", clubId, key);
            if (row is null) return (false, "Medlemstypen hittades inte.");
            var members = db.ExecuteScalar<int>("SELECT COUNT(*) FROM ClubMembership WHERE ClubId = @0 AND MembershipType = @1", clubId, row.TypeKey);
            var parts = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM MembershipFeePartType t JOIN MembershipFeePart p ON p.Id = t.PartId WHERE p.ClubId = @0 AND t.TypeKey = @1", clubId, row.TypeKey);
            var rates = db.ExecuteScalar<int>("SELECT COUNT(*) FROM MembershipFeeCategory WHERE ClubId = @0 AND IssuerType = 0 AND MembershipType = @1", clubId, row.TypeKey);
            if (members > 0 || parts > 0 || rates > 0)
                return (false, $"Typen används av {members} medlemmar, {parts} tillägg/avdrag och {rates} avgifter. Flytta medlemmarna och dölj typen i stället.");
            db.Execute("DELETE FROM ClubMembershipType WHERE Id = @0", row.Id);
            scope.Complete();
            return (true, null);
        }

        /// <summary>
        /// Sätter medlemstyp på valda aktiva medlemmar (massåtgärd), eller flyttar ALLA från en typ (fromKey) till en annan.
        /// Familj är hushållets typ (kopplas av klubben) och kan varken sättas eller lämnas här.
        /// </summary>
        public (int Changed, string? Error) SetMembersType(int clubId, string? toKey, IEnumerable<int>? memberIds, string? fromKey)
        {
            var target = _types.Types(clubId).FirstOrDefault(t => MembershipTypeKeys.Same(t.Key, toKey));
            if (target is null) return (0, "Okänd medlemstyp.");
            if (!target.IsActive) return (0, "Medlemstypen är dold. Visa den först.");
            if (MembershipTypeKeys.IsFamilj(target.Key)) return (0, "Familj sätts genom att koppla ihop hushållet, inte här.");
            var ids = memberIds?.Distinct().ToList() ?? new List<int>();
            if (string.IsNullOrWhiteSpace(fromKey) && ids.Count == 0) return (0, "Välj medlemmar eller en typ att flytta från.");
            if (!string.IsNullOrWhiteSpace(fromKey) && MembershipTypeKeys.IsFamilj(fromKey)) return (0, "Familj kan inte flyttas här.");

            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            const string active = "MembershipStatus NOT IN (N'Utträdd', N'Avliden')";
            int changed;
            if (!string.IsNullOrWhiteSpace(fromKey))
            {
                var from = _types.Types(clubId).FirstOrDefault(t => MembershipTypeKeys.Same(t.Key, fromKey));
                if (from is null) return (0, "Okänd medlemstyp att flytta från.");
                changed = db.Execute($"UPDATE ClubMembership SET MembershipType = @0 WHERE ClubId = @1 AND {active} AND MembershipType = @2 AND MembershipType <> @3",
                    target.Key, clubId, from.Key, MembershipTypeKeys.Familj);
            }
            else
            {
                changed = 0;
                foreach (var chunk in ids.Chunk(500))
                    changed += db.Execute($"UPDATE ClubMembership SET MembershipType = @0 WHERE ClubId = @1 AND {active} AND MemberId IN (@2) AND ISNULL(MembershipType, N'') <> @3",
                        target.Key, clubId, chunk, MembershipTypeKeys.Familj);
            }
            scope.Complete();
            return (changed, null);
        }

        // ── Medlemmens tillval ────────────────────────────────────────

        public List<int> GetMemberOptionIds(int clubId, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT OptionId FROM ClubMembershipOption WHERE ClubId = @0 AND MemberId = @1", clubId, memberId);
        }

        /// <summary>Sätter medlemmens tillval till exakt listan. Medlemmen måste vara aktiv i klubben; tillvalen måste höra till klubben.</summary>
        public string? SetMemberOptions(int clubId, int memberId, IEnumerable<int> optionIds)
        {
            var wanted = optionIds.Distinct().ToList();
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;

            var active = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ClubMembership WHERE ClubId = @0 AND MemberId = @1 AND MembershipStatus NOT IN (N'Utträdd', N'Avliden')",
                clubId, memberId);
            if (active == 0) return "Medlemmen finns inte i klubbens register.";

            var valid = db.Fetch<int>("SELECT Id FROM ClubFeeOption WHERE ClubId = @0", clubId).ToHashSet();
            if (wanted.Any(o => !valid.Contains(o))) return "Okänt tillval.";

            db.Execute("DELETE FROM ClubMembershipOption WHERE ClubId = @0 AND MemberId = @1", clubId, memberId);
            foreach (var o in wanted)
                db.Insert(new ClubMembershipOption { ClubId = clubId, MemberId = memberId, OptionId = o, CreatedDate = DateTime.UtcNow });
            scope.Complete();
            return null;
        }
    }
}
