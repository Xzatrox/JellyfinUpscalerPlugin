using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using JellyfinUpscalerPlugin.Services;
using JellyfinUpscalerPlugin.Models;
using JellyfinUpscalerPlugin.Controllers.Helpers;
using System.Net.Mime;
using IOFile = System.IO.File;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Video processing endpoints.
    /// </summary>
    [ApiController]
    [Authorize]
    public class VideoEndpoints : ControllerBase
    {
        private readonly ILogger<VideoEndpoints> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly VideoProcessor _videoProcessor;

        public VideoEndpoints(
            ILogger<VideoEndpoints> logger,
            ILibraryManager libraryManager,
            VideoProcessor videoProcessor)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _videoProcessor = videoProcessor;
        }

        /// <summary>
        /// Process a video file with AI upscaling.
        /// </summary>
        [HttpPost("Upscaler/process")]
        [Consumes(MediaTypeNames.Application.Json)]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> ProcessVideo([FromBody] VideoProcessRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.InputPath) || !IOFile.Exists(request.InputPath))
                    return BadRequest(new { success = false, error = "Input file not found" });

                if (string.IsNullOrEmpty(request.OutputPath))
                    return BadRequest(new { success = false, error = "Output path required" });

                if (!ValidateOutputPath(request.InputPath, request.OutputPath))
                    return BadRequest(new { success = false, error = "Output path must be in the same directory as the input file" });

                var fullInputPath = Path.GetFullPath(request.InputPath);
                var fullOutputPath = Path.GetFullPath(request.OutputPath);
                var outputDir = Path.GetDirectoryName(fullOutputPath);

                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                var options = new VideoProcessingOptions
                {
                    Model = request.Model ?? "auto",
                    Scale = request.Scale ?? 2,
                    Quality = request.Quality ?? "medium"
                };
                
                var result = await _videoProcessor.ProcessVideoAsync(fullInputPath, fullOutputPath, options);
                
                return Ok(new 
                {
                    success = result.Success,
                    outputPath = result.OutputPath,
                    processingTime = result.ProcessingTime.TotalSeconds,
                    method = result.Method.ToString(),
                    error = result.Error
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Video processing failed");
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Process a library item by ID.
        /// </summary>
        [HttpPost("Upscaler/process/item/{itemId}")]
        [Produces(MediaTypeNames.Application.Json)]
        public async Task<ActionResult<object>> ProcessItem(
            string itemId, 
            [FromQuery] string? model = null, 
            [FromQuery] int? scale = null)
        {
            try
            {
                if (model != null && model != "auto" && !ValidationHelper.IsValidModelName(model))
                    return BadRequest(new { message = "Invalid model name" });

                if (!Guid.TryParse(itemId, out var itemGuid))
                    return BadRequest(new { message = "Invalid item ID format" });
                
                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null) 
                    return NotFound(new { message = "Item not found" });

                if (string.IsNullOrEmpty(item.Path) || !IOFile.Exists(item.Path))
                    return BadRequest(new { message = "Item path not found or invalid" });

                var config = Plugin.Instance?.Configuration;
                var options = new VideoProcessingOptions
                {
                    Model = model ?? config?.Model ?? "auto",
                    ScaleFactor = scale ?? config?.ScaleFactor ?? 2,
                    QualityLevel = config?.QualityLevel ?? "medium",
                    EnableAIUpscaling = true
                };

                var directory = Path.GetDirectoryName(item.Path);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    return BadRequest(new { message = "Output directory not accessible" });

                var outputPath = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(item.Path) + "_upscaled" + Path.GetExtension(item.Path)
                );

                var result = await _videoProcessor.ProcessVideoAsync(item.Path, outputPath, options);

                return Ok(new 
                { 
                    success = result.Success, 
                    itemId = itemId, 
                    outputPath = result.OutputPath, 
                    error = result.Error 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process item {ItemId}", itemId);
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Get active video processing jobs.
        /// </summary>
        [HttpGet("Upscaler/jobs")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> GetActiveJobs()
        {
            try
            {
                var jobs = _videoProcessor.GetActiveJobs();
                return Ok(new { success = true, jobs = jobs });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve active jobs");
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Pause a video processing job.
        /// </summary>
        [HttpPost("Upscaler/jobs/{jobId}/pause")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> PauseJob(string jobId)
        {
            try
            {
                var result = _videoProcessor.PauseJob(jobId);
                return result 
                    ? Ok(new { success = true, message = $"Job {jobId} paused" })
                    : NotFound(new { success = false, message = "Job not found or cannot be paused" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause job {JobId}", jobId);
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Resume a paused video processing job.
        /// </summary>
        [HttpPost("Upscaler/jobs/{jobId}/resume")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> ResumeJob(string jobId)
        {
            try
            {
                var result = _videoProcessor.ResumeJob(jobId);
                return result
                    ? Ok(new { success = true, message = $"Job {jobId} resumed" })
                    : NotFound(new { success = false, message = "Job not found or cannot be resumed" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume job {JobId}", jobId);
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        /// <summary>
        /// Cancel a video processing job.
        /// </summary>
        [HttpPost("Upscaler/jobs/{jobId}/cancel")]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> CancelJob(string jobId)
        {
            try
            {
                var result = _videoProcessor.CancelJob(jobId);
                return result
                    ? Ok(new { success = true, message = $"Job {jobId} cancelled" })
                    : NotFound(new { success = false, message = "Job not found or cannot be cancelled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel job {JobId}", jobId);
                return StatusCode(500, new { success = false, error = "Internal server error" });
            }
        }

        private bool ValidateOutputPath(string inputPath, string outputPath)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            var fullOutputPath = Path.GetFullPath(outputPath);

            var inputDir = Path.GetFullPath(Path.GetDirectoryName(fullInputPath) ?? string.Empty);
            var outputDir = Path.GetFullPath(Path.GetDirectoryName(fullOutputPath) ?? string.Empty);
            var inputDirWithSep = inputDir.EndsWith(Path.DirectorySeparatorChar) 
                ? inputDir 
                : inputDir + Path.DirectorySeparatorChar;

            if (inputDir == null || outputDir == null)
                return false;

            if (outputDir.Equals(inputDir, StringComparison.OrdinalIgnoreCase))
                return true;

            if (outputDir.StartsWith(inputDirWithSep, StringComparison.OrdinalIgnoreCase))
                return true;

            _logger.LogWarning("Output path {OutputDir} is not under input directory {InputDir}", outputDir, inputDir);
            return false;
        }
    }
}
