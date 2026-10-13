using HpskSite.Models.StartLists;
using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.StartLists
{
    /// <summary>Växlarna för startlistornas lagring (skalplanen fas C2).</summary>
    public static class StartListStoreSettings
    {
        /// <summary><c>node</c> (standard) eller <c>table</c>.</summary>
        public const string StoreKey = "StartLists:Store";
        public const string StoreNode = "node";
        public const string StoreTable = "table";

        /// <summary><c>1</c> = alla skrivningar till startlistorna vägras (underhållsfönstret).</summary>
        public const string WriteFreezeKey = "StartLists:WriteFreeze";

        public const string WriteFreezeMessage = "Startlistorna uppdateras för underhåll några minuter. Försök igen om en stund.";

        public static bool UsesTable(SiteSettingService settings) =>
            string.Equals(settings.Get(StoreKey, StoreNode), StoreTable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Kastas när skrivningar till startlistorna är frysta under underhållsfönstret.</summary>
    public class StartListWriteFrozenException : InvalidOperationException
    {
        public StartListWriteFrozenException() : base(StartListStoreSettings.WriteFreezeMessage) { }
    }

    /// <summary>
    /// <b>Den registrerade <see cref="IStartListStore"/>.</b> Väljer lagring per anrop ur
    /// <see cref="StartListStoreSettings.StoreKey"/> och vägrar skrivningar när
    /// <see cref="StartListStoreSettings.WriteFreezeKey"/> är satt.
    ///
    /// <para><b>⚠️ Ingen dubbelskrivning, med flit.</b> Exakt en lagring gäller åt gången, som för
    /// resultatlistorna och anmälningarna. Flytten sker under frysning; efter växeln skrivs bara tabellen och
    /// noderna ligger orörda kvar för återställning tills nya skrivningar gjorts i tabellen.</para>
    /// </summary>
    public class SwitchingStartListStore : IStartListStore
    {
        private readonly NodeStartListStore _node;
        private readonly SqlStartListStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingStartListStore(NodeStartListStore node, SqlStartListStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        public bool UsesTable => StartListStoreSettings.UsesTable(_settings);
        public bool IsWriteFrozen => _settings.Get(StartListStoreSettings.WriteFreezeKey, "0") == "1";

        private IStartListStore Current => UsesTable ? _sql : _node;

        private IStartListStore Writable
        {
            get
            {
                if (IsWriteFrozen) throw new StartListWriteFrozenException();
                return Current;
            }
        }

        public IReadOnlyList<StartListRecord> List(int competitionId, StartListKind? kind = null) => Current.List(competitionId, kind);
        public IReadOnlyList<StartListRecord> ListPublished(int competitionId, StartListKind? kind = null) => Current.ListPublished(competitionId, kind);
        public StartListRecord? Get(int startListId) => Current.Get(startListId);
        public StartListRecord? Primary(int competitionId) => Current.Primary(competitionId);

        public StartListRecord Create(int competitionId, StartListKind kind, string name, Action<StartListRecord> init,
            StartListPublish publish = StartListPublish.Always)
            => Writable.Create(competitionId, kind, name, init, publish);

        public StartListRecord? Update(int startListId, Action<StartListRecord> change, StartListPublish publish = StartListPublish.Always)
            => Writable.Update(startListId, change, publish);

        public bool Delete(int startListId) => Writable.Delete(startListId);
    }
}
