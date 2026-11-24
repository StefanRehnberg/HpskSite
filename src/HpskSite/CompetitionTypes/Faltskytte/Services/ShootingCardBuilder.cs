using HpskSite.CompetitionTypes.Faltskytte.Controllers;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>Vilka patruller som skrivs ut. Båda null = alla. Patrullnummer går före vapengrupp.</summary>
    public record ShootingCardSelection(HashSet<int>? PatrolNumbers, string? WeaponGroup);

    public class ShootingCardBuildResult
    {
        public List<ShootingCardModel> Cards { get; } = new();
        public List<string> Notices { get; } = new();
        public int PatrolCount { get; set; }
    }

    /// <summary>
    /// Bygger skjutkorten ur patrullerna. Stationerna per skytt väljs EXAKT som kortinmatningen gör
    /// (<see cref="FaltskytteCardEntryController.ResolveKey"/> + <see cref="FaltskytteCardEntryController.ScoredStations"/>), så det som
    /// skrivs på papperet är det som kortinmatningen sedan tar emot.
    ///
    /// Skyttar i samma patrull med OLIKA förutsättningar (olika stationer, figurantal eller poängmål per vapengrupp) får olika kort — ett kort har
    /// alltid en enda uppsättning stationer (Stefan 2026-11-23). Skyttar markerade DNS skrivs inte ut. Fler än 12 skyttar på ett kort delas på flera.
    /// <see cref="Build"/> är en ren funktion (inga databasanrop) så att reglerna går att enhetstesta.
    /// </summary>
    public static class ShootingCardBuilder
    {
        public static HashSet<int>? ParsePatrolNumbers(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var set = new HashSet<int>();
            foreach (var raw in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Trim();
                var dash = part.IndexOf('-');
                if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b))
                {
                    if (b < a) (a, b) = (b, a);
                    for (var i = a; i <= b && i - a < 500; i++) set.Add(i);
                }
                else if (int.TryParse(part, out var one)) set.Add(one);
            }
            return set;
        }

        public static async Task<ShootingCardBuildResult> BuildAsync(IUmbracoDatabase db, FaltskytteCompetitionConfig config, int competitionId, ShootingCardSelection sel, int blankCount, string layoutId = ShootingCardLayout.Figures, bool poangRow = false)
        {
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);
            var ids = patrols.Select(p => p.Id).ToList();
            var members = ids.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>($"WHERE PatrolId IN ({string.Join(",", ids)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();
            return Build(config, patrols, members, sel, blankCount, layoutId, poangRow);
        }

        /// <param name="layoutId">
        /// Layouten avgör hur många skyttar ett kort rymmer och hur de delas (<see cref="IShootingCardLayout.Split"/>), och om skyttar bara delar kort när även
        /// poängmålens platser är lika (Skyttblock och Skyttkort ritar poängrutan vid en viss figur; dagens tabell-layouter visar bara antalet poängrutor).
        /// Okänt id = dagens standardlayout.
        /// </param>
        /// <param name="poangRow">Poängrutorna på en egen rad: då spelar poängmålens plats ingen roll för kortet, bara antalet.</param>
        public static ShootingCardBuildResult Build(FaltskytteCompetitionConfig config, IReadOnlyList<FaltskyttePatrol> allPatrols,
            IReadOnlyList<FaltskyttePatrolMember> members, ShootingCardSelection sel, int blankCount, string layoutId = ShootingCardLayout.Figures, bool poangRow = false)
        {
            var layout = ShootingCardLayouts.Get(layoutId) ?? ShootingCardLayouts.Default;
            var poangByFigure = layout.PoangByFigure && !poangRow;
            var result = new ShootingCardBuildResult();
            IEnumerable<FaltskyttePatrol> chosen = allPatrols.OrderBy(p => p.PatrolNumber);
            if (sel.PatrolNumbers != null) chosen = chosen.Where(p => sel.PatrolNumbers.Contains(p.PatrolNumber));
            else if (!string.IsNullOrWhiteSpace(sel.WeaponGroup))
                chosen = chosen.Where(p => string.Equals(p.WeaponGroup, sel.WeaponGroup, StringComparison.OrdinalIgnoreCase));
            var patrols = chosen.ToList();
            result.PatrolCount = patrols.Count;
            if (sel.PatrolNumbers != null && patrols.Count == 0) result.Notices.Add("Urvalet av patruller gav inga patruller.");

            var withoutConfig = new List<string>();
            foreach (var p in patrols)
            {
                var shooters = members.Where(m => m.PatrolId == p.Id && !FaltskytteCardEntryController.IsDns(m)).OrderBy(m => m.Position).ToList();
                // En grupp per uppsättning stationer, i den ordning skyttarna först förekommer.
                var groups = new List<(string Sig, List<ShootingCardStation> Stations, List<string> Keys, List<ShootingCardShooter> Shooters)>();
                foreach (var m in shooters)
                {
                    var key = FaltskytteCardEntryController.ResolveKey(config, p, m);
                    if (key == null) { withoutConfig.Add($"{m.MemberName} (patrull {p.PatrolNumber})"); continue; }
                    var stations = FaltskytteCardEntryController.ScoredStations(config, key).Select(ShootingCardStation.From).ToList();
                    var sig = string.Join(";", stations.Select(s => s.Signature(poangByFigure)));
                    var gi = groups.FindIndex(x => x.Sig == sig);
                    if (gi < 0) { groups.Add((sig, stations, new List<string>(), new List<ShootingCardShooter>())); gi = groups.Count - 1; }
                    var g = groups[gi];
                    if (!g.Keys.Contains(key)) g.Keys.Add(key);
                    g.Shooters.Add(new ShootingCardShooter
                    {
                        Name = m.MemberName,
                        Club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                        ShootingClass = ShootingClasses.DisplayName(m.ShootingClass),
                        Position = m.Position
                    });
                }

                foreach (var g in groups)
                {
                    var weaponText = groups.Count == 1 && !string.IsNullOrWhiteSpace(p.WeaponGroup)
                        ? ShootingClasses.WeaponGroupsText(p.WeaponGroup)
                        : ShootingClasses.WeaponGroupsText(string.Join("+", g.Keys));
                    var sizes = layout.Split(g.Shooters.Count);
                    var at = 0;
                    for (var part = 0; part < sizes.Count; part++)
                    {
                        result.Cards.Add(new ShootingCardModel
                        {
                            PatrolNumber = p.PatrolNumber,
                            Label = p.Label,
                            StartTime = ShootingCardLayout.TimeText(p.StartTime),
                            WeaponGroupText = weaponText,
                            Part = part + 1,
                            Parts = sizes.Count,
                            Stations = g.Stations,
                            Shooters = g.Shooters.Skip(at).Take(sizes[part]).ToList()
                        });
                        at += sizes[part];
                    }
                }
            }

            if (withoutConfig.Count > 0)
                result.Notices.Add($"{withoutConfig.Count} skyttar saknar stationskonfiguration för sin klass och finns inte på något skjutkort: "
                    + string.Join(", ", withoutConfig.Take(5)) + (withoutConfig.Count > 5 ? " …" : "") + ".");

            // Blanka kort: samma stationer som det mest krävande kortet; numret, tiden och namnen skrivs för hand.
            if (blankCount > 0)
            {
                var stations = MostDemanding(result.Cards.Select(c => c.Stations), layout) ?? MostDemanding(AllConfigStations(config), layout);
                if (stations == null) result.Notices.Add("Det finns ingen stationskonfiguration att göra blanka kort av.");
                else
                    for (var i = 0; i < Math.Min(blankCount, 50); i++)
                        result.Cards.Add(new ShootingCardModel
                        {
                            IsBlank = true,
                            Stations = stations,
                            Shooters = Enumerable.Range(0, layout.Capacity).Select(_ => new ShootingCardShooter()).ToList()
                        });
            }
            return result;
        }

        /// <summary>Alla stationsuppsättningar i tävlingens konfiguration, en per vapengrupp.</summary>
        public static List<List<ShootingCardStation>> AllConfigStations(FaltskytteCompetitionConfig config) =>
            config.WeaponConfigs.Keys
                .Select(k => FaltskytteCardEntryController.ScoredStations(config, k).Select(ShootingCardStation.From).ToList())
                .Where(l => l.Count > 0).ToList();

        /// <summary>Den uppsättning som tar mest plats på kortet (flest rutor), alltså den som blir trängst.</summary>
        public static List<ShootingCardStation>? MostDemanding(IEnumerable<IEnumerable<ShootingCardStation>> sets, IShootingCardLayout? layout = null)
        {
            layout ??= ShootingCardLayouts.Default;
            return sets.Select(s => s.ToList()).Where(l => l.Count > 0).OrderByDescending(l => layout.Demand(l)).FirstOrDefault();
        }
    }
}
