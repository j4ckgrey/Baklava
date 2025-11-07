using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Baklava.Services
{
    public class StartupService : IScheduledTask
    {
        public string Name => "MyJellyfinPlugin Startup";
        public string Key => "MyJellyfinPlugin.Startup";
        public string Description => "Registers file transformations for MyJellyfinPlugin";
        public string Category => "Startup Services";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // try
                // {
                //     PluginLogger.Log("StartupService: Executing, will register transformations");
                //     TransformationRegistrar.Register();
                // }
                // catch (Exception ex)
                // {
                //     PluginLogger.Log($"StartupService error: {ex.Message}");
                // }
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
