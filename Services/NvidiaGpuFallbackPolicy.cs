namespace System_Monitor.Services
{
    /// <summary>
    /// Decides whether the NVIDIA command-line fallback is worth running.
    ///
    /// The fallback exists to cover a metric the device-identified backend
    /// could not read. When the backend covered everything the fallback
    /// could contribute, running it would spawn a subprocess to produce
    /// values that would then be discarded — so it is not run.
    ///
    /// This is a pure decision, kept separate from the collection code so it
    /// can be tested without a WMI dependency.
    /// </summary>
    public static class NvidiaGpuFallbackPolicy
    {
        /// <summary>
        /// True when at least one metric the fallback can supply is missing
        /// from the backend's readings, and the fallback is therefore worth
        /// invoking.
        /// </summary>
        /// <param name="backendGpuTemperature">The backend's GPU temperature, if it read one.</param>
        /// <param name="backendGpuVramUsedBytes">The backend's VRAM used figure, if it read one.</param>
        /// <param name="backendGpuVramTotalBytes">The backend's VRAM total figure, if it read one.</param>
        public static bool ShouldInvoke(
            double? backendGpuTemperature,
            ulong? backendGpuVramUsedBytes,
            ulong? backendGpuVramTotalBytes)
        {
            return !backendGpuTemperature.HasValue
                || !backendGpuVramUsedBytes.HasValue
                || !backendGpuVramTotalBytes.HasValue;
        }
    }
}
