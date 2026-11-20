using Microsoft.AspNetCore.DataProtection;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Länken "Jag kan inte, tacka nej" i mejlet till en utsedd tävlingsledare (Stefans beslut 2026-11-19, ett medvetet undantag från regeln att en
    /// mejllänk aldrig ändrar något).
    ///
    /// <para><b>Vad länken kan:</b> bara den utsedda personens EGET utseende, och bara tacka nej. Aldrig acceptera, utse eller ändra annat. Signerad
    /// (Data Protection, egen purpose — byt aldrig strängen), tidsbegränsad, och <b>används en gång</b>: den prövar att rosterraden finns, och en
    /// avklarad nej tar bort raden — då är länken förbrukad utan en egen tabell.</para>
    /// </summary>
    public sealed class CompetitionManagerLinkService
    {
        private const string Purpose = "CompetitionManager.Decline.v1";
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);
        private readonly ITimeLimitedDataProtector _p;
        private readonly IConfiguration _config;

        public CompetitionManagerLinkService(IDataProtectionProvider dp, IConfiguration config)
        {
            _p = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector();
            _config = config;
        }

        public string SiteUrl => (_config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        public string Token(int competitionId, int memberId) => _p.Protect($"decline|{competitionId}|{memberId}", Lifetime);

        public string DeclineUrl(int competitionId, int memberId, string? baseUrl = null) =>
            $"{(string.IsNullOrWhiteSpace(baseUrl) ? SiteUrl : baseUrl.TrimEnd('/'))}/tavlingsledare/tacka-nej?t={Uri.EscapeDataString(Token(competitionId, memberId))}";

        /// <summary>Null = ogiltig, utgången eller manipulerad.</summary>
        public (int CompetitionId, int MemberId)? Parse(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                var parts = _p.Unprotect(token).Split('|');
                if (parts.Length == 3 && parts[0] == "decline" && int.TryParse(parts[1], out var c) && int.TryParse(parts[2], out var m) && c > 0 && m > 0)
                    return (c, m);
            }
            catch { /* ogiltig eller utgången */ }
            return null;
        }
    }
}
