using HpskSite.Models.Results;
using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.Results
{
    /// <summary>Växlarna för resultatlistornas lagring (skalplanen fas C1).</summary>
    public static class CompetitionResultStoreSettings
    {
        /// <summary><c>node</c> (standard) eller <c>table</c>.</summary>
        public const string StoreKey = "Results:Store";
        public const string StoreNode = "node";
        public const string StoreTable = "table";

        /// <summary><c>1</c> = alla skrivningar till resultatlistorna vägras (underhållsfönstret).</summary>
        public const string WriteFreezeKey = "Results:WriteFreeze";

        public const string WriteFreezeMessage = "Resultatlistorna uppdateras för underhåll några minuter. Försök igen om en stund.";

        public static bool UsesTable(SiteSettingService settings) =>
            string.Equals(settings.Get(StoreKey, StoreNode), StoreTable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Kastas när skrivningar till resultatlistorna är frysta under underhållsfönstret.</summary>
    public class CompetitionResultWriteFrozenException : InvalidOperationException
    {
        public CompetitionResultWriteFrozenException() : base(CompetitionResultStoreSettings.WriteFreezeMessage) { }
    }

    /// <summary>
    /// <b>Den registrerade <see cref="ICompetitionResultStore"/>.</b> Väljer lagring per anrop ur
    /// <see cref="CompetitionResultStoreSettings.StoreKey"/> och vägrar skrivningar när
    /// <see cref="CompetitionResultStoreSettings.WriteFreezeKey"/> är satt.
    ///
    /// <para><b>⚠️ Ingen dubbelskrivning, med flit.</b> Exakt en lagring gäller åt gången, som för
    /// anmälningarna. Flytten sker under frysning; efter växeln skrivs bara tabellen och noderna ligger
    /// orörda kvar för återställning tills nya skrivningar gjorts i tabellen.</para>
    /// </summary>
    public class SwitchingCompetitionResultStore : ICompetitionResultStore
    {
        private readonly NodeCompetitionResultStore _node;
        private readonly SqlCompetitionResultStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingCompetitionResultStore(NodeCompetitionResultStore node, SqlCompetitionResultStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        public bool UsesTable => CompetitionResultStoreSettings.UsesTable(_settings);
        public bool IsWriteFrozen => _settings.Get(CompetitionResultStoreSettings.WriteFreezeKey, "0") == "1";

        private ICompetitionResultStore Current => UsesTable ? _sql : _node;

        private ICompetitionResultStore Writable
        {
            get
            {
                if (IsWriteFrozen) throw new CompetitionResultWriteFrozenException();
                return Current;
            }
        }

        public CompetitionResultRecord? Get(int competitionId) => Current.Get(competitionId);
        public IReadOnlyDictionary<int, CompetitionResultRecord> GetMany(IReadOnlyCollection<int> competitionIds)
            => Current.GetMany(competitionIds);
        public bool CanStore(CompetitionResultField field) => Current.CanStore(field);

        public CompetitionResultRecord? Update(int competitionId, Action<CompetitionResultRecord> change, bool createIfMissing = false)
            => Writable.Update(competitionId, change, createIfMissing);
        public CompetitionResultRecord Ensure(int competitionId) => Writable.Ensure(competitionId);
    }
}
