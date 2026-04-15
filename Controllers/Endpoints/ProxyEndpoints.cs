using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// AI service proxy endpoints for model management, face restoration, and service configuration.
    /// Forwards requests to the Docker AI service with validation and security checks.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Upscaler")]
    public class ProxyEndpoints : ControllerBase
    {
        private static readonly Regex ValidModelNameRegex = new(@"^[a-zA-Z0-9\-_]+$", RegexOptions.Compiled);

        private readonly ILogger<ProxyEndpoints> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ProxyEndpoints(
            ILogger<ProxyEndpoints> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
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
        /// Load a model on the Docker AI service.
        /// </summary>
        [HttpPost("models/load")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> LoadModel([FromServices] Microsoft.AspNetCore.Http.HttpRequest request)
        {
            try
            {
                // Read model_name from query string, form body, or JSON body
                string? modelId = request.Query["model_name"].FirstOrDefault();
                if (string.IsNullOrEmpty(modelId) && request.HasFormContentType)
                {
                    var form = await request.ReadFormAsync();
                    modelId = form["model_name"].FirstOrDefault();
                }
                if (string.IsNullOrEmpty(modelId))
                {
                    try
                    {
                        // Check Content-Length before reading to prevent memory exhaustion
                        if (request.ContentLength > 1024 * 1024)
                        {
                            return new BadRequestObjectResult(new { error = "Request body too large" });
                        }
                        using var reader = new StreamReader(request.Body);
                        var body = await reader.ReadToEndAsync();
                        if (body.Length > 1024 * 1024) // 1MB payload limit (fallback for chunked transfers)
                        {
                            return new BadRequestObjectResult(new { error = "Request body too large" });
                        }
                        var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                        if (json != null && json.ContainsKey("model_name"))
                            modelId = json["model_name"];
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse JSON body for model_name, falling back to query/form");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to read request body for model_name, falling back to query/form");
                    }
                }

                if (string.IsNullOrEmpty(modelId))
                    return new BadRequestObjectResult(new { error = "model_name is required" });
                if (!ValidModelNameRegex.IsMatch(modelId))
                    return new BadRequestObjectResult(new { error = "Invalid model name — only alphanumeric, hyphens, and underscores allowed" });

                var config = Plugin.Instance?.Configuration;
                var serviceUrl = GetValidatedServiceUrl();

                // Docker AI service expects form-urlencoded POST — forward GPU settings
                var useGpu = config?.HardwareAcceleration ?? true;
                var gpuDeviceId = config?.GpuDeviceIndex ?? 0;
                var formContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("model_name", modelId),
                    new KeyValuePair<string, string>("use_gpu", useGpu.ToString().ToLower()),
                    new KeyValuePair<string, string>("gpu_device_id", gpuDeviceId.ToString())
                });
                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/models/load", formContent);
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
                _logger.LogError(ex, "Failed to load model via proxy: {Error}", ex.Message);
                return new ObjectResult(new { error = "Internal server error" }) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// Load a face restoration model.
        /// </summary>
        [HttpPost("face-restore/load")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> FaceRestoreLoad([FromQuery] string model_name = "gfpgan-v1.4")
        {
            try
            {
                // Allowlist — match IDs registered in Docker MODELS
                var allowed = new[] { "gfpgan-v1.4", "codeformer" };
                if (!allowed.Contains(model_name))
                    return new BadRequestObjectResult(new { message = "Invalid face-restore model" });

                var serviceUrl = GetValidatedServiceUrl();
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("model_name", model_name)
                });
                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/face-restore/load", form);
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
                _logger.LogError(ex, "Face-restore load proxy failed");
                return new ObjectResult(new { error = "Face-restore load failed" }) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// Get face-restore subsystem status.
        /// </summary>
        [HttpGet("face-restore/status")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> FaceRestoreStatus()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/face-restore/status");
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
                _logger.LogDebug(ex, "Face-restore status proxy failed");
                return new ObjectResult(new { error = "Face-restore service unavailable", available = false }) 
                { 
                    StatusCode = 503 
                };
            }
        }

        /// <summary>
        /// Unload the face-restore model to free VRAM.
        /// </summary>
        [HttpPost("face-restore/unload")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> FaceRestoreUnload()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/face-restore/unload", null);
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
                _logger.LogError(ex, "Face-restore unload proxy failed");
                return new ObjectResult(new { error = "Face-restore unload failed" }) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// Update Docker AI service configuration.
        /// </summary>
        [HttpPost("service-config")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> UpdateServiceConfig(
            [FromQuery] bool? use_gpu, 
            [FromQuery] int? max_concurrent, 
            [FromQuery] int? gpu_device_id)
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();

                var formData = new List<KeyValuePair<string, string>>();
                if (use_gpu.HasValue) formData.Add(new("use_gpu", use_gpu.Value.ToString().ToLower()));
                if (max_concurrent.HasValue) formData.Add(new("max_concurrent", max_concurrent.Value.ToString()));
                if (gpu_device_id.HasValue) formData.Add(new("gpu_device_id", gpu_device_id.Value.ToString()));

                using var content = new FormUrlEncodedContent(formData);
                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/config", content);
                var result = await response.Content.ReadAsStringAsync();
                return new ContentResult { Content = result, ContentType = "application/json" };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to update service config");
                return new ObjectResult(new { error = "AI service unavailable" }) { StatusCode = 503 };
            }
        }

        /// <summary>
        /// Get model disk usage from Docker service.
        /// </summary>
        [HttpGet("models/disk-usage")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> ModelsDiskUsage()
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().GetAsync($"{serviceUrl}/models/disk-usage");
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult { Content = content, ContentType = "application/json" };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get model disk usage");
                return new ObjectResult(new { error = "AI service unavailable" }) { StatusCode = 503 };
            }
        }

        /// <summary>
        /// Cleanup unused models on Docker service.
        /// </summary>
        [HttpPost("models/cleanup")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult> ModelsCleanup(
            [FromQuery] int max_age_days = 30, 
            [FromQuery] bool dry_run = true)
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();
                using var response = await GetAiServiceClient().PostAsync(
                    $"{serviceUrl}/models/cleanup?max_age_days={max_age_days}&dry_run={dry_run.ToString().ToLower()}",
                    null);
                var content = await response.Content.ReadAsStringAsync();
                return new ContentResult { Content = content, ContentType = "application/json" };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cleanup models");
                return new ObjectResult(new { error = "AI service unavailable" }) { StatusCode = 503 };
            }
        }

        /// <summary>
        /// Real-time frame upscaling proxy.
        /// </summary>
        [HttpPost("upscale-frame")]
        [Authorize]
        [RequestSizeLimit(52_428_800)]
        public async Task<ActionResult> UpscaleFrame([FromServices] Microsoft.AspNetCore.Http.HttpRequest request)
        {
            try
            {
                var serviceUrl = GetValidatedServiceUrl();

                // Read raw body
                using var ms = new MemoryStream();
                await request.Body.CopyToAsync(ms);
                var body = ms.ToArray();

                if (body.Length == 0)
                    return new BadRequestObjectResult("Empty body");

                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                using var response = await GetAiServiceClient().PostAsync($"{serviceUrl}/upscale-frame", content);

                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    return new ObjectResult("AI service busy") { StatusCode = 503 };

                if (!response.IsSuccessStatusCode)
                    return new ObjectResult("Frame upscaling failed") { StatusCode = (int)response.StatusCode };

                var result = await response.Content.ReadAsByteArrayAsync();
                return new FileContentResult(result, "image/jpeg");
            }
            catch (TaskCanceledException)
            {
                return new ObjectResult("Request timeout") { StatusCode = 408 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Frame upscaling proxy failed");
                return new ObjectResult(new { error = "Frame upscaling failed" }) { StatusCode = 500 };
            }
        }
    }
}
