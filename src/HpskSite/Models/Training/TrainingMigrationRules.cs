using System.Globalization;

namespace HpskSite.Models.Training
{
    /// <summary>
    /// Fas B3: vilka gamla <c>clubSimpleEvent</c>-händelser som blir en <see cref="ClubTraining"/>,
    /// och hur händelsens datum blir träningens dag + klockslag. Rena funktioner — ingen databas,
    /// inget innehållsträd — så att regeln som avgör vad som flyttas går att pröva utan att flytta
    /// något.
    ///
    /// <para><b>Beslut 2026-10-05 (Stefan):</b> typen <c>Träning</c> migreras, och grennamnen
    /// <c>Duell</c>, <c>Precision</c>, <c>Fältskjutning</c> och <c>IPSC Handgun PCC</c> migreras som
    /// träning MED grenen satt. <c>Utbildning</c> och allt annat står kvar som händelse.</para>
    /// </summary>
    public static class TrainingMigrationRules
    {
        /// <summary>Utfallet för ett eventType-värde.</summary>
        public sealed record Classification(bool Migrate, string? Discipline, string Reason);

        /// <summary>
        /// Avgör om en händelse med det här eventType-värdet ska bli en träning.
        ///
        /// <para>⚠️ <b>Matchningen är EXAKT</b> (skiftlägesokänslig, trimmad) för grennamnen —
        /// aldrig en delsträng. "Precisionskurs för nybörjare" är en utbildning, inte en
        /// precisionsträning, och ett felaktigt "ja" här flyttar en händelse ur kalendern utan
        /// att någon bett om det. Bara ordet träning matchas som delsträng ("Träning",
        /// "Klubbträning", "Träning precision"), eftersom alla stavningar av det betyder samma sak.</para>
        /// </summary>
        public static Classification Classify(string? eventType)
        {
            var value = (eventType ?? "").Trim();
            if (value.Length == 0)
                return new Classification(false, null, "Saknar typ — står kvar som händelse");

            if (value.Contains("träning", StringComparison.OrdinalIgnoreCase)
                || value.Contains("traning", StringComparison.OrdinalIgnoreCase))
                return new Classification(true, null, "Träning");

            foreach (var (name, discipline) in DisciplineTypes)
                if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return new Classification(true, discipline, $"Grenen {name}");

            return new Classification(false, null, $"Typen \"{value}\" står kvar som händelse");
        }

        /// <summary>
        /// Grennamn som förekommer som eventType i prod (torrkörningen 2026-10-05) och deras
        /// kanoniska disciplin-id. <b>IPSC har ingen gren hos oss</b> — den migreras som träning
        /// utan gren (null), och namnet bär IPSC.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, string? Discipline)> DisciplineTypes = new[]
        {
            ("Precision", (string?)"Precision"),
            ("Duell", "Duell"),
            ("Fältskjutning", "Faltskytte"),
            ("Fältskytte", "Faltskytte"),
            ("IPSC Handgun PCC", null),
        };

        /// <summary>
        /// Dag + klockslag ur händelsens start- och slutdatum.
        ///
        /// <para>⚠️ <b>00:00 är "inget klockslag", aldrig midnatt.</b> En händelse utan tid lagras
        /// med datumet ensamt; att skriva <c>StartTime = "00:00"</c> hade gjort en riktig påminnelse
        /// kvällen före av en tid ingen angett. Sluttiden tas bara när slutet ligger SAMMA dag —
        /// en träning är en kväll, och en sluttid på en annan dag går inte att uttrycka med ett
        /// klockslag (den rapporteras i stället).</para>
        /// </summary>
        public static (DateTime Date, string? StartTime, string? EndTime, bool EndDropped) SplitTimes(
            DateTime start, DateTime? end)
        {
            var date = start.Date;
            var startTime = start.TimeOfDay == TimeSpan.Zero
                ? null
                : start.ToString("HH:mm", CultureInfo.InvariantCulture);

            string? endTime = null;
            var dropped = false;
            if (end.HasValue && end.Value.Year > 1900)
            {
                if (end.Value.Date == date && end.Value.TimeOfDay != TimeSpan.Zero && end.Value > start)
                    endTime = end.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
                else if (end.Value.Date != date)
                    dropped = true;
            }
            return (date, startTime, endTime, dropped);
        }
    }
}
