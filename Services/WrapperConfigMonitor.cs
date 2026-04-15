using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinUpscalerPlugin.Services
{
    /// <summary>
    /// Monitors plugin configuration changes and regenerates wrapper script when needed.
    /// </summary>
    public class WrapperConfigMonitor : IHostedService
    {
        private readonly ILogger<WrapperConfigMonitor> _logger;
        private readonly IFFmpegWrapperService _wrapperService;
        private string? _lastConfigHash;

        public WrapperConfigMonitor(
            ILogger<WrapperConfigMonitor> logger,
            IFFmpegWrapperService wrapperService)
        {
            _logger = logger;
            _wrapperService = wrapperService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Wrapper configuration monitor started");
            
            // Hook into plugin configuration changes
            if (Plugin.Instance != null)
            {
                Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
                _lastConfigHash = GetConfigHash();
            }
            
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Wrapper configuration monitor stopped");
            
            if (Plugin.Instance != null)
            {
                Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
            }
            
            return Task.CompletedTask;
        }

        private async void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        {
            try
            {
                var newHash = GetConfigHash();
                
                // Only regenerate if relevant config changed and wrapper is installed
                if (newHash != _lastConfigHash && _wrapperService.IsWrapperInstalled())
                {
                    _logger.LogInformation("Configuration changed, regenerating FFmpeg wrapper script");
                    await _wrapperService.GenerateWrapperScriptAsync();
                    _lastConfigHash = newHash;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to regenerate wrapper script on configuration change");
            }
        }

        private string GetConfigHash()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) return string.Empty;
            
            // Hash relevant config properties that affect wrapper script
            return $"{config.AiServiceUrl}|{config.AiServiceApiToken}|{config.EnableRemoteTranscoding}|{config.RemoteHost}|{config.RemoteSshPort}";
        }
    }
}
