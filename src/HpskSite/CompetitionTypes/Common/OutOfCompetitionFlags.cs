using HpskSite.Models;
using HpskSite.Services.Registrations;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Mängden (skytt, klass) som skjuter UTANFÖR MÄSTERSKAPET i en tävling. Nyckeln är skyttens id och klassens Id-form
    /// (<see cref="ShootingClasses.NormalizeKey"/>), så resultatlistans visningsnamn ("C Vet Y") och anmälans klassnyckel ("C_Vet_Y")
    /// träffar samma post.
    /// </summary>
    public sealed class OutOfCompetitionSet
    {
        public static readonly OutOfCompetitionSet None = new(new HashSet<(int, string)>());

        private readonly HashSet<(int MemberId, string ClassKey)> _set;

        internal OutOfCompetitionSet(HashSet<(int, string)> set) => _set = set;

        public int Count => _set.Count;

        /// <summary>En ny mängd med skytten i klassen tillagd. Används av Rätta-flödets förhandsvisning ("hur ser listan ut om…") — rör aldrig lagringen.</summary>
        public OutOfCompetitionSet With(int memberId, string? shootingClassIdOrName)
        {
            var copy = new HashSet<(int, string)>(_set) { (memberId, ShootingClasses.NormalizeKey(shootingClassIdOrName)) };
            return new OutOfCompetitionSet(copy);
        }

        /// <summary>En ny mängd med skytten i klassen borttagen (flyttas TILLBAKA IN). Förhandsvisningens motsvarighet till <see cref="With"/>.</summary>
        public OutOfCompetitionSet Without(int memberId, string? shootingClassIdOrName)
        {
            var copy = new HashSet<(int, string)>(_set);
            copy.Remove((memberId, ShootingClasses.NormalizeKey(shootingClassIdOrName)));
            return new OutOfCompetitionSet(copy);
        }

        /// <summary>Är skytten utanför mästerskapet i NÅGON klass? Lagbildningen frågar så — ett lag är klubbens, inte klassens.</summary>
        public bool HasMember(int memberId) => _set.Any(k => k.MemberId == memberId);

        /// <summary>Är skytten utanför mästerskapet i just den här klassen (Id eller visningsnamn)?</summary>
        public bool Contains(int memberId, string? shootingClassIdOrName) =>
            _set.Count > 0 && _set.Contains((memberId, ShootingClasses.NormalizeKey(shootingClassIdOrName)));
    }

    /// <summary>
    /// Läget för en klassrad: arrangörens beslut (om något) och förslaget (om något). Det GÄLLANDE värdet är beslutet, annars förslaget.
    /// </summary>
    public sealed record OutOfCompetitionState(
        int RegistrationId, int MemberId, string ClassKey, bool? Decision, OutOfCompetitionSuggestion? Suggestion, string? ClassRaw = null)
    {
        // ClassKey är den normaliserade jämförelsenyckeln; ClassRaw är klassen som anmälan lagrar den (det storen tar emot).
        public bool Effective => Decision ?? Suggestion != null;
    }

    /// <summary>
    /// EN läsare av vilka som skjuter utanför mästerskapet, för varje yta som behöver veta det (byggaren, särskjutningen, lagbildningen,
    /// anmälningslistan). Tom när beteendet inte gäller tävlingen (<see cref="ChampionshipRule.OutOfCompetitionApplies"/>) — då läses
    /// ingenting och allt beter sig som förut.
    ///
    /// <para><b>Gällande värde = arrangörens beslut, annars förslaget.</b> Förslaget (annan krets/landsdel/klubb) görs bara när tävlingen
    /// satt "vilka deltar" (<c>ChampionshipParticipants</c>) och lagras aldrig; arrangören kan alltid skriva över det med ett beslut åt
    /// båda håll (kryssrutan i Redigera anmälan).</para>
    /// </summary>
    public class OutOfCompetitionFlags
    {
        private readonly ICompetitionRegistrationStore _registrations;
        private readonly IClubRegionLookup _regions;
        private readonly IMemberClubsLookup _memberClubs;

        public OutOfCompetitionFlags(ICompetitionRegistrationStore registrations, IClubRegionLookup regions, IMemberClubsLookup memberClubs)
        {
            _registrations = registrations;
            _regions = regions;
            _memberClubs = memberClubs;
        }

        /// <summary>Läget per (anmälan, klass) för de klasser mästerskapet omfattar. Tom när beteendet inte gäller tävlingen.</summary>
        public IReadOnlyList<OutOfCompetitionState> States(HpskSite.Models.Competitions.CompetitionInfo competition, ChampionshipRule rule)
        {
            if (!rule.OutOfCompetitionApplies) return Array.Empty<OutOfCompetitionState>();

            var regs = _registrations.List(competition.Id).Where(r => r.IsActive).ToList();
            var wantsSuggestions = ChampionshipParticipantsSetting.Normalize(competition.ChampionshipParticipants) != null;

            CompetitionOrigin? origin = null;
            IReadOnlyDictionary<int, IReadOnlyList<int>>? clubsByMember = null;
            if (wantsSuggestions)
            {
                origin = _regions.OriginOf(competition);
                clubsByMember = _memberClubs.ClubsOf(regs.Select(r => r.MemberId));
            }

            var list = new List<OutOfCompetitionState>();
            foreach (var reg in regs)
            {
                OutOfCompetitionSuggestion? suggestion = null;
                var suggestionDone = false;

                foreach (var cls in reg.Classes)
                {
                    // Bara en klass som mästerskapet omfattar kan vara "utanför" — i övriga finns ingen mästerskapsplacering att stå utanför.
                    if (!rule.AppliesToClass(cls.Class)) continue;

                    if (cls.OutOfCompetition == null && wantsSuggestions && !suggestionDone)
                    {
                        // Förslaget gäller skytten (var hen tävlar för), inte klassen — räknas en gång per anmälan.
                        var all = clubsByMember!.TryGetValue(reg.MemberId, out var c) ? c : Array.Empty<int>();
                        suggestion = OutOfCompetitionRules.Suggest(
                            rule.Kind, competition.ChampionshipParticipants, origin!, competition.IsClubOnly,
                            ResolveCompetesFor(reg), all, _regions.OriginOf);
                        suggestionDone = true;
                    }

                    list.Add(new OutOfCompetitionState(reg.Id, reg.MemberId, ShootingClasses.NormalizeKey(cls.Class),
                        cls.OutOfCompetition, cls.OutOfCompetition == null ? suggestion : null, cls.Class));
                }
            }
            return list;
        }

        /// <summary>
        /// STÄMPLING vid publicering: varje förslag (klassrad utan beslut vars gällande värde är "utanför") blir ett beslut, så att
        /// listan inte ändras av att förutsättningarna (skyttens klubbar, tävlingens "vilka deltar") ändras efter publiceringen.
        /// Skriver aldrig över ett beslut arrangören satt — storen skriver bara när inget beslut finns. Idempotent. Returnerar antalet
        /// stämplade klassrader.
        /// </summary>
        public int StampSuggestions(HpskSite.Models.Competitions.CompetitionInfo competition, ChampionshipRule rule)
        {
            var stamped = 0;
            foreach (var s in States(competition, rule))
            {
                if (s.Decision != null || s.Suggestion == null) continue;
                if (_registrations.StampOutOfCompetition(competition.Id, s.RegistrationId, s.ClassRaw ?? s.ClassKey, "suggestion-stamped")) stamped++;
            }
            return stamped;
        }

        /// <summary>
        /// FRYSNING (Stefan 2026-11-06): när en officiell lista publiceras eller räknas om får VARJE klassrad utan beslut ett — "utanför" där det
        /// finns ett förslag, annars "tävlar". Efter det är listans mängd alltid beslut, och ingenting som ändras senare (skyttens klubbar,
        /// tävlingens "vilka deltar", en ny anmälan, ett klassbyte) kan tyst göra en gäst till mästare eller tvärtom. Skriver aldrig över ett
        /// beslut. Idempotent. Returnerar antalet nya "utanför"- och "tävlar"-beslut.
        /// </summary>
        public (int Outside, int Inside) FreezeDecisions(HpskSite.Models.Competitions.CompetitionInfo competition, ChampionshipRule rule)
        {
            int outside = 0, inside = 0;
            foreach (var s in States(competition, rule))
            {
                if (s.Decision != null) continue;
                var cls = s.ClassRaw ?? s.ClassKey;
                if (s.Suggestion != null)
                {
                    if (_registrations.StampOutOfCompetition(competition.Id, s.RegistrationId, cls, "suggestion-stamped")) outside++;
                }
                else if (_registrations.StampInCompetition(competition.Id, s.RegistrationId, cls, "publish-frozen")) inside++;
            }
            return (outside, inside);
        }

        /// <summary>
        /// Gällande mängd = arrangörens beslut, annars förslaget. ✔ Förslaget följer av arrangörens egen inställning "vilka deltar" och visas
        /// därför redan i den preliminära listan och på live-tavlan (Stefan 2026-11-06: en gäst ska inte ses som ledare under tävlingen);
        /// arrangören rättar ett felaktigt förslag direkt i Redigera anmälan. Vid publicering fryses förslagen till beslut
        /// (<see cref="FreezeDecisions"/>).
        /// </summary>
        public OutOfCompetitionSet Load(HpskSite.Models.Competitions.CompetitionInfo competition, ChampionshipRule rule)
        {
            var set = new HashSet<(int, string)>();
            foreach (var s in States(competition, rule))
                if (s.Effective) set.Add((s.MemberId, s.ClassKey));
            return set.Count == 0 ? OutOfCompetitionSet.None : new OutOfCompetitionSet(set);
        }

        /// <summary>
        /// Klubben skytten tävlar för i den här tävlingen: anmälans klubb, annars den äldre klubbtexten när den är ett id. 0 = okänd
        /// (ger inget förslag).
        /// </summary>
        private static int ResolveCompetesFor(HpskSite.Models.Registrations.RegistrationRecord reg)
        {
            if (reg.ClubId > 0) return reg.ClubId;
            return int.TryParse((reg.LegacyClubText ?? "").Trim(), out var legacy) && legacy > 0 ? legacy : 0;
        }
    }
}
