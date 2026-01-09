#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Baklava.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    /// <summary>
    /// Controller for managing and serving external subtitles
    /// </summary>
    [ApiController]
    [Route("api/baklava/subtitles")]
    public class SubtitleController : ControllerBase
    {
        private readonly ILogger<SubtitleController> _logger;
        private readonly BaklavaDbService _dbService;
        private readonly HttpClient _httpClient;
        private readonly SubtitleService _subtitleService;

        public SubtitleController(
            ILogger<SubtitleController> logger,
            BaklavaDbService dbService,
            IHttpClientFactory httpClientFactory,
            SubtitleService subtitleService)
        {
            _logger = logger;
            _dbService = dbService;
            _httpClient = httpClientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _subtitleService = subtitleService;
        }

        /// <summary>
        /// Fetch external subtitles for an item from Gelato/Stremio
        /// </summary>
        [HttpGet("fetch")]
        public async Task<ActionResult> FetchExternalSubtitles([FromQuery] string itemId)
        {
            // Check if external subtitles are enabled
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.EnableExternalSubtitles)
            {
                return BadRequest(new { error = "External subtitles are disabled in Baklava configuration" });
            }

            if (string.IsNullOrEmpty(itemId))
            {
                return BadRequest(new { error = "Invalid itemId" });
            }

            try
            {
                var subtitles = await _subtitleService.FetchExternalSubtitlesAsync(itemId);
                
                if (subtitles != null && subtitles.Count > 0)
                {
                     return Ok(new { 
                        itemId = itemId,
                        subtitles = subtitles.Select(s => new {
                            id = s.Id,
                            language = s.Language,
                            title = s.Title,
                            baklavaUrl = $"/api/baklava/subtitles/proxy?itemId={itemId}&lang={s.Language}&subId={s.Id}"
                        })
                    });
                }

                return NotFound(new { error = "No subtitles found in Gelato cache" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Subtitle] Failed to fetch external subtitles for {ItemId}", itemId);
                return StatusCode(500, new { error = "Failed to fetch subtitles", details = ex.Message });
            }
        }

        /// <summary>
        /// Proxy/serve external subtitle from cache
        /// </summary>
        [HttpGet("proxy")]
        [AllowAnonymous] // Allow clients to fetch subtitles without auth (they'll use session token)
        public async Task<ActionResult> ProxySubtitle(
            [FromQuery] string itemId,
            [FromQuery] string? lang,
            [FromQuery] string? subId,
            [FromQuery] string? format) // "vtt" for web clients that need conversion
        {
            _logger.LogInformation("[Baklava.Subtitle] 🔵 [SUBTITLE PROXY] Request received: itemId={ItemId}, lang={Lang}, subId={SubId}, format={Format}", itemId, lang, subId, format);

            if (string.IsNullOrEmpty(itemId))
            {
                _logger.LogWarning("[Baklava.Subtitle] 🔴 [SUBTITLE PROXY] Invalid itemId: empty or null");
                return BadRequest("Invalid itemId");
            }

            try
            {
                // Get from cache via DB service directly as it's just retrieval
                _logger.LogInformation("[Baklava.Subtitle] 🔵 [SUBTITLE PROXY] Fetching cached subtitles for item {ItemId}...", itemId);
                var subtitles = await _dbService.GetExternalSubtitlesAsync(itemId);
                if (subtitles == null || subtitles.Count == 0)
                {
                    _logger.LogWarning("[Baklava.Subtitle] 🔴 [SUBTITLE PROXY] No cached subtitles found for item {ItemId}", itemId);
                    return NotFound("No cached subtitles");
                }
                
                _logger.LogInformation("[Baklava.Subtitle] 🔵 [SUBTITLE PROXY] Found {Count} cached subtitles for item {ItemId}", subtitles.Count, itemId);

                // Find matching subtitle
                ExternalSubtitleInfo? targetSub = null;
                if (!string.IsNullOrEmpty(subId))
                {
                    targetSub = subtitles.FirstOrDefault(s => s.Id == subId);
                }
                else if (!string.IsNullOrEmpty(lang))
                {
                    targetSub = subtitles.FirstOrDefault(s => s.Language == lang);
                }
                else
                {
                    targetSub = subtitles.FirstOrDefault();
                }

                if (targetSub == null || string.IsNullOrEmpty(targetSub.Url))
                {
                    _logger.LogWarning("[Baklava.Subtitle] 🔴 [SUBTITLE PROXY] No matching subtitle found. Looking for: subId={SubId}, lang={Lang}", subId, lang);
                    return NotFound("Subtitle not found");
                }

                _logger.LogInformation("[Baklava.Subtitle] 🔵 [SUBTITLE PROXY] Found match: id={Id}, title={Title}, url={Url}", targetSub.Id, targetSub.Title, targetSub.Url);

                // Fetch subtitle content from original URL
                var response = await _httpClient.GetAsync(targetSub.Url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Baklava.Subtitle] Failed to fetch subtitle from {Url}: {Status}", targetSub.Url, response.StatusCode);
                    return StatusCode((int)response.StatusCode, "Failed to fetch subtitle from origin");
                }

                var content = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "text/plain";

                // Convert to VTT if web client requests it (format=vtt)
                var needsVttConversion = string.Equals(format, "vtt", StringComparison.OrdinalIgnoreCase);
                if (needsVttConversion)
                {
                    var contentStr = Encoding.UTF8.GetString(content);
                    // Check if it's NOT already VTT
                    if (!contentStr.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("[Baklava.Subtitle] Converting SRT to VTT for web client");
                        // Convert SRT to VTT: Add header and fix timestamps (comma -> dot)
                        var vttContent = "WEBVTT\n\n" + Regex.Replace(contentStr, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2");
                        content = Encoding.UTF8.GetBytes(vttContent);
                    }
                    contentType = "text/vtt";
                }

                _logger.LogInformation("[Baklava.Subtitle] Serving subtitle for {ItemId}: {Bytes} bytes, format={Format}", itemId, content.Length, needsVttConversion ? "vtt" : "original");
                
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                return File(content, needsVttConversion ? "text/vtt" : contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Subtitle] Failed to proxy subtitle for {ItemId}", itemId);
                return StatusCode(500, "Failed to proxy subtitle");
            }
        }

        /// <summary>
        /// Get cached external subtitles for an item
        /// </summary>
        [HttpGet("list")]
        public async Task<ActionResult> ListSubtitles([FromQuery] string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return BadRequest(new { error = "Invalid itemId" });
            }

            var subtitles = await _dbService.GetExternalSubtitlesAsync(itemId);
            if (subtitles == null || subtitles.Count == 0)
            {
                return Ok(new { itemId, subtitles = Array.Empty<object>() });
            }

            return Ok(new {
                itemId,
                subtitles = subtitles.Select(s => new {
                    id = s.Id,
                    language = s.Language,
                    title = s.Title,
                    url = s.Url,
                    baklavaUrl = $"/api/baklava/subtitles/proxy?itemId={itemId}&subId={s.Id}",
                    cachedAt = s.CachedAt
                })
            });
        }

        /// <summary>
        /// Download and convert external subtitle to VTT format
        /// GET /api/baklava/Subtitle/vtt?url=...
        /// This matches Podium's endpoint for compatibility
        /// </summary>
        [HttpGet("/api/baklava/Subtitle/vtt")]
        [AllowAnonymous] // Allow players to fetch without full auth
        public async Task<ActionResult> GetVttSubtitle([FromQuery] string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return BadRequest("Subtitle URL is required");
            }

            try
            {
                _logger.LogInformation("[Baklava.Subtitle] Downloading subtitle from {Url}", url);

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Baklava.Subtitle] Failed to download subtitle. Status: {Status}", response.StatusCode);
                    return StatusCode((int)response.StatusCode, "Failed to download subtitle");
                }

                var content = await response.Content.ReadAsStringAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                _logger.LogInformation("[Baklava.Subtitle] Downloaded subtitle, size: {Size} bytes, type: {Type}", content.Length, contentType);

                // Detect format and convert to VTT
                string vttContent;
                if (IsSrt(content))
                {
                    _logger.LogInformation("[Baklava.Subtitle] Detected SRT format, converting to VTT");
                    vttContent = ConvertSrtToVtt(content);
                }
                else if (IsAss(content))
                {
                    _logger.LogInformation("[Baklava.Subtitle] Detected ASS/SSA format, converting to VTT");
                    vttContent = ConvertAssToVtt(content);
                }
                else if (IsVtt(content))
                {
                    _logger.LogInformation("[Baklava.Subtitle] Already VTT format, returning as-is");
                    vttContent = content;
                }
                else
                {
                    _logger.LogWarning("[Baklava.Subtitle] Unknown subtitle format, attempting SRT conversion");
                    vttContent = ConvertSrtToVtt(content);
                }

                return Content(vttContent, "text/vtt; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.Subtitle] Error converting subtitle from {Url}", url);
                return StatusCode(500, $"Error converting subtitle: {ex.Message}");
            }
        }

        #region VTT Conversion Helpers

        private bool IsSrt(string content)
        {
            // SRT format: starts with number, has --> for timestamps
            return Regex.IsMatch(content.TrimStart(), @"^\d+\s*\r?\n\d{2}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2}:\d{2}:\d{2},\d{3}");
        }

        private bool IsAss(string content)
        {
            // ASS/SSA format: has [Script Info] header
            return content.Contains("[Script Info]") || content.Contains("[V4+ Styles]");
        }

        private bool IsVtt(string content)
        {
            // VTT format: starts with WEBVTT
            return content.TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase);
        }

        private string ConvertSrtToVtt(string srtContent)
        {
            var lines = srtContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var vtt = new StringBuilder();
            vtt.AppendLine("WEBVTT");
            vtt.AppendLine();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Skip sequence numbers
                if (Regex.IsMatch(line, @"^\d+$"))
                {
                    continue;
                }

                // Convert timestamps: 00:00:20,000 --> 00:00:24,400 to 00:00:20.000 --> 00:00:24.400
                if (line.Contains("-->"))
                {
                    line = line.Replace(',', '.');
                    vtt.AppendLine(line);
                }
                // Keep subtitle text and blank lines
                else
                {
                    vtt.AppendLine(line);
                }
            }

            return vtt.ToString();
        }

        private string ConvertAssToVtt(string assContent)
        {
            var lines = assContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var vtt = new StringBuilder();
            vtt.AppendLine("WEBVTT");
            vtt.AppendLine();

            bool inEvents = false;
            int formatIndex = -1;
            int textIndex = -1;
            int startIndex = -1;
            int endIndex = -1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("[Events]"))
                {
                    inEvents = true;
                    continue;
                }

                if (trimmed.StartsWith("[") && !trimmed.StartsWith("[Events]"))
                {
                    inEvents = false;
                    continue;
                }

                if (inEvents && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse format line to find column indices
                    var format = trimmed.Substring(7).Trim();
                    var columns = format.Split(',');
                    for (int i = 0; i < columns.Length; i++)
                    {
                        var col = columns[i].Trim().ToLower();
                        if (col == "start") startIndex = i;
                        else if (col == "end") endIndex = i;
                        else if (col == "text") textIndex = i;
                    }
                    continue;
                }

                if (inEvents && trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    var dialogue = trimmed.Substring(9).Trim();
                    var parts = dialogue.Split(',');

                    if (startIndex >= 0 && endIndex >= 0 && textIndex >= 0 && parts.Length > textIndex)
                    {
                        var start = parts[startIndex].Trim();
                        var end = parts[endIndex].Trim();
                        
                        // Text might contain commas, so join remaining parts
                        var text = string.Join(",", parts, textIndex, parts.Length - textIndex);

                        // Convert ASS timestamp (0:00:20.00) to VTT (00:00:20.000)
                        start = ConvertAssTimestamp(start);
                        end = ConvertAssTimestamp(end);

                        // Remove ASS formatting tags
                        text = RemoveAssFormatting(text);

                        vtt.AppendLine($"{start} --> {end}");
                        vtt.AppendLine(text);
                        vtt.AppendLine();
                    }
                }
            }

            return vtt.ToString();
        }

        private string ConvertAssTimestamp(string assTime)
        {
            // ASS format: 0:00:20.00 or 0:00:20.000
            // VTT format: 00:00:20.000
            var match = Regex.Match(assTime, @"(\d+):(\d{2}):(\d{2})\.(\d+)");
            if (match.Success)
            {
                var hours = match.Groups[1].Value.PadLeft(2, '0');
                var minutes = match.Groups[2].Value;
                var seconds = match.Groups[3].Value;
                var ms = match.Groups[4].Value.PadRight(3, '0').Substring(0, 3); // Ensure 3 digits

                return $"{hours}:{minutes}:{seconds}.{ms}";
            }
            return assTime;
        }

        private string RemoveAssFormatting(string text)
        {
            // Remove ASS override tags like {\pos(x,y)} {\an8} etc
            text = Regex.Replace(text, @"\{[^}]*\}", "");
            
            // Convert ASS line breaks
            text = text.Replace("\\N", "\n").Replace("\\n", "\n");
            
            return text.Trim();
        }

        #endregion
    }

    public class ExternalSubtitleInfo
    {
        public string? Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Language { get; set; }
        public string? Title { get; set; }
        public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    }
}
