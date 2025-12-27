using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Baklava.Api
{
    public class UIInjectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                // Register our middleware early in the pipeline
                builder.UseMiddleware<UIInjectionMiddleware>();
                next(builder);
            };
        }
    }
}
