using System;
using System.Text.RegularExpressions;

namespace JellyfinUpscalerPlugin.Controllers.Helpers
{
    /// <summary>
    /// Input validation helpers for controller endpoints.
    /// </summary>
    public static class ValidationHelper
    {
        private static readonly Regex ValidModelNameRegex = new(@"^[a-zA-Z0-9\-_]+$", RegexOptions.Compiled);
        private static readonly Regex ValidHostRegex = new(@"^[a-zA-Z0-9.\-:]+$", RegexOptions.Compiled);
        private static readonly Regex ValidUserRegex = new(@"^[a-zA-Z0-9._\-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Validate model name (alphanumeric, hyphens, underscores only).
        /// </summary>
        public static bool IsValidModelName(string modelName)
        {
            return !string.IsNullOrEmpty(modelName) && ValidModelNameRegex.IsMatch(modelName);
        }

        /// <summary>
        /// Validate host format for SSH connections.
        /// </summary>
        public static bool IsValidHost(string host)
        {
            return !string.IsNullOrEmpty(host) && ValidHostRegex.IsMatch(host);
        }

        /// <summary>
        /// Validate username format for SSH connections.
        /// </summary>
        public static bool IsValidUser(string user)
        {
            return !string.IsNullOrEmpty(user) && ValidUserRegex.IsMatch(user);
        }

        /// <summary>
        /// Validate scale factor.
        /// </summary>
        public static bool IsValidScale(int scale)
        {
            var allowedScales = new[] { 2, 3, 4, 8 };
            return Array.Exists(allowedScales, s => s == scale);
        }

        /// <summary>
        /// Validate URL and reject non-http(s) schemes and control characters.
        /// </summary>
        public static bool IsValidServiceUrl(string url, out Uri? validUri)
        {
            validUri = null;
            
            if (string.IsNullOrEmpty(url))
                return false;

            // Reject URLs containing control characters that could enable header injection
            if (url.IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0)
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out validUri))
                return false;

            return validUri.Scheme == "http" || validUri.Scheme == "https";
        }

        /// <summary>
        /// Validate quality level.
        /// </summary>
        public static bool IsValidQualityLevel(string quality)
        {
            var validLevels = new[] { "fast", "medium", "high" };
            return Array.Exists(validLevels, q => q.Equals(quality, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate codec.
        /// </summary>
        public static bool IsValidCodec(string codec)
        {
            var validCodecs = new[] { "libx264", "libx265", "copy" };
            return Array.Exists(validCodecs, c => c.Equals(codec, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate realtime mode.
        /// </summary>
        public static bool IsValidRealtimeMode(string mode)
        {
            var validModes = new[] { "auto", "webgl", "server" };
            return Array.Exists(validModes, m => m.Equals(mode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validate filter preset.
        /// </summary>
        public static bool IsValidFilterPreset(string preset)
        {
            var validPresets = new[] { "none", "cinematic", "vintage", "vivid", "noir", "warm", 
                "cool", "hdr-pop", "sepia", "pastel", "cyberpunk", "drama", "soft-glow", 
                "sharp-hd", "retrogame", "teal-orange", "custom" };
            return Array.Exists(validPresets, p => p.Equals(preset, StringComparison.OrdinalIgnoreCase));
        }
    }
}
