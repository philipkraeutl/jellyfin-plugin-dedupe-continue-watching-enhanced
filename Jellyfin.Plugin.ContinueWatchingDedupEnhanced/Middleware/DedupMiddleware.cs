using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingDedupEnhanced.Middleware;

/// <summary>
/// Intercepts Continue Watching responses and, when enabled, Up Next responses.
/// Removes duplicate episodes belonging to the same series, keeping only the
/// most recently played.
/// </summary>
public class DedupMiddleware
{
    private static readonly ConcurrentDictionary<string, ContinueWatchingSnapshot> ContinueWatchingByUser =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, EpisodeWatermark>> EpisodeWatermarksByUser =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SeriesLifetime = TimeSpan.FromMinutes(1);
    private readonly RequestDelegate _next;
    private readonly ILogger<DedupMiddleware> _logger;

    public DedupMiddleware(RequestDelegate next, ILogger<DedupMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestStartedAt = DateTimeOffset.UtcNow;
        var path = context.Request.Path.Value ?? string.Empty;

        var config = Plugin.Instance?.Configuration;
        var endpoint = GetEndpointKind(path);
        if (config is null
            || !config.Enabled
            || endpoint == EndpointKind.None
            || (endpoint == EndpointKind.UpNext && !config.DeduplicateUpNext))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            if (context.Response.StatusCode != 200)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                context.Response.Body = originalBody;
                await buffer.CopyToAsync(originalBody);
                return;
            }

            buffer.Seek(0, SeekOrigin.Begin);
            var rawBytes = buffer.ToArray();
            if (rawBytes.Length == 0)
            {
                context.Response.Body = originalBody;
                return;
            }

            // Detect and decompress based on Content-Encoding
            var encoding = context.Response.Headers.ContentEncoding.ToString();
            string json;
            try
            {
                json = await DecompressAsync(rawBytes, encoding);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CWDedup] Failed to decompress response (encoding={Encoding})", encoding);
                context.Response.Body = originalBody;
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
                return;
            }

            if (string.IsNullOrEmpty(json))
            {
                context.Response.Body = originalBody;
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
                return;
            }

            string modified;
            try
            {
                HashSet<string>? continueWatchingSeries = null;
                var userKey = GetUserKey(context);

                if (endpoint == EndpointKind.UpNext && userKey is not null)
                {
                    continueWatchingSeries = await GetRecentContinueWatchingSeriesAsync(userKey, requestStartedAt);
                }

                modified = Deduplicate(json, config, endpoint, userKey, continueWatchingSeries);

                if (endpoint == EndpointKind.ContinueWatching && userKey is not null)
                {
                    StoreContinueWatchingSeries(userKey, modified);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CWDedup] Dedup failed for {Path}", path);
                context.Response.Body = originalBody;
                buffer.Seek(0, SeekOrigin.Begin);
                await buffer.CopyToAsync(originalBody);
                return;
            }

            // Recompress if the original was compressed
            var newBytes = Encoding.UTF8.GetBytes(modified);
            if (!string.IsNullOrEmpty(encoding))
            {
                newBytes = await CompressAsync(newBytes, encoding);
            }

            context.Response.Body = originalBody;
            context.Response.ContentLength = newBytes.Length;
            await context.Response.Body.WriteAsync(newBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CWDedup] Middleware error");
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> DecompressAsync(byte[] data, string encoding)
    {
        if (string.IsNullOrEmpty(encoding))
        {
            return Encoding.UTF8.GetString(data);
        }

        using var input = new MemoryStream(data);
        Stream decompressionStream = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            _ => input  // unknown encoding, return as-is
        };

        using (decompressionStream)
        using (var reader = new StreamReader(decompressionStream, Encoding.UTF8))
        {
            return await reader.ReadToEndAsync();
        }
    }

    private static async Task<byte[]> CompressAsync(byte[] data, string encoding)
    {
        using var output = new MemoryStream();
        Stream compressionStream = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => null!
        };

        if (compressionStream is null) return data;

        using (compressionStream)
        {
            await compressionStream.WriteAsync(data);
        }

        return output.ToArray();
    }

    private static EndpointKind GetEndpointKind(string path)
    {
        var trimmed = path.Trim('/');
        var parts = trimmed.Split('/');

        // Pattern 1: /Users/{userId}/Items/Resume (Jellyfin Web, official Android)
        if (parts.Length >= 4
            && string.Equals(parts[0], "Users", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], "Items", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[3], "Resume", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.ContinueWatching;
        }

        // Pattern 2: /UserItems/Resume (SwiftFin iOS, Wholphin Android - SDK-style)
        if (parts.Length == 2
            && string.Equals(parts[0], "UserItems", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "Resume", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.ContinueWatching;
        }

        // Pattern 3: /Shows/Resume (some clients)
        if (parts.Length == 2
            && string.Equals(parts[0], "Shows", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "Resume", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.ContinueWatching;
        }

        // Pattern 4: /HomeScreen/Section/ContinueWatching (Home Screen Sections
        // plugin and Jellyfin Enhanced — replace the stock home Continue Watching
        // row with their own endpoint, bypassing /Items/Resume entirely).
        if (parts.Length == 3
            && string.Equals(parts[0], "HomeScreen", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "Section", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], "ContinueWatching", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.ContinueWatching;
        }

        // /Shows/NextUp (standard Jellyfin Up Next endpoint)
        if (parts.Length == 2
            && string.Equals(parts[0], "Shows", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "NextUp", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.UpNext;
        }

        // /HomeScreen/Section/NextUp (Home Screen Sections / Jellyfin Enhanced)
        if (parts.Length == 3
            && string.Equals(parts[0], "HomeScreen", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "Section", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], "NextUp", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointKind.UpNext;
        }

        return EndpointKind.None;
    }

    private static string? GetUserKey(HttpContext context)
    {
        var pathParts = (context.Request.Path.Value ?? string.Empty).Trim('/').Split('/');
        if (pathParts.Length >= 2
            && string.Equals(pathParts[0], "Users", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(pathParts[1]))
        {
            return pathParts[1];
        }

        if (context.Request.Query.TryGetValue("UserId", out var userId)
            && !string.IsNullOrWhiteSpace(userId.ToString()))
        {
            return userId.ToString();
        }

        return context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub")
            ?? context.User.Identity?.Name;
    }

    private static async Task<HashSet<string>?> GetRecentContinueWatchingSeriesAsync(
        string userKey,
        DateTimeOffset requestStartedAt)
    {
        ContinueWatchingSnapshot? fallback = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (ContinueWatchingByUser.TryGetValue(userKey, out var snapshot))
            {
                fallback = snapshot;

                // Prefer the Continue Watching response from this page load. A small
                // tolerance also accepts a response that completed just before Up Next began.
                if (snapshot.CreatedAt >= requestStartedAt - TimeSpan.FromSeconds(2))
                {
                    return GetActiveSeries(snapshot);
                }
            }

            await Task.Delay(50);
        }

        return fallback is null ? null : GetActiveSeries(fallback);
    }

    private static void StoreContinueWatchingSeries(string userKey, string json)
    {
        var items = JsonNode.Parse(json)?["Items"]?.AsArray();
        if (items is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var seriesIds = items
            .Select(item => item?["SeriesId"]?.GetValue<string>())
            .Where(seriesId => !string.IsNullOrWhiteSpace(seriesId))
            .Select(seriesId => seriesId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ContinueWatchingByUser.AddOrUpdate(
            userKey,
            _ => new ContinueWatchingSnapshot(
                seriesIds.ToDictionary(id => id, _ => now, StringComparer.OrdinalIgnoreCase),
                now),
            (_, previous) =>
            {
                // Different Resume requests may contain different subsets. Keep each
                // recently observed series independently instead of replacing the set.
                var seenAt = previous.SeriesSeenAt
                    .Where(entry => now - entry.Value <= SeriesLifetime)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

                foreach (var seriesId in seriesIds)
                {
                    seenAt[seriesId] = now;
                }

                return new ContinueWatchingSnapshot(seenAt, now);
            });
    }

    private static HashSet<string> GetActiveSeries(ContinueWatchingSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        return snapshot.SeriesSeenAt
            .Where(entry => now - entry.Value <= SeriesLifetime)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the response JSON, deduplicates Items by SeriesId,
    /// keeps the most recently played per series, and re-serializes.
    /// </summary>
    private string Deduplicate(
        string json,
        Configuration.PluginConfiguration config,
        EndpointKind endpoint,
        string? userKey,
        HashSet<string>? excludedSeries = null)
    {
        var root = JsonNode.Parse(json);
        if (root is null) return json;

        var itemsNode = root["Items"]?.AsArray();
        if (itemsNode is null) return json;

        // Group items by series (or by item ID if movie/no series)
        var groups = new Dictionary<string, List<(JsonNode node, DateTime lastPlayed, int season, int episode)>>();
        var passthrough = new List<JsonNode>();

        foreach (var item in itemsNode)
        {
            if (item is null) continue;

            var itemType = item["Type"]?.GetValue<string>() ?? string.Empty;
            var seriesId = item["SeriesId"]?.GetValue<string>();
            var lastPlayed = ParseDate(item["UserData"]?["LastPlayedDate"]?.GetValue<string>());
            var season = ParseIndex(item["ParentIndexNumber"]);
            var episode = ParseIndex(item["IndexNumber"]);

            if (!string.IsNullOrEmpty(seriesId) && excludedSeries?.Contains(seriesId) == true)
            {
                continue;
            }

            // Episodes always group by SeriesId
            if (string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(seriesId))
            {
                if (!groups.TryGetValue(seriesId, out var list))
                {
                    list = new List<(JsonNode, DateTime, int, int)>();
                    groups[seriesId] = list;
                }
                list.Add((item, lastPlayed, season, episode));
                continue;
            }

            // Movies — only deduplicate if explicitly enabled
            if (config.DeduplicateMovies && string.Equals(itemType, "Movie", StringComparison.OrdinalIgnoreCase))
            {
                var movieKey = $"movie:{item["Id"]?.GetValue<string>()}";
                if (!groups.TryGetValue(movieKey, out var list))
                {
                    list = new List<(JsonNode, DateTime, int, int)>();
                    groups[movieKey] = list;
                }
                list.Add((item, lastPlayed, season, episode));
                continue;
            }

            // Anything else passes through unchanged
            passthrough.Add(item);
        }

        // Resume entries represent playback progress, so LastPlayedDate is the
        // correct signal. Next Up entries are usually unplayed and have no such
        // date; sorting them by LastPlayedDate would make an older, previously
        // started episode beat the actual next episode. For Next Up, prefer the
        // furthest episode in the series chronology instead.
        var keep = new List<JsonNode>();
        keep.AddRange(passthrough);

        var maxPerSeries = Math.Max(1, config.MaxEpisodesPerSeries);
        foreach (var group in groups)
        {
            var ordered = (endpoint == EndpointKind.UpNext
                    ? group.Value.OrderByDescending(t => t.season)
                        .ThenByDescending(t => t.episode)
                        .ThenByDescending(t => t.lastPlayed)
                    : group.Value.OrderByDescending(t => t.lastPlayed)
                        .ThenByDescending(t => t.season)
                        .ThenByDescending(t => t.episode))
                .Take(maxPerSeries)
                .ToList();

            if (endpoint == EndpointKind.ContinueWatching
                && userKey is not null
                && !group.Key.StartsWith("movie:", StringComparison.Ordinal)
                && ordered.Count > 0
                && ShouldSuppressPreviouslyDeduplicatedEpisode(userKey, group.Key, ordered[0]))
            {
                continue;
            }

            keep.AddRange(ordered.Select(t => t.node));
        }

        // Preserve original ordering (by index in the input)
        var indexMap = new Dictionary<JsonNode, int>();
        for (int i = 0; i < itemsNode.Count; i++)
        {
            if (itemsNode[i] is JsonNode n) indexMap[n] = i;
        }
        keep.Sort((a, b) =>
            (indexMap.TryGetValue(a, out var ia) ? ia : int.MaxValue)
            .CompareTo(indexMap.TryGetValue(b, out var ib) ? ib : int.MaxValue));

        var newArray = new JsonArray();
        foreach (var node in keep)
        {
            // Detach from parent before re-adding
            var clone = JsonNode.Parse(node.ToJsonString());
            if (clone is not null) newArray.Add(clone);
        }

        root["Items"] = newArray;
        if (root["TotalRecordCount"] is not null) root["TotalRecordCount"] = newArray.Count;

        var hidden = itemsNode.Count - newArray.Count;
        if (hidden > 0)
        {
            _logger.LogDebug("Deduplicated media response: {Original} → {Final} ({Hidden} hidden)",
                itemsNode.Count, newArray.Count, hidden);
        }

        return root.ToJsonString();
    }

    private static DateTime ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return DateTime.MinValue;
        return DateTime.TryParse(value, out var dt) ? dt : DateTime.MinValue;
    }

    private static int ParseIndex(JsonNode? value)
    {
        return value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var index)
            ? index
            : int.MinValue;
    }

    private static bool ShouldSuppressPreviouslyDeduplicatedEpisode(
        string userKey,
        string seriesId,
        (JsonNode node, DateTime lastPlayed, int season, int episode) candidate)
    {
        var watermarks = EpisodeWatermarksByUser.GetOrAdd(
            userKey,
            _ => new ConcurrentDictionary<string, EpisodeWatermark>(StringComparer.OrdinalIgnoreCase));

        if (watermarks.TryGetValue(seriesId, out var previous))
        {
            var isEarlier = candidate.season < previous.Season
                || (candidate.season == previous.Season && candidate.episode < previous.Episode);

            // A genuinely newer playback means the user intentionally returned
            // to the older episode. Otherwise it is stale progress that was
            // already hidden by a later episode and must stay hidden.
            if (isEarlier && candidate.lastPlayed <= previous.LastPlayed)
            {
                return true;
            }
        }

        watermarks.AddOrUpdate(
            seriesId,
            _ => new EpisodeWatermark(candidate.season, candidate.episode, candidate.lastPlayed),
            (_, current) => IsLater(candidate.season, candidate.episode, current)
                ? new EpisodeWatermark(candidate.season, candidate.episode, candidate.lastPlayed)
                : current);

        return false;
    }

    private static bool IsLater(int season, int episode, EpisodeWatermark current)
    {
        return season > current.Season || (season == current.Season && episode > current.Episode);
    }

    private sealed record ContinueWatchingSnapshot(
        Dictionary<string, DateTimeOffset> SeriesSeenAt,
        DateTimeOffset CreatedAt);

    private sealed record EpisodeWatermark(int Season, int Episode, DateTime LastPlayed);

    private enum EndpointKind
    {
        None,
        ContinueWatching,
        UpNext
    }
}
