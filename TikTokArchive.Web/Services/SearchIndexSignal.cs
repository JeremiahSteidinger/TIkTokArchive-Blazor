using System.Threading.Channels;

namespace TikTokArchive.Web.Services
{
    /// <summary>
    /// Wake-up signal for the search index outbox worker. Carries no payload — the
    /// SearchIndexOperations table is the single source of truth for pending work; this
    /// only lets the worker react promptly instead of waiting out its poll interval.
    /// </summary>
    public class SearchIndexSignal
    {
        private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public void Notify() => _channel.Writer.TryWrite(true);

        /// <summary>
        /// Waits until notified or the timeout elapses, whichever comes first.
        /// </summary>
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await _channel.Reader.ReadAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout elapsed — fall through to the next poll.
            }
        }
    }
}
