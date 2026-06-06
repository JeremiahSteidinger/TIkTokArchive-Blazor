using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace TikTokArchive.Web.Services
{
    public class RabbitMQService : IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<RabbitMQService> _logger;
        private IConnection? _connection;
        private IChannel? _channel;
        private bool _isConnected = false;

        private const string TRANSCRIPTION_QUEUE = "transcription_queue";
        private const string TRANSCRIPTION_EXCHANGE = "transcription_exchange";
        private const string TRANSCRIPTION_DLX = "transcription_dlx";
        private const string TRANSCRIPTION_DLQ = "transcription_dlq";
        private const int MAX_RETRIES = 3;

        public RabbitMQService(IConfiguration configuration, ILogger<RabbitMQService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<bool> InitializeAsync()
        {
            if (_isConnected)
            {
                return true;
            }

            try
            {
                var connectionString = _configuration["RABBITMQ_CONNECTION_STRING"] ?? "amqp://guest:guest@rabbitmq:5672";
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(connectionString),
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
                };

                _connection = await factory.CreateConnectionAsync();
                _channel = await _connection.CreateChannelAsync();

                // Declare dead-letter exchange and queue
                await _channel.ExchangeDeclareAsync(TRANSCRIPTION_DLX, ExchangeType.Direct, durable: true);
                await _channel.QueueDeclareAsync(TRANSCRIPTION_DLQ, durable: true, exclusive: false, autoDelete: false);
                await _channel.QueueBindAsync(TRANSCRIPTION_DLQ, TRANSCRIPTION_DLX, TRANSCRIPTION_QUEUE);

                // Declare main exchange
                await _channel.ExchangeDeclareAsync(TRANSCRIPTION_EXCHANGE, ExchangeType.Direct, durable: true);

                // Declare main queue with dead-letter exchange
                var args = new Dictionary<string, object?>
                {
                    { "x-dead-letter-exchange", TRANSCRIPTION_DLX }
                };
                await _channel.QueueDeclareAsync(TRANSCRIPTION_QUEUE, durable: true, exclusive: false, autoDelete: false, arguments: args);
                await _channel.QueueBindAsync(TRANSCRIPTION_QUEUE, TRANSCRIPTION_EXCHANGE, TRANSCRIPTION_QUEUE);

                // Set QoS to process one message at a time
                await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

                _isConnected = true;
                _logger.LogInformation("RabbitMQ connection established successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to RabbitMQ. Transcription service will be disabled.");
                _isConnected = false;
                return false;
            }
        }

        public async Task PublishTranscriptionMessageAsync(int videoId, string provider)
        {
            if (!_isConnected || _channel == null)
            {
                throw new InvalidOperationException("RabbitMQ is not connected");
            }

            var message = new TranscriptionMessage
            {
                VideoId = videoId,
                Provider = provider,
                QueuedAt = DateTime.UtcNow
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var properties = new BasicProperties
            {
                Persistent = true,
                Headers = new Dictionary<string, object?>
                {
                    { "x-retry-count", 0 }
                }
            };

            await _channel.BasicPublishAsync(
                exchange: TRANSCRIPTION_EXCHANGE,
                routingKey: TRANSCRIPTION_QUEUE,
                mandatory: false,
                basicProperties: properties,
                body: body
            );

            _logger.LogInformation("Published transcription message for video {VideoId}", videoId);
        }

        public IChannel? GetChannel()
        {
            return _isConnected ? _channel : null;
        }

        public bool IsConnected => _isConnected;

        public string GetQueueName() => TRANSCRIPTION_QUEUE;

        public int GetMaxRetries() => MAX_RETRIES;

        public void Dispose()
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
    }

    public class TranscriptionMessage
    {
        public int VideoId { get; set; }
        public string Provider { get; set; } = string.Empty;
        public DateTime QueuedAt { get; set; }
    }
}
