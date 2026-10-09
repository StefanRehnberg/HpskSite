using HpskSite.Models.Registrations;
using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.Registrations
{
    /// <summary>Växlarna för anmälningslagringen (sajtstrukturen fas 1c–1d).</summary>
    public static class RegistrationStoreSettings
    {
        /// <summary><c>node</c> (standard) eller <c>table</c>.</summary>
        public const string StoreKey = "Registrations:Store";
        public const string StoreNode = "node";
        public const string StoreTable = "table";

        /// <summary><c>1</c> = alla anmälningsskrivningar vägras (underhållsfönstret i fas 1d).</summary>
        public const string WriteFreezeKey = "Registrations:WriteFreeze";

        public const string WriteFreezeMessage = "Anmälan är stängd för underhåll några minuter. Försök igen om en stund.";
    }

    /// <summary>Kastas när anmälningsskrivningar är frysta under underhållsfönstret.</summary>
    public class RegistrationWriteFrozenException : InvalidOperationException
    {
        public RegistrationWriteFrozenException() : base(RegistrationStoreSettings.WriteFreezeMessage) { }
    }

    /// <summary>
    /// <b>Den registrerade <see cref="ICompetitionRegistrationStore"/>.</b> Väljer lagring per anrop ur
    /// <see cref="RegistrationStoreSettings.StoreKey"/> och vägrar skrivningar när
    /// <see cref="RegistrationStoreSettings.WriteFreezeKey"/> är satt.
    ///
    /// <para><b>⚠️ Ingen dubbelskrivning, med flit.</b> Exakt en lagring gäller åt gången. Flytten i fas 1d
    /// sker under frysning, och efter växeln skrivs bara tabellen; noderna ligger orörda kvar för
    /// återställning tills nya skrivningar gjorts i tabellen.</para>
    ///
    /// <para>Valet görs per ANROP, inte per instans: lagringen är scoped, och en begäran som börjar
    /// precis när växeln slås om ska inte läsa ur den ena och skriva i den andra längre än nödvändigt.
    /// Frysningen täcker just det fönstret.</para>
    /// </summary>
    public class SwitchingRegistrationStore : ICompetitionRegistrationStore
    {
        private readonly NodeRegistrationStore _node;
        private readonly SqlRegistrationStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingRegistrationStore(NodeRegistrationStore node, SqlRegistrationStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        public bool UsesTable =>
            string.Equals(_settings.Get(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreNode),
                RegistrationStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase);

        public bool IsWriteFrozen => _settings.Get(RegistrationStoreSettings.WriteFreezeKey, "0") == "1";

        private ICompetitionRegistrationStore Current => UsesTable ? _sql : _node;

        private ICompetitionRegistrationStore Writable
        {
            get
            {
                if (IsWriteFrozen) throw new RegistrationWriteFrozenException();
                return Current;
            }
        }

        public IReadOnlyList<RegistrationRecord> List(int competitionId) => Current.List(competitionId);
        public RegistrationRecord? Get(int competitionId, int registrationId) => Current.Get(competitionId, registrationId);
        public RegistrationRecord? GetById(int registrationId) => Current.GetById(registrationId);
        public RegistrationRecord? FindForMember(int competitionId, int memberId) => Current.FindForMember(competitionId, memberId);
        public IReadOnlyList<RegistrationRecord> ListForMemberInCompetition(int competitionId, int memberId)
            => Current.ListForMemberInCompetition(competitionId, memberId);
        public IReadOnlyList<RegistrationRecord> ListForMember(int memberId) => Current.ListForMember(memberId);
        public IReadOnlyList<(int MemberId, int CompetitionId)> CompetitionsForMembers(IReadOnlyCollection<int> memberIds)
            => Current.CompetitionsForMembers(memberIds);
        public IReadOnlyDictionary<int, int> CountByCompetition(IReadOnlyCollection<int>? competitionIds = null)
            => Current.CountByCompetition(competitionIds);

        public int Save(RegistrationRecord registration) => Writable.Save(registration);
        public bool Delete(int competitionId, int registrationId) => Writable.Delete(competitionId, registrationId);
    }
}
