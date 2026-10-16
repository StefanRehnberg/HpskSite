using HpskSite.Models.Competitions;
using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.Competitions
{
    /// <summary>Växlarna för tävlingarnas och seriernas lagring (C3-planen S1). Gäller båda.</summary>
    public static class CompetitionStoreSettings
    {
        /// <summary><c>node</c> (standard) eller <c>table</c>. ⚠️ I S1 bara <c>table</c> på en databaskopia.</summary>
        public const string StoreKey = "Competitions:Store";
        public const string StoreNode = "node";
        public const string StoreTable = "table";

        /// <summary><c>1</c> = alla skrivningar till tävlingar och serier vägras (underhållsfönstret).</summary>
        public const string WriteFreezeKey = "Competitions:WriteFreeze";

        public const string WriteFreezeMessage = "Tävlingarna uppdateras för underhåll en stund. Försök igen om en stund.";

        public static bool UsesTable(SiteSettingService settings) =>
            string.Equals(settings.Get(StoreKey, StoreNode), StoreTable, StringComparison.OrdinalIgnoreCase);

        public static bool IsWriteFrozen(SiteSettingService settings) => settings.Get(WriteFreezeKey, "0") == "1";
    }

    /// <summary>Kastas när skrivningar till tävlingarna är frysta. Visas av <c>WriteFrozenExceptionFilter</c>.</summary>
    public class CompetitionWriteFrozenException : InvalidOperationException
    {
        public CompetitionWriteFrozenException() : base(CompetitionStoreSettings.WriteFreezeMessage) { }
    }

    /// <summary>
    /// <b>Den registrerade <see cref="ICompetitionStore"/>.</b> Väljer lagring per anrop och vägrar
    /// skrivningar under frysning. Ingen dubbelskrivning: exakt en lagring gäller åt gången.
    /// </summary>
    public class SwitchingCompetitionStore : ICompetitionStore
    {
        private readonly NodeCompetitionStore _node;
        private readonly SqlCompetitionStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingCompetitionStore(NodeCompetitionStore node, SqlCompetitionStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        public bool UsesTable => CompetitionStoreSettings.UsesTable(_settings);
        private ICompetitionStore Current => UsesTable ? _sql : _node;

        public CompetitionInfo? Get(int id) => Current.Get(id);
        public CompetitionInfo? GetSaved(int id) => Current.GetSaved(id);
        public IReadOnlyDictionary<int, CompetitionInfo> GetMany(IReadOnlyCollection<int> ids) => Current.GetMany(ids);
        public IReadOnlyList<CompetitionInfo> List() => Current.List();
        public IReadOnlyList<CompetitionInfo> ListBySeries(int seriesId) => Current.ListBySeries(seriesId);

        public CompetitionUpdateResult<CompetitionInfo> Update(int id, long? expectedVersion, Action<CompetitionInfo> change)
        {
            if (CompetitionStoreSettings.IsWriteFrozen(_settings)) throw new CompetitionWriteFrozenException();
            return Current.Update(id, expectedVersion, change);
        }

        public CompetitionUpdateResult<CompetitionInfo> Create(int parentId, string name, Action<CompetitionInfo> init)
        {
            if (CompetitionStoreSettings.IsWriteFrozen(_settings)) throw new CompetitionWriteFrozenException();
            return Current.Create(parentId, name, init);
        }
    }

    /// <summary>Den registrerade <see cref="ICompetitionSeriesStore"/>. Samma växel som tävlingarna.</summary>
    public class SwitchingCompetitionSeriesStore : ICompetitionSeriesStore
    {
        private readonly NodeCompetitionSeriesStore _node;
        private readonly SqlCompetitionSeriesStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingCompetitionSeriesStore(NodeCompetitionSeriesStore node, SqlCompetitionSeriesStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        private ICompetitionSeriesStore Current => CompetitionStoreSettings.UsesTable(_settings) ? _sql : _node;

        public CompetitionSeriesInfo? Get(int id) => Current.Get(id);
        public CompetitionSeriesInfo? GetSaved(int id) => Current.GetSaved(id);
        public IReadOnlyDictionary<int, CompetitionSeriesInfo> GetMany(IReadOnlyCollection<int> ids) => Current.GetMany(ids);
        public IReadOnlyList<CompetitionSeriesInfo> List() => Current.List();

        public CompetitionUpdateResult<CompetitionSeriesInfo> Update(int id, long? expectedVersion, Action<CompetitionSeriesInfo> change)
        {
            if (CompetitionStoreSettings.IsWriteFrozen(_settings)) throw new CompetitionWriteFrozenException();
            return Current.Update(id, expectedVersion, change);
        }
    }
}
