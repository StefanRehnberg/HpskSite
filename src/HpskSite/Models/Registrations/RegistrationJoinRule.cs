namespace HpskSite.Models.Registrations
{
    /// <summary>
    /// "Måste gå i samma patrull som" (fältskytte): en klassanmälan pekar på en annan medlem i samma klubb. Reglerna ligger här, på ETT ställe, och körs av servern
    /// vid skyttens anmälan, vid anmälan åt någon annan och vid adminens redigering. Klienten är aldrig enda spärren.
    /// Kravet gäller vapengruppen (alla C är samma grupp): den valda måste ha en aktiv anmälan i samma vapengrupp. Ingen licenskontroll.
    /// </summary>
    public static class RegistrationJoinRule
    {
        /// <summary>Högst så många som kan peka på samma person i samma vapengrupp (annars fyller en person en hel patrull).</summary>
        public const int MaxFollowersPerPartner = 2;

        /// <summary>Vapengruppen (C/B/A/R) för en klass; okänd klass ger klassens eget id.</summary>
        public static string GroupOf(string? classId)
        {
            var code = ShootingClasses.GetWeaponClassCode(classId ?? "");
            return string.IsNullOrEmpty(code) ? (classId ?? "") : code;
        }

        /// <summary>
        /// Felmeddelande på svenska med namn, eller null när valet är giltigt.
        /// <paramref name="all"/> är tävlingens alla anmälningar (den egna får finnas med). <paramref name="clubOf"/> ger anmälans klubb ("Tävlar för"), inte medlemmens huvudklubb.
        /// </summary>
        public static string? Check(RegistrationRecord self, string classId, int joinMemberId, IEnumerable<RegistrationRecord> all,
            Func<RegistrationRecord, int> clubOf, Func<int, string> nameOf)
        {
            var group = GroupOf(classId);
            var name = nameOf(joinMemberId);
            if (joinMemberId <= 0) return "Välj en medlem.";
            if (joinMemberId == self.MemberId) return "Du kan inte välja dig själv.";
            var others = all.Where(r => r.IsActive && r.MemberId != self.MemberId).ToList();
            var partner = others.FirstOrDefault(r => r.MemberId == joinMemberId);
            if (partner == null || !partner.Classes.Any(c => GroupOf(c.Class) == group))
                return $"{name} är inte anmäld i klass {group}. Be personen anmäla sig först.";
            if (clubOf(partner) != clubOf(self))
                return $"{name} tävlar för en annan klubb än du. Du kan bara gå med någon i samma klubb.";
            var followers = others.Sum(r => r.Classes.Count(c => c.JoinMemberId == joinMemberId && GroupOf(c.Class) == group));
            if (followers >= MaxFollowersPerPartner)
                return $"{name} går redan med {followers} andra i klass {group}. Välj någon annan.";
            return null;
        }
    }
}
