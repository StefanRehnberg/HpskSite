namespace HpskSite.Shared.DTOs
{
    /// <summary>
    /// Standard API response wrapper
    /// </summary>
    /// <typeparam name="T">Type of the data payload</typeparam>
    public class ApiResponse<T>
    {
        /// <summary>
        /// Whether the request was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Message describing the result (error message if failed)
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// The data payload (null if failed)
        /// </summary>
        public T? Data { get; set; }

        /// <summary>
        /// Whether the user needs to set their shooter class (for handicap matches)
        /// </summary>
        public bool NeedsShooterClass { get; set; }

        /// <summary>
        /// Create a successful response with data
        /// </summary>
        public static ApiResponse<T> Ok(T data, string? message = null)
        {
            return new ApiResponse<T>
            {
                Success = true,
                Data = data,
                Message = message
            };
        }

        /// <summary>
        /// Create an error response
        /// </summary>
        public static ApiResponse<T> Error(string message)
        {
            return new ApiResponse<T>
            {
                Success = false,
                Message = message,
                Data = default
            };
        }
    }

    /// <summary>
    /// Standard API response without data payload
    /// </summary>
    public class ApiResponse
    {
        /// <summary>
        /// Whether the request was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Message describing the result
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Create a successful response
        /// </summary>
        public static ApiResponse Ok(string? message = null)
        {
            return new ApiResponse
            {
                Success = true,
                Message = message
            };
        }

        /// <summary>
        /// Create an error response
        /// </summary>
        public static ApiResponse Error(string message)
        {
            return new ApiResponse
            {
                Success = false,
                Message = message
            };
        }
    }

    /// <summary>
    /// Paginated response wrapper
    /// </summary>
    /// <typeparam name="T">Type of items in the list</typeparam>
    public class PagedResponse<T>
    {
        /// <summary>
        /// List of items for the current page
        /// </summary>
        public List<T> Items { get; set; } = new List<T>();

        /// <summary>
        /// Current page number (1-based)
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// Number of items per page
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Total number of items across all pages
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// Total number of pages
        /// </summary>
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

        /// <summary>
        /// Whether there is a next page
        /// </summary>
        public bool HasNextPage => Page < TotalPages;

        /// <summary>
        /// Whether there is a previous page
        /// </summary>
        public bool HasPreviousPage => Page > 1;
    }
}
