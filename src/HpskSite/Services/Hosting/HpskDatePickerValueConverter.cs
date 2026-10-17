using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors.ValueConverters;
using Umbraco.Extensions;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 16 lämnade ut datumegenskaperna (<c>Umbraco.DateTime</c>) ur den publicerade cachen med
    /// <see cref="DateTimeKind.Utc"/>; Umbraco 17 lämnar ut samma värde med <see cref="DateTimeKind.Unspecified"/>
    /// (mätt i fas E 2026-10-17). Märkningen avgör om JSON får ett <c>Z</c>, och därmed hur webbläsaren visar
    /// tiden. Den här omvandlaren ger värdet 16:s märkning tillbaka och rör inte själva värdet — samma
    /// märkning som <c>IContent.GetValue</c> och våra tabeller redan har i båda versionerna.
    /// </summary>
    public class HpskDatePickerValueConverter : DatePickerValueConverter
    {
        public override object? ConvertSourceToIntermediate(IPublishedElement owner, IPublishedPropertyType propertyType, object? source, bool preview)
        {
            var value = base.ConvertSourceToIntermediate(owner, propertyType, source, preview);
            return value is DateTime d && d.Kind != DateTimeKind.Utc ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : value;
        }
    }

    public sealed class HpskDatePickerValueConverterComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
            => builder.PropertyValueConverters().Replace<DatePickerValueConverter, HpskDatePickerValueConverter>();
    }
}
