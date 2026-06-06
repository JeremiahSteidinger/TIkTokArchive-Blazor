using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using TikTokArchive.Entities;

namespace TikTokArchive.Web.Services
{
    public class TranscriptionBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly RabbitMQService _rabbitMQService;
        private readonly SpeechToTextProviderFactory _providerFactory;
        private readonly ILogger<TranscriptionBackgroundService> _logger;

        public TranscriptionBackgroundService(
            IServiceProvider serviceProvider,
            RabbitMQService rabbitMQService,
            SpeechToTextProviderFactory providerFactory,
            ILogger<TranscriptionBackgroundService> logger)
        {
            _serviceProvider = serviceProvider;
            _rabbitMQService = rabbitMQService;
            _providerFactory = providerFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TranscriptionBackgroundService starting");

            // Check if STT is enabled
            if (!await _providerFactory.IsEnabledAsync())
            {
                _logger.LogInformation("Speech-to-text is disabled (STT_PROVIDER=None). TranscriptionBackgroundService will not process messages.");
                return;
            }

            // Initialize RabbitMQ
            var initialized = await _rabbitMQService.InitializeAsync();
            if (!initialized)
            {
                _logger.LogWarning("RabbitMQ is not available. TranscriptionBackgroundService will not process messages.");
                return;
            }

            // Re-queue any pending or failed items from database
            await RequeuePendingItemsAsync();

            // Start consuming messages
            var channel = _rabbitMQService.GetChannel();
            if (channel == null)
            {
                _logger.LogError("Failed to get RabbitMQ channel");
                return;
            }

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var messageJson = Encoding.UTF8.GetString(body);
                    var message = JsonSerializer.Deserialize<TranscriptionMessage>(messageJson);

                    if (message == null)
                    {
                        _logger.LogWarning("Received null message, rejecting");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        return;
                    }

                    _logger.LogInformation("Processing transcription for video {VideoId}", message.VideoId);

                    // Get retry count from headers
                    var retryCount = 0;
                    if (ea.BasicProperties.Headers != null && ea.BasicProperties.Headers.TryGetValue("x-retry-count", out var retryObj))
                    {
                        retryCount = Convert.ToInt32(retryObj);
                    }

                    // Update status to Processing
                    await UpdateQueueItemStatusAsync(message.VideoId, TranscriptionStatus.Processing, retryCount, null);

                    // Process transcription
                    using var scope = _serviceProvider.CreateScope();
                    var transcriptionService = scope.ServiceProvider.GetRequiredService<TranscriptionService>();

                    var success = await transcriptionService.ProcessTranscriptionAsync(message.VideoId, message.Provider);

                    if (success)
                    {
                        // Mark as completed
                        await UpdateQueueItemStatusAsync(message.VideoId, TranscriptionStatus.Completed, retryCount, null);
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                        _logger.LogInformation("Successfully processed transcription for video {VideoId}", message.VideoId);
                    }
                    else
                    {
                        // Provider not available, requeue for later
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                        _logger.LogWarning("STT provider not available, requeuing video {VideoId}", message.VideoId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing transcription message");

                    try
                    {
                        var body = ea.Body.ToArray();
                        var messageJson = Encoding.UTF8.GetString(body);
                        var message = JsonSerializer.Deserialize<TranscriptionMessage>(messageJson);

                        if (message != null)
                        {
                            // Get retry count
                            var retryCount = 0;
                            if (ea.BasicProperties.Headers != null && ea.BasicProperties.Headers.TryGetValue("x-retry-count", out var retryObj))
                            {
                                retryCount = Convert.ToInt32(retryObj);
                            }

                            retryCount++;

                            if (retryCount < _rabbitMQService.GetMaxRetries())
                            {
                                // Retry - republish with incremented retry count
                                _logger.LogWarning("Retrying transcription for video {VideoId} (attempt {RetryCount})", message.VideoId, retryCount);
                                
                                await UpdateQueueItemStatusAsync(message.VideoId, TranscriptionStatus.Pending, retryCount, ex.Message);
                                
                                var newBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
                                var properties = new BasicProperties
                                {
                                    Persistent = true,
                                    Headers = new Dictionary<string, object?>
                                    {
                                        { "x-retry-count", retryCount }
                                    }
                                };

                                await channel.BasicPublishAsync(
                                    exchange: "",
                                    routingKey: _rabbitMQService.GetQueueName(),
                                    mandatory: false,
                                    basicProperties: properties,
                                    body: newBody
                                );

                                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                            }
                            else
                            {
                                // Max retries exceeded, send to DLQ
                                _logger.LogError("Max retries exceeded for video {VideoId}, sending to DLQ", message.VideoId);
                                await UpdateQueueItemStatusAsync(message.VideoId, TranscriptionStatus.Failed, retryCount, ex.Message);
                                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                            }
                        }
                        else
                        {
                            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                        }
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Error handling transcription failure");
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                    }
                }
            };

            await channel.BasicConsumeAsync(
                queue: _rabbitMQService.GetQueueName(),
                autoAck: false,
                consumer: consumer,
                cancellationToken: stoppingToken
            );

            _logger.LogInformation("TranscriptionBackgroundService is now consuming messages");

            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task RequeuePendingItemsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var pendingItems = await dbContext.TranscriptionQueueItems
                    .Where(q => q.Status == TranscriptionStatus.Pending || q.Status == TranscriptionStatus.Failed)
                    .ToListAsync();

                foreach (var item in pendingItems)
                {
                    _logger.LogInformation("Re-queuing transcription for video {VideoId} (Status: {Status}, RetryCount: {RetryCount})", 
                        item.VideoId, item.Status, item.RetryCount);
                    
                    await _rabbitMQService.PublishTranscriptionMessageAsync(item.VideoId, item.Provider);
                }

                if (pendingItems.Any())
                {
                    _logger.LogInformation("Re-queued {Count} pending/failed transcription items", pendingItems.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error re-queuing pending transcription items");
            }
        }

        private async Task UpdateQueueItemStatusAsync(int videoId, TranscriptionStatus status, int retryCount, string? errorMessage)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();

                var item = await dbContext.TranscriptionQueueItems
                    .FirstOrDefaultAsync(q => q.VideoId == videoId);

                if (item != null)
                {
                    item.Status = status;
                    item.RetryCount = retryCount;
                    item.ErrorMessage = errorMessage;
                    
                    if (status == TranscriptionStatus.Completed || status == TranscriptionStatus.Failed)
                    {
                        item.ProcessedAt = DateTime.UtcNow;
                    }

                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating queue item status for video {VideoId}", videoId);
            }
        }
    }
}
