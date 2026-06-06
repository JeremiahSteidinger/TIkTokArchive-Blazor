using TikTokArchive.Entities;
using Microsoft.EntityFrameworkCore;

namespace TikTokArchive.Web.Services
{
    public enum SpeechToTextProviderType
    {
        None,
        WhisperLocal,
        OpenAI,
        Azure
    }

    public class SpeechToTextProviderFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SpeechToTextProviderFactory> _logger;
        private ISpeechToTextProvider? _provider;
        private bool _isInitialized = false;
        private string _currentProviderType = "None";

        public SpeechToTextProviderFactory(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<SpeechToTextProviderFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<ISpeechToTextProvider?> GetProviderAsync()
        {
            // Get configuration from database
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var config = await dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            
            var providerTypeString = config?.SttProvider ?? _configuration["STT_PROVIDER"] ?? "None";
            
            // Check if provider has changed
            if (_isInitialized && _currentProviderType != providerTypeString)
            {
                _logger.LogInformation("STT provider changed from {OldProvider} to {NewProvider}. Reinitializing.", 
                    _currentProviderType, providerTypeString);
                _isInitialized = false;
                _provider = null;
            }
            
            if (_isInitialized)
            {
                return _provider;
            }

            if (!Enum.TryParse<SpeechToTextProviderType>(providerTypeString, true, out var providerType))
            {
                _logger.LogWarning("Invalid STT_PROVIDER value: {ProviderType}. Defaulting to None.", providerTypeString);
                providerType = SpeechToTextProviderType.None;
            }

            _currentProviderType = providerTypeString;

            if (providerType == SpeechToTextProviderType.None)
            {
                _logger.LogInformation("Speech-to-text is disabled (STT_PROVIDER=None)");
                _isInitialized = true;
                return null;
            }

            ISpeechToTextProvider? provider = providerType switch
            {
                SpeechToTextProviderType.WhisperLocal => CreateWhisperLocalProvider(config),
                SpeechToTextProviderType.OpenAI => CreateOpenAIProvider(config),
                SpeechToTextProviderType.Azure => CreateAzureProvider(config),
                _ => null
            };

            if (provider != null)
            {
                _logger.LogInformation("Testing speech-to-text provider: {ProviderName}", provider.ProviderName);
                var isAvailable = await provider.IsAvailableAsync();
                
                if (isAvailable)
                {
                    _logger.LogInformation("Speech-to-text provider {ProviderName} is available and ready", provider.ProviderName);
                    _provider = provider;
                }
                else
                {
                    _logger.LogWarning("Speech-to-text provider {ProviderName} is configured but not available. Transcription will be disabled.", provider.ProviderName);
                    _provider = null;
                }
            }

            _isInitialized = true;
            return _provider;
        }
        
        private WhisperLocalProvider CreateWhisperLocalProvider(SearchIndexConfiguration? config)
        {
            var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
            
            // Create a temporary configuration with database values
            var tempConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WHISPER_URL"] = config?.WhisperUrl ?? _configuration["WHISPER_URL"] ?? "http://localhost:9000",
                    ["WHISPER_MODEL"] = config?.WhisperModel ?? _configuration["WHISPER_MODEL"] ?? "base"
                })
                .Build();
            
            return new WhisperLocalProvider(httpClient, tempConfig, 
                _serviceProvider.GetRequiredService<ILogger<WhisperLocalProvider>>());
        }
        
        private OpenAIWhisperProvider CreateOpenAIProvider(SearchIndexConfiguration? config)
        {
            var apiKey = config?.OpenAiApiKey ?? _configuration["OPENAI_API_KEY"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("OpenAI API key not configured");
                throw new InvalidOperationException("OpenAI API key is required for OpenAI provider");
            }
            
            var httpClient = _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
            
            // Create a temporary configuration with database values
            var tempConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OPENAI_API_KEY"] = apiKey
                })
                .Build();
            
            return new OpenAIWhisperProvider(httpClient, tempConfig, 
                _serviceProvider.GetRequiredService<ILogger<OpenAIWhisperProvider>>());
        }
        
        private AzureSpeechProvider CreateAzureProvider(SearchIndexConfiguration? config)
        {
            var key = config?.AzureSpeechKey ?? _configuration["AZURE_SPEECH_KEY"];
            var region = config?.AzureSpeechRegion ?? _configuration["AZURE_SPEECH_REGION"];
            
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region))
            {
                _logger.LogError("Azure Speech key and region not configured");
                throw new InvalidOperationException("Azure Speech key and region are required for Azure provider");
            }
            
            // Create a temporary configuration with database values
            var tempConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AZURE_SPEECH_KEY"] = key,
                    ["AZURE_SPEECH_REGION"] = region
                })
                .Build();
            
            return new AzureSpeechProvider(tempConfig, 
                _serviceProvider.GetRequiredService<ILogger<AzureSpeechProvider>>());
        }

        public async Task<bool> IsEnabledAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var config = await dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            
            var providerTypeString = config?.SttProvider ?? _configuration["STT_PROVIDER"] ?? "None";
            return !string.IsNullOrEmpty(providerTypeString) && 
                   !providerTypeString.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetConfiguredProviderTypeAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TikTokArchiveDbContext>();
            var config = await dbContext.SearchIndexConfigurations.FirstOrDefaultAsync();
            
            return config?.SttProvider ?? _configuration["STT_PROVIDER"] ?? "None";
        }
    }
}
