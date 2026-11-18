namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// De lagrade värdena för arrangörens medaljindelning (<c>Competition.MedalGroupingMode</c>). Kolumnen har ett CHECK-villkor på
    /// exakt de här tre — ett annat värde ger 500 vid sparning — så ALLT som skrivs går genom <see cref="Normalize"/>
    /// (okänt/tomt blir null = "följ <c>medalsPerWeaponGroup</c>", alltså dagens beteende).
    ///
    /// <para><b>Vad värdena betyder</b> (plan <c>notes/medaljindelning-egna-regler-plan-v2-2026-11-18.md</c>):
    /// <c>shb</c> = förbundets mästerskapsklasser (A1–A3 är "A", C delas i fem); <c>weapongroup</c> = en uppsättning medaljer per
    /// vapengrupp ("totalen"); <c>class</c> = per klass, så som resultatlistan visar klasserna (sammanslagna klasser är EN grupp).</para>
    /// </summary>
    public static class MedalGroupingModes
    {
        public const string Shb = "shb";
        public const string WeaponGroup = "weapongroup";
        public const string Class = "class";

        public static readonly IReadOnlyList<string> All = new[] { Shb, WeaponGroup, Class };

        /// <summary>Det kanoniska värdet (trimmat, gemener), eller null för tomt/okänt.</summary>
        public static string? Normalize(string? value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return All.Contains(v) ? v : null;
        }
    }

    /// <summary>
    /// Hur en oavgjord medaljplats avgörs (<c>Competition.MedalTieRule</c>): null = särskjutning (som idag), <c>shared</c> = delad plats,
    /// <c>series</c> = serieräkningen avgör. Klubben väljer (Stefan 2026-11-18). Kolumnen har ett CHECK-villkor; skriv bara via <see cref="Normalize"/>.
    /// </summary>
    public static class MedalTieRules
    {
        public const string ShootOff = "shootoff";
        public const string Shared = "shared";
        public const string Series = "series";

        public static readonly IReadOnlyList<string> All = new[] { ShootOff, Shared, Series };

        public static string? Normalize(string? value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return All.Contains(v) ? v : null;
        }
    }
}
