using Silk.NET.Vulkan;
namespace Ryujinx.Graphics.Vulkan
{
    // Tests run production scheduling with fake driver operations, not a GPU.
    internal sealed class PipelineCacheManager
    {
        public Action<int, bool> Maintenance;
        public PipelineCache GetWorkerCache(int index) => new((ulong)index + 1);
        public void NotifyWorkerPipelineCreated(int index, bool allowCheckpoint = true)
        {
            Maintenance?.Invoke(index, allowCheckpoint);
        }
    }
}
