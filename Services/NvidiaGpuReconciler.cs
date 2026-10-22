using System;
using System.Collections.Generic;

namespace System_Monitor.Services
{
    /// <summary>
    /// The outcome of reconciling an nvidia-smi sample against a
    /// device-identified backend device.
    /// </summary>
    public enum NvidiaReconciliationOutcome
    {
        /// <summary>
        /// The sample was matched to exactly one backend device by an
        /// identity key. The reading may be attributed to that device.
        /// </summary>
        Matched,

        /// <summary>
        /// The sample's identity could not be reconciled: the key is absent,
        /// or it matches more than one backend device, or the backend did not
        /// report the device at all. The reading must be kept separate or
        /// omitted — never attributed to a device chosen by position or name.
        /// </summary>
        Unreconciled,

        /// <summary>
        /// The sample's identity is ambiguous among the samples themselves:
        /// two or more nvidia-smi rows carry the same key. None of them may be
        /// attributed to a specific device.
        /// </summary>
        Ambiguous,
    }

    /// <summary>
    /// A sample together with the result of its reconciliation.
    /// </summary>
    public sealed record NvidiaReconciliationResult(
        NvidiaGpuSample Sample,
        NvidiaReconciliationOutcome Outcome,
        string? MatchedDeviceId)
    {
        /// <summary>
        /// True when the reading may be attributed to a specific device.
        /// </summary>
        public bool IsMatchable => Outcome == NvidiaReconciliationOutcome.Matched;
    }

    /// <summary>
    /// Reconciles nvidia-smi samples against the devices a device-identified
    /// backend reported.
    ///
    /// The rule this class exists to enforce: a reading is never merged into
    /// another GPU merely by row index or by display name. A sample is
    /// attributed to a device only when an identity key matches exactly one
    /// backend device and exactly one sample.
    ///
    /// That means:
    ///
    ///   - A sample whose PCI device key is absent, or whose key matches no
    ///     backend device, is unreconciled.
    ///   - A sample whose key matches two backend devices (two identical
    ///     cards of the same model) is ambiguous, because the key alone
    ///     cannot say which card was measured.
    ///   - Two samples sharing one key (two identical cards) make each other
    ///     ambiguous, because the key alone cannot say which is which.
    ///
    /// In every one of those cases the caller must keep the reading separate
    /// or omit it, rather than assign it to a device it may not have come
    /// from.
    /// </summary>
    public static class NvidiaGpuReconciler
    {
        /// <summary>
        /// Reconciles each sample against the backend devices.
        ///
        /// <paramref name="backendDeviceIds"/> is the set of PCI device keys
        /// the backend reported, each paired with the backend's own device id
        /// so the caller can attribute the reading.
        /// </summary>
        public static IReadOnlyList<NvidiaReconciliationResult> Reconcile(
            IReadOnlyList<NvidiaGpuSample> samples,
            IReadOnlyList<BackendGpuIdentity> backendDeviceIds)
        {
            var results = new List<NvidiaReconciliationResult>();

            if (samples is null || samples.Count == 0)
                return results;

            // Group the backend devices by identity key. A key held by more
            // than one backend device cannot identify a single device.
            var backendByKey = new Dictionary<string, List<BackendGpuIdentity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in backendDeviceIds ?? Array.Empty<BackendGpuIdentity>())
            {
                if (string.IsNullOrWhiteSpace(device.PciDeviceKey))
                    continue;

                if (!backendByKey.TryGetValue(device.PciDeviceKey, out var list))
                {
                    list = new List<BackendGpuIdentity>();
                    backendByKey[device.PciDeviceKey] = list;
                }

                list.Add(device);
            }

            // Group the samples by identity key, for the same reason.
            var sampleCountsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in samples)
            {
                if (string.IsNullOrWhiteSpace(sample.PciDeviceKey))
                    continue;

                sampleCountsByKey.TryGetValue(sample.PciDeviceKey, out var count);
                sampleCountsByKey[sample.PciDeviceKey] = count + 1;
            }

            foreach (var sample in samples)
            {
                if (string.IsNullOrWhiteSpace(sample.PciDeviceKey))
                {
                    // No identity to reconcile against. The sample may still
                    // carry a UUID, but the backend does not expose one, so
                    // there is nothing to match it to.
                    results.Add(new NvidiaReconciliationResult(
                        sample,
                        NvidiaReconciliationOutcome.Unreconciled,
                        null));
                    continue;
                }

                if (sampleCountsByKey.TryGetValue(sample.PciDeviceKey, out var count) && count > 1)
                {
                    // Two identical cards: the key cannot say which is which.
                    results.Add(new NvidiaReconciliationResult(
                        sample,
                        NvidiaReconciliationOutcome.Ambiguous,
                        null));
                    continue;
                }

                if (!backendByKey.TryGetValue(sample.PciDeviceKey, out var candidates) || candidates.Count == 0)
                {
                    results.Add(new NvidiaReconciliationResult(
                        sample,
                        NvidiaReconciliationOutcome.Unreconciled,
                        null));
                    continue;
                }

                if (candidates.Count > 1)
                {
                    // The backend reported two devices with the same key, so
                    // the key cannot say which one was measured.
                    results.Add(new NvidiaReconciliationResult(
                        sample,
                        NvidiaReconciliationOutcome.Ambiguous,
                        null));
                    continue;
                }

                results.Add(new NvidiaReconciliationResult(
                    sample,
                    NvidiaReconciliationOutcome.Matched,
                    candidates[0].DeviceId));
            }

            return results;
        }
    }

    /// <summary>
    /// A backend device's identity, as seen by the reconciler.
    /// </summary>
    /// <param name="DeviceId">The backend's own device id.</param>
    /// <param name="PciDeviceKey">The normalized PCI device key, e.g. "VEN_10DE&amp;DEV_1E91".</param>
    public sealed record BackendGpuIdentity(string DeviceId, string PciDeviceKey);
}
