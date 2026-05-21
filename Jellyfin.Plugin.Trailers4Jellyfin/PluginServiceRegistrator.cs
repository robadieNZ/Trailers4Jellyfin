using Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Trailers4Jellyfin
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<TmdbService>();
            serviceCollection.AddSingleton<TrailerDownloadService>();
            serviceCollection.AddTransient<IScheduledTask, DownloadTrailersTask>();
            serviceCollection.AddSingleton<IIntroProvider, TrailerIntroProvider>();
        }
    }
}
