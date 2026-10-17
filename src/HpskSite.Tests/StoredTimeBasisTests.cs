using System.Reflection;
using FluentAssertions;
using HpskSite.Services.Hosting;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Fas E: varje lagrad tid ska visas i svensk tid. <see cref="StoredTimeBasis"/> säger vilka kolumner som
    /// är UTC; <see cref="StoredTimeMapper"/> märker datumen efter det.
    /// </summary>
    public class StoredTimeBasisTests
    {
        [TableName("CompetitionTeam")]
        private sealed class UtcTable { public DateTime CreatedAt { get; set; } public DateTime? Other { get; set; } }

        [TableName("CompetitionRegistration")]
        private sealed class LocalTable { public DateTime RegistrationDate { get; set; } }

        private sealed class NoTable { public DateTime CreatedAt { get; set; } }

        private static MemberInfo M<T>(string name) => typeof(T).GetProperty(name)!;

        [Fact]
        public void En_registrerad_kolumn_marks_utc()
            => StoredTimeMapper.KindFor(M<UtcTable>(nameof(UtcTable.CreatedAt))).Should().Be(DateTimeKind.Utc);

        [Fact]
        public void En_oregistrerad_kolumn_ar_svensk_tid()
        {
            StoredTimeMapper.KindFor(M<LocalTable>(nameof(LocalTable.RegistrationDate))).Should().Be(DateTimeKind.Unspecified);
            StoredTimeMapper.KindFor(M<UtcTable>(nameof(UtcTable.Other))).Should().Be(DateTimeKind.Unspecified);
        }

        [Fact]
        public void En_klass_utan_tabell_lamnas_at_umbraco()
            => StoredTimeMapper.KindFor(M<NoTable>(nameof(NoTable.CreatedAt))).Should().BeNull();

        [Fact]
        public void Omvandlaren_flyttar_inte_vardet()
        {
            var convert = new StoredTimeMapper().GetFromDbConverter(M<LocalTable>(nameof(LocalTable.RegistrationDate)), typeof(DateTime))!;
            var stored = new DateTime(2026, 10, 4, 7, 22, 35, DateTimeKind.Unspecified);
            var read = (DateTime)convert(stored);
            read.Ticks.Should().Be(stored.Ticks);
            read.Kind.Should().Be(DateTimeKind.Unspecified);
        }

        /// <summary>En kolumn som heter …Utc men saknas i registret visas två timmar fel. Fäller bygget.</summary>
        [Fact]
        public void Varje_Utc_kolumn_i_vara_tabeller_finns_i_registret()
        {
            var missing = typeof(StoredTimeBasis).Assembly.GetTypes()
                .Where(t => t.Namespace?.StartsWith("HpskSite") == true)
                .Select(t => (Type: t, Table: t.GetCustomAttribute<TableNameAttribute>()?.Value))
                .Where(x => !string.IsNullOrEmpty(x.Table))
                .SelectMany(x => x.Type.GetProperties()
                    .Where(p => p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?))
                    .Select(p => (x.Table, Column: p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name)))
                .Where(c => c.Column.EndsWith("Utc", StringComparison.Ordinal) && !StoredTimeBasis.IsUtc(c.Table!, c.Column))
                .Select(c => c.Table + "." + c.Column)
                .Distinct().ToList();
            missing.Should().BeEmpty("en …Utc-kolumn ska stå i StoredTimeBasis.UtcColumns");
        }
    }
}
