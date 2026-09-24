using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Projektdimensionen — bokföringsradens andra etikett.
    ///
    /// <para><b>⚠️ ETT FEL HÄR SYNS INTE SOM ETT FEL.</b> Verifikationen går ihop, debet möter
    /// kredit, ingenting felar. Det enda som blir tokigt är projektets resultatrapport, och den
    /// jämförs med bokföringen först när någon undrar varför tävlingen gick back — ofta ett år
    /// senare, på ett årsmöte. Det är samma sorts tystnad som <see cref="LedgerAmountsTests"/>
    /// finns för.</para>
    ///
    /// <para>Prövar <see cref="LedgerPostingService.BuildLines"/> och
    /// <see cref="LedgerPostingService.BuildCorrectionLines"/> direkt. De rör ingen databas — allt
    /// de behöver kommer in som uppslagstabeller — så testerna mäter den riktiga logiken och inte
    /// en attrapp som svarar det jag hoppades på.</para>
    /// </summary>
    public class LedgerProjectDimensionTests
    {
        // ── Uppsättning ─────────────────────────────────────────────────────────────────────

        private const int SsmProjectId = 10;
        private const int KretsmasterskapProjectId = 11;
        private const int OtherClubProjectId = 99;

        private static Dictionary<int, LedgerAccount> Accounts() => new()
        {
            [1930] = new LedgerAccount { Number = 1930, Name = "Föreningskonto" },
            [3020] = new LedgerAccount { Number = 3020, Name = "Anmälningsavgifter" },
            // Kiosken är den gren en förening faktiskt kan vara momsregistrerad för.
            [3040] = new LedgerAccount { Number = 3040, Name = "Kiosk och försäljning", DefaultVatRate = 25m },
            [2610] = new LedgerAccount { Number = 2610, Name = "Utgående moms" },
            [4030] = new LedgerAccount { Number = 4030, Name = "Priser och medaljer" },
            [8999] = new LedgerAccount { Number = 8999, Name = "Öresavrundning" }
        };

        private static Dictionary<string, int> Roles() => new()
        {
            [LedgerAccountRoles.BankAccount] = 1930,
            [LedgerAccountRoles.RevenueParticipationFee] = 3020,
            [LedgerAccountRoles.VatOutgoing] = 2610,
            [LedgerAccountRoles.Rounding] = 8999
        };

        /// <summary>
        /// Föreningens egna projekt. <b>Projekt 99 finns inte här</b> — det tillhör en annan
        /// förening, och uppslagstabellen är redan skopad till utställaren.
        /// </summary>
        private static Dictionary<int, LedgerProject> Projects() => new()
        {
            [SsmProjectId] = new LedgerProject { Id = SsmProjectId, Name = "SSM 2025" },
            [KretsmasterskapProjectId] = new LedgerProject { Id = KretsmasterskapProjectId, Name = "KrM 2025" }
        };

        // ⚠️ Momsregistrerad: kioskkontots 25 % ska ge en momsrad i de här testen. Grinden för en
        //    oregistrerad förening prövas i LedgerVatTests.
        private static List<LedgerJournalEntryLine> Build(LedgerPostingRequest request, out string? error)
            => LedgerPostingService.BuildLines(request, Accounts(), Roles(), Projects(), vatRegistered: true, out error);

        // ── Verifikationens projekt smittar av sig på raderna ───────────────────────────────

        [Fact]
        public void Projektet_pa_begaran_hamnar_pa_alla_rader()
        {
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines =
                {
                    new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 300 },
                    new LedgerPostingLine { Role = LedgerAccountRoles.RevenueParticipationFee, Credit = 300 }
                }
            };

            var lines = Build(request, out var error);

            error.Should().BeNull();
            lines.Should().HaveCount(2);
            lines.Should().OnlyContain(l => l.ProjectId == SsmProjectId);
        }

        [Fact]
        public void Projektets_namn_snapshottas_pa_raden()
        {
            // Namnet fryses för handlingar som lämnar systemet — en utskriven verifikationslista
            // från 2025 ska säga det som stod där då, även om projektet döps om sedan.
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines = { new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 300 } }
            };

            var lines = Build(request, out _);

            lines[0].ProjectName.Should().Be("SSM 2025");
        }

        [Fact]
        public void Utan_projekt_lamnas_raden_omarkt()
        {
            // ⚠️ Det NORMALA fallet. En förening som inte bryr sig om projekt ska kunna bokföra
            // hela året utan att något klagar eller fylls i åt den.
            var request = new LedgerPostingRequest
            {
                Lines = { new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 300 } }
            };

            var lines = Build(request, out var error);

            error.Should().BeNull();
            lines[0].ProjectId.Should().BeNull();
            lines[0].ProjectName.Should().BeEmpty();
        }

        [Fact]
        public void Radens_eget_projekt_vinner_over_begarans()
        {
            // Så här bokförs en betalning som täcker två tävlingar — den enda anledningen till att
            // dimensionen sitter på raden och inte på verifikationen.
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines =
                {
                    new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 500 },
                    new LedgerPostingLine
                    {
                        Role = LedgerAccountRoles.RevenueParticipationFee,
                        Credit = 500,
                        ProjectId = KretsmasterskapProjectId
                    }
                }
            };

            var lines = Build(request, out _);

            lines[0].ProjectId.Should().Be(SsmProjectId);
            lines[1].ProjectId.Should().Be(KretsmasterskapProjectId);
            lines[1].ProjectName.Should().Be("KrM 2025");
        }

        // ── De tre raderna tjänsten lägger till själv ───────────────────────────────────────

        [Fact]
        public void Momsraden_arver_kallradens_projekt()
        {
            // ⚠️ Gjorde den inte det skulle kioskens intäkt ligga på projektet och dess moms
            // utanför — projektets resultat skulle inte gå ihop med bokföringens.
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines =
                {
                    new LedgerPostingLine { AccountNumber = 3040, Credit = 125 }
                }
            };

            var lines = Build(request, out var error);

            error.Should().BeNull();
            lines.Should().HaveCount(2, "kioskintäkten och dess moms är två rader");

            var vatLine = lines.Single(l => l.AccountNumber == 2610);
            vatLine.Credit.Should().Be(25m);
            vatLine.ProjectId.Should().Be(SsmProjectId);
        }

        [Fact]
        public void Momsraden_arver_radens_projekt_nar_raden_avviker()
        {
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines =
                {
                    new LedgerPostingLine
                    {
                        AccountNumber = 3040,
                        Credit = 125,
                        ProjectId = KretsmasterskapProjectId
                    }
                }
            };

            var lines = Build(request, out _);

            lines.Single(l => l.AccountNumber == 2610).ProjectId.Should().Be(KretsmasterskapProjectId);
        }

        [Fact]
        public void Avrundningsraden_far_begarans_projekt_aldrig_en_enskild_rads()
        {
            // Öret hör till verifikationen som helhet. Delar den sig mellan två projekt finns det
            // inget sant svar på vems öret är, och då är omärkt ärligare än att gissa.
            var rounding = LedgerPostingService.BuildRoundingLine(
                0.01m, Accounts(), Roles(), out var error);

            error.Should().BeNull();
            rounding!.AccountNumber.Should().Be(8999);
            rounding.ProjectId.Should().BeNull("BuildRoundingLine märker inte själv — Post gör det");
        }

        // ── Spärren ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Ett_projekt_hos_en_annan_forening_avvisas()
        {
            // ⚠️ Kretsens projekt får aldrig gå att bokföra på ur en klubb. Uppslagstabellen är
            // skopad till utställaren, så ett främmande id ser likadant ut som ett som inte finns
            // — och båda är fel att släppa igenom.
            var request = new LedgerPostingRequest
            {
                ProjectId = OtherClubProjectId,
                Lines = { new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 300 } }
            };

            Build(request, out var error);

            error.Should().NotBeNull();
            error.Should().Contain("99", "felet måste namnge projektet, annars vet ingen vad som ska rättas");
        }

        [Fact]
        public void Ett_okant_projekt_pa_en_enskild_rad_avvisas_ocksa()
        {
            var request = new LedgerPostingRequest
            {
                ProjectId = SsmProjectId,
                Lines =
                {
                    new LedgerPostingLine { Role = LedgerAccountRoles.BankAccount, Debit = 300 },
                    new LedgerPostingLine
                    {
                        Role = LedgerAccountRoles.RevenueParticipationFee,
                        Credit = 300,
                        ProjectId = OtherClubProjectId
                    }
                }
            };

            Build(request, out var error);

            error.Should().NotBeNull();
        }

        // ── Rättelsen ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Rattelsen_bar_projektet_ur_originalet_per_rad()
        {
            // ⚠️ DET FARLIGASTE STÄLLET. En rättelse som tappar projektet lämnar kostnaden kvar i
            // projektets resultat medan den är borta ur bokföringens — projektrapporten visar då
            // ett underskott som inte går att hitta i böckerna.
            var original = new List<LedgerJournalEntryLine>
            {
                new()
                {
                    AccountNumber = 1930, AccountName = "Föreningskonto",
                    Debit = 300, Credit = 0,
                    ProjectId = SsmProjectId, ProjectName = "SSM 2025"
                },
                new()
                {
                    AccountNumber = 3020, AccountName = "Anmälningsavgifter",
                    Debit = 0, Credit = 300,
                    ProjectId = KretsmasterskapProjectId, ProjectName = "KrM 2025"
                }
            };

            var corrected = LedgerPostingService.BuildCorrectionLines(original);

            corrected.Should().HaveCount(2);
            corrected[0].ProjectId.Should().Be(SsmProjectId);
            corrected[1].ProjectId.Should().Be(KretsmasterskapProjectId);
        }

        [Fact]
        public void Rattelsen_vander_belopp_och_lagger_aldrig_moms_pa_momsen()
        {
            var original = new List<LedgerJournalEntryLine>
            {
                new() { AccountNumber = 3040, Debit = 0, Credit = 100, VatRate = 25m, VatAmount = 25m },
                new() { AccountNumber = 2610, Debit = 0, Credit = 25 }
            };

            var corrected = LedgerPostingService.BuildCorrectionLines(original);

            corrected[0].Debit.Should().Be(100);
            corrected[0].Credit.Should().Be(0);
            corrected.Should().OnlyContain(l => l.VatRate == 0,
                "momsen är redan uträknad i originalet och ligger på egen rad");
        }

        [Fact]
        public void Rattelsen_av_en_omarkt_rad_forblir_omarkt()
        {
            var original = new List<LedgerJournalEntryLine>
            {
                new() { AccountNumber = 1930, Debit = 300, Credit = 0 }
            };

            LedgerPostingService.BuildCorrectionLines(original)[0].ProjectId.Should().BeNull();
        }
    }
}
