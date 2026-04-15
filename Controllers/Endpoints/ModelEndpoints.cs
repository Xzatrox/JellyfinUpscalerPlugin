using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JellyfinUpscalerPlugin.Controllers.Helpers;
using System.Net.Mime;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Model management endpoints for AI upscaling models.
    /// </summary>
    [ApiController]
    [Authorize]
    public class ModelEndpoints : ControllerBase
    {
        private readonly ILogger<ModelEndpoints> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ModelEndpoints(ILogger<ModelEndpoints> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        private HttpClient GetAiServiceClient() => _httpClientFactory.CreateClient("AiUpscaler");

        private string GetValidatedServiceUrl()
        {
            const string fallback = "http://localhost:5000";
            var config = Plugin.Instance?.Configuration;
            var url = config?.AiServiceUrl?.Trim();

            if (string.IsNullOrEmpty(url))
                return fallback;

            if (!ValidationHelper.IsValidServiceUrl(url, out var uri))
            {
                _logger.LogWarning("AiServiceUrl rejected (invalid), using fallback");
                return fallback;
            }

            return url.TrimEnd('/');
        }

        /// <summary>
        /// Get available AI models from the Docker service.
        /// </summary>
        [HttpGet("Upscaler/models")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> GetAvailableModels()
        {
            var baseUrl = GetValidatedServiceUrl();

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await GetAiServiceClient().GetAsync($"{baseUrl}/models", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("Models proxy success from {Url}: {Length} chars", baseUrl, json.Length);
                    return Content(json, "application/json");
                }
                _logger.LogWarning("Models proxy failed: HTTP {Status} from {Url}", (int)response.StatusCode, baseUrl);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not reach Docker AI service at {Url}/models: {Error}", baseUrl, ex.Message);
            }

            // Fallback: return hardcoded base models if Docker service is unavailable
            var fallbackModels = GetFallbackModels();
            return Ok(new { models = fallbackModels, total = fallbackModels.Count });
        }

        /// <summary>
        /// Load a model on the Docker AI service.
        /// </summary>
        [HttpPost("Upscaler/models/load")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> LoadModel()
        {
            try
            {
                string? modelId = Request.Query["model_name"].FirstOrDefault();
                if (string.IsNullOrEmpty(modelId) && Request.HasFormContentType)
                {
                    var form = await Request.ReadFormAsync();
                    modelId = form["model_name"].FirstOrDefault();
                }
                if (string.IsNullOrEmpty(modelId))
                {
                    try
                    {
                        if (Request.ContentLength > 1024 * 1024)
                            return BadRequest(new { error = "Request body too large" });

                        using var reader = new System.IO.StreamReader(Request.Body);
                        var body = await reader.ReadToEndAsync();
                        if (body.Length > 1024 * 1024)
                            return BadRequest(new { error = "Request body too large" });

                        var json = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(body);
                        if (json != null && json.ContainsKey("model_name"))
                            modelId = json["model_name"];
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse JSON body for model_name");
                    }
                }

                if (string.IsNullOrEmpty(modelId))
                    return BadRequest(new { error = "model_name is required" });
                
                if (!ValidationHelper.IsValidModelName(modelId))
                    return BadRequest(new { error = "Invalid model name — only alphanumeric, hyphens, and underscores allowed" });

                var config = Plugin.Instance?.Configuration;
                var serviceUrl = GetValidatedServiceUrl();

                var useGpu = config?.HardwareAcceleration ?? true;
                var gpuDeviceId = config?.GpuDeviceIndex ?? 0;
                var formContent = new FormUrlEncodedContent(new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, string>("model_name", modelId),
                    new System.Collections.Generic.KeyValuePair<string, string>("use_gpu", useGpu.ToString().ToLower()),
                    new System.Collections.Generic.KeyValuePair<string, string>("gpu_device_id", gpuDeviceId.ToString())
                });
                
                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/models/load", formContent);
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult { Content = content, ContentType = "application/json", StatusCode = (int)response.StatusCode };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load model via proxy");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Run benchmark on the currently loaded model.
        /// </summary>
        [HttpGet("Upscaler/model-benchmark")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> ModelBenchmark()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/benchmark");
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult { Content = content, ContentType = "application/json", StatusCode = (int)response.StatusCode };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run model benchmark");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get model disk usage from Docker service.
        /// </summary>
        [HttpGet("Upscaler/models/disk-usage")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> ModelsDiskUsage()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/models/disk-usage");
                var content = await response.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get model disk usage");
                return StatusCode(503, new { error = "AI service unavailable" });
            }
        }

        /// <summary>
        /// Model cleanup on Docker service (LRU removal of unused models).
        /// </summary>
        [HttpPost("Upscaler/models/cleanup")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> ModelsCleanup([FromQuery] int max_age_days = 30, [FromQuery] bool dry_run = true)
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().PostAsync(
                    $"{serviceUrl}/models/cleanup?max_age_days={max_age_days}&dry_run={dry_run.ToString().ToLower()}",
                    null);
                var content = await response.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cleanup models");
                return StatusCode(503, new { error = "AI service unavailable" });
            }
        }

        private static System.Collections.Generic.List<object> GetFallbackModels()
        {
            return new System.Collections.Generic.List<object>
            {
                new { id = "realesrgan-x4", name = "Real-ESRGAN x4 (Best Quality)", description = "Best quality 4x (67MB ONNX)", scale = new[] { 4 }, category = "realesrgan", type = "onnx", downloaded = false, loaded = false, available = true },
                new { id = "realesrgan-x4-256", name = "Real-ESRGAN x4 (256px optimized)", description = "Optimized for 256px tiles, low VRAM", scale = new[] { 4 }, category = "realesrgan", type = "onnx", downloaded = false, loaded = false, available = true },
                new { id = "span-x2", name = "SPAN x2 (Fast Quality)", description = "NTIRE 2023 winner 2x", scale = new[] { 2 }, category = "nextgen", type = "onnx", downloaded = false, loaded = false, available = true },
                new { id = "span-x4", name = "SPAN x4 (Fast Quality)", description = "NTIRE 2023 winner 4x", scale = new[] { 4 }, category = "nextgen", type = "onnx", downloaded = false, loaded = false, available = true },
                new { id = "fsrcnn-x2", name = "FSRCNN x2 (Fast)", description = "Very fast 2x upscaling", scale = new[] { 2 }, category = "fast", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "fsrcnn-x3", name = "FSRCNN x3 (Fast)", description = "Fast 3x upscaling", scale = new[] { 3 }, category = "fast", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "fsrcnn-x4", name = "FSRCNN x4 (Fast)", description = "Fast 4x, lower quality", scale = new[] { 4 }, category = "fast", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "espcn-x2", name = "ESPCN x2 (Fastest)", description = "Fastest model", scale = new[] { 2 }, category = "fast", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "espcn-x4", name = "ESPCN x4 (Fastest)", description = "Fastest 4x", scale = new[] { 4 }, category = "fast", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "edsr-x4", name = "EDSR x4 (Best OpenCV)", description = "Best quality 4x OpenCV", scale = new[] { 4 }, category = "quality", type = "pb", downloaded = false, loaded = false, available = true },
                new { id = "gfpgan-v1.4", name = "GFPGAN v1.4 (Face Restore)", description = "Tencent ARC face restoration GAN — 512x512 crops", scale = new[] { 1 }, category = "face_restore", type = "onnx", downloaded = false, loaded = false, available = true },
                new { id = "codeformer", name = "CodeFormer (Face Restore)", description = "Transformer-codebook face restoration — 512x512 crops", scale = new[] { 1 }, category = "face_restore", type = "onnx", downloaded = false, loaded = false, available = true }
            };
        }
    }
}
