using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Baklava.Services
{
    public class StartupService : IScheduledTask
    {
        public string Name => "Baklava Injection Service";
        public string Key => "Baklava.Injection";
        public string Description => "Injects Baklava UI scripts into Jellyfin's web interface (index.html)";
        public string Category => "Startup Services";

        private readonly MediaBrowser.Controller.Configuration.IServerConfigurationManager _configManager;

        public StartupService(MediaBrowser.Controller.Configuration.IServerConfigurationManager configManager)
        {
            _configManager = configManager;
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                try
                {
                    PluginLogger.Log("StartupService: Checking for legacy file modifications...");
                    
                    var webDir = _configManager.ApplicationPaths.WebPath;
                    var indexFile = System.IO.Path.Combine(webDir, "index.html");
                    var backupFile = System.IO.Path.Combine(webDir, "index.html.baklava.bak");

                    if (System.IO.File.Exists(backupFile))
                    {
                        PluginLogger.Log("Found legacy backup file. Attempting to restore...");
                        try 
                        {
                            System.IO.File.Copy(backupFile, indexFile, true);
                            System.IO.File.Delete(backupFile);
                            PluginLogger.Log("Successfully restored index.html from legacy backup.");
                        }
                        catch (Exception ex)
                        {
                            PluginLogger.Log($"Could not restore legacy backup (likely Read-Only filesystem): {ex.Message}");
                        }
                    }
                    else
                    {
                        PluginLogger.Log("No legacy backup found. Filesystem clean.");
                    }
                }
                catch (Exception ex)
                {
                    PluginLogger.Log($"StartupService warning: {ex.Message}");
                }
            }, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            };
        }
    }
}
