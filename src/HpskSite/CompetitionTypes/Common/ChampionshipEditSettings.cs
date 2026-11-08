using HpskSite.Models.Competitions;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Mästerskapets inställningar i Redigera tävling: <b>vilka som deltar</b> och (fält) <b>vilka vapengrupper som avgör mästerskapet</b>.
    /// De hörde förut till kortet överst i Anmälningar; arrangörer söker dem där de ställer in tävlingen, bredvid omfattningen.
    ///
    /// <para>Två saker bor här så att dialogen och sparningen aldrig kan säga olika saker: <see cref="ForEditor"/> (vad dialogen får veta) och
    /// <see cref="Refusal"/> (vad en sparning får ändra). Reglerna är de som gällde kortet: vägras när listan är publicerad, finalen byggd eller
    /// en kvalgrupp låst; SM har ingen deltagarbegränsning; grannkretsar bara för kretsmästerskap; precisionsfamiljen har inget vapengruppsval.</para>
    /// </summary>
    public static class ChampionshipEditSettings
    {
        public const string ParticipantsKey = "championshipParticipants";
        public const string WeaponGroupsKey = "championshipWeaponGroups";

        /// <summary>
        /// Det Redigera tävling får veta om tävlingen. <c>available</c> = grenen använder beteendet (omfattningen avgör dialogen själv, eftersom den
        /// kan bytas innan man sparar). <c>offeredGroups</c> är tom för grenar utan vapengruppsval.
        /// </summary>
        public static object ForEditor(CompetitionInfo competition, OutOfCompetitionLockReason lockReason)
        {
            var rule = ChampionshipRule.For(competition);
            var used = competition.ClassIds.Select(ChampionshipCategory.WeaponGroupFor);
            var stored = ChampionshipWeaponGroups.Parse(competition.ChampionshipWeaponGroups);
            return new
            {
                available = rule.DisciplineEnabled,
                participants = ChampionshipParticipantsSetting.Normalize(competition.ChampionshipParticipants) ?? ChampionshipParticipantsSetting.All,
                weaponGroups = stored.ToArray(),
                offeredGroups = ChampionshipWeaponGroups.OfferedFor(competition.CompetitionType, used, stored).ToArray(),
                lockReason = lockReason.ToString(),
                isPrecisionFamily = OutOfCompetitionLock.IsPrecisionFamily(competition.TypeId)
            };
        }

        /// <summary>
        /// Prövar en sparning av Redigera tävling. Null = gå vidare. Bara det som faktiskt ändras prövas: en tävling som redan är låst kan
        /// sparas som vanligt så länge inställningarna inte rörs (dialogen skickar dem bara när de gäller och inte är låsta, men en annan
        /// klient kan skicka dem — och skriver då aldrig över ett låst läge).
        /// </summary>
        /// <param name="saved">Tävlingen som den är sparad.</param>
        /// <param name="fields">Fälten som sparningen skickar (nycklarna <c>competitionScope</c>, <c>championshipParticipants</c>, <c>championshipWeaponGroups</c>).</param>
        /// <param name="disciplineEnabled">Bara för test: tvingar svaret på "använder grenen beteendet" (null = sajtinställningen avgör).</param>
        public static string? Refusal(CompetitionInfo saved, IDictionary<string, object>? fields, OutOfCompetitionLockReason lockReason,
            bool? disciplineEnabled = null)
        {
            if (fields == null) return null;
            var sentParticipants = TryGet(fields, ParticipantsKey, out var pText);
            var sentGroups = TryGet(fields, WeaponGroupsKey, out var gText);
            if (!sentParticipants && !sentGroups) return null;

            var savedParticipants = ChampionshipParticipantsSetting.Normalize(saved.ChampionshipParticipants);
            var savedGroups = ChampionshipWeaponGroups.Canonicalize(saved.ChampionshipWeaponGroups);
            var participants = sentParticipants ? ChampionshipParticipantsSetting.Normalize(pText) : savedParticipants;
            var groups = sentGroups ? ChampionshipWeaponGroups.Canonicalize(gText) : savedGroups;

            var valuesChanged = participants != savedParticipants || groups != savedGroups;

            // Reglerna prövas mot omfattningen som sparningen ger, inte den gamla (arrangören byter ofta båda i samma dialog). Ett byte av
            // omfattning prövar reglerna även när värdena står kvar (t.ex. "bara egna" kvar på ett tävling som blir SM), men spärren frågar
            // bara om värdena ändras.
            var scope = TryGet(fields, "competitionScope", out var scopeText) ? scopeText : saved.CompetitionScope;
            var scopeChanged = !string.Equals((scope ?? "").Trim(), (saved.CompetitionScope ?? "").Trim(), StringComparison.Ordinal);
            if (!valuesChanged && !scopeChanged) return null;   // inget ändras
            var rule = ChampionshipRule.For(new CompetitionInfo
            {
                CompetitionScope = scope,
                CompetitionType = saved.CompetitionType,
                ChampionshipWeaponGroups = groups
            });

            if (!(disciplineEnabled ?? rule.DisciplineEnabled))
                return "Inställningarna för mästerskapet gäller inte den här grenen.";

            var precisionFamily = OutOfCompetitionLock.IsPrecisionFamily(saved.TypeId);
            switch (valuesChanged ? lockReason : OutOfCompetitionLockReason.None)
            {
                case OutOfCompetitionLockReason.ResultsPublished:
                    return precisionFamily
                        ? "Resultatlistan är publicerad. Avpublicera den först på Resultat-fliken, ändra, och publicera igen."
                        : "Resultatlistan är publicerad. Avpublicera den först, eller rätta enskilda skyttar med \"Rätta mästerskapet\".";
                case OutOfCompetitionLockReason.FinalsBuilt:
                    return "Finalstartlistan är redan byggd. Vilka som deltar och vilka vapengrupper som avgör mästerskapet kan inte ändras nu. Bygg om finalstartlistan först.";
                case OutOfCompetitionLockReason.QualifyingFrozen:
                    return "En kvalgrupp är låst inför finalen. Lås upp gruppen först, så att finalisterna räknas om efter ändringen.";
            }

            if (participants != null)
            {
                if (!rule.IsChampionship) return "Vilka som deltar kan bara anges för ett mästerskap.";
                if (rule.Kind == ChampionshipScopeKind.Sm) return "I ett svenskt mästerskap deltar alla; där finns inga automatiska förslag.";
                if (participants == ChampionshipParticipantsSetting.OwnPlusNeighbours && rule.Kind != ChampionshipScopeKind.Krets)
                    return "Angränsande kretsar kan bara väljas för kretsmästerskap.";
            }

            if (groups != null)
            {
                if (precisionFamily) return "Vapengrupper kan inte väljas för den här grenen — mästerskapet gäller alla vapengrupper.";
                if (!rule.IsChampionship) return "Vapengrupper kan bara anges för ett mästerskap.";
            }

            return null;
        }

        private static bool TryGet(IDictionary<string, object> fields, string key, out string? text)
        {
            foreach (var kv in fields)
            {
                if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                text = kv.Value?.ToString();
                return true;
            }
            text = null;
            return false;
        }
    }
}
