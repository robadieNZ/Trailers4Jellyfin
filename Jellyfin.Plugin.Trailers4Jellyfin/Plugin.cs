using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Trailers4Jellyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Trailers4Jellyfin
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Trailers4Jellyfin";

        public override Guid Id => Guid.Parse("7635bf62-6b22-4c4c-9bc7-5d55f0c2bff0");

        public static Plugin Instance { get; private set; } = null!;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html",
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "movie"
            };
        }
    }
}
