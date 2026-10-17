using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 17: ett datum som sparas i en datumegenskap med <see cref="DateTimeKind.Local"/> (t.ex.
    /// <c>SetValue("lastUpdated", DateTime.Now)</c>) lagras rätt i databasen, men den publicerade cachen
    /// räknar om det till UTC — sidorna läste då tiden två timmar fel (mätt i fas E 2026-10-17: lagrat
    /// 09:31, publicerat 07:31). Ett värde utan Kind lagras och cachas likadant.
    ///
    /// Hanteraren tar bort Kind från varje datumvärde innan det sparas, utan att ändra värdet, så att
    /// databasen och cachen får samma tid — Umbraco 16:s beteende. Den gäller alla skrivvägar på en gång,
    /// också de som går via en variabel.
    /// </summary>
    public sealed class PropertyDateKindNormalizer :
        INotificationHandler<ContentSavingNotification>,
        INotificationHandler<MemberSavingNotification>
    {
        public void Handle(ContentSavingNotification notification)
        {
            foreach (var entity in notification.SavedEntities) Normalize(entity);
        }

        public void Handle(MemberSavingNotification notification)
        {
            foreach (var entity in notification.SavedEntities) Normalize(entity);
        }

        internal static void Normalize(IContentBase entity)
        {
            foreach (var property in entity.Properties)
            foreach (var value in property.Values.ToList())
            {
                if (value.EditedValue is DateTime d && d.Kind != DateTimeKind.Unspecified)
                    property.SetValue(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), value.Culture, value.Segment);
            }
        }
    }

    public sealed class PropertyDateKindNormalizerComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationHandler<ContentSavingNotification, PropertyDateKindNormalizer>();
            builder.AddNotificationHandler<MemberSavingNotification, PropertyDateKindNormalizer>();
        }
    }
}
