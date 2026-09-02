using HpskSite.Models.Firearms;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Bokningsfönstret och överlappsregeln.
    ///
    /// <para><b>Varför den här sviten fattades och behövde skrivas:</b> överlappet var punkt 6:s
    /// enda verkliga spärr — inget unikt index kan uttrycka "överlappar i tid" — och det var
    /// bevisat bara av en e2e-körning. En regel som två personers anspråk på samma vapen hänger på
    /// förtjänar test som kan falla på en minut.</para>
    ///
    /// <para><b>⚠️ SQL-satserna i <c>FirearmBookingService</c> måste spegla
    /// <see cref="FirearmBookingWindow.Overlaps"/> exakt.</b> Testerna nedan pinnar semantiken;
    /// de kan inte se SQL:en. Ändras den ena måste den andra följa.</para>
    /// </summary>
    public class FirearmBookingWindowTests
    {
        // Fast "nu" så gränserna går att pröva utan att flytta systemklockan.
        private static readonly DateTime Now = new DateTime(2026, 9, 2, 14, 30, 0);
        private static DateTime Day(int addDays) => Now.Date.AddDays(addDays);

        // ── Tom sluttid = hela dagen ─────────────────────────────────────────────────────────
        // Det är vad en medlem menar med "jag vill låna det på lördag". Utan tolkningen blir
        // fönstret noll sekunder långt, krockar med ingenting, och bokningen bokar inget.

        [Fact]
        public void TryNormalise_UtanSluttid_BlirHelaDagen()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(1), default, Now, out var f, out var t, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(Day(1), f);
            Assert.Equal(Day(1).AddDays(1).AddSeconds(-1), t);
        }

        [Fact]
        public void TryNormalise_BadaMidnattSammaDag_BlirHelaDagen()
        {
            // Datumväljaren skickar två datum utan klockslag när medlemmen bara valt en dag.
            var ok = FirearmBookingWindow.TryNormalise(
                Day(1), Day(1), Now, out var f, out var t, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(Day(1), f);
            Assert.Equal(Day(1).AddDays(1).AddSeconds(-1), t);
        }

        // ── ⚠️ Ett BAKVÄNT fönster är ett fel, inte hela dagen ───────────────────────────────
        // Detta är precis där de två kopiorna av regeln hade glidit isär: tillgänglighetslistan
        // tolkade 14:00–10:00 som hela dagen och visade vapnet som ledigt, medan bokningen vägrade
        // samma fönster. En rad som ser bokbar ut och nekas i nästa klick.

        [Fact]
        public void TryNormalise_SluttidForeStarttid_ArEttFel()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(1).AddHours(14), Day(1).AddHours(10), Now,
                out _, out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
            Assert.Contains("sluta efter", error);
        }

        [Fact]
        public void TryNormalise_SammaTidpunktBadaHall_ArEttFel()
        {
            // Nollängd är inte "hela dagen" när ett klockslag är satt — då har medlemmen sagt något.
            var ok = FirearmBookingWindow.TryNormalise(
                Day(1).AddHours(10), Day(1).AddHours(10), Now, out _, out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
        }

        // ── Taken och bakåt i tiden ──────────────────────────────────────────────────────────

        [Fact]
        public void TryNormalise_IdagArTillatet()
        {
            // ⚠️ Jämförelsen går mot DATUMET, inte mot klockan. "Nu" är 14:30 och en bokning från
            // 08:00 i dag måste gå att lägga in — funktionären registrerar i efterhand samma dag.
            var ok = FirearmBookingWindow.TryNormalise(
                Now.Date.AddHours(8), Now.Date.AddHours(12), Now, out _, out _, out var error);

            Assert.True(ok);
            Assert.Null(error);
        }

        [Fact]
        public void TryNormalise_IgarNekas()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(-1), default, Now, out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("bakåt", error);
        }

        [Fact]
        public void TryNormalise_PrecisPaFramatgransenTillats()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(FirearmBookingWindow.MaxDaysAhead), default, Now, out _, out _, out var error);

            Assert.True(ok);
            Assert.Null(error);
        }

        [Fact]
        public void TryNormalise_EnDagUtanforFramatgransenNekas()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(FirearmBookingWindow.MaxDaysAhead + 1), default, Now, out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("framåt", error);
        }

        [Fact]
        public void TryNormalise_ForLangBokningNekas()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                Day(1), Day(1 + FirearmBookingWindow.MaxDurationDays).AddHours(1), Now,
                out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("högst", error);
        }

        [Fact]
        public void TryNormalise_UtanStarttidNekas()
        {
            var ok = FirearmBookingWindow.TryNormalise(
                default, Day(1), Now, out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("börjar", error);
        }

        // ── ⚠️ Överlappet: KANT-I-KANT TILLÅTS ───────────────────────────────────────────────
        // Överlämningen sker just då. Utan det kunde två pass i följd aldrig dela ett vapen —
        // vilket gör lånevapenpoolen halvt oanvändbar på en tävlingsdag.

        [Fact]
        public void Overlaps_KantIKant_KrockarInte()
        {
            var a = (from: Day(1).AddHours(9), to: Day(1).AddHours(12));
            var b = (from: Day(1).AddHours(12), to: Day(1).AddHours(15));

            Assert.False(FirearmBookingWindow.Overlaps(a.from, a.to, b.from, b.to));
            Assert.False(FirearmBookingWindow.Overlaps(b.from, b.to, a.from, a.to));
        }

        [Fact]
        public void Overlaps_EnEndaSekundsOverlapp_Krockar()
        {
            // Kontrollprov mot kant-i-kant ovan: en regel som släpper igenom allt hade klarat
            // testet över men gjort spärren verkningslös.
            var a = (from: Day(1).AddHours(9), to: Day(1).AddHours(12));
            var b = (from: Day(1).AddHours(12).AddSeconds(-1), to: Day(1).AddHours(15));

            Assert.True(FirearmBookingWindow.Overlaps(a.from, a.to, b.from, b.to));
        }

        [Fact]
        public void Overlaps_HeltInnesluten_Krockar()
        {
            Assert.True(FirearmBookingWindow.Overlaps(
                Day(1).AddHours(10), Day(1).AddHours(11),
                Day(1).AddHours(8), Day(1).AddHours(16)));
        }

        [Fact]
        public void Overlaps_HeltOmslutande_Krockar()
        {
            Assert.True(FirearmBookingWindow.Overlaps(
                Day(1).AddHours(8), Day(1).AddHours(16),
                Day(1).AddHours(10), Day(1).AddHours(11)));
        }

        [Fact]
        public void Overlaps_Symmetrisk()
        {
            var a = (from: Day(1).AddHours(9), to: Day(1).AddHours(13));
            var b = (from: Day(1).AddHours(11), to: Day(1).AddHours(15));

            Assert.Equal(
                FirearmBookingWindow.Overlaps(a.from, a.to, b.from, b.to),
                FirearmBookingWindow.Overlaps(b.from, b.to, a.from, a.to));
            Assert.True(FirearmBookingWindow.Overlaps(a.from, a.to, b.from, b.to));
        }

        [Fact]
        public void Overlaps_OlikaDagar_KrockarInte()
        {
            Assert.False(FirearmBookingWindow.Overlaps(
                Day(1), Day(1).AddDays(1).AddSeconds(-1),
                Day(2), Day(2).AddDays(1).AddSeconds(-1)));
        }

        [Fact]
        public void Overlaps_TvaHeldagarSammaDag_Krockar()
        {
            // Två medlemmar som båda bokar "hela lördagen" på samma vapen. Det normaliserade
            // fönstret slutar 23:59:59, så heldagar på samma datum MÅSTE krocka — annars är
            // heldagsbokningen en bokning som inte bokar.
            var f = Day(1);
            var t = Day(1).AddDays(1).AddSeconds(-1);

            Assert.True(FirearmBookingWindow.Overlaps(f, t, f, t));
        }

        // ── Etiketten ────────────────────────────────────────────────────────────────────────
        // Ligger i samma klass för att listan och bokningen ska beskriva samma fönster med samma
        // ord. Sa listan "hela dagen" medan bokningen visade klockslag lästes de som två fönster.

        [Fact]
        public void Label_HelaDagen_SagerHelaDagen()
        {
            FirearmBookingWindow.TryNormalise(Day(1), default, Now, out var f, out var t, out _);
            Assert.Contains("hela dagen", FirearmBookingWindow.Label(f, t));
        }

        [Fact]
        public void Label_SammaDagMedKlockslag_VisarBaraTiderna()
        {
            var label = FirearmBookingWindow.Label(Day(1).AddHours(9), Day(1).AddHours(12));

            Assert.Contains("09:00", label);
            Assert.Contains("12:00", label);
            Assert.DoesNotContain("hela dagen", label);
        }

        [Fact]
        public void Label_OverDagsgrans_VisarBadaDatumen()
        {
            var label = FirearmBookingWindow.Label(Day(1).AddHours(9), Day(3).AddHours(12));

            Assert.Contains(Day(1).ToString("yyyy-MM-dd"), label);
            Assert.Contains(Day(3).ToString("yyyy-MM-dd"), label);
        }
    }
}
