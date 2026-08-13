using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ContinueWatchingDedupEnhanced.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ContinueWatchingDedupEnhanced;

/// <summary>
/// Plugin entry point. Deduplicates the Continue Watching row so each series
/// appears only once — represented by the most recently played episode.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Continue Watching Deduplicator Enhanced";

    public override Guid Id => Guid.Parse("58ee4cec-e3e2-4bbf-bf25-4f34e9e96fbc");

    public override string Description =>
        "Deduplicates Continue Watching and optionally Up Next, " +
        "including duplicates shared across both sections.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "ContinueWatchingDeduplicatorEnhanced",
                DisplayName = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true
            }
        };
    }
}
