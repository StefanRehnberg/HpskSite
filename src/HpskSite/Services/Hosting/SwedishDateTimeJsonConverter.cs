using System.Text.Json;
using System.Text.Json.Serialization;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Tider som webbläsaren skickar in med en tidszon — <c>new Date().toISOString()</c> ger
    /// <c>"2026-03-31T22:00:00.000Z"</c> för svensk midnatt 1 april — räknas om till svensk tid innan de når
    /// koden. Utan det lagrades UTC-värdet rakt av: seriernas datum hamnade på kvällen före och fältskyttets
    /// patrulltider två timmar för tidigt (mätt i fas E, 2026-10-17).
    ///
    /// En tid utan tidszon (<c>"2026-04-01"</c>, <c>"2026-04-01T18:30"</c>) är redan svensk tid och lämnas som
    /// den är. Utskriften är oförändrad (standardformatet, med Kind). Gäller våra controllers; Umbracos
    /// backoffice har egna JSON-inställningar.
    /// </summary>
    public sealed class SwedishDateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.Kind switch
            {
                DateTimeKind.Utc => DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Unspecified),
                DateTimeKind.Local => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
                _ => value,
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    public sealed class SwedishDateTimeJsonComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
            => builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
                o.JsonSerializerOptions.Converters.Add(new SwedishDateTimeJsonConverter()));
    }
}
