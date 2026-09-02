namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Behörighetsbesluten som RENA predikat — inga uppslag, inget tillstånd, ingen inloggad
    /// användare. Bara sanningstabeller.
    ///
    /// <para><b>Varför de bor här och inte inne i <see cref="FirearmAuthorizationService"/>:</b>
    /// tjänsten hänger på <c>IMemberService</c>, <c>IMemberGroupService</c>, <c>BoardRoleService</c>
    /// och <c>AdminAuthorizationService</c> — konkreta klasser med databasberoenden, alltså inte
    /// rimligt mockbara utan en gränssnittsrefaktorering. Följden hade blivit att just den mest
    /// säkerhetskritiska logiken i hela funktionen var den enda som inte gick att enhetstesta. Nu är
    /// tjänsten reducerad till att SAMLA IN de booleska svaren, och BESLUTET är prövat uttömmande.</para>
    /// </summary>
    public static class FirearmAccessRules
    {
        /// <summary>
        /// Får den här personen läsa en medlems vapeninnehav som klubbens föreningsintygsansvarige?
        ///
        /// <para><b>⚠️ Lägg ALDRIG till en sajtadmin-parameter här.</b> Frånvaron av den är löftet:
        /// driften kan tekniskt komma åt datat via databas och nyckel, men det finns ingen kodväg som
        /// renderar det. Varje annan klubbkontroll i kodbasen börjar med
        /// <c>if (IsCurrentUserAdminAsync()) return true;</c> — den här får inte göra det. En
        /// sajtadmin som ÄR styrelsemedlem med gruppen läser genom de två parametrarna nedan, precis
        /// som alla andra.</para>
        ///
        /// <para><b>⚠️ Ingen mandatparameter heller.</b> Ett utgånget mandat får inte stänga
        /// behörigheten: en styrelse sitter regelmässigt kvar från mandatets utgång till nästa
        /// årsmöte, och skulle luckan stänga läsrätten stod klubben utan läsare i just det fönstret
        /// — alltså precis det fel designen finns för att undvika. Utgånget mandat är en VARNING på
        /// adminytan, inte en spärr.</para>
        /// </summary>
        /// <param name="holdsGroupForClub">Bär <c>Foreningsintygsansvarig_{klubb}</c>.</param>
        /// <param name="hasActiveBoardSeatInSameClub">Har en aktiv styrelserad i <b>samma</b> klubb.</param>
        public static bool ViewerHasAccess(bool holdsGroupForClub, bool hasActiveBoardSeatInSameClub)
            => holdsGroupForClub && hasActiveBoardSeatInSameClub;

        /// <summary>
        /// Får den här personen utse och återkalla klubbens föreningsintygsansvariga?
        ///
        /// <para><b>⚠️ `isClubAdmin` ensamt räcker INTE, och det är hela poängen.</b>
        /// <c>IsClubAdminForClub</c> viker in klubbens KRETSADMINISTRATÖRER — det står i metodens egen
        /// dokumentation. Utan kravet på ett styrelseuppdrag i samma klubb kunde alltså en kretsadmin
        /// utan uppdrag i klubben utse den som får läsa klubbens medlemmars vapeninnehav. Att bara
        /// servera klubbformen är det fel den här kodbasen gjort fyra separata gånger.</para>
        ///
        /// <para>Sajtadmin får utse UTAN styrelseuppdrag: det är den kvarvarande vägen när en klubb
        /// blivit utan läsare och ingen kvarvarande styrelsemedlem också är klubbadmin. <b>Att utse
        /// är inte att läsa</b> — jämför <see cref="ViewerHasAccess"/>, som inte har parametern.</para>
        /// </summary>
        public static bool CanAssign(bool isSiteAdmin, bool isClubAdmin, bool hasActiveBoardSeatInSameClub)
            => isSiteAdmin || (isClubAdmin && hasActiveBoardSeatInSameClub);

        /// <summary>
        /// Ska en borttagning ur styrelsen varna för att klubben blir UTAN läsare?
        /// Bara när personen faktiskt är läsare och är den sista — att en av tre tas bort är ingen
        /// händelse, och en varning som kommer varje gång slutar läsas.
        /// </summary>
        public static bool RemovalWouldLeaveClubWithoutViewer(bool isViewer, int activeViewersBeforeRemoval)
            => isViewer && activeViewersBeforeRemoval <= 1;
    }
}
