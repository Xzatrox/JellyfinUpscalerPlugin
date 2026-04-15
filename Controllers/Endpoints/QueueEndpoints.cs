using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using JellyfinUpscalerPlugin.Services;
using JellyfinUpscalerPlugin.Models;

namespace JellyfinUpscalerPlugin.Controllers.Endpoints
{
    /// <summary>
    /// Queue management endpoints for video processing queue operations.
    /// Handles job enqueueing, cancellation, priority management, and queue control.
    /// </summary>
    public class QueueEndpoints
    {
        private static readonly Regex ValidModelNameRegex = new(@"^[a-zA-Z0-9\-_]+$", RegexOptions.Compiled);

        private readonly ILogger<QueueEndpoints> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ProcessingQueue _processingQueue;

        public QueueEndpoints(
            ILogger<QueueEndpoints> logger,
            ILibraryManager libraryManager,
            ProcessingQueue processingQueue)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _processingQueue = processingQueue;
        }

        /// <summary>
        /// Get queue status — pending, active, completed jobs.
        /// </summary>
        [HttpGet("queue")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> GetQueueStatus()
        {
            return new OkObjectResult(new { success = true, queue = _processingQueue.GetStatus() });
        }

        /// <summary>
        /// Enqueue a video for processing with optional priority.
        /// </summary>
        [HttpPost("queue/add")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> EnqueueJob(
            [FromQuery] string inputPath,
            [FromQuery] string? outputPath = null,
            [FromQuery] string? model = null,
            [FromQuery] int priority = 5,
            [FromQuery] string? itemName = null)
        {
            if (string.IsNullOrEmpty(inputPath))
                return new BadRequestObjectResult(new { success = false, error = "inputPath required" });

            if (model != null && model != "auto" && !ValidModelNameRegex.IsMatch(model))
                return new BadRequestObjectResult(new { success = false, error = "Invalid model name" });

            // Path traversal protection — normalize and validate against library paths (allowlist)
            inputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(inputPath))
                return new BadRequestObjectResult(new { success = false, error = "Input file does not exist" });

            var libraryFolders = _libraryManager.GetVirtualFolders();
            var isInLibrary = libraryFolders.Any(folder =>
                folder.Locations.Any(loc =>
                    inputPath.StartsWith(Path.GetFullPath(loc), StringComparison.OrdinalIgnoreCase)));
            if (!isInLibrary)
                return new BadRequestObjectResult(new { success = false, error = "Input path must be within a Jellyfin media library" });

            if (outputPath != null)
            {
                outputPath = Path.GetFullPath(outputPath);

                // Restrict output to be under the same parent directory as input or under the Jellyfin transcode path
                var inputParent = Path.GetFullPath(Path.GetDirectoryName(inputPath) ?? string.Empty);
                var outputParent = Path.GetFullPath(Path.GetDirectoryName(outputPath) ?? string.Empty);
                var inputParentWithSep = inputParent.EndsWith(Path.DirectorySeparatorChar) ? inputParent : inputParent + Path.DirectorySeparatorChar;
                var transcodePath = Plugin.Instance?.Configuration?.RemoteTranscodePath ?? "";
                var validTranscode = !string.IsNullOrEmpty(transcodePath) && Path.IsPathRooted(transcodePath);
                var transcodeWithSep = validTranscode ? (transcodePath.EndsWith(Path.DirectorySeparatorChar) ? transcodePath : transcodePath + Path.DirectorySeparatorChar) : "";

                if (!outputParent.Equals(inputParent, StringComparison.OrdinalIgnoreCase) &&
                    !outputParent.StartsWith(inputParentWithSep, StringComparison.OrdinalIgnoreCase) &&
                    !(validTranscode && (outputParent.Equals(transcodePath, StringComparison.OrdinalIgnoreCase) ||
                      outputParent.StartsWith(transcodeWithSep, StringComparison.OrdinalIgnoreCase))))
                {
                    return new BadRequestObjectResult(new { success = false, error = "Output path must be under the input directory or transcode path" });
                }
            }

            var effectiveOutput = outputPath ?? Path.Combine(
                Path.GetDirectoryName(inputPath) ?? "",
                Path.GetFileNameWithoutExtension(inputPath) + "_upscaled" + Path.GetExtension(inputPath));

            var config = Plugin.Instance?.Configuration;
            var options = new VideoProcessingOptions
            {
                Model = model ?? config?.Model ?? "auto",
                ScaleFactor = config?.ScaleFactor ?? 2,
                QualityLevel = config?.QualityLevel ?? "medium",
                EnableAIUpscaling = true,
                PreserveAudio = true,
                PreserveSubtitles = true
            };

            var jobId = Guid.NewGuid().ToString("N")[..12];
            var enqueued = _processingQueue.Enqueue(jobId, inputPath, effectiveOutput, options, priority, itemName);

            if (!enqueued)
                return new ObjectResult(new { success = false, error = "Queue is full" }) { StatusCode = 429 };

            return new OkObjectResult(new { success = true, job_id = jobId, position = _processingQueue.QueueSize });
        }

        /// <summary>
        /// Cancel a pending queued job.
        /// </summary>
        [HttpPost("queue/{jobId}/cancel")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> CancelQueuedJob(string jobId)
        {
            var cancelled = _processingQueue.Cancel(jobId);
            return new OkObjectResult(new { success = cancelled, job_id = jobId });
        }

        /// <summary>
        /// Change priority of a pending job (1=highest, 10=lowest).
        /// </summary>
        [HttpPost("queue/{jobId}/priority")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> SetJobPriority(string jobId, [FromQuery] int priority)
        {
            if (priority < 1 || priority > 10)
                return new BadRequestObjectResult(new { success = false, error = "Priority must be 1-10" });

            var updated = _processingQueue.SetPriority(jobId, priority);
            return new OkObjectResult(new { success = updated, job_id = jobId, priority });
        }

        /// <summary>
        /// Pause the processing queue (active jobs finish, no new jobs start).
        /// </summary>
        [HttpPost("queue/pause")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> PauseQueue()
        {
            _processingQueue.Pause();
            return new OkObjectResult(new { success = true, paused = true });
        }

        /// <summary>
        /// Resume the processing queue.
        /// </summary>
        [HttpPost("queue/resume")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        public ActionResult<object> ResumeQueue()
        {
            _processingQueue.Resume();
            return new OkObjectResult(new { success = true, paused = false });
        }
    }
}
