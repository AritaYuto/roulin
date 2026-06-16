using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Injector;
using UnityEditor.Build.Pipeline.Interfaces;

namespace Roulin.Editor.Build.CustomBuildTasks
{
    // Inject via INTERFACE: SBP's Extract path throws on concrete types and
    // ContextUsage.In skips that path. Collections are mutated through the
    // shared references; the struct itself never changes.
    public abstract class RoulinBuildTaskBase : IBuildTask
    {
#pragma warning disable 649
        [InjectContext(ContextUsage.In)]
        protected IRoulinBuildSharedContext roulinContext;
#pragma warning restore 649
        public abstract int Version { get; }

        public abstract ReturnCode Run();
    }
}
