using HpskSite.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds a club's per-year <see cref="MarkenOrderList"/> — antal per valör att beställa från
    /// förbundet, plus en utdelningslista per medlem.
    ///
    /// <b>⚠️ STANDARDMEDALJER INGÅR INTE</b> (Stefan 2026-08-31). De räknas samman per medlem på sin
    /// egen flik och hör inte till den här summeringen — lägg inte tillbaka dem "för fullständighetens
    /// skull", det gör antalet man beställer efter fel.
    ///
    /// <b>Definitionen är ÅRETS FÖRVÄRVADE MÄRKEN</b> (Stefan 2026-08-31): allt medlemmarna tog
    /// under året, oavsett om klubben redan hunnit beställa det. Ingenting bokförs, så listan är
    /// helt HÄRLEDD ur märkesliggaren (<see cref="MarkenLedgerService"/>) och kan därför aldrig
    /// glida från den. Att bokföra
    /// beställningar — och därmed kunna svara "det som ännu inte beställts" — är ett medvetet
    /// senare val som kräver en egen tabell.
    ///
    /// ⚠️ Läses alltid för ETT år. Ett årtalsmärke är däremot en funktion av HELA historiken, så
    /// den delen måste läsa medlemmens alla kvalifikationsår — se <see cref="ArtalsmarkeEarned"/>.
    /// </summary>
    public class MarkenOrderListService
    {
        private readonly MarkenLedgerService _ledger;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public MarkenOrderListService(
            MarkenLedgerService ledger,
            IMemberService memberService,
            ClubService clubService)
        {
            _ledger = ledger;
            _memberService = memberService;
            _clubService = clubService;
        }

        // Group labels — also the render order (see GroupSort).
        public const string GroupArtalsmarken = "Årtalsmärken";
        public const string GroupGuldfodring = "Guldfodring";

        public async Task<MarkenOrderList> BuildAsync(int clubId, int year)
        {
            var result = new MarkenOrderList
            {
                Year = year,
                ClubId = clubId,
                ClubName = _clubService.GetClubNameById(clubId) ?? $"Klubb {clubId}"
            };

            // The year's harvest, read once each. Everyone who earned ANYTHING this year, nationally —
            // then narrowed to this club's members. The opposite direction (walk the club roster and
            // ask per member) would miss nobody either, but costs a query per member for the many who
            // earned nothing.
            var badges = await _ledger.GetBadgesEarnedInYearAsync(year);
            var quals = await _ledger.GetFulfilledQualificationsForYearAsync(year);

            var candidates = new HashSet<int>(badges.Select(b => b.MemberId));
            foreach (var q in quals) candidates.Add(q.MemberId);

            // Narrow to the club. Primary club only — the same rule the rest of the Märken surfaces
            // use, so a member cannot appear on two clubs' order lists for the same badge.
            var names = new Dictionary<int, string>();
            var mine = new List<int>();
            foreach (var mid in candidates)
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;
                if (!int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;
                mine.Add(mid);
                names[mid] = member.Name ?? $"Medlem {mid}";
            }
            if (mine.Count == 0) return result;

            var mineSet = new HashSet<int>(mine);

            // Årtalsmärke needs each member's whole qualification history, not just this year's row.
            var historyByMember = (await _ledger.GetQualificationsForMembersAsync(mine))
                .Where(q => q.Fulfilled)
                .GroupBy(q => q.MemberId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var badgesByMember = badges.Where(b => mineSet.Contains(b.MemberId))
                .GroupBy(b => b.MemberId).ToDictionary(g => g.Key, g => g.ToList());
            var qualsByMember = quals.Where(q => mineSet.Contains(q.MemberId))
                .GroupBy(q => q.MemberId).ToDictionary(g => g.Key, g => g.ToList());

            var sv = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);

            foreach (var mid in mine.OrderBy(id => names[id], sv))
            {
                var entry = new MarkenHandoutMember { MemberId = mid, Name = names[mid] };

                // ── 1. Grundmärken och familjevalörer tagna i år ──────────────
                foreach (var b in (badgesByMember.GetValueOrDefault(mid) ?? new List<MemberBadge>())
                                  .Where(b => b.LevelOrdinal is >= 1 and <= 3)
                                  .OrderBy(b => FamilyLabel(b.BadgeFamily), sv)
                                  .ThenBy(b => b.LevelOrdinal))
                {
                    string family = FamilyLabel(b.BadgeFamily);
                    bool needsNumber = b.BadgeFamily == Marken.FamilyPistolskytte && b.Level == Marken.LevelGuld;
                    string detail = "";

                    if (needsNumber)
                    {
                        if (string.IsNullOrWhiteSpace(b.UniqueNumber))
                        {
                            // A Guld without its registration number cannot be ordered. Say so here
                            // rather than shipping a count the club discovers is unusable.
                            result.Warnings.Add($"{entry.Name}: guldmärket saknar registreringsnummer — fyll i det innan beställning.");
                            detail = "Guldnummer saknas";
                        }
                        else detail = $"Guldnr {b.UniqueNumber}";
                    }

                    entry.Items.Add(new MarkenHandoutItem
                    {
                        Group = family,
                        Item = b.Level,
                        Detail = detail,
                        Unverified = b.Status == Marken.StatusReported
                    });
                }

                // ── 2. Årtalsmärke som nåddes i år ────────────────────────────
                // Ett årtalsmärke delas ut var tredje uppfyllt guldfodringsår (per familjens
                // kadens). Ett uppfyllt år som INTE korsar ett steg ger alltså inget föremål att
                // beställa — men det läses upp på årsmötet, så det står med som ej beställningsbart.
                foreach (var q in (qualsByMember.GetValueOrDefault(mid) ?? new List<MemberBadgeQualification>())
                                  .OrderBy(q => FamilyLabel(q.BadgeFamily), sv))
                {
                    var history = historyByMember.GetValueOrDefault(mid) ?? new List<MemberBadgeQualification>();
                    var (stepName, throughYear) = ArtalsmarkeEarned(history, q.BadgeFamily, year);

                    if (!string.IsNullOrEmpty(stepName))
                    {
                        entry.Items.Add(new MarkenHandoutItem
                        {
                            Group = GroupArtalsmarken,
                            Item = stepName,
                            Detail = $"{FamilyLabel(q.BadgeFamily)} · {throughYear} uppfyllda år",
                            Unverified = q.Status == Marken.StatusReported
                        });
                    }
                    else
                    {
                        // Säg NÄR nästa märke kommer. Raden "8 uppfyllda år (inget nytt årtalsmärke)"
                        // är obegriplig utan den — den ser ut som att systemet glömt något, när
                        // sanningen är att stegen ligger var tredje år. Rapporterat 2026-08-31.
                        int nextAt = MarkenFamilies.Artalsmarke(q.BadgeFamily, throughYear).NextAtYears;
                        string when = nextAt > 0
                            ? $"nästa årtalsmärke vid {nextAt} år ({nextAt - throughYear} kvar)"
                            : "högsta årtalsmärket är redan uppnått";

                        entry.Items.Add(new MarkenHandoutItem
                        {
                            Group = GroupGuldfodring,
                            Item = $"Guldfodring {year} uppfylld",
                            Detail = $"{FamilyLabel(q.BadgeFamily)} · {throughYear} uppfyllda år · {when}",
                            Orderable = false,
                            Unverified = q.Status == Marken.StatusReported
                        });
                    }
                }

                if (entry.Items.Count > 0) result.Handout.Add(entry);
            }

            // ── Beställningslistan är en aggregering av utdelningslistan ──────
            // Byggd UR samma poster, aldrig ur en egen fråga: två frågor över samma sak är två svar
            // som är fria att säga emot varandra, och då är det antalet man beställer efter som blir fel.
            var lines = new Dictionary<(string Group, string Item), MarkenOrderLine>();
            int unverified2 = 0;
            foreach (var item in result.Handout.SelectMany(h => h.Items))
            {
                if (item.Unverified) unverified2++;
                if (!item.Orderable) continue;

                var key = (item.Group, item.Item);
                if (!lines.TryGetValue(key, out var line))
                {
                    line = new MarkenOrderLine
                    {
                        Group = item.Group,
                        Item = item.Item,
                        Sort = ItemSort(item.Item),
                        Note = item.Group == Marken.FamilyDisplayName(Marken.FamilyPistolskytte) && item.Item == Marken.LevelGuld
                            ? "Registreringsnummer krävs per märke"
                            : ""
                    };
                    lines[key] = line;
                }
                line.Count++;
            }

            result.UnverifiedItems = unverified2;
            result.Order = lines.Values
                .OrderBy(l => GroupSort(l.Group))
                .ThenBy(l => l.Group, sv)
                .ThenBy(l => l.Sort)
                .ThenBy(l => l.Item, sv)
                .ToList();
            result.TotalItems = result.Order.Sum(l => l.Count);

            if (result.UnverifiedItems > 0)
                result.Warnings.Add($"{result.UnverifiedItems} poster är ännu inte granskade av en funktionär. De räknas med i listan — granska dem först om du inte vill beställa på egenrapporterade uppgifter.");

            return result;
        }

        /// <summary>
        /// Did the member reach a NEW årtalsmärke-steg during <paramref name="year"/>, for this family?
        /// Compares the step reached through the year with the step reached through the year before —
        /// the step is a function of the cumulative count, so the year's own row cannot answer it.
        /// Returns the step name (empty when no new step) and the cumulative fulfilled-year count.
        /// </summary>
        private static (string StepName, int ThroughYear) ArtalsmarkeEarned(
            List<MemberBadgeQualification> fulfilledHistory, string family, int year)
        {
            int through = fulfilledHistory.Count(q => q.BadgeFamily == family && q.Year <= year);
            int before = fulfilledHistory.Count(q => q.BadgeFamily == family && q.Year <= year - 1);

            // Compare the NAMES, not a re-derived step index: the cadence differs per family
            // (Pistolskyttets 17-stegsstege vs familjernas egna), and MarkenFamilies already owns it.
            string now = MarkenFamilies.Artalsmarke(family, through).Name;
            string prev = MarkenFamilies.Artalsmarke(family, before).Name;

            return (!string.IsNullOrEmpty(now) && now != prev ? now : "", through);
        }

        private static string FamilyLabel(string? family) => family switch
        {
            Marken.FamilyPistolskytte => Marken.FamilyDisplayName(family),
            Marken.FamilyMastar => "Mästarmärket",
            Marken.FamilyStormastar => "Stormästarmärket",
            _ => MarkenFamilies.DisplayName(family)
        };

        /// <summary>Pistolskyttemärket first (the keystone), then families, then årtalsmärken, then medaljer.</summary>
        private static int GroupSort(string group) =>
            group == Marken.FamilyDisplayName(Marken.FamilyPistolskytte) ? 0
            : group == GroupArtalsmarken ? 2
            : 1;

        private static int ItemSort(string item)
        {
            int ord = Marken.LevelOrdinal(item);
            if (ord > 0) return ord;
            if (item.StartsWith("Brons")) return 1;
            if (item.StartsWith("Silver")) return 2;
            if (item.StartsWith("Guld")) return 3;
            return 9;
        }
    }
}
