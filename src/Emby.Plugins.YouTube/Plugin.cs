using System;
using System.Collections.Generic;
using System.IO;
using Emby.Plugins.YouTube.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugins.YouTube
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override Guid Id => new Guid("6b1e2f8a-3c47-4d5b-9a10-7e2c4f8d1b93");

        public override string Name => "YouTube";

        public override string Description =>
            "Browse your YouTube subscriptions, recent uploads, recommendations and searches as an Emby channel.";

        /// <summary>
        /// Where the managed yt-dlp binary and the cookie jar live. Emby hands each plugin a data
        /// folder under program data, so this survives a container image rebuild as long as the
        /// config volume is mounted — which is the whole point on Docker/Unraid.
        /// </summary>
        public string PluginDataPath
        {
            get
            {
                var path = DataFolderPath;
                Directory.CreateDirectory(path);
                return path;
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "youtube",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true,
                    DisplayName = "YouTube",
                    EnableInMainMenu = true,
                    MenuIcon = "smart_display"
                }
            };
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
