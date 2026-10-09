using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Events;
using HpskSite.Services.Events;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Reglerna för flytten av händelserna till tabellen (sajtstrukturen fas 2d). Det avgörande fallet
    /// är klubben som återanvänder ett namn varje år — det problem årsadressen finns för.
    /// </summary>
    public class ClubEventMigrationPlannerTests
    {
        private static string Slug(string s) => s.Trim().ToLowerInvariant().Replace(' ', '-');

        private static ClubEventRecord Ev(int id, int owner, string name, DateTime? start, string url = "", bool published = true) => new()
        {
            Id = id, OwnerId = owner, OwnerType = ClubEventRecord.OwnerClub, PageName = name, Name = name,
            StartsAt = start, IsPublished = published, Url = url, CreatedDate = new DateTime(2026, 1, 15)
        };

        private static readonly HashSet<int> None = new();

        [Fact]
        public void Varje_nod_behaller_sitt_id_och_far_arets_adress()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[] { Ev(9001, 2604, "Gasaskjutning", new DateTime(2026, 9, 1)) }, None, Slug);
            var row = Assert.Single(plan.Rows);
            Assert.Equal(9001, row.Row.Id);
            Assert.Equal(2026, row.UrlYear);
            Assert.Equal("gasaskjutning", row.Slug);
        }

        [Fact]
        public void Samma_namn_olika_ar_krockar_inte()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[]
            {
                Ev(1, 2604, "Gasaskjutning", new DateTime(2025, 9, 1)),
                Ev(2, 2604, "Gasaskjutning", new DateTime(2026, 9, 1)),
            }, None, Slug);
            Assert.All(plan.Rows, r => Assert.Equal("gasaskjutning", r.Slug));
            Assert.Equal(0, plan.SlugCollisions);
        }

        [Fact]
        public void Samma_namn_samma_ar_far_suffix_i_id_ordning()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[]
            {
                Ev(20, 2604, "Nybörjare", new DateTime(2026, 9, 8)),
                Ev(10, 2604, "Nybörjare", new DateTime(2026, 9, 1)),
                Ev(30, 2604, "Nybörjare", new DateTime(2026, 9, 15)),
            }, None, Slug);
            Assert.Equal("nybörjare", plan.Rows.Single(r => r.Row.Id == 10).Slug);
            Assert.Equal("nybörjare-2", plan.Rows.Single(r => r.Row.Id == 20).Slug);
            Assert.Equal("nybörjare-3", plan.Rows.Single(r => r.Row.Id == 30).Slug);
            Assert.Equal(2, plan.SlugCollisions);
        }

        [Fact]
        public void Samma_namn_hos_olika_agare_krockar_inte()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[]
            {
                Ev(1, 2604, "Städdag", new DateTime(2026, 4, 1)),
                Ev(2, 2607, "Städdag", new DateTime(2026, 4, 1)),
            }, None, Slug);
            Assert.All(plan.Rows, r => Assert.Equal("städdag", r.Slug));
        }

        [Fact]
        public void Handelse_utan_datum_far_skapandearet()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[] { Ev(5, 2604, "Info", null) }, None, Slug);
            Assert.Equal(2026, plan.Rows.Single().UrlYear);
        }

        [Fact]
        public void Handelse_som_blivit_traning_hoppas_over()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[] { Ev(7, 2604, "Träning", new DateTime(2026, 5, 5)) }, new HashSet<int> { 7 }, Slug);
            Assert.Empty(plan.Rows);
            Assert.Equal(1, plan.SkippedMovedToTraining);
        }

        [Fact]
        public void Opublicerad_flyttas_opublicerad()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[] { Ev(8, 2604, "Utkast", new DateTime(2026, 5, 5), published: false) }, None, Slug);
            Assert.False(plan.Rows.Single().Row.IsPublished);
            Assert.Equal(1, plan.Unpublished);
        }

        [Fact]
        public void Gamla_tradadressens_sista_del_sparas()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[]
            {
                Ev(9, 2604, "Gasaskjutning", new DateTime(2026, 9, 1), "https://pistol.nu/halland/klubbar/x/gasaskjutning-1/")
            }, None, Slug);
            Assert.Equal("gasaskjutning-1", plan.Rows.Single().LegacySlug);
        }

        [Fact]
        public void Tomt_namn_far_en_slug_anda()
        {
            var plan = ClubEventMigrationPlanner.Plan(new[] { Ev(11, 2604, "", new DateTime(2026, 9, 1)) }, None, Slug);
            Assert.Equal("handelse", plan.Rows.Single().Slug);
        }
    }
}
