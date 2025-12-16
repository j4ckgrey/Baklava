using Baklava.Filters;
using Baklava.Services;
using MediaBrowser.Controller.Library;
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

            // Configure MVC to use the SearchActionFilter with order 0 to run before Gelato (order 2)
            services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            {
                options.Filters.AddService<SearchActionFilter>(order: 0);
            });

            // Decorate MediaSourceManager to inject external subtitles
            services.Decorate<IMediaSourceManager, BaklavaMediaSourceManagerDecorator>();
        }
    }
}
