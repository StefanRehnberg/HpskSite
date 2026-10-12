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
        /// Sätter identiteten så att nästa rad får ett id över tabellens, varje källas och identitetens nuvarande
        /// värde. Källor vars tabell saknas (ekonomin är inte överallt) hoppas över. Returnerar värdet.
        /// </summary>
        public static int Reseed(IUmbracoDatabase db, string table, IEnumerable<Source> sources)
        {
            var floor = db.ExecuteScalar<int?>($"SELECT CAST(IDENT_CURRENT('{table}') AS INT)") ?? 0;
            floor = Math.Max(floor, db.ExecuteScalar<int?>($"SELECT MAX(Id) FROM {table}") ?? 0);
            foreach (var s in sources)
            {
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0)", s.RequiredTable) == null) continue;
                floor = Math.Max(floor, db.ExecuteScalar<int?>(s.MaxSql) ?? 0);
            }
            if (floor > 0) db.Execute($"DBCC CHECKIDENT('{table}', RESEED, {floor}) WITH NO_INFOMSGS");
            return floor;
        }
    }
}
