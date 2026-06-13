namespace TikTokArchive.Web.Services
{
    public static class ExceptionMessageExtensions
    {
        /// <summary>
        /// Flattens the message chain so wrapper messages like DbUpdateException's
        /// "see the inner exception for details" actually carry the cause.
        /// </summary>
        public static string GetFullMessage(this Exception exception)
        {
            var messages = new List<string>();
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (!messages.Contains(current.Message))
                {
                    messages.Add(current.Message);
                }
            }

            return string.Join(" — ", messages);
        }
    }
}
