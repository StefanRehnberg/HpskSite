using HpskSite.Models;
using HpskSite.Models.Ledger;
using NPoco;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Delar ut luckfria nummer ur en <see cref="LedgerNumberSeries"/>.
    ///
    /// <para><b>⚠️⚠️ VARFÖR INTE IDENTITY.</b> En <c>IDENTITY</c>-kolumn lämnar hål: numret
    /// konsumeras när raden försöker skrivas, och en rollback lämnar luckan kvar. För ett
    /// löpnummer i en tabell spelar det ingen roll — för ett <b>verifikationsnummer</b> är
    /// luckfrihet ett legalt krav, och ett hål är exakt det en revisor letar efter som tecken på
    /// att en post tagits bort. Därför en räknare i en egen rad, uppräknad med <c>UPDLOCK</c>.</para>
    ///
    /// <para><b>⚠️ ANROPAS INUTI SAMMA TRANSAKTION SOM VERIFIKATIONEN SKRIVS.</b> Metoderna tar en
    /// <see cref="IDatabase"/> i stället för att öppna en egen — delar de inte transaktion är
    /// garantin borta: numret skulle kunna delas ut och sedan gå förlorat när insert:en rullas
    /// tillbaka.</para>
    ///
    /// <para><b>⚠️ FÖLJDEN ATT LEVA MED: serien serialiserar bokföringen per utställare.</b>
    /// <c>UPDLOCK</c> hålls till transaktionen committar, så två samtidiga bokföringar för samma
    /// klubb köar. Det är oproblematiskt i volym — en klubb bokför inte hundra verifikationer i
    /// sekunden — men det betyder att <b>inget långsamt får hållas öppet inuti transaktionen</b>:
    /// inget mejlutskick, ingen filskrivning, ingen Umbraco-publicering. Bygg den ordningen redan
    /// i bokföringstjänsten: räkna ut allt, öppna transaktionen, allokera, skriv, committa, och
    /// gör biverkningarna efteråt.</para>
    ///
    /// <para>Sista försvaret är ändå databasen: <c>UX_LedgerJournalEntry_SeriesNumber</c> gör en
    /// dublett omöjlig även om den här klassen en dag får en bugg.</para>
    /// </summary>
    public class LedgerNumberAllocator
    {
        /// <summary>
        /// Tar nästa nummer ur serien och räknar upp den, i anroparens transaktion.
        ///
        /// <para>En rad per (utställare, år, slag) skapas vid behov — en förening som bokför sin
        /// första post ska inte behöva en uppsättningsdialog först.</para>
        /// </summary>
        /// <param name="db">Databasen MED en öppen transaktion. Se klassens varning.</param>
        /// <param name="issuerType">Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</param>
        /// <param name="issuerId">Klubbens eller kretsens nod-id.</param>
        /// <param name="year">Räkenskapsåret serien tillhör.</param>
        /// <param name="kind">Ur <see cref="LedgerSeriesKind"/>.</param>
        public (int SeriesId, int Number, string Prefix) Allocate(
            IDatabase db,
            int issuerType,
            int issuerId,
            int year,
            string kind)
        {
            EnsureSeries(db, issuerType, issuerId, year, kind);

            // UPDATE ... OUTPUT deleted i ETT uttryck: läsningen och uppräkningen kan inte glida
            // isär, och raden är låst tills anroparens transaktion committar. Ett SELECT följt av
            // ett UPDATE hade gett två samtidiga bokföringar samma nummer.
            // ⚠⚠ NPOCO-FÄLLAN: Fetch<T> genererar "SELECT * FROM <T>" när SQL:en inte BÖRJAR med
            // SELECT — och den här börjar med UPDATE. Följden var
            // "Invalid object name SeriesAllocation": NPoco frågade efter en tabell uppkallad
            // efter POCO:n. Samma fälla som MemberMergeService gick i.
            //
            // ⚠️ Att det aldrig märkts beror på att raden ALDRIG KÖRTS: ingen förening hade
            // liggare, så Confirm vägrade långt innan numret skulle tilldelas. Både kvitto- och
            // verifikationsnummer gick genom den här metoden, alltså var hela numreringen otestad.
            // EnableAutoSelect sitter pa NPocos konkreta Database, inte pa IDatabase.
            var npoco = db as Database;
            if (npoco == null)
            {
                // ⚠️ Hellre hogljutt an tyst fel nummer. Kan vi inte stanga av autoselect vet vi
                // inte vilken fraga som gar till servern, och ett nummer vi inte kan garantera ar
                // varre an inget nummer — samma regel som raden nedan redan foljer.
                throw new InvalidOperationException(
                    "Nummertilldelningen kraver NPocos Database for att kunna stanga av autoselect.");
            }

            var autoSelect = npoco.EnableAutoSelect;
            npoco.EnableAutoSelect = false;
            List<SeriesAllocation> rows;
            try
            {
                rows = db.Fetch<SeriesAllocation>(
                LedgerSchema.Sql(issuerId, @"UPDATE dbo.LedgerNumberSeries
                     SET NextNumber = NextNumber + 1
                  OUTPUT deleted.Id AS SeriesId, deleted.NextNumber AS Number, deleted.Prefix AS Prefix
                  WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2 AND Kind = @3"),
                issuerType, issuerId, year, kind);
            }
            finally
            {
                // Återställ ALLTID — databasen är delad inom requesten, och en kvarglömd
                // avstängd autoselect hade tyst brutit varje annan Fetch efter den här.
                npoco.EnableAutoSelect = autoSelect;
            }

            if (rows.Count != 1)
            {
                // Kan bara inträffa om serien försvann mellan EnsureSeries och hit. Att kasta är
                // rätt: ett nummer vi inte kan garantera är värre än inget nummer.
                throw new InvalidOperationException(
                    $"Kunde inte tilldela nummer ur serien ({issuerType}, {issuerId}, {year}, {kind}).");
            }

            return (rows[0].SeriesId, rows[0].Number, rows[0].Prefix ?? "");
        }

        /// <summary>
        /// Numret så som det ska stå på en handling: prefix + nummer, t.ex. <c>V-214</c>.
        /// <para>Prefixet är föreningens eget val (F1) — en klubb som samtidigt kör ett annat
        /// bokföringsprogram låter sina pistol.nu-verifikationer börja med en egen bokstav, så att
        /// de två serierna kan samexistera.</para>
        /// </summary>
        public static string Format(string prefix, int number)
            => string.IsNullOrWhiteSpace(prefix) ? number.ToString() : $"{prefix}-{number}";

        /// <summary>
        /// Skapar serien om den saknas. Tyst om den redan finns.
        /// <para>Skrivningen är guardad av <c>UX_LedgerNumberSeries_Issuer</c>: två samtidiga
        /// förstagångsbokföringar kan båda nå hit, och den som förlorar kapplöpningen ska bara
        /// fortsätta med den rad vinnaren skapade — inte fela.</para>
        /// </summary>
        private void EnsureSeries(IDatabase db, int issuerType, int issuerId, int year, string kind)
        {
            var exists = db.ExecuteScalar<int>(
                LedgerSchema.Sql(issuerId, @"SELECT COUNT(1) FROM dbo.LedgerNumberSeries
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2 AND Kind = @3"),
                issuerType, issuerId, year, kind);

            if (exists > 0) return;

            try
            {
                db.Execute(
                    LedgerSchema.Sql(issuerId, @"INSERT INTO dbo.LedgerNumberSeries (IssuerType, IssuerId, Year, Kind, Prefix, NextNumber)
                      VALUES (@0, @1, @2, @3, @4, 1)"),
                    issuerType, issuerId, year, kind, DefaultPrefix(kind));
            }
            catch (Exception)
            {
                // Förlorad kapplöpning mot det unika indexet. Serien finns nu — det var allt vi
                // ville. Kastar Allocate ändå efteråt är det ett verkligt fel och syns där.
                if (db.ExecuteScalar<int>(
                        LedgerSchema.Sql(issuerId, @"SELECT COUNT(1) FROM dbo.LedgerNumberSeries
                           WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2 AND Kind = @3"),
                        issuerType, issuerId, year, kind) == 0)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Förvalt prefix. Föreningen kan ändra det — men bara innan serien tagits i bruk, annars
        /// får två handlingar i samma serie olika utseende.
        /// </summary>
        private static string DefaultPrefix(string kind)
            => kind switch
            {
                LedgerSeriesKind.Receipt => "K",
                LedgerSeriesKind.Invoice => "F",
                _ => "V"
            };

        private class SeriesAllocation
        {
            public int SeriesId { get; set; }
            public int Number { get; set; }
            public string? Prefix { get; set; }
        }
    }
}
