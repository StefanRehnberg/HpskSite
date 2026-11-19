namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Raden på tävlingssidan som säger skyttarna hur medaljerna delas ut — innan de anmäler sig. Ren funktion (ingen tjänst, ingen databas),
    /// delad av vyn och testerna.
    ///
    /// <para>Visas bara när tävlingens regler AVVIKER från förbundets: grenen har ytan, arrangören får välja (inte SM/landsdel) och tävlingen delar ut
    /// placeringsmedaljer med en annan indelning, ett fast antal platser eller en annan oavgjort-regel än SHB:s förval. Ett vanligt
    /// klub- eller kretsmästerskap med förbundets regler får ingen rad — där är det ingen nyhet. Samma klartext som prisutdelningen
    /// (<see cref="MedalGrouping.Describe(string?, int, string?)"/>), så sidan och ceremonin aldrig säger emot varandra.</para>
    /// </summary>
    public static class MedalRulePublicLine
    {
        /// <summary>Texten, eller null när ingen rad ska visas.</summary>
        public static string? For(Models.Competitions.CompetitionInfo? competition)
        {
            if (competition == null) return null;
            if (!MedalRuleEditSettings.SurfaceAppliesTo(competition.CompetitionType)) return null;

            var rule = ChampionshipRule.For(competition);
            if (!rule.MayChooseMedalRules || !rule.AwardsPlacementMedals) return null;

            var differsFromFederation = rule.GroupingModeName != MedalGroupingModes.Shb
                                        || rule.MedalPlaces > 0
                                        || rule.TieRule != MedalTieRules.ShootOff;
            return differsFromFederation
                ? MedalGrouping.Describe(rule.GroupingModeName, rule.MedalPlaces, rule.TieRule)
                : null;
        }
    }
}
