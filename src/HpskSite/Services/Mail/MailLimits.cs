namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Gränser för utskick som går genom sajtens egen e-post (Simply). EN plats, så att klubbutskick,
    /// kallelser och kursmejl inte bär var sin kopia som glider isär.
    ///
    /// <para><b>Varför en gräns fast <c>websmtp.simply.com</c> inte har någon:</b> alla klubbar och
    /// kretsar delar samma avsändare (<c>admin@pistol.nu</c>). Får den adressen ett dåligt rykte hos
    /// Gmail/Outlook hamnar ALLT sajten skickar i skräpposten — kvitton, lösenord, kallelser — för
    /// alla klubbar på en gång (Stefan 2026-10-06: "potentiellt hundratals klubbar … alla skickar
    /// mail i olika funktioner"). Större utskick hör hemma i Brevo, med avregistrering och statistik.</para>
    /// </summary>
    public static class MailLimits
    {
        /// <summary>Högsta antal mottagare i ETT utskick via sajtens e-post.</summary>
        public const int MaxRecipientsPerSend = 250;
    }
}
