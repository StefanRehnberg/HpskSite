namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vad som händer när en refresh-token visas upp (säkerhetsplanen 2026-11-02, H6).
    ///
    /// <para>Varje användning roterar token: den gamla återkallas och en ny utfärdas i samma familj. En token som
    /// visas upp IGEN efter att den roterats är antingen ett nätglapp (mobilen skickade, svaret nådde aldrig fram, appen
    /// försöker om) eller en stulen kopia. De skiljs åt med ett <b>nådefönster</b>: inom <see cref="GraceWindow"/> efter
    /// rotationen får token användas en gång till (en ny efterföljare utfärdas), därefter återkallas HELA familjen — då
    /// loggas både tjuven och den riktiga ägaren ut, och ägaren loggar in på nytt.</para>
    /// </summary>
    public static class RefreshRotationRules
    {
        public static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(30);

        public enum Outcome
        {
            /// <summary>Giltig och oanvänd: rotera (återkalla den, utfärda en ny).</summary>
            Rotate,
            /// <summary>Redan roterad men inom nådefönstret: utfärda en ny efterföljare utan att återkalla något mer.</summary>
            GraceRetry,
            /// <summary>Roterad och utanför nådefönstret — återanvändning. Återkalla hela familjen.</summary>
            ReuseDetected,
            /// <summary>Oanvänd men utgången.</summary>
            Expired
        }

        public static Outcome Decide(DateTime nowUtc, DateTime expiresAtUtc, DateTime? revokedAtUtc, DateTime? lastUsedAtUtc, bool hasReplacement)
        {
            if (revokedAtUtc != null)
            {
                var withinGrace = hasReplacement
                    && lastUsedAtUtc != null
                    && nowUtc - lastUsedAtUtc.Value <= GraceWindow
                    && nowUtc >= lastUsedAtUtc.Value - TimeSpan.FromSeconds(5);   // klockavvikelse
                return withinGrace ? Outcome.GraceRetry : Outcome.ReuseDetected;
            }
            return nowUtc >= expiresAtUtc ? Outcome.Expired : Outcome.Rotate;
        }
    }
}
