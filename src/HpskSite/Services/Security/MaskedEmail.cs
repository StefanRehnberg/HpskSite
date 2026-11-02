namespace HpskSite.Services.Security
{
    /// <summary>
    /// Döljer det mesta av en e-postadress där den visas för någon som bara har en länk (säkerhetsplanen
    /// 2026-11-02): "anna.svensson@gmail.com" → "a***@gmail.com". Personen känner igen sin egen adress, men
    /// en utomstående får inte hela.
    /// </summary>
    public static class MaskedEmail
    {
        public static string Mask(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return string.Empty;
            var at = email.IndexOf('@');
            if (at <= 0) return "***";
            return email[0] + "***" + email.Substring(at);
        }
    }
}
