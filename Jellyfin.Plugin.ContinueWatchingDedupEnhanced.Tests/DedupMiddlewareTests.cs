using System.Reflection;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ContinueWatchingDedupEnhanced.Configuration;
using Jellyfin.Plugin.ContinueWatchingDedupEnhanced.Middleware;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ContinueWatchingDedupEnhanced.Tests;

public class DedupMiddlewareTests
{
    [Fact]
    public void UpNextPrefersLaterUnplayedEpisodeOverOlderPlayedEpisode()
    {
        const string json = """
            {
              "Items": [
                {
                  "Id": "episode-3",
                  "Type": "Episode",
                  "SeriesId": "series-1",
                  "ParentIndexNumber": 4,
                  "IndexNumber": 3,
                  "UserData": { "LastPlayedDate": "2026-08-19T20:00:00Z" }
                },
                {
                  "Id": "episode-11",
                  "Type": "Episode",
                  "SeriesId": "series-1",
                  "ParentIndexNumber": 4,
                  "IndexNumber": 11,
                  "UserData": {}
                }
              ],
              "TotalRecordCount": 2
            }
            """;

        var result = InvokeDeduplicate(json, "UpNext");
        var root = JsonNode.Parse(result)!;

        Assert.Equal("episode-11", root["Items"]![0]!["Id"]!.GetValue<string>());
        Assert.Equal(1, root["TotalRecordCount"]!.GetValue<int>());
    }

    [Fact]
    public void ContinueWatchingStillPrefersMostRecentlyPlayedEpisode()
    {
        const string json = """
            {
              "Items": [
                {
                  "Id": "episode-3",
                  "Type": "Episode",
                  "SeriesId": "series-1",
                  "ParentIndexNumber": 4,
                  "IndexNumber": 3,
                  "UserData": { "LastPlayedDate": "2026-08-19T20:00:00Z" }
                },
                {
                  "Id": "episode-11",
                  "Type": "Episode",
                  "SeriesId": "series-1",
                  "ParentIndexNumber": 4,
                  "IndexNumber": 11,
                  "UserData": {}
                }
              ]
            }
            """;

        var result = InvokeDeduplicate(json, "ContinueWatching");
        var root = JsonNode.Parse(result)!;

        Assert.Equal("episode-3", root["Items"]![0]!["Id"]!.GetValue<string>());
    }

    [Fact]
    public void PreviouslyHiddenEarlierEpisodeDoesNotReappearAfterLaterEpisodeCompletes()
    {
        var user = Guid.NewGuid().ToString();
        const string firstResponse = """
            { "Items": [
              { "Id":"episode-3", "Type":"Episode", "SeriesId":"series-1", "ParentIndexNumber":4, "IndexNumber":3,
                "UserData":{"LastPlayedDate":"2026-08-18T20:00:00Z"} },
              { "Id":"episode-10", "Type":"Episode", "SeriesId":"series-1", "ParentIndexNumber":4, "IndexNumber":10,
                "UserData":{"LastPlayedDate":"2026-08-19T20:00:00Z"} }
            ] }
            """;
        const string afterEpisodeTenCompleted = """
            { "Items": [
              { "Id":"episode-3", "Type":"Episode", "SeriesId":"series-1", "ParentIndexNumber":4, "IndexNumber":3,
                "UserData":{"LastPlayedDate":"2026-08-18T20:00:00Z"} }
            ] }
            """;

        var first = JsonNode.Parse(InvokeDeduplicate(firstResponse, "ContinueWatching", user))!;
        var second = JsonNode.Parse(InvokeDeduplicate(afterEpisodeTenCompleted, "ContinueWatching", user))!;

        Assert.Equal("episode-10", first["Items"]![0]!["Id"]!.GetValue<string>());
        Assert.Empty(second["Items"]!.AsArray());
    }

    private static string InvokeDeduplicate(string json, string endpointName, string? userKey = null)
    {
        var middleware = new DedupMiddleware(_ => Task.CompletedTask, NullLogger<DedupMiddleware>.Instance);
        var middlewareType = typeof(DedupMiddleware);
        var endpointType = middlewareType.GetNestedType("EndpointKind", BindingFlags.NonPublic)!;
        var endpoint = Enum.Parse(endpointType, endpointName);
        var method = middlewareType.GetMethod("Deduplicate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var config = new PluginConfiguration { MaxEpisodesPerSeries = 1 };

        return (string)method.Invoke(middleware, new object?[] { json, config, endpoint, userKey, null })!;
    }
}
