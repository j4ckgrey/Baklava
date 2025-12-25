using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Baklava.Filters
{
    /// <summary>
    /// Action filter that intercepts search requests and handles the "local:" prefix.
    /// When a search term starts with "local:", it strips the prefix and passes it to Jellyfin's default search.
    /// When no prefix is present, the search is handled by external search (e.g., Gelato if configured).
    /// </summary>
    public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILogger<SearchActionFilter> _log;
        private const string LocalPrefix = "local:";

        public SearchActionFilter(ILogger<SearchActionFilter> log)
        {
            _log = log;
        }

        public int Order => 0;

        public async Task OnActionExecutionAsync(
            ActionExecutingContext ctx,
            ActionExecutionDelegate next
        )
        {
            // Check if the filter is enabled in configuration
            if (Plugin.Instance?.Configuration?.EnableSearchFilter != true)
            {
                await next();
                return;
            }

            // check if the modal is disabled, if so we should not intercept
            if (Plugin.Instance?.Configuration?.DisableModal == true)
            {
                await next();
                return;
            }

            // Check if this is a search action
            if (!IsSearchAction(ctx))
            {
                await next();
                return;
            }

            // Try to get the search term from action arguments
            if (!TryGetSearchTerm(ctx, out var searchTerm))
            {
                _log.LogWarning("SearchActionFilter (Baklava): Search action detected but no search term found");
                await next();
                return;
            }

            _log.LogInformation("SearchActionFilter (Baklava): Processing search term: '{SearchTerm}'", searchTerm);

            // Check if TV client local search is forced by admin
            bool isTVClient = IsTVClient(ctx);
            bool forceTVLocalSearch = Plugin.Instance?.Configuration?.ForceTVClientLocalSearch == true;

            // If this is a TV client and admin has forced local search, add the prefix if not present
            if (isTVClient && forceTVLocalSearch && !searchTerm.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var forcedLocalSearchTerm = LocalPrefix + searchTerm;
                
                _log.LogInformation(
                    "SearchActionFilter (Baklava): TV client detected with forced local search. Adding 'local:' prefix. Original: '{Original}', Modified: '{Modified}'",
                    searchTerm,
                    forcedLocalSearchTerm
                );

                UpdateSearchTerm(ctx, forcedLocalSearchTerm);
            }
            else if (searchTerm.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation(
                    "SearchActionFilter (Baklava): Search already has 'local:' prefix - passing through to Gelato: '{SearchTerm}'",
                    searchTerm
                );
            }
            else
            {
                _log.LogInformation(
                    "SearchActionFilter (Baklava): No 'local:' prefix - passing through to Gelato for external search: '{SearchTerm}'",
                    searchTerm
                );
            }

            // Continue with the request - pass to Gelato which will handle the prefix
            await next();
        }

        private bool IsSearchAction(ActionExecutingContext ctx)
        {
            var actionName = ctx.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor descriptor
                ? descriptor.ActionName
                : null;

            // Check if this is a search-related action
            // The action name is "GetItems" and we need to check if searchTerm parameter exists
            bool hasSearchTerm = ctx.ActionArguments.ContainsKey("searchTerm");
            bool isGetItems = actionName != null && actionName.Equals("GetItems", StringComparison.OrdinalIgnoreCase);
            
            bool isSearch = isGetItems && hasSearchTerm;

            if (isGetItems && !hasSearchTerm)
            {
                // Debug log to see what arguments are actually present
                var argKeys = string.Join(", ", ctx.ActionArguments.Keys);
                _log.LogDebug("SearchActionFilter (Baklava): GetItems without searchTerm. Arguments: {Arguments}", argKeys);
            }

            if (isSearch)
            {
                _log.LogInformation("SearchActionFilter (Baklava): Search detected! ActionName='{ActionName}', Path='{Path}'", 
                    actionName, ctx.HttpContext.Request.Path.Value);
            }
            
            return isSearch;
        }

        private bool IsTVClient(ActionExecutingContext ctx)
        {
            // Check User-Agent header to detect TV clients
            var userAgent = ctx.HttpContext.Request.Headers["User-Agent"].ToString().ToLowerInvariant();
            var embyClient = ctx.HttpContext.Request.Headers["X-Emby-Client"].ToString().ToLowerInvariant();
            
            _log.LogInformation("SearchActionFilter (Baklava): Checking TV client. User-Agent='{UserAgent}', X-Emby-Client='{Client}'", 
                userAgent, embyClient);
            
            // Common TV client identifiers
            var tvClientIdentifiers = new[]
            {
                "android tv",  // Must come before other android checks
                "jellyfin tv",
                "androidtv",
                "firetv",
                "webos",
                "tizen",
                "roku",
                "appletv",
                "smarttv",
                "hbbtv",
                "playstation",
                "xbox"
            };

            foreach (var identifier in tvClientIdentifiers)
            {
                if (userAgent.Contains(identifier))
                {
                    _log.LogInformation("SearchActionFilter (Baklava): ✓ Detected TV client via User-Agent: '{UserAgent}' (matched: '{Identifier}')", userAgent, identifier);
                    return true;
                }
            }

            // Check X-Emby-Client header (Jellyfin clients send this)
            if (!string.IsNullOrEmpty(embyClient))
            {
                foreach (var identifier in tvClientIdentifiers)
                {
                    if (embyClient.Contains(identifier))
                    {
                        _log.LogInformation("SearchActionFilter (Baklava): ✓ Detected TV client via X-Emby-Client: '{Client}' (matched: '{Identifier}')", embyClient, identifier);
                        return true;
                    }
                }
            }

            _log.LogInformation("SearchActionFilter (Baklava): ✗ Not a TV client");
            return false;
        }

        private bool TryGetSearchTerm(ActionExecutingContext ctx, out string searchTerm)
        {
            searchTerm = string.Empty;

            // Check common parameter names for search terms
            var searchKeys = new[] { "searchTerm", "SearchTerm", "searchQuery", "query", "q" };

            foreach (var key in searchKeys)
            {
                if (ctx.ActionArguments.TryGetValue(key, out var value) && value is string str && !string.IsNullOrWhiteSpace(str))
                {
                    searchTerm = str;
                    return true;
                }
            }

            // Also check query string
            var query = ctx.HttpContext.Request.Query;
            foreach (var key in searchKeys)
            {
                if (query.TryGetValue(key, out var values) && values.Count > 0 && !string.IsNullOrWhiteSpace(values[0]))
                {
                    searchTerm = values[0];
                    return true;
                }
            }

            return false;
        }

        private void UpdateSearchTerm(ActionExecutingContext ctx, string newSearchTerm)
        {
            // Update in action arguments
            var searchKeys = new[] { "searchTerm", "SearchTerm", "searchQuery", "query", "q" };

            foreach (var key in searchKeys)
            {
                if (ctx.ActionArguments.ContainsKey(key))
                {
                    ctx.ActionArguments[key] = newSearchTerm;
                }
            }
        }
    }
}
