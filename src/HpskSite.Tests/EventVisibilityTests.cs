using System.Runtime.CompilerServices;
using HpskSite.Models.Events;
using HpskSite.Services.Events;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Händelsernas synlighet (klubbsidorna 2c, <see cref="ClubEventRecord.ReadVisibility"/>): reglerna är rena, varje <c>EventViewer.System</c> står i en lista som
    /// ett prov läser, ingen utom växeln läser lagringen direkt, och tabellens DEFAULT är fail-closed (1 = Inloggade, aldrig 0 = Alla).
    /// </summary>
    public class EventVisibilityTests
    {
        private static ClubEventRecord Ev(SiteVisibility v, string ownerType = ClubEventRecord.OwnerClub, int ownerId = 9003) =>
            new() { Id = 1, OwnerType = ownerType, OwnerId = ownerId, ReadVisibility = v };

        // ── Reglerna ──────────────────────────────────────────────────────────────

        [Fact]
        public void Medlemmar_styrelse_och_dold_hos_alla_tittare()
        {
            var anon = EventViewer.Anonymous;
            var logged = EventViewer.ForOwner(new SiteViewer(true, false, false, false));
            var member = EventViewer.ForOwner(new SiteViewer(true, true, false, false));
            var board = EventViewer.ForOwner(new SiteViewer(true, false, true, false));
            var admin = EventViewer.ForOwner(new SiteViewer(true, false, false, true));

            var a0 = Ev(SiteVisibility.Alla);
            Assert.True(anon.CanSee(a0)); Assert.True(logged.CanSee(a0)); Assert.True(member.CanSee(a0)); Assert.True(admin.CanSee(a0));

            var i1 = Ev(SiteVisibility.Inloggade);
            Assert.False(anon.CanSee(i1)); Assert.True(logged.CanSee(i1)); Assert.True(member.CanSee(i1)); Assert.True(admin.CanSee(i1));

            var m = Ev(SiteVisibility.Klubbmedlemmar);
            Assert.False(anon.CanSee(m)); Assert.False(logged.CanSee(m)); Assert.True(member.CanSee(m)); Assert.True(admin.CanSee(m));

            var s = Ev(SiteVisibility.Styrelse);
            Assert.False(anon.CanSee(s)); Assert.False(logged.CanSee(s)); Assert.False(member.CanSee(s)); Assert.True(board.CanSee(s)); Assert.True(admin.CanSee(s));

            var d = Ev(SiteVisibility.Dold);
            Assert.False(anon.CanSee(d)); Assert.False(member.CanSee(d)); Assert.False(board.CanSee(d)); Assert.True(admin.CanSee(d));
        }

        [Fact]
        public void System_ser_allt_men_Anonymous_ser_bara_Alla()
        {
            foreach (var v in new[] { SiteVisibility.Alla, SiteVisibility.Inloggade, SiteVisibility.Klubbmedlemmar, SiteVisibility.Styrelse, SiteVisibility.Dold })
            {
                Assert.True(EventViewer.System.CanSee(Ev(v)));
                Assert.Equal(v == SiteVisibility.Alla, EventViewer.Anonymous.CanSee(Ev(v)));
            }
        }

        [Fact]
        public void Ett_okant_varde_syns_aldrig_for_en_besokare()
        {
            var weird = Ev((SiteVisibility)42);
            Assert.False(EventViewer.Anonymous.CanSee(weird));
            Assert.False(EventViewer.ForOwner(new SiteViewer(true, true, true, true)).CanSee(weird));
        }

        [Fact]
        public void En_tittare_per_agare_prövas_mot_just_den_agaren()
        {
            // Medlem i klubb 1 men inte i klubb 2: en Klubbmedlemmar-händelse syns bara hos klubb 1.
            var viewer = EventViewer.Of((type, id) => new SiteViewer(true, id == 1, false, false));
            Assert.True(viewer.CanSee(Ev(SiteVisibility.Klubbmedlemmar, ownerId: 1)));
            Assert.False(viewer.CanSee(Ev(SiteVisibility.Klubbmedlemmar, ownerId: 2)));
        }

        [Fact]
        public void En_ny_handelse_ar_oppen_for_alla_som_forut()
        {
            Assert.Equal(SiteVisibility.Alla, new ClubEventRecord().ReadVisibility);
        }

        // ── Indata ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("0", SiteVisibility.Alla)]
        [InlineData("1", SiteVisibility.Inloggade)]
        [InlineData("2", SiteVisibility.Klubbmedlemmar)]
        [InlineData("3", SiteVisibility.Styrelse)]
        [InlineData("4", SiteVisibility.Dold)]
        public void Indata_tar_0_till_4(string value, SiteVisibility expected)
        {
            var r = new ClubEventRecord { ReadVisibility = SiteVisibility.Styrelse };
            Assert.Null(ClubEventInput.ApplyRegistrationFields(r, false, 0, false, "", readVisibility: value));
            Assert.Equal(expected, r.ReadVisibility);
        }

        [Fact]
        public void Tomt_varde_rör_inte_synligheten()
        {
            var r = new ClubEventRecord { ReadVisibility = SiteVisibility.Styrelse };
            Assert.Null(ClubEventInput.ApplyRegistrationFields(r, false, 0, false, "", readVisibility: ""));
            Assert.Equal(SiteVisibility.Styrelse, r.ReadVisibility);
        }

        [Theory]
        [InlineData("5")]      // Utloggad är ett systemvärde
        [InlineData("-1")]
        [InlineData("alla")]
        [InlineData("99")]
        public void Ogiltigt_varde_vagras_och_skriver_ingenting(string value)
        {
            var r = new ClubEventRecord { ReadVisibility = SiteVisibility.Klubbmedlemmar };
            var err = ClubEventInput.ApplyRegistrationFields(r, false, 0, false, "", readVisibility: value);
            Assert.NotNull(err);
            Assert.Contains("Ingenting sparades", err);
            Assert.Equal(SiteVisibility.Klubbmedlemmar, r.ReadVisibility);
        }

        [Fact]
        public void Nodlagringen_vagrar_en_handelse_som_kraver_synligheten()
        {
            var nodeSupport = new ClubEventFieldSupport(true, true, true, true, true, true, ReadVisibility: false);
            Assert.Contains("readVisibility", ClubEventInput.UnsupportedValues(Ev(SiteVisibility.Inloggade), nodeSupport));
            Assert.DoesNotContain("readVisibility", ClubEventInput.UnsupportedValues(Ev(SiteVisibility.Alla), nodeSupport));
        }

        // ── Spärrar ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Varje användning av <c>EventViewer.System</c> (en rad per anrop). Ett nytt anrop kräver att någon lägger till det här — ett medvetet val:
        /// är det en adminyta som redan prövat behörighet, ett bakgrundsjobb eller en flytt (System), eller en besökare (en riktig tittare)?
        /// </summary>
        private static readonly Dictionary<string, int> SystemUses = new()
        {
            ["Controllers/AdminStatisticsController.cs"] = 1,        // sajtadmins statistik
            ["Controllers/AnkeborgSeedController.cs"] = 1,           // utvecklingsverktyg
            ["Controllers/ClubController.cs"] = 11,                  // ändra/radera/kopiera/ladda upp (klubbens och kretsens adminpaneler)
            ["Controllers/ClubEventCompetitionLinkController.cs"] = 1,
            ["Controllers/ClubEventController.cs"] = 1,              // mejlets svarsadress
            ["Controllers/ClubEventDeskController.cs"] = 1,          // diskens länk till händelsen
            ["Controllers/ClubStatisticsController.cs"] = 1,
            ["Controllers/RegionalStatisticsController.cs"] = 1,
            ["Services/ClubEventParticipationService.cs"] = 1,       // anmälans regelmotor; IsEligible/SeesEvent prövar synligheten per person
            ["Services/Events/CompetitionEventLinkService.cs"] = 3,
            ["Services/Events/EventOwnerResolver.cs"] = 1,
            ["Services/Firearms/LoanWeaponClubRules.cs"] = 1,
            ["Services/Kretsgranskning/KretsCalendarService.cs"] = 1, // delat index; synligheten prövas när listan byggs
            ["Services/Ledger/LedgerIssuerResolver.cs"] = 1,
            ["Services/Ledger/LedgerOverviewService.cs"] = 1,
            ["Services/Ledger/LedgerSourceProjectResolver.cs"] = 1,
            ["Services/RangeCalendarService.cs"] = 1,                // läser alla, MASKERAR i Events() ("Upptaget")
            ["Services/Training/TrainingMigrationService.cs"] = 4,
            ["Views/ClubSimpleEventPage.cshtml"] = 1,                 // node mode only (no visibility field); table mode arrives as routed from ServeEvent, which checks visibility
            ["Services/WhatsHappeningService.cs"] = 1,               // cachat flöde; synligheten prövas per rad i _WhatsHappening.cshtml
        };

        [Fact]
        public void Varje_EventViewerSystem_star_i_listan()
        {
            var root = SiteRoot();
            var found = new Dictionary<string, int>();
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}umbraco{Path.DirectorySeparatorChar}")))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel == "Services/Events/EventViewer.cs") continue;
                var n = File.ReadAllLines(file).Count(l => l.Contains("EventViewer.System") && !l.TrimStart().StartsWith("//"));
                if (n > 0) found[rel] = n;
            }
            var unexpected = found.Where(kv => !SystemUses.TryGetValue(kv.Key, out var expected) || expected != kv.Value)
                .Select(kv => $"{kv.Key}: {kv.Value} (väntat {(SystemUses.TryGetValue(kv.Key, out var e) ? e : 0)})")
                .Concat(SystemUses.Where(kv => !found.ContainsKey(kv.Key)).Select(kv => $"{kv.Key}: 0 (väntat {kv.Value})"))
                .ToList();
            Assert.True(unexpected.Count == 0, "EventViewer.System används annorlunda än listan — ett medvetet val saknas:\n" + string.Join("\n", unexpected));
        }

        [Fact]
        public void Bara_vaxeln_och_adressuppslagen_laser_lagringen_direkt()
        {
            var allowed = new HashSet<string>
            {
                "Composers/AdminServicesComposer.cs", "Controllers/CompetitionPagesController.cs", "Routing/ClubEventLegacyUrlFinder.cs",
                "Routing/LegacyRedirectFinder.cs", "Services/Events/ClubEventStoreSwitch.cs", "Services/Events/IClubEventStore.cs",
                "Services/Events/NodeClubEventStore.cs", "Services/Events/SqlClubEventStore.cs",
            };
            var root = SiteRoot();
            var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs") || f.EndsWith(".cshtml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}umbraco{Path.DirectorySeparatorChar}"))
                .Where(f => File.ReadAllText(f).Contains("SqlClubEventStore") || File.ReadAllText(f).Contains("NodeClubEventStore") || File.ReadAllText(f).Contains("IClubEventBackend"))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !allowed.Contains(f))
                .ToList();
            Assert.True(offenders.Count == 0, "Lagringen läses förbi synlighetsprövningen i: " + string.Join(", ", offenders));
        }

        [Fact]
        public void Varje_lasning_i_vaxeln_provar_synligheten()
        {
            var text = File.ReadAllText(Path.Combine(SiteRoot(), "Services/Events/ClubEventStoreSwitch.cs"));
            foreach (var read in new[] { "Get(int id, EventViewer viewer)", "GetMany(", "ListForOwner(", "ListPublishedBetween(", "ListAllForOwnerType(" })
                Assert.Contains(read, text);
            // Get prövar CanSee själv, listorna går genom Visible(), som prövar CanSee.
            Assert.Contains("viewer.CanSee(r)", text);
            Assert.Contains("all.Where(viewer.CanSee)", text);
            foreach (var list in new[] { "GetMany(ids), viewer", "ListForOwner(ownerId, publishedOnly), viewer", "ListPublishedBetween(from, to), viewer", "ListAllForOwnerType(ownerType), viewer" })
                Assert.Contains("Visible(Current." + list, text);
        }

        // ── Tabellen ──────────────────────────────────────────────────────────────

        [SqlFact]
        public void Kolumnens_default_ar_Inloggade_aldrig_Alla_och_har_en_CHECK()
        {
            using var f = new SiteSqlFixture();
            var def = f.Db.ExecuteScalar<string>(
                "SELECT dc.definition FROM sys.default_constraints dc JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
                "WHERE dc.parent_object_id = OBJECT_ID('dbo.ClubEvent') AND c.name = 'ReadVisibility'");
            Assert.Equal("((1))", def);
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_ClubEvent_ReadVisibility' AND parent_object_id = OBJECT_ID('dbo.ClubEvent')"));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ClubEvent WHERE ReadVisibility > 4"));
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            var src = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            var site = Path.Combine(src, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
