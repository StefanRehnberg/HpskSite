using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Identiteten för en tabell som tagit över Umbraco-noder med BEVARADE id:n (sajtstrukturen).
    ///
    /// <para><b>⚠️ Nästa id ska ligga över varje id som NÅGON pekar på</b> — inte bara över tabellens eget.
    /// Borttagna anmälningar och händelser har lämnat rader kvar som bär deras id (liggaren, avgiftsvalen,
    /// deltagarna, lånevapnen). Får en ny rad samma id ärver den deras pengar, val och namn. Mängden är
    /// fast — nya noder av typen skapas inte längre — så det räcker att identiteten hoppar förbi den.</para>
    ///
    /// <para><b>⚠️ Identiteten går aldrig BAKÅT.</b> En tömning inför ett nytt försök får inte sätta den till
    /// 0: raderna som skapats i tabellen försvinner, men deras referenser står kvar (liggaren är
    /// oföränderlig), och ett återanvänt id hade fått dem. Därför används samma omsättning efter flytten och
    /// efter tömningen.</para>
    ///
    /// <para>Ett id jämförs bara med id:n av SAMMA sort (anmälan med anmälan, händelse med händelse).
    /// Att en ny anmälan får samma tal som en klubbnod är därför ofarligt; en väg som blandar sorterna är
    /// felet, inte talet.</para>
    /// </summary>
    public static class IdentityFloor
    {
        /// <summary>En källa till id:n: tabellen som måste finnas och frågan som ger dess högsta id.</summary>
        public sealed record Source(string RequiredTable, string MaxSql);

        /// <summary>Högsta id:t i en Umbraco-doctype, papperskorgen inräknad.</summary>
        public static Source NodesOfType(string alias) => new("dbo.umbracoNode",
            "SELECT MAX(n.id) FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id " +
            "JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId WHERE ct.alias = '" + alias.Replace("'", "''") + "'");

        /// <summary>
        /// Läser bara (C3-planen F6, <c>/health/competitions</c>): vilket id nästa rad får, och det högsta id
        /// någon pekar på. <c>NextId &gt; HighestReferenced</c> är golvet. Ingen omsättning, inget lås.
        /// </summary>
        public static (int NextId, int HighestReferenced) Inspect(IUmbracoDatabase db, string table, IEnumerable<Source> sources)
        {
            var highest = 0;
            foreach (var s in sources)
            {
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0)", s.RequiredTable) == null) continue;
                highest = Math.Max(highest, db.ExecuteScalar<int?>(s.MaxSql) ?? 0);
            }
            highest = Math.Max(highest, db.ExecuteScalar<int?>($"SELECT MAX(Id) FROM {table}") ?? 0);
            var current = db.ExecuteScalar<int?>($"SELECT CAST(IDENT_CURRENT('{table}') AS INT)") ?? 0;
            var neverUsed = db.ExecuteScalar<int>(
                "SELECT CASE WHEN last_value IS NULL THEN 1 ELSE 0 END FROM sys.identity_columns WHERE object_id = OBJECT_ID(@0)", table) == 1;
            return (neverUsed ? current : current + 1, highest);
        }

        /// <summary>
        /// Sätter identiteten så att nästa rad får ett id över tabellens, varje källas och identitetens nuvarande
        /// värde. Källor vars tabell saknas (ekonomin är inte överallt) hoppas över. Returnerar värdet.
        ///
        /// <para><b>Låser tabellen</b> (inom anroparens transaktion) innan identiteten och tabellens högsta id
        /// läses. Annars kan en rad som skrivs mellan läsningen och omsättningen få ett id över värdet, och
        /// omsättningen flyttar då identiteten BAKÅT. Källorna läses före låset — de kan vara tunga
        /// (<c>umbracoPropertyData</c>), och ett nytt id som tillkommer under tiden hamnar i tabellen, som
        /// läses under låset. Därför går den att köra när som helst, även i drift (startkontrollen gör det
        /// vid varje start).</para>
        /// </summary>
        public static int Reseed(IUmbracoDatabase db, string table, IEnumerable<Source> sources)
        {
            var floor = 0;
            foreach (var s in sources)
            {
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0)", s.RequiredTable) == null) continue;
                floor = Math.Max(floor, db.ExecuteScalar<int?>(s.MaxSql) ?? 0);
            }
            db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WITH (TABLOCKX, HOLDLOCK)");
            floor = Math.Max(floor, db.ExecuteScalar<int?>($"SELECT CAST(IDENT_CURRENT('{table}') AS INT)") ?? 0);
            floor = Math.Max(floor, db.ExecuteScalar<int?>($"SELECT MAX(Id) FROM {table}") ?? 0);
            if (floor <= 0) return floor;
            // En tabell som aldrig haft en rad ger nästa rad EXAKT omsättningsvärdet, inte värdet + 1 —
            // då hade den fått det högsta refererade id:t. Ett steg till; det går bara framåt.
            var neverUsed = db.ExecuteScalar<int>(
                "SELECT CASE WHEN last_value IS NULL THEN 1 ELSE 0 END FROM sys.identity_columns WHERE object_id = OBJECT_ID(@0)", table) == 1;
            var seed = neverUsed ? floor + 1 : floor;
            db.Execute($"DBCC CHECKIDENT('{table}', RESEED, {seed}) WITH NO_INFOMSGS");
            return seed;
        }
    }
}
