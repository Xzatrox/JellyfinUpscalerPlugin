using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace JellyfinUpscalerPlugin.Controllers.Helpers
{
    /// <summary>
    /// Per-user sliding-window rate limiter for upscale endpoints.
    /// </summary>
    public class RateLimiter
    {
        private const int MaxRequests = 10;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _tracker = new();

        /// <summary>
        /// Check if the request should be rate limited.
        /// Returns true if the request should be rejected (rate exceeded).
        /// </summary>
        public static bool IsRateLimited(HttpContext httpContext)
        {
            var userId = httpContext.User?.Identity?.Name 
                ?? httpContext.Connection.RemoteIpAddress?.ToString() 
                ?? "unknown";
            
            var now = DateTime.UtcNow;
            var entry = _tracker.AddOrUpdate(
                userId,
                _ => (1, now),
                (_, existing) =>
                {
                    if (now - existing.WindowStart > Window)
                        return (1, now);
                    return (existing.Count + 1, existing.WindowStart);
                });

            // Opportunistic pruning to prevent unbounded growth
            if (_tracker.Count > 500)
            {
                var cutoff = now - Window;
                foreach (var key in _tracker.Keys)
                {
                    if (_tracker.TryGetValue(key, out var v) && v.WindowStart < cutoff)
                        _tracker.TryRemove(key, out _);
                }
            }

            return entry.Count > MaxRequests;
        }
    }
}
