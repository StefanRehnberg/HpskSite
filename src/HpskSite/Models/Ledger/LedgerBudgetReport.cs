using System.Globalization;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// "Utfall mot budget" — Rapport-ytan. Härledd vid varje läsning, lagras aldrig.
    ///
    /// <para><b>⚠️ Rapporten är INTE en resultaträkning.</b> Den hör till Bokslut, steg 6. Den här
    /// svarar på <i>håller vi budgeten?</i>, och den frågan ställs på varje styrelsemöte medan
    /// bokslutet görs en gång om året.</para>
    /// </summary>
    public class LedgerBudgetReport
    {
        public int FiscalYearId { get; set; }

        public int Year { get; set; }

        /// <summary>Sant först när en budget är ANTAGEN. Ett utkast är inte en budget.</summary>
        public bool HasBudget { get; set; }

        /// <summary>Ett utkast ligger och väntar på att antas. Styr vad tomma läget erbjuder.</summary>
        public bool HasDraft { get; set; }

        public int? BudgetId { get; set; }

        public int? DraftId { get; set; }

        public int Revision { get; set; }

        public DateTime? AdoptedDate { get; set; }

        /// <summary>T.ex. "Årsmötet 14 mars 2026" — kassörens egen formulering när den finns.</summary>
        public string? AdoptedLabel { get; set; }

        /// <summary>
        /// "Årsmötet" eller "Styrelsen".
        ///
        /// <para><b>⚠️ Ytan MÅSTE bygga sina meningar av den här och <see cref="AdoptedDate"/>,
        /// aldrig av <see cref="AdoptedLabel"/>.</b> Etiketten kan vara kassörens egen fritext, och
        /// "Den antogs Årsmötet 14 mars" är ingen mening. Första versionen skrev
        /// <i>"Budget Arsmotet 14 mars."</i> under tabellen — vilket syntes först när sidan
        /// renderades och lästes.</para>
        /// </summary>
        public string? AdoptedBodyLabel { get; set; }

        /// <summary>Kassörens egen anteckning, om någon. Visas för sig, aldrig inbakad i en mening.</summary>
        public string? AdoptedNote { get; set; }

        public DateTime PeriodStart { get; set; }

        public DateTime PeriodEnd { get; set; }

        public List<LedgerBudgetReportRow> Income { get; set; } = new();

        public List<LedgerBudgetReportRow> Costs { get; set; } = new();

        public decimal BudgetResult { get; set; }

        public decimal ActualResult { get; set; }

        public decimal ResultDiff { get; set; }

        public string? Error { get; set; }

        /// <summary>
        /// "1 januari – 30 september 2026".
        ///
        /// <para><b>⚠️ Perioden MÅSTE stå på ytan.</b> Budgeten är hela årets och proportioneras
        /// aldrig, så −49 % på kiosken i maj betyder "säsongen har inte börjat" och inte "katastrof".
        /// Utan perioden i rubriken är siffran obegriplig — och den läses av en styrelse.</para>
        /// </summary>
        public string PeriodLabel
        {
            get
            {
                var sv = CultureInfo.GetCultureInfo("sv-SE");
                var same = PeriodStart.Year == PeriodEnd.Year;

                return same
                    ? $"{PeriodStart.ToString("d MMMM", sv)} – {PeriodEnd.ToString("d MMMM yyyy", sv)}"
                    : $"{PeriodStart.ToString("d MMMM yyyy", sv)} – {PeriodEnd.ToString("d MMMM yyyy", sv)}";
            }
        }

        /// <summary>Resultatraden är grön när utfallet är bättre än budget — åt båda hållen.</summary>
        public string ResultTone =>
            ResultDiff >= 0m ? LedgerBudgetTone.Good : LedgerBudgetTone.Attention;
    }

    /// <summary>En kontorad i uppföljningen.</summary>
    public class LedgerBudgetReportRow
    {
        public int AccountNumber { get; set; }

        public string AccountName { get; set; } = "";

        /// <summary>Alltid positivt, i kontots egen riktning. 0 = kontot är inte budgeterat.</summary>
        public decimal Budget { get; set; }

        /// <summary>Utfallet i perioden, i samma riktning som budgeten.</summary>
        public decimal Actual { get; set; }

        /// <summary>Utfall − budget. Tecknet betyder olika saker för intäkt och kostnad.</summary>
        public decimal Diff { get; set; }

        /// <summary>Null när kontot saknar budget — då finns inget att räkna procent på.</summary>
        public decimal? DiffPercent { get; set; }

        /// <summary>0–100. Utfallet som andel av budgeten, kapat. 0 när budget saknas.</summary>
        public int BarPercent { get; set; }

        /// <summary>Ur <see cref="LedgerBudgetTone"/>.</summary>
        public string Tone { get; set; } = LedgerBudgetTone.Neutral;

        /// <summary>Sant för ett konto med utfall men utan budget — en post ingen räknade med.</summary>
        public bool Unbudgeted => Budget <= 0m && Actual != 0m;
    }

    /// <summary>
    /// Färgspråket i uppföljningen.
    ///
    /// <para><b>⚠️ Bara tre lägen, och "bra" är inte spegelbilden av "dåligt".</b> En kostnad under
    /// budget blir aldrig grön — att ha spenderat mindre än beslutat betyder ofta att något inte
    /// blivit gjort.</para>
    /// </summary>
    public static class LedgerBudgetTone
    {
        /// <summary>Intäkt över budget. Entydigt bra.</summary>
        public const string Good = "ok";

        /// <summary>Avviker åt fel håll, och tillräckligt mycket för att vara värt en blick.</summary>
        public const string Attention = "attn";

        /// <summary>Allt annat — inklusive kostnader under budget.</summary>
        public const string Neutral = "neutral";
    }
}
