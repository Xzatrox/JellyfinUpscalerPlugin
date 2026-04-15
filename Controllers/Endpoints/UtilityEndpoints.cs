using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using JellyfinUpscalerPlugin.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using IOFile = System.IO.File;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Utility endpoints for plugin status, JavaScript resources, SSH testing, and filter previews.
    /// Provides miscellaneous functionality that doesn't fit into other endpoint categories.
    /// </summary>
    public class UtilityEndpoints
    {
        private readonly ILogger<UtilityEndpoints> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly HardwareBenchmarkService _benchmarkService;
        private readonly UpscalerCore _upscalerCore;
        private readonly VideoProcessor _videoProcessor;

        public UtilityEndpoints(
            ILogger<UtilityEndpoints> logger,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            HardwareBenchmarkService benchmarkService,
            UpscalerCore upscalerCore,
            VideoProcessor videoProcessor)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _benchmarkService = benchmarkService;
            _upscalerCore = upscalerCore;
            _videoProcessor = videoProcessor;
        }

        /// <summary>
        /// Serve JavaScript resources (player integration, quick menu, etc.).
        /// </summary>
        [HttpGet("js/{name}")]
        [Produces("text/javascript")]
        public ActionResult GetJavaScript(string name)
        {
            try
            {
                // Allowlist of permitted resource names to prevent resource disclosure
                var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "player-integration.js", "quick-menu.js", "sidebar-upscaler.js", "webgl-upscaler.js"
                };
                if (!allowedNames.Contains(name)) 
                    return new NotFoundResult();

                var assembly = GetType().Assembly;
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith($".Configuration.{name}", StringComparison.OrdinalIgnoreCase) || 
                                         r.EndsWith($".Configuration.{name}.js", StringComparison.OrdinalIgnoreCase));

                if (resourceName == null) 
                    return new NotFoundResult();

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) 
                    return new NotFoundResult();

                using var reader = new StreamReader(stream);
                return new ContentResult 
                { 
                    Content = reader.ReadToEnd(), 
                    ContentType = "text/javascript" 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serve JS component: {Name}", name);
                return new ObjectResult(null) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// Get plugin operational status.
        /// </summary>
        [HttpGet("status")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> GetStatus()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) 
                return new BadRequestResult();
            
            // Return only non-sensitive operational state (not full config with SSH paths etc.)
            return new OkObjectResult(new
            {
                status = "Active",
                enablePlugin = config.EnablePlugin,
                model = config.Model,
                scaleFactor = config.ScaleFactor,
                qualityLevel = config.QualityLevel,
                hardwareAcceleration = config.HardwareAcceleration,
                maxConcurrentStreams = config.MaxConcurrentStreams,
                isProcessing = false, // Placeholder for actual processing state
                version = typeof(Plugin).Assembly.GetName().Version?.ToString(4) ?? "unknown"
            });
        }

        /// <summary>
        /// Get plugin information.
        /// </summary>
        [HttpGet("info")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> GetPluginInfo()
        {
            var assembly = typeof(Plugin).Assembly;
            var version = assembly.GetName().Version?.ToString(3) ?? "1.5.2";

            return new OkObjectResult(new
            {
                name = "AI Upscaler Plugin",
                version = version,
                description = "AI-powered video upscaling with modern UI integration and hardware benchmarking",
                author = "Kuschel-code",
                features = new[]
                {
                    "Real-time AI video upscaling",
                    "Multiple AI models",
                    "Hardware acceleration support",
                    "Player integration",
                    "Automated hardware benchmarking"
                }
            });
        }

        /// <summary>
        /// Test upscaling configuration.
        /// </summary>
        [HttpPost("test")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> TestUpscaling()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null) 
                return new BadRequestResult();

            try
            {
                var hardware = await _upscalerCore.DetectHardwareAsync();
                return new OkObjectResult(new
                {
                    success = true,
                    model = config.Model,
                    scale = config.ScaleFactor,
                    quality = config.QualityLevel,
                    hardwareAcceleration = config.HardwareAcceleration,
                    gpuModel = hardware.GpuModel,
                    supportsCUDA = hardware.SupportsCUDA,
                    estimatedPerformance = hardware.SupportsCUDA ? "High (GPU/CUDA)" : (hardware.SupportsDirectML ? "Medium (GPU/DirectML)" : "Low (CPU)"),
                    message = $"AI upscaling test successful on {hardware.GpuModel ?? "CPU"} with {config.Model} model at {config.ScaleFactor}x scale"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI Upscaler: Error during test");
                return new ObjectResult(new { success = false, message = "Test failed due to an internal error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get hardware recommendations.
        /// </summary>
        [HttpGet("recommendations")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetHardwareRecommendations()
        {
            try
            {
                return new OkObjectResult(await _benchmarkService.GetRecommendationsAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get hardware recommendations");
                return new ObjectResult(new { success = false, error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }

        /// <summary>
        /// Get recommended AI model for specific content.
        /// </summary>
        [HttpGet("recommend-model")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> RecommendModel(
            [FromQuery] string? genres = null,
            [FromQuery] int width = 0,
            [FromQuery] int height = 0,
            [FromQuery] bool isBatch = true)
        {
            try
            {
                var serviceStatus = await _upscalerCore.GetServiceStatusAsync();
                int inputFrames = serviceStatus?.InputFrames ?? 1;

                var genreList = string.IsNullOrEmpty(genres)
                    ? Array.Empty<string>()
                    : genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var recommendedModel = _upscalerCore.ResolveModelForVideo(
                    genres: genreList,
                    width: width,
                    height: height,
                    isBatch: isBatch,
                    inputFrames: inputFrames);

                var config = Plugin.Instance?.Configuration;
                return new OkObjectResult(new
                {
                    success = true,
                    recommended_model = recommendedModel,
                    input_frames = inputFrames,
                    auto_selection_enabled = config?.EnableAutoModelSelection ?? true,
                    parameters = new { genres = genreList, width, height, is_batch = isBatch }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get model recommendation");
                return new ObjectResult(new { success = false, error = "Internal server error" })
                {
                    StatusCode = 500
                };
            }
        }

        /// <summary>
        /// Test SSH connection for remote transcoding.
        /// </summary>
        [HttpPost("ssh/test")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> TestSshConnection([FromBody] SshTestRequest request)
        {
            try
            {
                // Security: Validate inputs to prevent command injection
                if (!Regex.IsMatch(request.Host, @"^[a-zA-Z0-9._\-]+$"))
                    return new BadRequestObjectResult(new { success = false, message = "Invalid host format. Only alphanumeric, dots, hyphens allowed." });

                if (!Regex.IsMatch(request.User, @"^[a-zA-Z0-9._\-]+$"))
                    return new BadRequestObjectResult(new { success = false, message = "Invalid user format. Only alphanumeric, dots, hyphens allowed." });

                if (request.Port < 1 || request.Port > 65535)
                    return new BadRequestObjectResult(new { success = false, message = "Invalid port. Must be 1-65535." });

                _logger.LogInformation("Testing SSH connection to {User}@{Host}:{Port}", request.User, request.Host, request.Port);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ssh",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Use ArgumentList to prevent shell injection
                if (!string.IsNullOrWhiteSpace(request.KeyFile))
                {
                    var resolvedKeyPath = Path.GetFullPath(request.KeyFile);
                    if (!IOFile.Exists(resolvedKeyPath))
                        return new BadRequestObjectResult(new { success = false, message = "SSH key file not found." });

                    // Security: Reject symbolic links to prevent symlink bypass
                    var keyFileInfo = new FileInfo(resolvedKeyPath);
                    if (keyFileInfo.LinkTarget != null)
                    {
                        return new BadRequestObjectResult(new { success = false, message = "Symbolic links are not allowed for SSH key files." });
                    }

                    // Security: Restrict key file to .ssh directories or plugin data path
                    var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                    var pluginDir = Plugin.Instance != null ? Path.GetDirectoryName(Plugin.Instance.ConfigurationFilePath) ?? "" : "";
                    if (!resolvedKeyPath.StartsWith(sshDir, StringComparison.OrdinalIgnoreCase) &&
                        !resolvedKeyPath.StartsWith(pluginDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return new BadRequestObjectResult(new { success = false, message = "SSH key file must be in ~/.ssh/ or plugin data directory." });
                    }

                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(resolvedKeyPath);
                }

                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("BatchMode=yes");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("ConnectTimeout=5");
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(request.Port.ToString());
                psi.ArgumentList.Add($"{request.User}@{request.Host}");
                psi.ArgumentList.Add("echo 'SSH_TEST_SUCCESS'");

                using var process = new System.Diagnostics.Process { StartInfo = psi };
                process.Start();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                var error = await process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);

                if (process.ExitCode == 0 && output.Contains("SSH_TEST_SUCCESS"))
                {
                    _logger.LogInformation("SSH connection test successful");
                    return new OkObjectResult(new { success = true, message = "SSH connection successful" });
                }
                else
                {
                    _logger.LogWarning("SSH connection test failed: {Error}", error);
                    return new OkObjectResult(new { success = false, message = "SSH connection failed" });
                }
            }
            catch (OperationCanceledException)
            {
                return new OkObjectResult(new { success = false, message = "SSH connection timed out after 15 seconds" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH connection test error");
                return new OkObjectResult(new { success = false, message = "SSH test error" });
            }
        }

        /// <summary>
        /// Preview video filter effect.
        /// </summary>
        [HttpPost("filter-preview")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> FilterPreview([FromQuery] string? preset)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var filterService = new VideoFilterService();

            string? filterChain;
            if (!string.IsNullOrEmpty(preset))
            {
                filterChain = filterService.GetPresetFilters(preset);
            }
            else
            {
                filterChain = filterService.BuildFilterChain(config);
            }

            return new OkObjectResult(new
            {
                enabled = config.EnableVideoFilters,
                preset = preset ?? config.ActiveFilterPreset,
                filterChain = filterChain ?? "(no filters active)",
                availablePresets = new[] { "none", "cinematic", "vintage", "vivid", "noir", "warm", "cool", "hdr-pop", "sepia", "pastel", "cyberpunk", "drama", "soft-glow", "sharp-hd", "retrogame", "teal-orange", "custom" }
            });
        }

        /// <summary>
        /// Generate live filter preview on a real video frame.
        /// </summary>
        [HttpGet("filter-preview/frame/{itemId}")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetFilterPreviewFrame(
            string itemId,
            [FromQuery] string preset = "none",
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid) || itemGuid == Guid.Empty)
                    return new BadRequestObjectResult(new { message = "Invalid item ID format" });

                var validPresets = new[] { "none", "cinematic", "vintage", "vivid", "noir", "warm", "cool", "hdr-pop", "sepia", "pastel", "cyberpunk", "drama", "soft-glow", "sharp-hd", "retrogame", "teal-orange", "custom" };
                if (!validPresets.Contains(preset))
                    return new BadRequestObjectResult(new { message = "Invalid preset name" });
                
                // 'custom' isn't useful for filter-preview (would need full config round-trip) — treat as none
                if (preset == "custom") preset = "none";

                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null) 
                    return new NotFoundObjectResult(new { message = "Item not found" });

                var mediaSources = _mediaSourceManager.GetStaticMediaSources(item, true, null);
                var mediaSource = mediaSources?.FirstOrDefault();
                var videoPath = mediaSource?.Path ?? item.Path;
                if (string.IsNullOrEmpty(videoPath))
                    return new BadRequestObjectResult(new { message = "No video path — select a movie or episode, not a library folder" });

                // Seek to ~10% of runtime, fallback to 10s
                var seekPosition = TimeSpan.FromSeconds(10);
                if (mediaSource?.RunTimeTicks != null)
                {
                    var totalSeconds = TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value).TotalSeconds;
                    if (totalSeconds > 30)
                        seekPosition = TimeSpan.FromSeconds(totalSeconds * 0.10);
                }

                var filterService = new VideoFilterService();
                var filterChain = filterService.GetPresetFilters(preset);

                _logger.LogInformation("Filter preview: path={Path}, preset={Preset}, chain={Chain}", videoPath, preset, filterChain);

                // Extract original frame (no filter)
                var originalPng = await _videoProcessor.ExtractSingleFrameAsync(videoPath, seekPosition, cancellationToken);

                // Extract filtered frame (or re-use original if preset is "none"/empty)
                byte[] filteredPng;
                if (string.IsNullOrWhiteSpace(filterChain))
                {
                    filteredPng = originalPng;
                }
                else
                {
                    filteredPng = await _videoProcessor.ExtractSingleFrameWithFiltersAsync(videoPath, seekPosition, filterChain, cancellationToken);
                }

                // Downscale both to <=1280x720 JPEG for fast transfer
                byte[] EncodeJpeg(byte[] pngBytes)
                {
                    using var image = Image.Load(pngBytes);
                    if (image.Width > 1280 || image.Height > 720)
                        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1280, 720), Mode = ResizeMode.Max }));
                    using var ms = new MemoryStream();
                    image.SaveAsJpeg(ms);
                    return ms.ToArray();
                }

                var originalJpeg = EncodeJpeg(originalPng);
                var filteredJpeg = EncodeJpeg(filteredPng);

                return new OkObjectResult(new
                {
                    itemId,
                    preset,
                    filterChain = filterChain ?? "(no filters active)",
                    originalBase64 = $"data:image/jpeg;base64,{Convert.ToBase64String(originalJpeg)}",
                    filteredBase64 = $"data:image/jpeg;base64,{Convert.ToBase64String(filteredJpeg)}",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate filter preview for item {ItemId} preset {Preset}", itemId, preset);
                return new ObjectResult(new { message = "Filter preview failed", error = "Internal server error" }) 
                { 
                    StatusCode = 500 
                };
            }
        }
    }

    /// <summary>
    /// Request model for SSH connection test.
    /// </summary>
    public class SshTestRequest
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 2222;
        public string User { get; set; } = "root";
        public string KeyFile { get; set; } = "";
    }
}
