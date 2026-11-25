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
