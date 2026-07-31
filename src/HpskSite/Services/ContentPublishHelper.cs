using System;
using System.Linq;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Save + publish a content node with a retry on transient database errors.
    ///
    /// Background: on a busy request Umbraco's own URL-segment persistence
    /// (DocumentUrlService.CreateOrUpdateUrlSegmentsAsync → DocumentUrlRepository.Save)
    /// writes umbracoDocumentUrl through SqlBulkCopy, whose timeout is a fixed 30 s and
    /// does NOT inherit "Command Timeout" from the connection string. When the database
    /// is momentarily slow that call throws "Execution Timeout Expired" and the publish
    /// fails even though nothing is wrong with the content — the club admin sees
    /// "Fel vid sparande av händelse" and a saved-but-unpublished orphan is left behind.
    ///
    /// This helper retries the transient case and reports a Swedish message the caller
    /// can show as-is. It also surfaces PublishResult failures, which are easy to ignore
    /// because ContentService.Publish returns a result instead of throwing.
    /// </summary>
    public static class ContentPublishHelper
    {
        /// <summary>
        /// Saves and publishes <paramref name="content"/>, retrying transient database
        /// timeouts. Returns whether the node ended up published, plus a message
        /// (Swedish, user-facing) when it did not.
        /// </summary>
        public static async Task<(bool Success, string? Error)> SaveAndPublishAsync(
            IContentService contentService,
            IContent content,
            int attempts = 3)
        {
            string? lastError = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    contentService.Save(content);
                    var result = contentService.Publish(content, new[] { "*" }, -1);
                    if (result.Success)
                    {
                        return (true, null);
                    }

                    // A validation/business failure will not fix itself — stop here.
                    return (false, $"Publicering nekades ({result.Result}).");
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    lastError = "Databasen svarade inte i tid.";
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }

                if (attempt < attempts)
                {
                    await Task.Delay(500 * attempt);
                }
            }

            return (false, lastError ?? "Okänt fel vid publicering.");
        }

        /// <summary>
        /// True for database errors that are worth retrying: command timeouts and deadlocks.
        /// Matched on the flattened message so we do not need a direct SqlClient reference,
        /// and so Umbraco's AggregateException wrapping is handled.
        /// </summary>
        public static bool IsTransient(Exception ex)
        {
            foreach (var inner in Flatten(ex))
            {
                var message = inner.Message ?? string.Empty;
                if (message.Contains("Execution Timeout Expired", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("The wait operation timed out", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("was deadlocked on lock", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Timeout expired", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static System.Collections.Generic.IEnumerable<Exception> Flatten(Exception ex)
        {
            if (ex is AggregateException aggregate)
            {
                yield return aggregate;
                foreach (var child in aggregate.Flatten().InnerExceptions.SelectMany(Flatten))
                {
                    yield return child;
                }
                yield break;
            }

            var current = ex;
            while (current != null)
            {
                yield return current;
                current = current.InnerException;
            }
        }
    }
}
