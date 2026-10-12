using System;
using HpskSite.Models.StartLists;
using HpskSite.Services.StartLists;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tabellagringens rena delar (skalplanen C2): radens mappning, ändringsjämförelsen och id-gränsen.
    /// Själva SQL:en prövas av sviten <c>hpsk-verify/startliststore-migration-verify.mjs</c>.
    /// </summary>
    public class SqlStartListStoreTests
    {
        private static StartListRecord Sample() => new()
        {
            Id = 5,
            CompetitionId = 10,
            Kind = StartListKind.Finals,
            Name = "Finalstartlista C",
            ConfigurationData = "{\"Teams\":[]}",
            StartListContent = "<p>x</p>",
            IsOfficial = true,
            GeneratedDate = new DateTime(2026, 10, 12, 18, 30, 15),
            GeneratedBy = "Kalle",
            TeamFormat = "Championship Finals",
            Notes = "n",
            QualifyingResultsSnapshot = "{}",
            PerClassConfigData = "{}",
            TotalFinalists = 10,
            MaxShootersPerTeam = 12,
            QualificationStartListId = 4,
        };

        [Fact]
        public void Ett_negativt_id_ar_en_vanlig_rad()
        {
            // Nya rader får NEGATIVA id:n (NegativeIdentity), så ett startliste-id aldrig kan vara samma tal
            // som en Umbraco-nod. Mappningen får inte behandla tecknet som något särskilt.
            var rec = SqlStartListStore.ToRecord(new SqlStartListStore.Row { Id = -3, CompetitionId = 7, Kind = 0, Name = "Startlista" });
            Assert.Equal(-3, rec.Id);
        }

        [Fact]
        public void En_oforandrad_lista_ar_ingen_andring()
        {
            var a = Sample();
            Assert.False(SqlStartListStore.Differs(a, a.Clone()));
        }

        [Fact]
        public void Tom_strang_och_null_ar_samma_varde()
        {
            var a = Sample();
            a.Notes = "";
            var b = a.Clone();
            b.Notes = null!;
            Assert.False(SqlStartListStore.Differs(a, b));
        }

        [Theory]
        [InlineData("Name")]
        [InlineData("ConfigurationData")]
        [InlineData("StartListContent")]
        [InlineData("IsOfficial")]
        [InlineData("GeneratedDate")]
        [InlineData("GeneratedBy")]
        [InlineData("TeamFormat")]
        [InlineData("Notes")]
        [InlineData("QualifyingResultsSnapshot")]
        [InlineData("PerClassConfigData")]
        [InlineData("TotalFinalists")]
        [InlineData("MaxShootersPerTeam")]
        [InlineData("QualificationStartListId")]
        public void Varje_sparat_falt_raknas_som_en_andring(string field)
        {
            var a = Sample();
            var b = a.Clone();
            switch (field)
            {
                case "Name": b.Name = "Annan"; break;
                case "ConfigurationData": b.ConfigurationData = "{}"; break;
                case "StartListContent": b.StartListContent = ""; break;
                case "IsOfficial": b.IsOfficial = false; break;
                case "GeneratedDate": b.GeneratedDate = a.GeneratedDate!.Value.AddMinutes(1); break;
                case "GeneratedBy": b.GeneratedBy = "Lisa"; break;
                case "TeamFormat": b.TeamFormat = "Final"; break;
                case "Notes": b.Notes = "m"; break;
                case "QualifyingResultsSnapshot": b.QualifyingResultsSnapshot = "{\"a\":1}"; break;
                case "PerClassConfigData": b.PerClassConfigData = "{\"a\":1}"; break;
                case "TotalFinalists": b.TotalFinalists = 11; break;
                case "MaxShootersPerTeam": b.MaxShootersPerTeam = 13; break;
                case "QualificationStartListId": b.QualificationStartListId = 9; break;
            }
            Assert.True(SqlStartListStore.Differs(a, b), field);
        }

        [Fact]
        public void Tabellen_lagrar_sekunder()
        {
            var d = new DateTime(2026, 10, 12, 18, 30, 15).AddMilliseconds(789);
            Assert.Equal(new DateTime(2026, 10, 12, 18, 30, 15), SqlStartListStore.Truncate(d));
            Assert.Null(SqlStartListStore.Truncate(null));
        }

        [Fact]
        public void Raden_blir_en_post_med_ratt_sort_och_alltid_publicerad()
        {
            var rec = SqlStartListStore.ToRecord(new SqlStartListStore.Row
            {
                Id = 100_000_001, CompetitionId = 7, Kind = 1, Name = "Finalstartlista A", UrlSegment = "finalstartlista-a",
                ConfigurationData = null, IsOfficial = false, LegacyHub = true, SortOrder = 3,
            });
            Assert.Equal(StartListKind.Finals, rec.Kind);
            Assert.Equal("finalstartlista-a", rec.UrlSegment);
            Assert.Equal("", rec.ConfigurationData);
            Assert.True(rec.LegacyHub);
            Assert.True(rec.IsPublished);
            Assert.Equal(3, rec.SortOrder);

            var qual = SqlStartListStore.ToRecord(new SqlStartListStore.Row { Kind = 0, Name = "Startlista" });
            Assert.Equal(StartListKind.Qualifying, qual.Kind);
        }
    }
}
