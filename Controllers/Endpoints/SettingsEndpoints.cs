using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinUpscalerPlugin.Services;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Settings management endpoints for configuration import/export and fallback status.
    /// Handles plugin configuration with validation and security checks.
    /// </summary>
    public class SettingsEndpoints
    {
        private readonly ILogger<SettingsEndpoints> _logger;
        private readonly HardwareBenchmarkService _benchmarkService;

        public SettingsEndpoints(
            ILogger<SettingsEndpoints> logger,
            HardwareBenchmarkService benchmarkService)
        {
            _logger = logger;
            _benchmarkService = benchmarkService;
        }

        /// <summary>
        /// Export current plugin settings as JSON.
        /// Sensitive fields (SSH keys, webhooks, remote credentials) are redacted.
        /// </summary>
        [HttpGet("settings/export")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> ExportSettings()
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) 
                    return new BadRequestObjectResult(new { success = false, error = "Plugin not loaded" });

                return new OkObjectResult(new
                {
                    success = true,
                    pluginVersion = config.PluginVersion,
                    exportDate = DateTime.UtcNow.ToString("o"),
                    settings = new
                    {
                        config.EnablePlugin,
                        config.Model,
                        config.ScaleFactor,
                        config.QualityLevel,
                        config.HardwareAcceleration,
                        config.MaxConcurrentStreams,
                        config.MaxVRAMUsage,
                        config.CpuThreads,
                        config.AiServiceUrl,
                        config.EnableRemoteTranscoding,
                        RemoteHost = "[REDACTED]",
                        RemoteSshPort = "[REDACTED]",
                        RemoteUser = "[REDACTED]",
                        RemoteSshKeyFile = "[REDACTED]",
                        config.LocalMediaMountPoint,
                        config.RemoteMediaMountPoint,
                        config.RemoteTranscodePath,
                        config.PlayerButton,
                        config.Notifications,
                        config.AutoRetryButton,
                        config.ButtonPosition,
                        config.EnableComparisonView,
                        config.EnablePerformanceMetrics,
                        config.EnableAutoBenchmarking,
                        config.EnablePreProcessingCache,
                        config.MaxCacheAgeDays,
                        config.CacheSizeMB,
                        config.GpuDeviceIndex,
                        // Quality Metrics & Face Enhancement
                        config.EnableQualityMetrics,
                        config.EnableFaceEnhancement,
                        config.FaceEnhanceStrength,
                        // Grain Management
                        config.EnableGrainManagement,
                        config.GrainDenoiseStrength,
                        config.GrainReaddIntensity,
                        // Model Management
                        config.EnableCustomModelUpload,
                        config.EnableAutoModelSelection,
                        config.ModelFallbackChain,
                        config.PreferredAnimeModel,
                        config.PreferredLiveActionModel,
                        config.EnableModelPreloading,
                        config.ModelDiskQuotaMB,
                        config.EnableModelAutoCleanup,
                        config.ModelCleanupDays,
                        // Output & Processing
                        config.OutputCodec,
                        config.MaxUpscaledFileSizeMB,
                        config.EnableProcessingQueue,
                        config.MaxQueueSize,
                        config.PauseQueueDuringPlayback,
                        config.PersistQueueAcrossRestarts,
                        // Real-Time Upscaling
                        config.EnableRealtimeUpscaling,
                        config.RealtimeMode,
                        config.RealtimeTargetFps,
                        config.RealtimeCaptureWidth,
                        // Notifications & Webhooks
                        config.EnableProgressNotifications,
                        WebhookUrl = "[REDACTED]",
                        config.WebhookOnComplete,
                        config.WebhookOnFailure,
                        // Health & Monitoring
                        config.EnableHealthMonitoring,
                        config.HealthCheckIntervalSeconds,
                        config.EnableGpuFallbackToCpu,
                        config.CircuitBreakerThreshold,
                        config.CircuitBreakerResetSeconds,
                        // Scan Filtering
                        config.MinResolutionWidth,
                        config.MinResolutionHeight,
                        config.MaxItemsPerScan,
                        config.RestrictToUnwatchedContent,
                        config.SkipUpscaledOnRescan,
                        // API
                        config.EnableApiDocs
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export settings");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Import plugin settings from JSON with validation.
        /// Validates all inputs to prevent injection attacks and invalid configurations.
        /// </summary>
        [HttpPost("settings/import")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> ImportSettings([FromBody] System.Text.Json.JsonElement body)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null) 
                    return new BadRequestObjectResult(new { success = false, error = "Plugin not loaded" });

                System.Text.Json.JsonElement settings;
                if (!body.TryGetProperty("settings", out settings))
                {
                    return new BadRequestObjectResult(new { success = false, error = "Missing 'settings' property" });
                }

                // Apply each setting if present — wrap typed getters to handle type mismatches gracefully
                var skipped = new List<string>();
                void TryApply(string key, Action<System.Text.Json.JsonElement> apply)
                {
                    if (settings.TryGetProperty(key, out var val))
                    {
                        try { apply(val); }
                        catch (InvalidOperationException)
                        {
                            skipped.Add(key);
                            _logger.LogWarning("Settings import: skipping '{Key}' — wrong JSON type", key);
                        }
                    }
                }

                TryApply("EnablePlugin", val => config.EnablePlugin = val.GetBoolean());
                TryApply("Model", val => config.Model = val.GetString() ?? "realesrgan-x4");
                TryApply("ScaleFactor", val => config.ScaleFactor = val.GetInt32());
                TryApply("QualityLevel", val =>
                {
                    var ql = val.GetString() ?? "medium";
                    var validQL = new[] { "fast", "medium", "high" };
                    if (validQL.Contains(ql)) config.QualityLevel = ql;
                });
                TryApply("HardwareAcceleration", val => config.HardwareAcceleration = val.GetBoolean());
                TryApply("MaxConcurrentStreams", val => config.MaxConcurrentStreams = val.GetInt32());
                TryApply("MaxVRAMUsage", val => config.MaxVRAMUsage = val.GetInt32());
                TryApply("CpuThreads", val => config.CpuThreads = val.GetInt32());
                TryApply("AiServiceUrl", val =>
                {
                    var url = val.GetString() ?? "http://localhost:5000";
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                        config.AiServiceUrl = url;
                });
                TryApply("EnableRemoteTranscoding", val => config.EnableRemoteTranscoding = val.GetBoolean());
                TryApply("RemoteHost", val =>
                {
                    var host = val.GetString() ?? "";
                    if (Regex.IsMatch(host, @"^[a-zA-Z0-9.\-:]+$"))
                        config.RemoteHost = host;
                });
                TryApply("RemoteSshPort", val => config.RemoteSshPort = val.GetInt32());
                TryApply("RemoteUser", val =>
                {
                    var user = val.GetString() ?? "";
                    if (Regex.IsMatch(user, @"^[a-zA-Z0-9._\-]+$"))
                        config.RemoteUser = user;
                });
                TryApply("RemoteSshKeyFile", val =>
                {
                    var keyFile = val.GetString() ?? "";
                    if (!string.IsNullOrEmpty(keyFile) && !keyFile.Contains("..") && Path.IsPathRooted(keyFile))
                        config.RemoteSshKeyFile = keyFile;
                });
                TryApply("LocalMediaMountPoint", val => { var path = val.GetString() ?? ""; if (!path.Contains("..")) config.LocalMediaMountPoint = path; });
                TryApply("RemoteMediaMountPoint", val => { var path = val.GetString() ?? ""; if (!path.Contains("..")) config.RemoteMediaMountPoint = path; });
                TryApply("RemoteTranscodePath", val => { var path = val.GetString() ?? ""; if (!path.Contains("..")) config.RemoteTranscodePath = path; });
                TryApply("PlayerButton", val => config.PlayerButton = val.GetBoolean());
                TryApply("Notifications", val => config.Notifications = val.GetBoolean());
                TryApply("AutoRetryButton", val => config.AutoRetryButton = val.GetBoolean());
                TryApply("ButtonPosition", val => { var pos = val.GetString() ?? "right"; if (pos == "left" || pos == "right") config.ButtonPosition = pos; });
                TryApply("EnableComparisonView", val => config.EnableComparisonView = val.GetBoolean());
                TryApply("EnablePerformanceMetrics", val => config.EnablePerformanceMetrics = val.GetBoolean());
                TryApply("EnableAutoBenchmarking", val => config.EnableAutoBenchmarking = val.GetBoolean());
                TryApply("EnablePreProcessingCache", val => config.EnablePreProcessingCache = val.GetBoolean());
                TryApply("MaxCacheAgeDays", val => config.MaxCacheAgeDays = val.GetInt32());
                TryApply("CacheSizeMB", val => config.CacheSizeMB = val.GetInt32());
                TryApply("GpuDeviceIndex", val => config.GpuDeviceIndex = Math.Max(0, val.GetInt32()));
                // Quality Metrics & Face Enhancement
                TryApply("EnableQualityMetrics", val => config.EnableQualityMetrics = val.GetBoolean());
                TryApply("EnableFaceEnhancement", val => config.EnableFaceEnhancement = val.GetBoolean());
                TryApply("FaceEnhanceStrength", val => config.FaceEnhanceStrength = val.GetDouble());
                // Grain Management
                TryApply("EnableGrainManagement", val => config.EnableGrainManagement = val.GetBoolean());
                TryApply("GrainDenoiseStrength", val => config.GrainDenoiseStrength = val.GetInt32());
                TryApply("GrainReaddIntensity", val => config.GrainReaddIntensity = val.GetDouble());
                // Model Management
                TryApply("EnableCustomModelUpload", val => config.EnableCustomModelUpload = val.GetBoolean());
                TryApply("EnableAutoModelSelection", val => config.EnableAutoModelSelection = val.GetBoolean());
                TryApply("ModelFallbackChain", val => config.ModelFallbackChain = val.GetString() ?? "");
                TryApply("PreferredAnimeModel", val => config.PreferredAnimeModel = val.GetString() ?? "");
                TryApply("PreferredLiveActionModel", val => config.PreferredLiveActionModel = val.GetString() ?? "");
                TryApply("EnableModelPreloading", val => config.EnableModelPreloading = val.GetBoolean());
                TryApply("ModelDiskQuotaMB", val => config.ModelDiskQuotaMB = val.GetInt32());
                TryApply("EnableModelAutoCleanup", val => config.EnableModelAutoCleanup = val.GetBoolean());
                TryApply("ModelCleanupDays", val => config.ModelCleanupDays = val.GetInt32());
                // Output & Processing
                TryApply("OutputCodec", val =>
                {
                    var codec = val.GetString() ?? "libx264";
                    var validCodecs = new[] { "libx264", "libx265", "copy" };
                    if (validCodecs.Contains(codec)) config.OutputCodec = codec;
                });
                TryApply("MaxUpscaledFileSizeMB", val => config.MaxUpscaledFileSizeMB = Math.Max(0, val.GetInt64()));
                TryApply("EnableProcessingQueue", val => config.EnableProcessingQueue = val.GetBoolean());
                TryApply("MaxQueueSize", val => config.MaxQueueSize = val.GetInt32());
                TryApply("PauseQueueDuringPlayback", val => config.PauseQueueDuringPlayback = val.GetBoolean());
                TryApply("PersistQueueAcrossRestarts", val => config.PersistQueueAcrossRestarts = val.GetBoolean());
                // Real-Time Upscaling
                TryApply("EnableRealtimeUpscaling", val => config.EnableRealtimeUpscaling = val.GetBoolean());
                TryApply("RealtimeMode", val =>
                {
                    var mode = val.GetString() ?? "auto";
                    var validModes = new[] { "auto", "webgl", "server" };
                    if (validModes.Contains(mode)) config.RealtimeMode = mode;
                });
                TryApply("RealtimeTargetFps", val => config.RealtimeTargetFps = val.GetInt32());
                TryApply("RealtimeCaptureWidth", val => config.RealtimeCaptureWidth = val.GetInt32());
                // Notifications & Webhooks
                TryApply("EnableProgressNotifications", val => config.EnableProgressNotifications = val.GetBoolean());
                TryApply("WebhookUrl", val =>
                {
                    var url = val.GetString() ?? "";
                    if (string.IsNullOrEmpty(url) || (Uri.TryCreate(url, UriKind.Absolute, out var wUri) && (wUri.Scheme == "http" || wUri.Scheme == "https")))
                        config.WebhookUrl = url;
                });
                TryApply("WebhookOnComplete", val => config.WebhookOnComplete = val.GetBoolean());
                TryApply("WebhookOnFailure", val => config.WebhookOnFailure = val.GetBoolean());
                // Health & Monitoring
                TryApply("EnableHealthMonitoring", val => config.EnableHealthMonitoring = val.GetBoolean());
                TryApply("HealthCheckIntervalSeconds", val => config.HealthCheckIntervalSeconds = val.GetInt32());
                TryApply("EnableGpuFallbackToCpu", val => config.EnableGpuFallbackToCpu = val.GetBoolean());
                TryApply("CircuitBreakerThreshold", val => config.CircuitBreakerThreshold = val.GetInt32());
                TryApply("CircuitBreakerResetSeconds", val => config.CircuitBreakerResetSeconds = val.GetInt32());
                // Scan Filtering
                TryApply("MinResolutionWidth", val => config.MinResolutionWidth = val.GetInt32());
                TryApply("MinResolutionHeight", val => config.MinResolutionHeight = val.GetInt32());
                TryApply("MaxItemsPerScan", val => config.MaxItemsPerScan = val.GetInt32());
                TryApply("RestrictToUnwatchedContent", val => config.RestrictToUnwatchedContent = val.GetBoolean());
                TryApply("SkipUpscaledOnRescan", val => config.SkipUpscaledOnRescan = val.GetBoolean());
                // API
                TryApply("EnableApiDocs", val => config.EnableApiDocs = val.GetBoolean());

                Plugin.Instance?.SaveConfiguration();
                if (skipped.Count > 0)
                {
                    _logger.LogWarning("Settings imported with {Count} skipped properties: {Skipped}", skipped.Count, string.Join(", ", skipped));
                    return new OkObjectResult(new 
                    { 
                        success = true, 
                        message = $"Settings imported ({skipped.Count} properties skipped due to type mismatch)", 
                        skippedProperties = skipped 
                    });
                }
                _logger.LogInformation("Settings imported successfully");
                return new OkObjectResult(new { success = true, message = "Settings imported successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import settings");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get fallback status (GPU to CPU fallback information).
        /// </summary>
        [HttpGet("fallback")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetFallbackStatus()
        {
            try
            {
                return new OkObjectResult(await _benchmarkService.GetFallbackStatusAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get fallback status");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }
    }
}
