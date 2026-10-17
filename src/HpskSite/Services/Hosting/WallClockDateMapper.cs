using System.Reflection;
using NPoco;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 17 märker varje <see cref="DateTime"/> som NPoco läser in i en klass med
    /// <see cref="DateTimeKind.Utc"/> (<c>UmbracoDefaultMapper</c>), eftersom Umbracos egna systemdatum nu
    /// lagras i UTC. VÄRDET lämnas orört — bara märkningen ändras. Våra egna tabeller och dokumenttypernas
    /// datumegenskaper lagras i lokal tid (de migrerades inte), så märkningen blev fel: JSON fick ett
    /// <c>Z</c> och webbläsaren visade tiden två timmar fel, och <c>ToLocalTime()</c>/<c>ToUniversalTime()</c>
    /// räknade fel (mätt i fas E 2026-10-17).
    ///
    /// Den här mapparen ger tillbaka Umbraco 16:s beteende — <see cref="DateTimeKind.Unspecified"/> — för
    /// datumfält i VÅRA klasser (namnrymden HpskSite) och för datumegenskapernas lagrade värde
    /// (<c>PropertyDataDto.DateValue</c>). Allt annat svarar den inte på, så Umbracos egna
    /// systemdatum förblir UTC. Den ligger först i samlingen (<see cref="WallClockDateMapperComposer"/>),
    /// eftersom NPoco tar den första mappare som svarar.
    ///
    /// Skrivning påverkas inte: NPoco lagrar värdet som det är oavsett Kind (mätt).
    /// </summary>
    public sealed class WallClockDateMapper : DefaultMapper
    {
        public override Func<object, object>? GetFromDbConverter(MemberInfo destMemberInfo, Type sourceType)
        {
            if (sourceType != typeof(DateTime)) return null;
            var memberType = destMemberInfo switch
            {
                PropertyInfo p => p.PropertyType,
                FieldInfo f => f.FieldType,
                _ => null,
            };
            if (memberType != typeof(DateTime) && memberType != typeof(DateTime?)) return null;
            if (!AppliesTo(destMemberInfo)) return null;
            return value => value is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified) : value;
        }

        private const string PropertyDataDtoName = "Umbraco.Cms.Infrastructure.Persistence.Dtos.PropertyDataDto";

        internal static bool AppliesTo(MemberInfo member)
        {
            var type = member.DeclaringType;
            if (type == null) return false;
            // PropertyDataDto är intern i Umbraco, därför jämförs namnet.
            if (type.FullName == PropertyDataDtoName && member.Name == "DateValue") return true;
            return type.Namespace != null && type.Namespace.StartsWith("HpskSite", StringComparison.Ordinal);
        }
    }

    /// <summary>Lägger <see cref="WallClockDateMapper"/> FÖRST bland NPoco-mapparna.</summary>
    public sealed class WallClockDateMapperComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            var mappers = builder.NPocoMappers();
            if (mappers == null) return;
            var existing = mappers.GetTypes().Where(t => t != typeof(WallClockDateMapper)).ToList();
            mappers.Clear();
            mappers.Add<WallClockDateMapper>();
            foreach (var t in existing) mappers.Add(t);
        }
    }
}
