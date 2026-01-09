using Baklava.Filters;
using Baklava.Services;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Baklava
{
    /// <summary>
    /// Service registrator for Baklava plugin.
    /// Registers action filters and other services with the Jellyfin dependency injection container.
    /// </summary>
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, MediaBrowser.Controller.IServerApplicationHost host)
        {
            // Register the SearchActionFilter as a singleton
            services.AddSingleton<SearchActionFilter>();

            // Register IStartupFilter to inject UI middleware
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Baklava.Api.UIInjectionStartupFilter>();

            // Register Baklava Database Service as a singleton
            services.AddSingleton<BaklavaDbService>();
            
            // Register Refactored Services

            services.AddSingleton<StreamService>();
            services.AddSingleton<SubtitleService>();

            // Register Scheduled Tasks
            services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, Baklava.Tasks.CatalogSyncTask>();
        }
    }
}
