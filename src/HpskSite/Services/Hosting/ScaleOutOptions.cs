namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Konfigurationen <c>ScaleOut</c> (skalplanen D4, 2026-10-19): det som behövs för att köra sajten på mer
    /// än en instans. <b>Utan sektion beter sig sajten exakt som förut</b> — en server, Umbracos egen
    /// serverregistrering, adresser ur förfrågan, ingen Redis.
    ///
    /// <code>
    /// "ScaleOut": {
    ///   "ServerRole": "SchedulingPublisher",       // eller "Subscriber"; tomt = som förut
    ///   "PublicBaseUrl": "https://pistol.nu",       // tomt = adresser ur förfrågan, som förut
    ///   "ForwardedHeaders": { "Enabled": true, "KnownProxies": [ "10.0.0.4" ], "KnownNetworks": [ "10.0.0.0/16" ] }
    /// },
    /// "ConnectionStrings": { "redis": "host:6379,password=…,ssl=True" }   // tomt = ingen Redis
    /// </code>
    /// </summary>
    public class ScaleOutOptions
    {
        public const string SectionName = "ScaleOut";

        /// <summary>
        /// "SchedulingPublisher" = den här instansen kör de schemalagda jobben (påminnelser, svep, utskick);
        /// "Subscriber" = den gör det inte. Tomt = som förut: allt körs här, och Umbraco väljer roll själv.
        /// </summary>
        public string? ServerRole { get; set; }

        /// <summary>Sajtens adress utåt. Tomt = byggs ur förfrågan (Scheme + Host), som förut.</summary>
        public string? PublicBaseUrl { get; set; }

        public ForwardedHeadersSection ForwardedHeaders { get; set; } = new();

        public sealed class ForwardedHeadersSection
        {
            /// <summary>Läs X-Forwarded-For/-Proto/-Host — bara bakom en lastbalanserare.</summary>
            public bool Enabled { get; set; }
            public string[] KnownProxies { get; set; } = Array.Empty<string>();
            public string[] KnownNetworks { get; set; } = Array.Empty<string>();
        }

        public const string Publisher = "SchedulingPublisher";
        public const string Subscriber = "Subscriber";

        /// <summary>Är rollen uttryckligen satt? Annars beter sig allt som förut.</summary>
        public bool RoleConfigured =>
            string.Equals(ServerRole, Publisher, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ServerRole, Subscriber, StringComparison.OrdinalIgnoreCase);
    }
}
