using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Numreringen av noterna till resultat- och balansräkningen. Numret lagras aldrig — det
    /// här är enda stället det bestäms.
    /// </summary>
    public class LedgerNoteNumberingTests
    {
        // Räkningarnas ordning: intäkter, kostnader, tillgångar, kapital och skulder.
        private static readonly List<int> Order = new() { 3010, 3040, 5010, 6980, 1220, 1229, 1930, 2060 };

        private static Dictionary<int, int> Numbers(params LedgerNoteNumbering.Input[] notes)
            => LedgerNoteNumbering.Number(notes, Order).ToDictionary(n => n.Id, n => n.Number);

        private static LedgerNoteNumbering.Input N(int id, params int[] accounts) => new(id, accounts);

        [Fact]
        public void Kontonoter_numreras_i_rakningarnas_ordning_inte_i_skrivordning()
        {
            // Balansnoten skrevs först, men intäktsnoten står först i räkningarna.
            var nr = Numbers(N(1, 1220), N(2, 3040));
            Assert.Equal(1, nr[2]);
            Assert.Equal(2, nr[1]);
        }

        [Fact]
        public void Allmanna_noter_star_forst_i_skrivordning()
        {
            var nr = Numbers(N(1, 3010), N(2), N(3));
            Assert.Equal(1, nr[2]);
            Assert.Equal(2, nr[3]);
            Assert.Equal(3, nr[1]);
        }

        [Fact]
        public void En_not_pa_flera_konton_placeras_efter_sitt_forsta_konto()
        {
            // Inventarier 1220 + avskrivningar 1229 delar not; kostnadsnoten 6980 kommer före.
            var nr = Numbers(N(1, 1229, 1220), N(2, 6980));
            Assert.Equal(1, nr[2]);
            Assert.Equal(2, nr[1]);
        }

        [Fact]
        public void Not_vars_konton_saknas_i_rakningarna_hamnar_sist_och_flaggas()
        {
            var result = LedgerNoteNumbering.Number(new[] { N(1, 9999), N(2, 3010) }, Order);
            var orphan = result.Single(n => n.Id == 1);
            Assert.Equal(2, orphan.Number);
            Assert.True(orphan.IsUnreferenced);
            Assert.Equal(new[] { 9999 }, orphan.MissingAccounts);
        }

        [Fact]
        public void Delvis_saknat_konto_flaggas_men_noten_ar_fortfarande_hanvisad()
        {
            var n = LedgerNoteNumbering.Number(new[] { N(1, 1220, 9999) }, Order).Single();
            Assert.False(n.IsUnreferenced);
            Assert.Equal(new[] { 9999 }, n.MissingAccounts);
        }

        [Fact]
        public void Lika_placering_avgors_av_skrivordning()
        {
            var nr = Numbers(N(5, 1930), N(3, 1930));
            Assert.Equal(1, nr[3]);
            Assert.Equal(2, nr[5]);
        }

        [Fact]
        public void Numren_ar_obrutna_fran_ett()
        {
            var result = LedgerNoteNumbering.Number(new[] { N(1, 9999), N(2), N(3, 1220), N(4, 3010) }, Order);
            Assert.Equal(new[] { 1, 2, 3, 4 }, result.Select(r => r.Number).OrderBy(x => x));
        }

        [Fact]
        public void Rakningarnas_ordning_ar_intakter_kostnader_tillgangar_kapital()
        {
            var s = new LedgerFinancialStatements();
            s.Assets.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 1930 });
            s.Revenue.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 3010 });
            s.EquityAndLiabilities.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 2060 });
            s.Costs.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 5010 });

            Assert.Equal(new[] { 3010, 5010, 1930, 2060 }, LedgerNoteNumbering.StatementOrder(s));
        }
    }
}
