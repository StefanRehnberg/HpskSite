using System;

namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Vart ett svar på ett utgående mejl ska landa.
    ///
    /// <para><b>⚠️ FINNS FÖR ATT SVAREN FÖRSVANN.</b> Sajtens kärnväg satte bara <c>From</c> till
    /// den autentiserade adressen och aldrig någon <c>Reply-To</c>. Alltså gick VARJE svar på
    /// VARJE mejl appen skickar till <c>admin@pistol.nu</c> — och ingen på klubben såg det.
    /// Rapporterat 2026-09-09: en medlem som ombetts komplettera sin föreningsintygsförfrågan
    /// svarade på mejlet så fort hen fixat uppgifterna, och klubben satt kvar med "Väntar på
    /// medlemmen" medan svaret låg hos sajtägaren.</para>
    ///
    /// <para><b>⚠️ SVARSADRESSEN ÄR MOTPARTEN I SAMTALET, inte alltid klubben.</b> Ett mejl från
    /// klubben till medlemmen ska svaras till klubben; ett mejl om medlemmens begäran till
    /// klubbens ansvariga ska svaras till MEDLEMMEN. Att alltid välja klubben hade flyttat
    /// problemet ett steg i stället för att lösa det.</para>
    ///
    /// <para><b>⚠️ From-ADRESSEN ÄNDRAS ALDRIG.</b> <c>pistol.nu</c> har
    /// <c>v=spf1 include:spf.simply.com -all</c> och DMARC <c>p=reject</c> (mätt 2026-09-09), så en
    /// klubbs egen adress i <c>From</c> skulle studsa hos varje mottagare som kontrollerar. Bara
    /// VISNINGSNAMNET och <c>Reply-To</c> speglar avsändaren. Samma regel som
    /// <c>EmailService.SendHtmlEmailAsync</c> redan följde för klubbutskicken.</para>
    /// </summary>
    public sealed class MailReplyTo
    {
        /// <summary>Hur värdet ska hanteras när mejlet byggs.</summary>
        public enum ReplyKind
        {
            /// <summary>En riktig adress: <see cref="Email"/> gäller.</summary>
            Address = 0,

            /// <summary>Sajtens egen adress. <c>EmailService</c> fyller i <c>Email:AdminEmail</c>.</summary>
            SiteAdmin = 1,

            /// <summary>Inget svar väntas. Ingen <c>Reply-To</c>, och fotnoten säger det rakt ut.</summary>
            NoReply = 2,
        }

        public ReplyKind Kind { get; }

        /// <summary>Adressen svaret går till. Tom för <see cref="ReplyKind.SiteAdmin"/> och <see cref="ReplyKind.NoReply"/>.</summary>
        public string Email { get; }

        /// <summary>Namnet som visas på svarsadressen, och i fotnoten. Får vara tomt.</summary>
        public string Name { get; }

        /// <summary>
        /// Visningsnamn i <c>From</c>, t.ex. "Vetlanda PK via Pistol.nu".
        ///
        /// <para>Null = behåll sajtens eget (<c>Email:FromName</c>). Sätts bara när mejlet
        /// verkligen är någon annans — ett besked FRÅN klubben — aldrig för sajtens egna mejl.</para>
        /// </summary>
        public string? FromDisplayName { get; }

        private MailReplyTo(ReplyKind kind, string email, string name, string? fromDisplayName)
        {
            Kind = kind;
            Email = email ?? "";
            Name = name ?? "";
            FromDisplayName = string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName.Trim();
        }

        /// <summary>
        /// Sajtens egen adress. <b>Ett medvetet val</b>, för mejl där sajtägaren verkligen är rätt
        /// mottagare av ett svar: bugrapporter, "klubben saknas", förfrågan om testaccess.
        /// </summary>
        public static readonly MailReplyTo SiteAdmin =
            new MailReplyTo(ReplyKind.SiteAdmin, "", "Pistol.nu", null);

        /// <summary>
        /// Inget svar väntas — lösenordsåterställning, kontolåsning, kvitton.
        ///
        /// <para><b>⚠️ Inte samma sak som att glömma välja.</b> Fotnoten skriver ut att mejlet inte
        /// går att svara på, så mottagaren inte sitter och väntar på ett svar som ingen läser.</para>
        /// </summary>
        public static readonly MailReplyTo NoReply =
            new MailReplyTo(ReplyKind.NoReply, "", "", null);

        /// <summary>
        /// En namngiven adress. Tom eller ogiltig adress faller tillbaka på
        /// <see cref="SiteAdmin"/> — hellre sajtägaren än ett svart hål.
        /// </summary>
        public static MailReplyTo To(string? email, string? name = null, string? fromDisplayName = null)
        {
            var addr = (email ?? "").Trim();
            if (addr.Length == 0 || !addr.Contains('@')) return SiteAdmin;
            return new MailReplyTo(ReplyKind.Address, addr, (name ?? "").Trim(), fromDisplayName);
        }

        /// <summary>
        /// Ett besked som kommer FRÅN en klubb eller krets. Sätter både visningsnamnet i
        /// <c>From</c> och svarsadressen, så mottagaren ser vem det är från innan hen öppnar.
        /// </summary>
        public static MailReplyTo FromClub(string? clubName, string? contactEmail)
        {
            var club = (clubName ?? "").Trim();
            var display = club.Length > 0 ? $"{club} via Pistol.nu" : null;
            var r = To(contactEmail, club, display);

            // ⚠️ Saknar klubben kontaktadress blir svaret sajtägarens igen — men VISNINGSNAMNET
            // ska ändå säga vem beskedet kommer från. Utan det ser ett klubbeslut ut som sajtens.
            if (r.Kind != ReplyKind.Address && display != null)
                return new MailReplyTo(ReplyKind.SiteAdmin, "", club, display);

            return r;
        }

        /// <summary>Går det att svara på det här mejlet över huvud taget?</summary>
        public bool IsReplyable => Kind != ReplyKind.NoReply;
    }
}
