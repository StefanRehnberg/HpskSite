using HpskSite.Models.Events;
using HpskSite.Services.SiteSettings;

namespace HpskSite.Services.Events
{
    /// <summary>Växlarna för händelselagringen (sajtstrukturen fas 2c–2d).</summary>
    public static class ClubEventStoreSettings
    {
        /// <summary><c>node</c> (standard) eller <c>table</c>.</summary>
        public const string StoreKey = "Events:Store";
        public const string StoreNode = "node";
        public const string StoreTable = "table";

        /// <summary><c>1</c> = alla skrivningar av händelser vägras (underhållsfönstret).</summary>
        public const string WriteFreezeKey = "Events:WriteFreeze";

        public const string WriteFreezeMessage = "Händelserna går inte att ändra just nu — underhåll pågår i några minuter. Försök igen om en stund.";

        public static bool UsesTable(SiteSettingService settings)
            => string.Equals(settings.Get(StoreKey, StoreNode), StoreTable, StringComparison.OrdinalIgnoreCase);
    }

    public class ClubEventWriteFrozenException : InvalidOperationException
    {
        public ClubEventWriteFrozenException() : base(ClubEventStoreSettings.WriteFreezeMessage) { }
    }

    /// <summary>
    /// <b>Den registrerade <see cref="IClubEventStore"/>.</b> Väljer lagring per anrop ur
    /// <see cref="ClubEventStoreSettings.StoreKey"/> och vägrar skrivningar under frysning. Exakt en
    /// lagring gäller åt gången — ingen dubbelskrivning (samma grepp som anmälningarna).
    /// </summary>
    public class SwitchingClubEventStore : IClubEventStore
    {
        private readonly NodeClubEventStore _node;
        private readonly SqlClubEventStore _sql;
        private readonly SiteSettingService _settings;

        public SwitchingClubEventStore(NodeClubEventStore node, SqlClubEventStore sql, SiteSettingService settings)
        {
            _node = node;
            _sql = sql;
            _settings = settings;
        }

        private IClubEventStore Current => ClubEventStoreSettings.UsesTable(_settings) ? _sql : _node;
        private bool Frozen => _settings.Get(ClubEventStoreSettings.WriteFreezeKey, "0") == "1";

        public ClubEventFieldSupport Support => Current.Support;
        public ClubEventRecord? Get(int id) => Current.Get(id);
        public IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, bool publishedOnly = true) => Current.ListForOwner(ownerId, publishedOnly);
        public IReadOnlyList<ClubEventRecord> ListAllPublished() => Current.ListAllPublished();
        public IReadOnlyList<ClubEventRecord> ListAllForOwnerType(string ownerType) => Current.ListAllForOwnerType(ownerType);

        public Task<(int Id, string? Error)> SaveAsync(ClubEventRecord record)
            => Frozen ? Task.FromResult((0, (string?)ClubEventStoreSettings.WriteFreezeMessage)) : Current.SaveAsync(record);

        public bool Delete(int id)
        {
            if (Frozen) throw new ClubEventWriteFrozenException();
            return Current.Delete(id);
        }

        public bool SetPublished(int id, bool published)
        {
            if (Frozen) throw new ClubEventWriteFrozenException();
            return Current.SetPublished(id, published);
        }
    }
}
