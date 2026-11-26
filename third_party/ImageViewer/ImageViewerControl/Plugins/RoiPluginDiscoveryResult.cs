using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ImageViewer.Plugins
{
    public sealed record RoiPluginDiscoveryFailure(string ModuleTypeName, Exception Exception);

    public sealed class RoiPluginDiscoveryResult
    {
        public RoiPluginDiscoveryResult(
            IEnumerable<string> registeredModuleTypeNames,
            IEnumerable<RoiPluginDiscoveryFailure> failures)
        {
            RegisteredModuleTypeNames = new ReadOnlyCollection<string>(registeredModuleTypeNames.ToList());
            Failures = new ReadOnlyCollection<RoiPluginDiscoveryFailure>(failures.ToList());
        }

        public IReadOnlyList<string> RegisteredModuleTypeNames { get; }

        public IReadOnlyList<RoiPluginDiscoveryFailure> Failures { get; }

        public bool HasFailures => Failures.Count > 0;
    }
}
