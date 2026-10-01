using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Signerade, tidsbegränsade länkar för kretsgranskningen.
    ///
    /// <para><b>Två sorter, och de får aldrig gå att byta mot varandra</b> — därför två skilda
    /// <c>purpose</c>-strängar. En länk för att bekräfta ett uppdrag ska inte kunna användas som en
    /// länk för att godkänna en resultatlista, och tvärtom.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Uppdragsförfrågan</b> (30 dagar): "Kalle vill bli resultatgranskare i Halland".
    /// Bekräftaren loggar ändå in — länken bär bara VAD som ska bekräftas, aldrig rätten.</item>
    /// <item><b>Ärendelänk</b> (60 dagar, samma som svarslänken): ett enda ärende i länkläget, för en
    /// krets som inte utsett någon i rollen. Bär ärendets kontrollsumma, så att länken slutar gälla
    /// när det granskade ändras (<see cref="CaseLinkPayload.Checksum"/>).</item>
    /// </list>
    ///
    /// <para><b>⚠️ Purpose-strängarna är en del av signaturen.</b> Ändras de slutar varje utskickad
    /// länk att gälla, utan felmeddelande. Höj versionssuffixet om formen måste ändras.</para>
    /// </summary>
    public class KretsLinkTokenService
    {
        public const string UppdragRequestPurpose = "Kretsgranskning.UppdragRequest.v1";
        public const string CaseLinkPurpose = "Kretsgranskning.CaseLink.v1";

        public static readonly TimeSpan UppdragRequestLifetime = TimeSpan.FromDays(30);
        public static readonly TimeSpan CaseLinkLifetime = TimeSpan.FromDays(60);

        private readonly ITimeLimitedDataProtector _request;
        private readonly ITimeLimitedDataProtector _case;

        public KretsLinkTokenService(IDataProtectionProvider provider)
        {
            _request = provider.CreateProtector(UppdragRequestPurpose).ToTimeLimitedDataProtector();
            _case = provider.CreateProtector(CaseLinkPurpose).ToTimeLimitedDataProtector();
        }

        // ── Uppdragsförfrågan ──────────────────────────────────────────────────────────────

        public string CreateUppdragRequest(int regionId, int memberId, string roleKey)
            => _request.Protect(
                JsonSerializer.Serialize(new UppdragRequestPayload(regionId, memberId, roleKey, DateTime.Now)),
                UppdragRequestLifetime);

        /// <summary>Null för en ogiltig, manipulerad eller utgången länk — skillnaden visas inte.</summary>
        public UppdragRequestPayload? ReadUppdragRequest(string? token)
            => Read<UppdragRequestPayload>(_request, token);

        // ── Ärendelänk (länkläget, faserna 2–4) ─────────────────────────────────────────────

        public string CreateCaseLink(string caseKind, int caseId, int regionId, string checksum, string sentTo)
            => _case.Protect(
                JsonSerializer.Serialize(new CaseLinkPayload(caseKind, caseId, regionId, checksum, sentTo, DateTime.Now)),
                CaseLinkLifetime);

        /// <summary>
        /// Läser en ärendelänk. Null för en ogiltig eller utgången länk. ATT KONTROLLSUMMAN
        /// fortfarande stämmer är anroparens fråga — bara ärendet vet sin nuvarande summa.
        /// </summary>
        public CaseLinkPayload? ReadCaseLink(string? token)
            => Read<CaseLinkPayload>(_case, token);

        private static T? Read<T>(ITimeLimitedDataProtector protector, string? token) where T : class
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                var json = protector.Unprotect(token, out _);
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (CryptographicException) { return null; }
            catch (JsonException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    public record UppdragRequestPayload(int RegionId, int MemberId, string RoleKey, DateTime RequestedAt);

    public record CaseLinkPayload(string CaseKind, int CaseId, int RegionId, string Checksum, string SentTo, DateTime CreatedAt);
}
