using NPoco;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// NPoco 6 (Umbraco 17) tar parametrarna till de asynkrona metoderna som <c>object[] args</c> utan
    /// <c>params</c>, följt av en <see cref="CancellationToken"/>. Kodbasens anrop skriver parametrarna
    /// direkt efter SQL:en (<c>db.FirstOrDefaultAsync&lt;T&gt;("WHERE Id = @0", id)</c>), som de synkrona
    /// metoderna fortfarande tar.
    ///
    /// Kompilatorn provar tilläggsmetoder först när ingen instansmetod passar, så de här tar bara de
    /// anrop som annars inte kompilerar, och skickar dem vidare oförändrade. Ett anrop som redan skickar
    /// ett <c>object[]</c> eller en <see cref="CancellationToken"/> går direkt till NPoco.
    ///
    /// ⚠️ En lista som ENSAM parameter (för <c>IN (@0)</c>) blir ett element i <c>args</c>, precis som med
    /// <c>params</c> förut — NPoco expanderar den. Skicka aldrig en <c>object[]</c> som en enda
    /// IN-parameter; den tolkas som hela parameterlistan (så var det också förut).
    /// </summary>
    public static class NPocoAsyncParamsExtensions
    {
        public static Task<int> ExecuteAsync(this IAsyncDatabase db, string sql, params object[] args)
            => db.ExecuteAsync(sql, args, default);

        public static Task<T> ExecuteScalarAsync<T>(this IAsyncDatabase db, string sql, params object[] args)
            => db.ExecuteScalarAsync<T>(sql, args, default);

        public static Task<List<T>> FetchAsync<T>(this IAsyncQueryDatabase db, string sql, params object[] args)
            => db.FetchAsync<T>(sql, args, default);

        public static Task<List<T>> FetchAsync<T>(this IAsyncQueryDatabase db, long page, long itemsPerPage, string sql, params object[] args)
            => db.FetchAsync<T>(page, itemsPerPage, sql, args, default);

        public static Task<Page<T>> PageAsync<T>(this IAsyncQueryDatabase db, long page, long itemsPerPage, string sql, params object[] args)
            => db.PageAsync<T>(page, itemsPerPage, sql, args, default);

        public static Task<T> FirstAsync<T>(this IAsyncQueryDatabase db, string sql, params object[] args)
            => db.FirstAsync<T>(sql, args, default);

        public static Task<T> FirstOrDefaultAsync<T>(this IAsyncQueryDatabase db, string sql, params object[] args)
            => db.FirstOrDefaultAsync<T>(sql, args, default);

        public static Task<T> SingleAsync<T>(this IAsyncQueryDatabase db, string sql, params object[] args)
            => db.SingleAsync<T>(sql, args, default);

        public static Task<T> SingleOrDefaultAsync<T>(this IAsyncQueryDatabase db, string sql, params object[] args)
            => db.SingleOrDefaultAsync<T>(sql, args, default);
    }
}
