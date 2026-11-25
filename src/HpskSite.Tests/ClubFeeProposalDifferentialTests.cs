using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️ Skyddsnät för kalkylatorn (medlemsavgift steg 1.4). <see cref="Frozen"/> är en FRYST kopia av
    /// <c>ClubFeeProposal.Build</c> som den såg ut 2026-11-25 — den får aldrig ändras. Utan tillägg och
    /// avdrag ska den nya beräkningen ge exakt samma Kind/Amount/CategoryId/PrimaryMemberId som kopian.
    /// Två skydd mot ett vakuöst grönt test: (1) golden data med de förväntade talen skrivna för hand,
    /// (2) en kontroll att urvalet innehåller varje <see cref="ClubFeeKind"/>.
    /// </summary>
    public class ClubFeeProposalDifferentialTests
    {
        // ── FRYST kopia. RÖR INTE. ───────────────────────────────────────────────────────────────
        private static class Frozen
        {
            public static Dictionary<int, ClubFeeProposalRow> Build(
                IEnumerable<MembershipFeeCategory> categories, IEnumerable<MemberFeeInput> members)
            {
                var catByType = new Dictionary<string, MembershipFeeCategory>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in categories)
                    if (!string.IsNullOrWhiteSpace(c.MembershipType))
                        catByType[c.MembershipType.Trim()] = c;

                var list = members.ToList();
                var result = new Dictionary<int, ClubFeeProposalRow>();
                var anySelectable = catByType.Values.Any(c => c.MemberSelectable);
                var inHousehold = new HashSet<int>();

                foreach (var group in list
                             .Where(m => !string.IsNullOrWhiteSpace(m.HouseholdId))
                             .GroupBy(m => m.HouseholdId!.Trim(), StringComparer.OrdinalIgnoreCase)
                             .Where(g => g.Count() > 1))
                {
                    foreach (var m in group) inHousehold.Add(m.MemberId);
                    var primary = group.FirstOrDefault(m => m.HouseholdPrimary) ?? group.OrderBy(m => m.MemberId).First();
                    var type = (primary.MembershipType ?? "").Trim();
                    if (type.Length == 0 || !catByType.TryGetValue(type, out var fam)) continue;

                    result[primary.MemberId] = new ClubFeeProposalRow(primary.MemberId, ClubFeeKind.HouseholdPrimary,
                        fam.Amount, fam.Id, type, null);
                    foreach (var m in group.Where(m => m.MemberId != primary.MemberId))
                        result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.Covered, 0m, fam.Id,
                            (m.MembershipType ?? "").Trim(), primary.MemberId);
                }

                foreach (var m in list.Where(m => !result.ContainsKey(m.MemberId)))
                {
                    var type = (m.MembershipType ?? "").Trim();
                    if (type.Length == 0)
                        result[m.MemberId] = new ClubFeeProposalRow(m.MemberId,
                            anySelectable && !inHousehold.Contains(m.MemberId) ? ClubFeeKind.ChooseType : ClubFeeKind.NoType,
                            0m, null, null, null);
                    else if (!catByType.TryGetValue(type, out var cat))
                        result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.NoCategory, 0m, null, type, null);
                    else
                        result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.Individual, cat.Amount, cat.Id, type, null);
                }
                return result;
            }
        }

        private static MembershipFeeCategory Cat(int id, string type, decimal amount, bool selectable = true)
            => new() { Id = id, MembershipType = type, Label = type, Amount = amount, MemberSelectable = selectable };

        private static MemberFeeInput M(int id, string? type, string? household = null, bool primary = false)
            => new() { MemberId = id, MembershipType = type, HouseholdId = household, HouseholdPrimary = primary };

        private static readonly MembershipFeeCategory[] Categories =
        {
            Cat(1, "Senior", 600m),
            Cat(2, "Pensionär", 400m),
            Cat(3, "Junior", 150m),
            Cat(4, "Familj", 900m, selectable: false),
        };

        // Ett urval som träffar varje ClubFeeKind: Individual, HouseholdPrimary, Covered, NoCategory, NoType, ChooseType.
        private static List<MemberFeeInput> Members() => new()
        {
            M(10, "Senior"),
            M(11, "senior"),                         // skiftläge
            M(12, "Pensionär"),
            M(13, " Junior "),                       // blanksteg
            M(20, "Familj", "H1", primary: true),    // huvudmedlem
            M(21, "Familj", "H1"),                   // ingår
            M(22, "Senior", "H1"),                   // ingår, annan typ
            M(30, "Veteran"),                        // okänd typ → NoCategory
            M(40, null),                             // saknar typ → ChooseType (typer går att välja)
            M(41, ""),
            M(50, "Familj", "H2"),                   // ensam i sitt hushåll → individ
            M(60, null, "H3"), M(61, "Senior", "H3"),// hushåll vars huvudmedlem saknar typ → faller ur hushållet
            M(70, "Hedersmedlem"),                   // typ utan avgift → NoCategory
        };

        [Fact]
        public void Urvalet_innehaller_varje_ClubFeeKind()
        {
            var kinds = ClubFeeProposal.Build(Categories, Members()).Values.Select(r => r.Kind).ToHashSet();
            foreach (var k in Enum.GetValues<ClubFeeKind>())
                Assert.Contains(k, kinds); // annars är differentialtestet vakuöst för den sorten
        }

        [Fact]
        public void NoType_ar_med_nar_ingen_typ_gar_att_valja()
        {
            // Samma urval men ingen kategori är valbar → saknad typ blir NoType, inte ChooseType.
            var cats = Categories.Select(c => Cat(c.Id, c.MembershipType, c.Amount, selectable: false)).ToArray();
            var kinds = ClubFeeProposal.Build(cats, Members()).Values.Select(r => r.Kind).ToHashSet();
            Assert.Contains(ClubFeeKind.NoType, kinds);
        }

        [Fact]
        public void Nuvarande_beraekning_ar_identisk_med_den_frusna_kopian()
        {
            AssertSame(Frozen.Build(Categories, Members()), ClubFeeProposal.Build(Categories, Members()));

            var noSelectable = Categories.Select(c => Cat(c.Id, c.MembershipType, c.Amount, selectable: false)).ToArray();
            AssertSame(Frozen.Build(noSelectable, Members()), ClubFeeProposal.Build(noSelectable, Members()));

            AssertSame(Frozen.Build(Array.Empty<MembershipFeeCategory>(), Members()),
                       ClubFeeProposal.Build(Array.Empty<MembershipFeeCategory>(), Members()));
        }

        /// <summary>Golden data: förväntade tal skrivna för hand, så att även kopian prövas mot något annat än sig själv.</summary>
        [Fact]
        public void Golden_data_for_urvalet()
        {
            var p = ClubFeeProposal.Build(Categories, Members());

            Assert.Equal((ClubFeeKind.Individual, 600m, 1), (p[10].Kind, p[10].Amount, p[10].CategoryId!.Value));
            Assert.Equal((ClubFeeKind.Individual, 600m, 1), (p[11].Kind, p[11].Amount, p[11].CategoryId!.Value));
            Assert.Equal((ClubFeeKind.Individual, 400m, 2), (p[12].Kind, p[12].Amount, p[12].CategoryId!.Value));
            Assert.Equal((ClubFeeKind.Individual, 150m, 3), (p[13].Kind, p[13].Amount, p[13].CategoryId!.Value));

            Assert.Equal((ClubFeeKind.HouseholdPrimary, 900m, 4), (p[20].Kind, p[20].Amount, p[20].CategoryId!.Value));
            Assert.Equal((ClubFeeKind.Covered, 0m, 20), (p[21].Kind, p[21].Amount, p[21].PrimaryMemberId!.Value));
            Assert.Equal((ClubFeeKind.Covered, 0m, 20), (p[22].Kind, p[22].Amount, p[22].PrimaryMemberId!.Value));

            Assert.Equal((ClubFeeKind.NoCategory, 0m), (p[30].Kind, p[30].Amount));
            Assert.Equal((ClubFeeKind.ChooseType, 0m), (p[40].Kind, p[40].Amount));
            Assert.Equal((ClubFeeKind.ChooseType, 0m), (p[41].Kind, p[41].Amount));
            Assert.Equal(ClubFeeKind.Individual, p[50].Kind); // hushåll med en person är inget hushåll
            Assert.Equal((ClubFeeKind.NoCategory, 0m), (p[70].Kind, p[70].Amount));
        }

        [Fact]
        public void Hushall_vars_huvudmedlem_saknar_typ_faller_tillbaka_pa_individer_utan_val()
        {
            // M60 (ingen typ) är lägsta id och därmed huvudmedlem → hushållet hoppas över i första loopen,
            // och eftersom båda STÅR i ett hushåll får M60 aldrig välja själv (NoType, inte ChooseType).
            var p = ClubFeeProposal.Build(Categories, Members());
            Assert.Equal(ClubFeeKind.NoType, p[60].Kind);
            Assert.Equal((ClubFeeKind.Individual, 600m), (p[61].Kind, p[61].Amount));
        }

        private static void AssertSame(Dictionary<int, ClubFeeProposalRow> expected, Dictionary<int, ClubFeeProposalRow> actual)
        {
            Assert.Equal(expected.Keys.OrderBy(x => x), actual.Keys.OrderBy(x => x));
            foreach (var (id, e) in expected)
            {
                var a = actual[id];
                Assert.True(e == a, $"Medlem {id}: väntade {e}, fick {a}");
            }
        }
    }
}
