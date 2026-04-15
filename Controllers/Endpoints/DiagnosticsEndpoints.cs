using System;
using System.Net.Http;
using System.Net.Mime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinUpscalerPlugin.Services;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Diagnostic and health monitoring endpoints.
    /// Provides service health checks, GPU verification, metrics, and cache statistics.
    /// </summary>
    public class DiagnosticsEndpoints
    {
        private readonly ILogger<DiagnosticsEndpoints> _logger;
        private readonly HardwareBenchmarkService _benchmarkService;
        private readonly CacheManager _cacheManager;
        private readonly UpscalerCore _upscalerCore;
        private readonly IHttpClientFactory _httpClientFactory;

        public DiagnosticsEndpoints(
            ILogger<DiagnosticsEndpoints> logger,
            HardwareBenchmarkService benchmarkService,
            CacheManager cacheManager,
            UpscalerCore upscalerCore,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _benchmarkService = benchmarkService;
            _cacheManager = cacheManager;
            _upscalerCore = upscalerCore;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Get HttpClient for AI service communication.
        /// </summary>
        private HttpClient GetAiServiceClient() => _httpClientFactory.CreateClient("AiUpscaler");

        /// <summary>
        /// Validate and return the AI service URL.
        /// </summary>
        private string GetValidatedServiceUrl()
        {
            var config = Plugin.Instance?.Configuration;
            var serviceUrl = config?.AiServiceUrl?.TrimEnd('/') ?? "http://localhost:8188";
            
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                throw new InvalidOperationException("Invalid AI service URL");
            }

            return serviceUrl;
        }

        /// <summary>
        /// Check AI service health and availability.
        /// </summary>
        [HttpGet("service-health")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> CheckServiceHealth()
        {
            try
            {
                // Always do a fresh check when user explicitly clicks Test Connection
                _benchmarkService.InvalidateHealthCache();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var isAvailable = await _benchmarkService.IsServiceAvailableAsync();
                stopwatch.Stop();

                var status = isAvailable ? await _benchmarkService.GetServiceStatusAsync() : null;

                return new OkObjectResult(new
                {
                    success = true,
                    available = isAvailable,
                    latencyMs = stopwatch.ElapsedMilliseconds,
                    currentModel = status?.CurrentModel,
                    usingGpu = status?.UsingGpu ?? false,
                    processingCount = status?.ProcessingCount ?? 0,
                    maxConcurrent = status?.MaxConcurrent ?? 0,
                    providers = status?.AvailableProviders ?? Array.Empty<string>()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Service health check failed");
                return new OkObjectResult(new { success = false, available = false, error = "Service health check failed" });
            }
        }

        /// <summary>
        /// Get detailed health status with comprehensive diagnostics.
        /// </summary>
        [HttpGet("health/detailed")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> GetDetailedHealth()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/health/detailed");
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult 
                { 
                    Content = content, 
                    ContentType = "application/json", 
                    StatusCode = (int)response.StatusCode 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get detailed health status");
                return new ObjectResult(new { error = "Failed to get detailed health status" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Verify GPU availability and configuration.
        /// </summary>
        [HttpGet("gpu-verify")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> VerifyGpu()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/gpu-verify");
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult 
                { 
                    Content = content, 
                    ContentType = "application/json", 
                    StatusCode = (int)response.StatusCode 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify GPU");
                return new ObjectResult(new { error = "Failed to verify GPU" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get available GPUs from the AI Docker service.
        /// </summary>
        [HttpGet("gpus")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetGpuList()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/gpus");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return new ContentResult 
                    { 
                        Content = content, 
                        ContentType = "application/json" 
                    };
                }
                return new ObjectResult(new { error = "Failed to get GPU list from AI service" }) 
                { 
                    StatusCode = (int)response.StatusCode 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get GPU list");
                return new OkObjectResult(new { gpus = Array.Empty<object>() });
            }
        }

        /// <summary>
        /// Get performance metrics from AI service.
        /// </summary>
        [HttpGet("metrics")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> GetMetrics()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/metrics");
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult 
                { 
                    Content = content, 
                    ContentType = "application/json", 
                    StatusCode = (int)response.StatusCode 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get metrics");
                return new ObjectResult(new { error = "Failed to get metrics" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        [HttpGet("cache/stats")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> GetCacheStats()
        {
            try
            {
                var stats = _cacheManager.GetCacheStatistics();
                return new OkObjectResult(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get cache statistics");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Clear the cache.
        /// </summary>
        [HttpPost("cache/clear")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> ClearCache()
        {
            try
            {
                await _cacheManager.ClearCacheAsync();
                return new OkObjectResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear cache");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get hardware profile information.
        /// </summary>
        [HttpGet("hardware")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetHardwareProfile()
        {
            try
            {
                return new OkObjectResult(await _upscalerCore.DetectHardwareAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get hardware profile");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get hardware information from AI service.
        /// </summary>
        [HttpGet("hardware-info")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetHardwareInfo()
        {
            try
            {
                var profile = await _upscalerCore.DetectHardwareAsync();
                var isAvailable = await _benchmarkService.IsServiceAvailableAsync();
                var status = isAvailable ? await _benchmarkService.GetServiceStatusAsync() : null;

                return new OkObjectResult(new
                {
                    success = true,
                    hardware = profile,
                    serviceAvailable = isAvailable,
                    serviceStatus = status
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get hardware info");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Configure cache settings.
        /// </summary>
        [HttpPost("cache/config")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> ConfigureCache([FromQuery] int? maxSizeMB, [FromQuery] int? maxAgeDays)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                    return new BadRequestObjectResult(new { success = false, error = "Plugin not loaded" });

                if (maxSizeMB.HasValue)
                {
                    if (maxSizeMB.Value < 0 || maxSizeMB.Value > 100000)
                        return new BadRequestObjectResult(new { success = false, error = "maxSizeMB must be 0-100000" });
                    config.CacheSizeMB = maxSizeMB.Value;
                }

                if (maxAgeDays.HasValue)
                {
                    if (maxAgeDays.Value < 0 || maxAgeDays.Value > 365)
                        return new BadRequestObjectResult(new { success = false, error = "maxAgeDays must be 0-365" });
                    config.MaxCacheAgeDays = maxAgeDays.Value;
                }

                Plugin.Instance?.SaveConfiguration();
                return new OkObjectResult(new 
                { 
                    success = true, 
                    cacheSizeMB = config.CacheSizeMB, 
                    maxCacheAgeDays = config.MaxCacheAgeDays 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure cache");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }
    }
}
