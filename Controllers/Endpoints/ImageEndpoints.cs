using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using JellyfinUpscalerPlugin.Services;
using JellyfinUpscalerPlugin.Controllers.Helpers;
using MediaBrowser.Model.Entities;
using System.Collections.Generic;
using System.Net.Mime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using IOFile = System.IO.File;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Image upscaling endpoints.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("Upscaler")]
    public class ImageEndpoints : ControllerBase
    {
        private const long MaxUploadSizeBytes = 50 * 1024 * 1024; // 50 MB
        
        private readonly ILogger<ImageEndpoints> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly UpscalerCore _upscalerCore;
        private readonly VideoProcessor _videoProcessor;

        public ImageEndpoints(
            ILogger<ImageEndpoints> logger,
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            UpscalerCore upscalerCore,
            VideoProcessor videoProcessor)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _upscalerCore = upscalerCore;
            _videoProcessor = videoProcessor;
        }

        /// <summary>
        /// Upscale a raw image (binary upload).
        /// </summary>
        [HttpPost("upscale/image")]
        [Consumes("application/octet-stream")]
        [Produces("application/octet-stream")]
        [RequestSizeLimit(52428800)] // 50MB max
        public async Task<ActionResult> UpscaleImage([FromQuery] string model = "realesrgan-x4", [FromQuery] int scale = 2)
        {
            if (RateLimiter.IsRateLimited(HttpContext))
                return StatusCode(429, new { error = "Rate limit exceeded. Max 10 upscale requests per minute." });

            try
            {
                if (!ValidationHelper.IsValidScale(scale))
                    return BadRequest(new { error = "Invalid scale. Allowed values: 2, 3, 4, 8" });

                if (!ValidationHelper.IsValidModelName(model))
                    return BadRequest(new { error = "Invalid model name - only alphanumeric, hyphens, and underscores allowed" });

                if (Request.ContentLength > MaxUploadSizeBytes)
                    return BadRequest(new { error = "Image too large. Maximum size is 50MB." });

                using var memoryStream = new MemoryStream();
                await Request.Body.CopyToAsync(memoryStream);

                if (memoryStream.Length > MaxUploadSizeBytes)
                    return BadRequest(new { error = "Image too large. Maximum size is 50MB." });
                
                var inputImage = memoryStream.ToArray();
                var upscaledImage = await _upscalerCore.UpscaleImageAsync(inputImage, model, scale);
                
                if (upscaledImage == null)
                    return StatusCode(503, new { error = "AI upscaling service unavailable" });
                
                return File(upscaledImage, "image/jpeg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Image upscaling failed");
                return StatusCode(500);
            }
        }

        /// <summary>
        /// Upscale all images for a library item (poster, backdrop, thumbnail, logo).
        /// </summary>
        [HttpPost("upscale-images/{itemId}")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> UpscaleItemImages(
            string itemId,
            [FromQuery] string model = "auto",
            [FromQuery] int scale = 2,
            [FromQuery] string? imageTypes = null)
        {
            if (RateLimiter.IsRateLimited(HttpContext))
                return StatusCode(429, new { error = "Rate limit exceeded. Max 10 upscale requests per minute." });

            try
            {
                if (scale < 1 || scale > 8)
                    return BadRequest(new { success = false, error = "Scale must be between 1 and 8" });

                if (model != "auto" && !ValidationHelper.IsValidModelName(model))
                    return BadRequest(new { success = false, error = "Invalid model name" });

                if (!Guid.TryParse(itemId, out var itemGuid))
                    return BadRequest(new { success = false, error = "Invalid item ID format" });

                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null)
                    return NotFound(new { success = false, error = "Item not found" });

                var targetTypes = ParseImageTypes(imageTypes);
                var results = new List<object>();
                int successCount = 0, failCount = 0;

                foreach (var imageType in targetTypes)
                {
                    var images = item.GetImages(imageType).ToList();
                    if (images.Count == 0) continue;

                    for (int idx = 0; idx < images.Count; idx++)
                    {
                        var imagePath = images[idx].Path;
                        if (string.IsNullOrEmpty(imagePath) || !IOFile.Exists(imagePath))
                            continue;

                        try
                        {
                            var originalData = await IOFile.ReadAllBytesAsync(imagePath);
                            var upscaledData = await _upscalerCore.UpscaleImageAsync(originalData, model, scale);

                            if (upscaledData != null && upscaledData.Length > 0)
                            {
                                var outputPath = GenerateUpscaledImagePath(imagePath);
                                await IOFile.WriteAllBytesAsync(outputPath, upscaledData);

                                successCount++;
                                results.Add(new
                                {
                                    type = imageType.ToString(),
                                    index = idx,
                                    original = Path.GetFileName(imagePath),
                                    upscaled = Path.GetFileName(outputPath),
                                    original_size = originalData.Length,
                                    upscaled_size = upscaledData.Length,
                                    success = true
                                });
                            }
                            else
                            {
                                failCount++;
                                results.Add(new { type = imageType.ToString(), index = idx, success = false, error = "Upscaling returned empty result" });
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            results.Add(new { type = imageType.ToString(), index = idx, success = false, error = "Image upscaling failed" });
                            _logger.LogWarning(ex, "Failed to upscale {Type} image {Index} for item {ItemId}", imageType, idx, itemId);
                        }
                    }
                }

                return Ok(new
                {
                    success = true,
                    item_id = itemId,
                    item_name = item.Name,
                    model,
                    scale,
                    total_processed = successCount + failCount,
                    success_count = successCount,
                    fail_count = failCount,
                    results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upscale images for item {ItemId}", itemId);
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get comparison data for a video item (original vs upscaled frame).
        /// </summary>
        [HttpGet("compare/{itemId}")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> GetComparisonData(
            string itemId,
            [FromQuery] string model = "realesrgan",
            [FromQuery] int scale = 2,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ValidationHelper.IsValidModelName(model))
                    return BadRequest(new { message = "Invalid model name" });

                if (!Guid.TryParse(itemId, out var itemGuid) || itemGuid == Guid.Empty)
                    return BadRequest(new { message = "Invalid item ID format" });

                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null) return NotFound(new { message = "Item not found" });

                var mediaSources = _mediaSourceManager.GetStaticMediaSources(item, true, null);
                var mediaSource = mediaSources?.FirstOrDefault();
                var videoPath = mediaSource?.Path ?? item.Path;
                
                if (string.IsNullOrEmpty(videoPath))
                    return BadRequest(new { message = "No video path — select a movie or episode, not a library folder" });

                _logger.LogInformation("Comparison: extracting frame from {Path}", videoPath);

                var seekPosition = CalculateSeekPosition(mediaSource);
                byte[] originalImageBytes = await _videoProcessor.ExtractSingleFrameAsync(videoPath, seekPosition, cancellationToken);
                byte[] originalData = DownscaleForBrowser(originalImageBytes);
                
                var upscaledData = await _upscalerCore.UpscaleImageAsync(originalData, model, scale);
                if (upscaledData == null)
                    return StatusCode(503, new { message = "AI upscaling service unavailable" });

                return Ok(new
                {
                    itemId = itemId,
                    model = model,
                    scale = scale,
                    originalBase64 = $"data:image/jpeg;base64,{Convert.ToBase64String(originalData)}",
                    upscaledBase64 = $"data:image/jpeg;base64,{Convert.ToBase64String(upscaledData)}",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate comparison data for item {ItemId}", itemId);
                return StatusCode(500, new { message = "Comparison failed", error = "Internal server error" });
            }
        }

        private static List<ImageType> ParseImageTypes(string? imageTypes)
        {
            var defaultTypes = new List<ImageType> { ImageType.Primary, ImageType.Backdrop, ImageType.Thumb, ImageType.Logo, ImageType.Banner };
            
            if (string.IsNullOrEmpty(imageTypes))
                return defaultTypes;

            return imageTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => Enum.TryParse<ImageType>(t, true, out var parsed) ? parsed : (ImageType?)null)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();
        }

        private static string GenerateUpscaledImagePath(string originalPath)
        {
            var dir = Path.GetDirectoryName(originalPath) ?? "";
            var ext = Path.GetExtension(originalPath);
            var baseName = Path.GetFileNameWithoutExtension(originalPath);
            return Path.Combine(dir, baseName + "_upscaled" + ext);
        }

        private static TimeSpan CalculateSeekPosition(MediaBrowser.Model.Dto.MediaSourceInfo? mediaSource)
        {
            var seekPosition = TimeSpan.FromSeconds(10);
            if (mediaSource?.RunTimeTicks != null)
            {
                var totalSeconds = TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value).TotalSeconds;
                if (totalSeconds > 30)
                    seekPosition = TimeSpan.FromSeconds(totalSeconds * 0.10);
            }
            return seekPosition;
        }

        private static byte[] DownscaleForBrowser(byte[] imageBytes)
        {
            using var image = SixLabors.ImageSharp.Image.Load(imageBytes);
            if (image.Width > 1280 || image.Height > 720)
            {
                image.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(1280, 720),
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
                }));
            }
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return ms.ToArray();
        }
    }
}
