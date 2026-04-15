using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace JellyfinUpscalerPlugin.Services
{
    public interface IFFmpegWrapperService
    {
        Task<string> GenerateWrapperScriptAsync();
        Task<bool> InstallWrapperAsync();
        Task<bool> UninstallWrapperAsync();
        bool IsWrapperInstalled();
        string GetWrapperPath();
        string GetOriginalFfmpegPath();
    }

    public class FFmpegWrapperService : IFFmpegWrapperService
    {
        private readonly ILogger<FFmpegWrapperService> _logger;
        private readonly IPlatformDetectionService _platformService;
        private readonly IServerConfigurationManager _serverConfig;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly string _pluginDirectory;
        private string? _originalFfmpegPath;

        public FFmpegWrapperService(
            ILogger<FFmpegWrapperService> logger,
            IPlatformDetectionService platformService,
            IServerConfigurationManager serverConfig,
            IMediaEncoder mediaEncoder)
        {
            _logger = logger;
            _platformService = platformService;
            _serverConfig = serverConfig;
            _mediaEncoder = mediaEncoder;
            _pluginDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
            
            // Load saved original FFmpeg path if it exists
            var originalPathFile = Path.Combine(_pluginDirectory, "original_ffmpeg_path.txt");
            if (File.Exists(originalPathFile))
            {
                _originalFfmpegPath = File.ReadAllText(originalPathFile).Trim();
            }
        }

        public async Task<string> GenerateWrapperScriptAsync()
        {
            var config = Plugin.Instance?.Configuration;
            var scriptExtension = _platformService.GetScriptExtension();
            var wrapperPath = Path.Combine(_pluginDirectory, $"upscale-wrapper{scriptExtension}");
            var psScriptPath = Path.Combine(_pluginDirectory, "upscale-logic.ps1");
            var framePipePath = Path.Combine(_pluginDirectory, "frame_pipe.py");
            
            // Determine real FFmpeg path
            var realFFmpegPath = _mediaEncoder.EncoderPath;
            if (string.IsNullOrEmpty(realFFmpegPath))
            {
                _logger.LogError("FFmpeg path not found from IMediaEncoder. Cannot generate wrapper script without a valid FFmpeg path");
                throw new InvalidOperationException("FFmpeg path not available. Ensure Jellyfin has a valid FFmpeg path configured.");
            }
            
            var logPath = Path.Combine(_pluginDirectory, "wrapper.log");
            var activeMarkerPath = Path.Combine(_pluginDirectory, "wrapper_active");

            string scriptContent;

            if (_platformService.IsWindows)
            {
                // On Windows, we define the Batch entrypoint AND the PowerShell logic script
                scriptContent = GenerateWindowsBatchScript(psScriptPath);
                
                // Generate the PowerShell logic script (handles path mapping & SSH)
                var psContent = GenerateWindowsPowerShellScript(realFFmpegPath, logPath, activeMarkerPath, config ?? new PluginConfiguration());
                await File.WriteAllTextAsync(psScriptPath, psContent);
            }
            else
            {
                // Unix - frame pipe logic for real-time upscaling
                scriptContent = GenerateUnixScript(realFFmpegPath, logPath, activeMarkerPath);
                
                // Copy frame_pipe.py to plugin directory if it doesn't exist
                var sourcePipePath = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "..", "Scripts", "frame_pipe.py");
                if (File.Exists(sourcePipePath) && !File.Exists(framePipePath))
                {
                    File.Copy(sourcePipePath, framePipePath, true);
                    _logger.LogInformation("Copied frame_pipe.py to plugin directory");
                }
            }

            await File.WriteAllTextAsync(wrapperPath, scriptContent);

            if (!_platformService.IsWindows)
            {
                try
                {
                    // Make wrapper script executable
                    using var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{wrapperPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(cts.Token);
                    
                    // Make frame_pipe.py executable
                    if (File.Exists(framePipePath))
                    {
                        using var process2 = new System.Diagnostics.Process
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "chmod",
                                Arguments = $"+x \"{framePipePath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        process2.Start();
                        using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await process2.WaitForExitAsync(cts2.Token);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set executable permission on wrapper script");
                }
            }

            _logger.LogInformation("Generated FFmpeg wrapper script at: {WrapperPath}", wrapperPath);
            if (_platformService.IsWindows) _logger.LogInformation("Generated PowerShell logic script at: {PsScriptPath}", psScriptPath);
            
            return wrapperPath;
        }

        private string GenerateWindowsBatchScript(string psScriptPath)
        {
            // Simple Batch wrapper that passes everything to PowerShell
            // We use -ExecutionPolicy Bypass to ensure it runs
            return $@"@echo off
pwsh -ExecutionPolicy Bypass -File ""{psScriptPath}"" %*
if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%
";
        }

        // Security: Sanitize a string for safe embedding in PowerShell double-quoted strings
        private static string SanitizeForPowerShell(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            // Escape backticks, double quotes, dollar signs, and newlines
            return input
                .Replace("`", "``")
                .Replace("\"", "`\"")
                .Replace("$", "`$")
                .Replace("\r", "")
                .Replace("\n", "");
        }

        private string GenerateWindowsPowerShellScript(string realFFmpegPath, string logPath, string activeMarkerPath, PluginConfiguration config)
        {
            bool enableRemote = config?.EnableRemoteTranscoding ?? false;
            string remoteUser = SanitizeForPowerShell(config?.RemoteUser ?? "root");
            string remoteHost = SanitizeForPowerShell(config?.RemoteHost ?? "localhost");
            int remotePort = Math.Clamp(config?.RemoteSshPort ?? 2222, 1, 65535);
            string keyFile = SanitizeForPowerShell(config?.RemoteSshKeyFile ?? "");

            string localMount = SanitizeForPowerShell(config?.LocalMediaMountPoint?.Replace("\\", "\\\\") ?? "");
            string remoteMount = SanitizeForPowerShell(config?.RemoteMediaMountPoint ?? "");
            string transcodeDir = SanitizeForPowerShell(config?.RemoteTranscodePath ?? "/transcode");

            string sanitizedFFmpegPath = SanitizeForPowerShell(realFFmpegPath);
            string sanitizedLogPath = SanitizeForPowerShell(logPath);
            string sanitizedMarkerPath = SanitizeForPowerShell(activeMarkerPath);

            // If remote is disabled, fall back to simple local execution with logging
            if (!enableRemote)
            {
                 return $@"
$RealFFmpeg = ""{sanitizedFFmpegPath}""
$LogFile = ""{sanitizedLogPath}""
$ActiveMarker = ""{sanitizedMarkerPath}""

if (-not (Test-Path $ActiveMarker)) {{
    & $RealFFmpeg @args
    exit $LASTEXITCODE
}}

Add-Content -Path $LogFile -Value ""[$(Get-Date)] Upscaler active (Local Mode)""
& $RealFFmpeg @args
exit $LASTEXITCODE
";
            }

            // Remote SSH Logic
            return $@"
$RealFFmpeg = ""{sanitizedFFmpegPath}""
$LogFile = ""{sanitizedLogPath}""
$ActiveMarker = ""{sanitizedMarkerPath}""

# Configuration
$RemoteUser = ""{remoteUser}""
$RemoteHost = ""{remoteHost}""
$RemotePort = {remotePort}
$KeyFile = ""{keyFile}""
$LocalMount = ""{localMount}""
$RemoteMount = ""{remoteMount}""
$RemoteTranscodeDir = ""{transcodeDir}""

# If Upcaler is disabled via marker file, run local FFmpeg
if (-not (Test-Path $ActiveMarker)) {{
    & $RealFFmpeg @args
    exit $LASTEXITCODE
}}

try {{
    $CmdArgs = @()
    
    # Iterate arguments for Path Mapping
    for ($i = 0; $i -lt $args.Count; $i++) {{
        $arg = $args[$i]
        
        # Map Input Files (-i)
        if ($arg -eq '-i' -and ($i + 1) -lt $args.Count) {{
            $CmdArgs += '-i'
            $path = $args[$i+1]
            
            # Translate Path
            if ($path -like ""$LocalMount*"") {{
                $relPath = $path.Substring($LocalMount.Length).Replace('\', '/')
                if (-not $relPath.StartsWith('/')) {{ $relPath = '/' + $relPath }}
                $newPath = ""$RemoteMount$relPath""
                $CmdArgs += $newPath
                $i++ # Skip next arg
            }} else {{
                $CmdArgs += $path
                $i++
            }}
            continue
        }}

        # Map Transcode Directory (if present in arguments)
        # Jellyfin often passes fully qualified paths for output
        if ($arg -like ""$LocalMount*"") {{
             $relPath = $arg.Substring($LocalMount.Length).Replace('\', '/')
             if (-not $relPath.StartsWith('/')) {{ $relPath = '/' + $relPath }}
             $newPath = ""$RemoteMount$relPath""
             $CmdArgs += $newPath
             continue
        }}
        
        # Pass through other args
        $CmdArgs += $arg
    }}

    # Construct SSH Command
    # We use -o BatchMode=yes to fail fast if auth fails
    # We strictly map stderr to host stderr to keep Jellyfin informed
    
    $SshArgs = @(
        '-p', $RemotePort,
        '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=accept-new',
        ""$RemoteUser@$RemoteHost""
    )
    
    if (-not [string]::IsNullOrWhiteSpace($KeyFile)) {{
        $SshArgs = @('-i', $KeyFile) + $SshArgs
    }}

    # The command to run inside Docker
    # We explicitly call the internal ffmpeg
    # Quote each argument to prevent shell metacharacter injection
    $QuotedArgs = $CmdArgs | ForEach-Object {{ ""'$($_.Replace(""'"", ""'\''""))'"" }}
    $RemoteCommand = ""ffmpeg "" + ($QuotedArgs -join ' ')

    Add-Content -Path $LogFile -Value ""[$(Get-Date)] Remote Command: $RemoteCommand""

    # Execute SSH
    # 2>&1 ensures stderr is piped back
    & ssh $SshArgs $RemoteCommand
    exit $LASTEXITCODE

}} catch {{
    Add-Content -Path $LogFile -Value ""[$(Get-Date)] Error: $_""
    exit 1
}}
";
        }

        private string GenerateUnixScript(string realFFmpegPath, string logPath, string activeMarkerPath)
        {
            // Sanitize: only allow safe characters in the path to prevent shell injection
            if (!System.Text.RegularExpressions.Regex.IsMatch(realFFmpegPath, @"^[a-zA-Z0-9/\-_.]+$"))
            {
                _logger.LogWarning("FFmpeg path contains unsafe characters ({Path}), using fallback /usr/bin/ffmpeg", realFFmpegPath);
                realFFmpegPath = "/usr/bin/ffmpeg";
            }

            var config = Plugin.Instance?.Configuration;
            var aiUrl = config?.AiServiceUrl ?? "http://localhost:5000";
            var apiToken = config?.AiServiceApiToken ?? "";
            var framePipePath = Path.Combine(_pluginDirectory, "frame_pipe.py");
            
            // Sanitize paths for bash
            var sanitizedFramePipePath = framePipePath.Replace("\"", "\\\"");
            var sanitizedAiUrl = aiUrl.Replace("\"", "\\\"");
            var sanitizedApiToken = apiToken.Replace("\"", "\\\"");

            return $@"#!/bin/bash
# AI Upscaler Plugin - FFmpeg Wrapper with Frame Pipe
REAL_FFMPEG=""{realFFmpegPath}""
AI_URL=""{sanitizedAiUrl}""
API_TOKEN=""{sanitizedApiToken}""
FRAME_PIPE=""{sanitizedFramePipePath}""
ACTIVE_MARKER=""{activeMarkerPath}""
LOG_FILE=""{logPath}""

# If wrapper is not active, pass through to real FFmpeg
if [ ! -f ""$ACTIVE_MARKER"" ]; then
    exec ""$REAL_FFMPEG"" ""$@""
fi

# Detect if this is a video transcode (has -vcodec or -c:v, not audio-only or probe)
IS_VIDEO_TRANSCODE=0
HAS_VIDEO_CODEC=0
IS_AUDIO_ONLY=0

for arg in ""$@""; do
    case ""$arg"" in
        -vcodec|-c:v)
            HAS_VIDEO_CODEC=1
            ;;
        -vn)
            IS_AUDIO_ONLY=1
            ;;
        -version)
            # FFmpeg version probe - pass through
            exec ""$REAL_FFMPEG"" ""$@""
            ;;
    esac
done

# Only intercept video transcodes (not audio-only, not probes)
if [ $HAS_VIDEO_CODEC -eq 1 ] && [ $IS_AUDIO_ONLY -eq 0 ]; then
    IS_VIDEO_TRANSCODE=1
fi

# If not a video transcode, pass through
if [ $IS_VIDEO_TRANSCODE -eq 0 ]; then
    exec ""$REAL_FFMPEG"" ""$@""
fi

# Log the interception
echo ""[$(date)] Intercepting video transcode for AI upscaling"" >> ""$LOG_FILE""

# Extract input resolution from FFmpeg args
# This is a simplified approach - in production, you'd parse more carefully
INPUT_WIDTH=1920
INPUT_HEIGHT=1080
SCALE_FACTOR=2

# Parse arguments to find input file and extract resolution
INPUT_FILE=""""
for i in ""${{!@}}""; do
    if [ ""${{!i}}"" = ""-i"" ]; then
        next_idx=$((i+1))
        INPUT_FILE=""${{!next_idx}}""
        break
    fi
done

# Use ffprobe to get actual resolution if input file is available
if [ -n ""$INPUT_FILE"" ] && [ -f ""$INPUT_FILE"" ]; then
    PROBE_OUTPUT=$(""${{REAL_FFMPEG%/*}}/ffprobe"" -v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 ""$INPUT_FILE"" 2>/dev/null)
    if [ -n ""$PROBE_OUTPUT"" ]; then
        INPUT_WIDTH=$(echo ""$PROBE_OUTPUT"" | cut -d'x' -f1)
        INPUT_HEIGHT=$(echo ""$PROBE_OUTPUT"" | cut -d'x' -f2)
    fi
fi

TARGET_WIDTH=$((INPUT_WIDTH * SCALE_FACTOR))
TARGET_HEIGHT=$((INPUT_HEIGHT * SCALE_FACTOR))

echo ""[$(date)] Input: ${{INPUT_WIDTH}}x${{INPUT_HEIGHT}}, Target: ${{TARGET_WIDTH}}x${{TARGET_HEIGHT}}"" >> ""$LOG_FILE""

# Create temporary named pipes
PIPE_IN=""/tmp/upscale_in_$$""
PIPE_OUT=""/tmp/upscale_out_$$""
mkfifo ""$PIPE_IN"" ""$PIPE_OUT""

# Cleanup function
cleanup() {{
    rm -f ""$PIPE_IN"" ""$PIPE_OUT""
}}
trap cleanup EXIT

# Check if Python3 and frame_pipe.py are available
if [ ! -f ""$FRAME_PIPE"" ]; then
    echo ""[$(date)] ERROR: frame_pipe.py not found at $FRAME_PIPE, falling back to direct FFmpeg"" >> ""$LOG_FILE""
    exec ""$REAL_FFMPEG"" ""$@""
fi

if ! command -v python3 &> /dev/null; then
    echo ""[$(date)] ERROR: python3 not found, falling back to direct FFmpeg"" >> ""$LOG_FILE""
    exec ""$REAL_FFMPEG"" ""$@""
fi

# Start the frame pipe processor in background
python3 ""$FRAME_PIPE"" \
    --input ""$PIPE_IN"" \
    --output ""$PIPE_OUT"" \
    --ai-url ""$AI_URL"" \
    --token ""$API_TOKEN"" \
    --width ""$INPUT_WIDTH"" \
    --height ""$INPUT_HEIGHT"" \
    --scale ""$SCALE_FACTOR"" \
    --drop-on-slow \
    >> ""$LOG_FILE"" 2>&1 &

FRAME_PIPE_PID=$!

# Build FFmpeg decode command (extract frames to pipe)
# We need to separate input args from output args
DECODE_ARGS=()
OUTPUT_ARGS=()
FOUND_OUTPUT=0

for arg in ""$@""; do
    if [ ""$arg"" = ""-f"" ] || [ ""$arg"" = ""-vcodec"" ] || [ ""$arg"" = ""-c:v"" ]; then
        FOUND_OUTPUT=1
    fi
    
    if [ $FOUND_OUTPUT -eq 0 ]; then
        DECODE_ARGS+=(""$arg"")
    else
        OUTPUT_ARGS+=(""$arg"")
    fi
done

# Decode to raw frames and pipe to frame processor
""$REAL_FFMPEG"" ""${{DECODE_ARGS[@]}}"" -f rawvideo -pix_fmt rgb24 ""$PIPE_IN"" &
DECODE_PID=$!

# Encode upscaled frames from pipe to final output
""$REAL_FFMPEG"" -f rawvideo -pix_fmt rgb24 -s ""${{TARGET_WIDTH}}x${{TARGET_HEIGHT}}"" -i ""$PIPE_OUT"" ""${{OUTPUT_ARGS[@]}}""
ENCODE_EXIT=$?

# Wait for background processes
wait $FRAME_PIPE_PID 2>/dev/null
wait $DECODE_PID 2>/dev/null

echo ""[$(date)] Transcode completed with exit code $ENCODE_EXIT"" >> ""$LOG_FILE""

exit $ENCODE_EXIT
";
        }

        public async Task<bool> InstallWrapperAsync()
        {
            try
            {
                // Generate the wrapper script
                var wrapperPath = await GenerateWrapperScriptAsync();
                
                // Create active marker
                var activeMarkerPath = Path.Combine(_pluginDirectory, "wrapper_active");
                await File.WriteAllTextAsync(activeMarkerPath, DateTime.UtcNow.ToString("O"));

                // Auto-configure Jellyfin's FFmpeg path
                try
                {
                    var encodingConfig = _serverConfig.GetConfiguration("encoding");
                    var encodingConfigType = encodingConfig.GetType();
                    var encoderPathProperty = encodingConfigType.GetProperty("EncoderAppPath");
                    
                    if (encoderPathProperty != null)
                    {
                        // Save original FFmpeg path before overwriting
                        var currentPath = encoderPathProperty.GetValue(encodingConfig) as string;
                        if (!string.IsNullOrEmpty(currentPath) && currentPath != wrapperPath)
                        {
                            _originalFfmpegPath = currentPath;
                            var originalPathFile = Path.Combine(_pluginDirectory, "original_ffmpeg_path.txt");
                            await File.WriteAllTextAsync(originalPathFile, currentPath);
                            _logger.LogInformation("Saved original FFmpeg path: {OriginalPath}", currentPath);
                        }
                        
                        // Set wrapper as new FFmpeg path
                        encoderPathProperty.SetValue(encodingConfig, wrapperPath);
                        _serverConfig.SaveConfiguration("encoding", encodingConfig);
                        
                        _logger.LogInformation("Auto-configured Jellyfin FFmpeg path to wrapper: {WrapperPath}", wrapperPath);
                        _logger.LogWarning("IMPORTANT: Restart Jellyfin for the FFmpeg path change to take effect");
                    }
                    else
                    {
                        _logger.LogWarning("Could not auto-configure FFmpeg path. Please manually set it to: {WrapperPath}", wrapperPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-configure Jellyfin FFmpeg path. Please manually set it to: {WrapperPath}", wrapperPath);
                }

                _logger.LogInformation("FFmpeg wrapper installed successfully at: {WrapperPath}", wrapperPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to install FFmpeg wrapper");
                return false;
            }
        }

        public async Task<bool> UninstallWrapperAsync()
        {
            try
            {
                // Remove active marker
                var activeMarkerPath = Path.Combine(_pluginDirectory, "wrapper_active");
                if (File.Exists(activeMarkerPath))
                {
                    File.Delete(activeMarkerPath);
                }

                // Restore original FFmpeg path
                try
                {
                    if (!string.IsNullOrEmpty(_originalFfmpegPath))
                    {
                        var encodingConfig = _serverConfig.GetConfiguration("encoding");
                        var encodingConfigType = encodingConfig.GetType();
                        var encoderPathProperty = encodingConfigType.GetProperty("EncoderAppPath");
                        
                        if (encoderPathProperty != null)
                        {
                            encoderPathProperty.SetValue(encodingConfig, _originalFfmpegPath);
                            _serverConfig.SaveConfiguration("encoding", encodingConfig);
                            
                            _logger.LogInformation("Restored original FFmpeg path: {OriginalPath}", _originalFfmpegPath);
                            _logger.LogWarning("IMPORTANT: Restart Jellyfin for the FFmpeg path change to take effect");
                        }
                        
                        // Clean up saved path file
                        var originalPathFile = Path.Combine(_pluginDirectory, "original_ffmpeg_path.txt");
                        if (File.Exists(originalPathFile))
                        {
                            File.Delete(originalPathFile);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No original FFmpeg path saved. Please manually restore FFmpeg path in Jellyfin settings");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore original FFmpeg path. Please manually restore it in Jellyfin settings");
                }

                _logger.LogInformation("FFmpeg wrapper uninstalled successfully");
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to uninstall FFmpeg wrapper");
                return false;
            }
        }

        public bool IsWrapperInstalled()
        {
            var activeMarkerPath = Path.Combine(_pluginDirectory, "wrapper_active");
            return File.Exists(activeMarkerPath);
        }

        public string GetWrapperPath()
        {
            var scriptExtension = _platformService.GetScriptExtension();
            return Path.Combine(_pluginDirectory, $"upscale-wrapper{scriptExtension}");
        }

        public string GetOriginalFfmpegPath()
        {
            return _originalFfmpegPath ?? _mediaEncoder.EncoderPath ?? "Unknown";
        }
    }
}
