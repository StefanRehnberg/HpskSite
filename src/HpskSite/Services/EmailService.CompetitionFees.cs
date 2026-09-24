using System.Net;
using HpskSite.Models.Ledger;
using HpskSite.Services.Mail;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejlen för tävlingsavgifterna i den nya modellen (P3/P4).
    ///
    /// <para><b>⚠️ Svarsadressen är ARRANGÖREN</b> (<c>mail-reply-to-required-on-core-path</c>): den
    /// som får mejlet ska kunna svara den som tar emot pengarna, aldrig pistol.nu.</para>
    /// </summary>
    public partial class EmailService
    {
        /// <summary>
        /// Betalningsuppgifterna för en tävlingsavgift — Swish och/eller bankgiro, med referensen.
        /// <para>⚠️ Referensen står i klartext och ska skrivas exakt: det är så arrangören ser vilken
        /// anmälan pengarna gäller. En betalning utan den går inte att pricka av.</para>
        /// </summary>
        public async Task<bool> SendCompetitionFeeCodeAsync(
            string toEmail, string toName, string competitionName, string organiserName,
            string? swishNumber, string? bankgiro, decimal amount, string reference, string? organiserEmail)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return false;
            string E(string? s) => WebUtility.HtmlEncode(s ?? "");

            var rows = "";
            if (!string.IsNullOrWhiteSpace(swishNumber))
                rows += $@"<tr><td><strong>Swish-nummer</strong></td><td>{E(swishNumber)}</td></tr>";
            if (!string.IsNullOrWhiteSpace(bankgiro))
                rows += $@"<tr><td><strong>Bankgiro</strong></td><td>{E(bankgiro)}</td></tr>";

            var body = $@"
<p>Hej {E(toName)},</p>
<p>Här är uppgifterna för att betala anmälan till <strong>{E(competitionName)}</strong>.</p>
<table cellpadding=""6"" style=""border-collapse:collapse"">
  {rows}
  <tr><td><strong>Belopp</strong></td><td>{amount:0.00} kr</td></tr>
  <tr><td><strong>Meddelande / referens</strong></td><td>{E(reference)}</td></tr>
</table>
<p>Skriv referensen exakt som den står — det är så {E(organiserName)} ser vilken anmälan betalningen gäller.</p>
<p style=""color:#666;font-size:.9em"">
  När du har betalat: öppna tävlingen på pistol.nu och tryck <em>Jag har betalat</em>,
  så vet arrangören att pengarna är på väg. Kvittot får du när arrangören tagit emot betalningen.</p>";

            return await SendEmailAsync(toEmail, $"Betalning för {competitionName}", body,
                MailReplyTo.FromClub(organiserName, organiserEmail));
        }

        /// <summary>
        /// Fakturan till en klubb, med en länk som fungerar <b>utan inloggning</b>.
        /// <para>Klubbens kassör ska aldrig behöva hitta en klubbsida eller förstå begreppet
        /// samlingsfaktura (<c>organiser-side-consolidation</c>) — mejlet är räkningen.</para>
        /// </summary>
        public async Task<bool> SendClubInvoiceAsync(string toEmail, LedgerCharge charge, string documentUrl, string? organiserEmail)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return false;
            string E(string? s) => WebUtility.HtmlEncode(s ?? "");

            var lines = string.Join("", charge.Lines.Select(l =>
                $@"<tr><td>{E(l.Description)}</td><td style=""text-align:right"">{l.Amount:0.00} kr</td></tr>"));

            var pay = "";
            if (!string.IsNullOrWhiteSpace(charge.IssuerBankgiro))
                pay += $@"<tr><td><strong>Bankgiro</strong></td><td>{E(charge.IssuerBankgiro)}</td></tr>";
            if (!string.IsNullOrWhiteSpace(charge.IssuerSwish))
                pay += $@"<tr><td><strong>Swish</strong></td><td>{E(charge.IssuerSwish)}</td></tr>";

            var body = $@"
<p>Hej,</p>
<p>{E(charge.IssuerName)} skickar här faktura <strong>{E(charge.NumberText)}</strong> till
   <strong>{E(charge.RecipientName)}</strong> för anmälningar till <strong>{E(charge.SourceName)}</strong>.</p>
<table cellpadding=""6"" style=""border-collapse:collapse;min-width:320px"">
  {lines}
  <tr><td><strong>Att betala</strong></td><td style=""text-align:right""><strong>{charge.Amount:0.00} kr</strong></td></tr>
</table>
<table cellpadding=""6"" style=""border-collapse:collapse;margin-top:12px"">
  {pay}
  <tr><td><strong>Referens</strong></td><td>{E(charge.Reference)}</td></tr>
  <tr><td><strong>Förfallodag</strong></td><td>{charge.DueDate:yyyy-MM-dd}</td></tr>
</table>
<p>Ange referensen vid betalningen. <a href=""{E(documentUrl)}"">Öppna fakturan</a> för att skriva ut den
   eller se om den är betald — länken fungerar utan inloggning.</p>
{(string.IsNullOrWhiteSpace(charge.Note) ? "" : $"<p>{E(charge.Note)}</p>")}";

            return await SendEmailAsync(toEmail,
                $"Faktura {charge.NumberText} från {charge.IssuerName} — {charge.SourceName}", body,
                MailReplyTo.FromClub(charge.IssuerName, organiserEmail ?? charge.IssuerEmail));
        }
    }
}
