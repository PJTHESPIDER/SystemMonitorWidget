using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// A synchronous metrics collector. Collects readings sequentially into
    /// one completed immutable snapshot. Implementations are independent of
    /// the update pipeline, so the pipeline can be tested with a fake
    /// collector that simulates slow collection, failures, etc.
    /// </summary>
    public interface IMetricsCollector
    {
        MetricsSnapshot Collect();
    }
}
