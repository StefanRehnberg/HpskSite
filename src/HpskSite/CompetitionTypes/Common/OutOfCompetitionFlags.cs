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

        /// <summary>Är skytten utanför mästerskapet i NÅGON klass? Lagbildningen frågar så — ett lag är klubbens, inte klassens.</summary>
        public bool HasMember(int memberId) => _set.Any(k => k.MemberId == memberId);

        /// <summary>Är skytten utanför mästerskapet i just den här klassen (Id eller visningsnamn)?</summary>
        public bool Contains(int memberId, string? shootingClassIdOrName) =>
            _set.Count > 0 && _set.Contains((memberId, ShootingClasses.NormalizeKey(shootingClassIdOrName)));
    }

    /// <summary>
    /// EN läsare av vilka som skjuter utanför mästerskapet, för varje yta som behöver veta det (byggaren, särskjutningen, lagbildningen).
    /// Tom när beteendet inte gäller tävlingen (<see cref="ChampionshipRule.OutOfCompetitionApplies"/>) — då läses ingenting och allt
    /// beter sig som förut.
    ///
    /// <para>Det gällande värdet är arrangörens beslut (<c>CompetitionRegistrationClassDecision</c>). Förslaget ("annan krets") läggs
    /// till här när det byggs (S5), så att alla ytor får det av samma läsare.</para>
    /// </summary>
    public class OutOfCompetitionFlags
    {
        private readonly ICompetitionRegistrationStore _registrations;

        public OutOfCompetitionFlags(ICompetitionRegistrationStore registrations) => _registrations = registrations;

        public OutOfCompetitionSet Load(HpskSite.Models.Competitions.CompetitionInfo competition, ChampionshipRule rule)
        {
            if (!rule.OutOfCompetitionApplies) return OutOfCompetitionSet.None;

            var set = new HashSet<(int, string)>();
            foreach (var registration in _registrations.List(competition.Id).Where(r => r.IsActive))
            {
                foreach (var cls in registration.Classes)
                {
                    // Bara en klass som mästerskapet gäller kan vara "utanför" — i övriga finns ingen mästerskapsplacering att stå utanför.
                    if (cls.OutOfCompetition == true && rule.AppliesToClass(cls.Class))
                        set.Add((registration.MemberId, ShootingClasses.NormalizeKey(cls.Class)));
                }
            }
            return set.Count == 0 ? OutOfCompetitionSet.None : new OutOfCompetitionSet(set);
        }
    }
}
