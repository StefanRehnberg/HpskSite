using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using HpskSite.Models.PrizeGiving;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Årets mästerskapsmedaljer för en klubb eller en krets — underlaget för beställning,
    /// gravyr och upprop på årsmötet.
    ///
    /// <para><b>⚠️ TJÄNSTEN RANKAR INTE OM NÅGOT.</b> Varje tävlings medaljörer hämtas genom
    /// <see cref="PrizeGivingService"/>, som i sin tur läser den sparade resultatartefaktens
    /// <c>MedalAwards</c>. Det är med flit: rankningen kräver mästerskapskategorin, finalist-
    /// filtret, innertiorna och särskjutningen samtidigt, och de finns i hand exakt en gång —
    /// när resultatlistan räknas ut. En andra rankning här vore en ny chans att förväxla
    /// mästerskapskategori med skicklighetsklass, vilket redan hänt flera gånger i kodbasen.
    /// Priset är att en tävling utan omräknad artefakt saknar medaljer, och det priset betalas
    /// med en NAMNGIVEN varning per tävling i stället för en tyst lucka.</para>
    ///
    /// <para><b>⚠️ Medaljindelningen kan skilja sig mellan tävlingarna i samma lista.</b>
    /// <c>medalsPerWeaponGroup</c> är arrangörens val per tävling (se <see cref="MedalGrouping"/>),
    /// så ett klubbmästerskap kan ha delat vapengrupp C i mästerskapsklasser medan nästa gav ett
    /// enda guld per vapengrupp. Därför bär varje tävlingsrad sin egen indelningstext, hämtad ur
    /// ARTEFAKTEN och inte ur tävlingens nuvarande inställning — samma regel som att enheten
    /// måste resa med talet. Utan den går det inte att se om ett saknat damguld är ett val eller
    /// ett fel, och det är precis den frågan man ställer sig när man beställer medaljerna.</para>
    ///
    /// <para>Springskytte har inga mästerskapsmedaljer byggda alls och bidrar därför aldrig med
    /// rader. En springskyttetävling som är ett mästerskap räknas ändå upp bland tävlingarna,
    /// med skälet utskrivet — en gren som tyst utelämnas läses som "inga medaljer".</para>
    /// </summary>
    public class MedalHandoutService
    {
        private const string ScanCacheKey = "medal_handout_competition_scan";
        private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromSeconds(60);

        public const string ScopeClub = "Club";
        public const string ScopeRegion = "Region";

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly PrizeGivingService _prizeGiving;
        private readonly AppCaches _appCaches;
        private readonly ILogger<MedalHandoutService> _logger;

        public MedalHandoutService(
            IUmbracoContextAccessor umbracoContextAccessor,
            PrizeGivingService prizeGiving,
            AppCaches appCaches,
            ILogger<MedalHandoutService> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _prizeGiving = prizeGiving;
            _appCaches = appCaches;
            _logger = logger;
        }

        /// <summary>Klubbens egna klubbmästerskap under året.</summary>
        public Task<MedalHandoutList> BuildForClubAsync(int clubId, int year) =>
            BuildAsync(ScopeClub, clubId.ToString(), year);

        /// <summary>Kretsens kretsmästerskap under året — oavsett om kretsen själv eller en
        /// klubb i kretsen stod som arrangör. Medaljerna är kretsens att dela ut.</summary>
        public Task<MedalHandoutList> BuildForRegionAsync(string regionCode, int year) =>
            BuildAsync(ScopeRegion, regionCode, year);

        private async Task<MedalHandoutList> BuildAsync(string scopeType, string scopeId, int year)
        {
            var list = new MedalHandoutList
            {
                Year = year,
                ScopeType = scopeType,
                ScopeId = scopeId,
                Scope = scopeType == ScopeRegion
                    ? CompetitionScopeHelper.Kretsmasterskap
                    : CompetitionScopeHelper.Klubbmasterskap
            };

            var scan = GetScan();
            list.ScopeName = scopeType == ScopeRegion
                ? ResolveRegionName(scan, scopeId)
                : (scan.Clubs.FirstOrDefault(c => c.Id.ToString() == scopeId) is { } club
                    ? (club.Value<string>("clubName") ?? club.Name ?? "")
                    : "");

            var competitions = SelectCompetitions(scan, scopeType, scopeId, year, list.Scope);
            if (competitions.Count == 0)
            {
                list.Warnings.Add(scopeType == ScopeRegion
                    ? $"Inga kretsmästerskap med tävlingsdatum under {year} hittades. Kontrollera att tävlingarnas omfattning är satt till Kretsmästerskap."
                    : $"Inga klubbmästerskap med tävlingsdatum under {year} hittades. Kontrollera att tävlingarnas omfattning är satt till Klubbmästerskap.");
                return list;
            }

            // Mottagarna slås ihop på medlems-id över alla tävlingar — en person kallas fram en
            // gång och får allt hen vunnit under året, inte en gång per tävling. Nyckel 0 finns
            // för en lagmedlem utan medlemskoppling; de får inte slås ihop med varandra, så de
            // nyckas på namnet i stället.
            var recipients = new Dictionary<string, MedalHandoutRecipient>();
            MedalHandoutRecipient Recipient(int memberId, string name, string clubName)
            {
                var key = memberId > 0 ? $"m{memberId}" : $"n{(name ?? "").Trim().ToLowerInvariant()}";
                if (!recipients.TryGetValue(key, out var r))
                {
                    r = new MedalHandoutRecipient { MemberId = memberId, Name = name ?? "", Club = clubName ?? "" };
                    recipients[key] = r;
                }
                // Klubbnamnet fylls i när det blir känt — en lagmedalj bär lagets klubb även när
                // den individuella raden saknade den.
                if (string.IsNullOrWhiteSpace(r.Club) && !string.IsNullOrWhiteSpace(clubName)) r.Club = clubName;
                return r;
            }

            var order = new Dictionary<(string Group, string Medal), MedalHandoutOrderLine>();
            void CountMedal(string group, string medal, bool unresolved)
            {
                if (string.IsNullOrWhiteSpace(medal)) return;
                if (!order.TryGetValue((group, medal), out var line))
                {
                    line = new MedalHandoutOrderLine
                    {
                        Group = group,
                        Medal = medal,
                        Sort = (group == "Lagmedaljer" ? 100 : 0) + MedalSort(medal)
                    };
                    order[(group, medal)] = line;
                }
                line.Count++;
                if (unresolved) line.Note = "Minst en av dem saknar ännu mottagare — se nedan.";
            }

            foreach (var comp in competitions.OrderBy(c => c.Value<DateTime?>("competitionDate") ?? DateTime.MinValue))
            {
                // ⚠️ Value<DateTime?> ger DateTime.MinValue (inte null) för en osatt egenskap, så
                // ett tomt slutdatum måste rensas — annars ser varje tävling ut att ha avslutats
                // år 1 och skulle räknas som avgjord.
                var endDate = comp.Value<DateTime?>("competitionEndDate");
                if (endDate == DateTime.MinValue) endDate = null;

                var row = new MedalHandoutCompetition
                {
                    CompetitionId = comp.Id,
                    Name = comp.Value<string>("competitionName") ?? comp.Name ?? "Tävling",
                    Date = comp.Value<DateTime?>("competitionDate"),
                    EndDate = endDate,
                    // Råvärdet av samma skäl som omfattningen: grenen är en dropdown på en del
                    // tävlingar och en ren sträng på andra, och konverteraren kastar på den senare.
                    CompetitionType = comp.GetProperty("competitionType")?.GetSourceValue()?.ToString() ?? ""
                };

                // ⚠️ Mäts på SISTA tävlingsdagen, och "i dag" räknas som kommande: en tävling som
                // avgörs i kväll har rimligen inga resultat på förmiddagen. Saknas datum helt kan
                // vi inte påstå att den är kommande — då gäller den vanliga bedömningen nedan.
                var lastDay = (row.EndDate ?? row.Date)?.Date;
                row.IsUpcoming = lastDay.HasValue && lastDay.Value >= DateTime.Today;

                list.Competitions.Add(row);

                PrizeGivingModel? prize;
                try
                {
                    prize = await _prizeGiving.BuildAsync(comp.Id, null, canEdit: false);
                }
                catch (Exception ex)
                {
                    // En trasig tävling får inte ta ner hela sammanställningen, men den får
                    // heller inte försvinna: raden står kvar med skälet utskrivet.
                    _logger.LogError(ex, "Kunde inte läsa medaljerna för tävling {CompetitionId}", comp.Id);
                    row.Problem = "Medaljerna kunde inte läsas för den här tävlingen.";
                    list.Warnings.Add($"{row.Name}: medaljerna kunde inte läsas.");
                    continue;
                }

                if (prize == null)
                {
                    row.Problem = "Tävlingen kunde inte läsas.";
                    continue;
                }

                row.ResultsUpdatedAt = prize.ResultsUpdatedAt;
                row.MedalGroupingText = prize.MedalGroupingText;
                row.MedalGroupingStale = prize.MedalGroupingStale;

                if (!string.IsNullOrWhiteSpace(prize.MedalGroupingStale))
                    list.Warnings.Add($"{row.Name}: {prize.MedalGroupingStale}");

                if (!prize.HasResultList)
                {
                    // ⚠️ En kommande tävling SAKNAR resultat därför att den inte ägt rum, inte
                    // därför att någon glömt något. Den står kvar i underlaget (arrangören ska se
                    // att den är inräknad) men utan varning och utan uppmaning att åtgärda.
                    row.Problem = row.IsUpcoming
                        ? "Tävlingen har inte avgjorts än."
                        : "Tävlingen har ingen resultatlista, så inga medaljer är uträknade.";
                    if (!row.IsUpcoming)
                        list.Warnings.Add($"{row.Name}: ingen resultatlista — medaljerna saknas i sammanställningen.");
                    continue;
                }

                if (!prize.MedalsComputed && prize.Individual.Count == 0)
                {
                    row.Problem = row.IsUpcoming
                        ? "Tävlingen har inte avgjorts än."
                        : "Resultatlistan räknades ut innan medaljfunktionen fanns. "
                          + "Klicka Uppdatera på tävlingens flik Resultat.";
                    if (!row.IsUpcoming)
                        list.Warnings.Add($"{row.Name}: medaljerna är inte uträknade — klicka Uppdatera på tävlingens flik Resultat.");
                    continue;
                }

                // ── Individuella medaljer ────────────────────────────────────────
                foreach (var cat in prize.Individual)
                {
                    foreach (var a in cat.Awards)
                    {
                        var detail = $"{a.ShootingClass} · {a.TotalScore} {prize.ScoreUnit}";
                        if (a.XCount > 0) detail += $" ({a.XCount} {prize.SecondaryUnit})";
                        if (!string.IsNullOrWhiteSpace(a.DecidedBy)) detail += $" · {a.DecidedBy}";

                        Recipient(a.MemberId, a.Name, a.Club).Items.Add(new MedalHandoutItem
                        {
                            Medal = a.Medal,
                            Category = cat.CategoryName,
                            CompetitionId = comp.Id,
                            CompetitionName = row.Name,
                            CompetitionDate = row.Date,
                            Detail = detail
                        });
                        CountMedal("Individuella medaljer", a.Medal, unresolved: false);
                        row.MedalCount++;
                    }

                    foreach (var u in cat.Unresolved)
                    {
                        list.Unresolved.Add(new MedalHandoutUnresolved
                        {
                            CompetitionId = comp.Id,
                            CompetitionName = row.Name,
                            Category = cat.CategoryName,
                            Text = u
                        });
                        CountMedal("Individuella medaljer", MedalOf(u), unresolved: true);
                        row.MedalCount++;
                    }
                }

                // ── Lagmedaljer ──────────────────────────────────────────────────
                //
                // ⚠️ EN LAGMEDALJ ÄR FLERA MEDALJER. Den delas ut till varje skytt i laget, så
                // antalet att beställa är antalet lagmedlemmar — inte antalet lag. Ett lagguld i
                // en fyramannaklass är fyra graveringar.
                foreach (var tg in prize.Teams)
                {
                    foreach (var a in tg.Awards)
                    {
                        var detail = $"{a.TeamName} · {a.TotalScore} {prize.TeamScoreUnit}";
                        if (a.XCount > 0) detail += $" ({a.XCount} {prize.TeamSecondaryUnit})";

                        foreach (var m in a.Members)
                        {
                            Recipient(m.MemberId, m.Name, a.ClubName).Items.Add(new MedalHandoutItem
                            {
                                Medal = a.Medal,
                                Category = $"Lag {tg.TeamClass}",
                                CompetitionId = comp.Id,
                                CompetitionName = row.Name,
                                CompetitionDate = row.Date,
                                IsTeam = true,
                                TeamName = a.TeamName,
                                Detail = detail
                            });
                            CountMedal("Lagmedaljer", a.Medal, unresolved: false);
                            row.MedalCount++;
                        }

                        if (a.Members.Count == 0)
                        {
                            // Ett lag utan kända medlemmar går inte att gravera. Det ska sägas,
                            // för medaljen ska ändå beställas.
                            list.Warnings.Add(
                                $"{row.Name}: laget {a.TeamName} har {a.Medal.ToLowerInvariant()} men inga " +
                                "lagmedlemmar registrerade — antalet medaljer går inte att räkna ut.");
                        }
                    }

                    foreach (var u in tg.Unresolved)
                    {
                        list.Unresolved.Add(new MedalHandoutUnresolved
                        {
                            CompetitionId = comp.Id,
                            CompetitionName = row.Name,
                            Category = $"Lag {tg.TeamClass}",
                            Text = u,
                            IsTeam = true
                        });
                        // Antalet lagmedlemmar är okänt tills striden är avgjord, så en oavgjord
                        // lagmedalj räknas som EN och noteras. Att gissa lagstorleken vore att
                        // beställa fel antal.
                        CountMedal("Lagmedaljer", MedalOf(u), unresolved: true);
                        row.MedalCount++;
                    }
                }

                row.HasMedals = row.MedalCount > 0;
                if (!row.HasMedals && row.Problem == null)
                {
                    row.Problem = !prize.IsChampionship
                        ? "Tävlingen är inte markerad som mästerskap, så inga placeringsmedaljer delas ut."
                        : row.IsUpcoming
                            ? "Tävlingen har inte avgjorts än."
                            : "Tävlingen har inga uträknade medaljer.";
                }
            }

            list.Order = order.Values.OrderBy(l => l.Sort).ToList();
            list.TotalMedals = list.Order.Sum(l => l.Count);

            var sv = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);
            list.Handout = recipients.Values
                .OrderBy(r => r.Name, sv)
                .ToList();
            foreach (var r in list.Handout)
                r.Items = r.Items
                    .OrderBy(i => i.CompetitionDate ?? DateTime.MinValue)
                    .ThenBy(i => MedalSort(i.Medal))
                    .ToList();
            list.RecipientCount = list.Handout.Count;

            if (list.Unresolved.Count > 0)
                list.Warnings.Add($"{list.Unresolved.Count} medaljplats(er) saknar ännu mottagare — "
                    + "särskjutningen eller lagordningen måste avgöras innan gravyren beställs.");

            return list;
        }

        /// <summary>
        /// Valören ur en oavgjord medaljplats. Raderna skrivs alltid som "Guld — ..." av de tre
        /// ställen som producerar dem (precisionens medaljräkning, fältskyttets artefakt och
        /// lagmedaljerna här intill).
        ///
        /// ⚠️ Faller tillbaka på tom sträng i stället för att gissa. Raden hamnar då inte i
        /// beställningen, men den står kvar bland de oavgjorda med hela sin text — en felräknad
        /// beställning är värre än en som saknar en rad man kan se.
        /// </summary>
        public static string MedalOf(string? unresolvedLine)
        {
            if (string.IsNullOrWhiteSpace(unresolvedLine)) return "";
            var head = unresolvedLine.Split('—')[0].Trim();
            return head is "Guld" or "Silver" or "Brons" ? head : "";
        }

        public static int MedalSort(string? medal) => medal switch
        {
            "Guld" => 1,
            "Silver" => 2,
            "Brons" => 3,
            _ => 9
        };

        // ── Tävlingsurvalet ──────────────────────────────────────────────────────

        private List<IPublishedContent> SelectCompetitions(
            CompetitionScan scan, string scopeType, string scopeId, int year, string wantedScope)
        {
            var result = new List<IPublishedContent>();

            foreach (var comp in scan.Competitions)
            {
                if ((comp.Value<DateTime?>("competitionDate")?.Year ?? 0) != year) continue;

                // ⚠️ MÅSTE gå genom ReadScope. Även den OTYPADE Value() kör FlexibleDropdownens
                // värdekonverterare, som JSON-deserialiserar värdet och kastar på en tävling vars
                // omfattning lagrats som ren sträng — mätt i dev: hela listan föll med
                // "'K' is an invalid start of a value". ReadScope läser råvärdet i stället.
                var scope = CompetitionScopeHelper.ReadScope(comp);
                if (!string.Equals(scope, wantedScope, StringComparison.Ordinal)) continue;

                if (scopeType == ScopeRegion)
                {
                    // ⚠️ Kretsens lista följer KRETSEN, inte arrangörsklubben. Ett kretsmästerskap
                    // som en klubb arrangerar på kretsens uppdrag är fortfarande kretsens medaljer
                    // att dela ut på kretsens årsmöte (Stefan 2026-09-20). Därför räknas både den
                    // klubbvärdda och den kretsvärdda formen in — se att en tävling kan sakna
                    // clubId helt och bära kretskoden själv.
                    if (!string.Equals(ResolveRegion(scan, comp), NormalizeRegionCode(scopeId),
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else
                {
                    if (comp.Value<int>("clubId").ToString() != scopeId) continue;
                }

                result.Add(comp);
            }

            return result;
        }

        private static string ResolveRegion(CompetitionScan scan, IPublishedContent competition)
        {
            var clubId = competition.Value<int>("clubId");
            if (clubId > 0 && scan.ClubRegions.TryGetValue(clubId, out var clubRegion) && clubRegion.Length > 0)
                return clubRegion;
            return NormalizeRegionCode(competition.Value("regionalFederation")?.ToString());
        }

        /// <summary>En dropdown-backad egenskap kan ligga som JSON-array (["Halland"]).</summary>
        private static string NormalizeRegionCode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var s = value.Trim();
            if (s.StartsWith("[") && s.EndsWith("]"))
                s = s.Trim('[', ']').Trim().Trim('"');
            return s.Trim().ToLowerInvariant();
        }

        private static string ResolveRegionName(CompetitionScan scan, string regionCode)
        {
            var wanted = NormalizeRegionCode(regionCode);
            var node = scan.Regions.FirstOrDefault(r =>
                NormalizeRegionCode(r.Value<string>("regionCode")) == wanted);
            return node?.Value<string>("regionName") ?? node?.Name ?? regionCode;
        }

        private sealed class CompetitionScan
        {
            public List<IPublishedContent> Competitions { get; } = new();
            public List<IPublishedContent> Clubs { get; } = new();
            public List<IPublishedContent> Regions { get; } = new();
            public Dictionary<int, string> ClubRegions { get; } = new();
        }

        /// <summary>
        /// Ett svep genom den publicerade cachen efter tävlingar, klubbar och kretssidor. Cachat
        /// kort — sammanställningen laddas om när året byts eller listan skrivs ut, och det är
        /// samma svep varje gång.
        /// </summary>
        private CompetitionScan GetScan()
        {
            if (_appCaches.RuntimeCache.Get(ScanCacheKey) is CompetitionScan cached) return cached;

            var scan = new CompetitionScan();
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                foreach (var root in ctx.Content.GetAtRoot())
                    foreach (var node in root.DescendantsOrSelf<IPublishedContent>())
                    {
                        switch (node.ContentType.Alias)
                        {
                            case "competition": scan.Competitions.Add(node); break;
                            case "club": scan.Clubs.Add(node); break;
                            case "regionalPage": scan.Regions.Add(node); break;
                        }
                    }
            }

            foreach (var club in scan.Clubs)
                scan.ClubRegions[club.Id] = NormalizeRegionCode(club.Value("regionalFederation")?.ToString());

            _appCaches.RuntimeCache.Insert(ScanCacheKey, () => scan, ScanCacheDuration);
            return scan;
        }
    }
}
