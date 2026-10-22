using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace System_Monitor.Services
{
    /// <summary>
    /// An immutable description of a widget instance.
    ///
    /// Entries are immutable on purpose: the registry stores them in a
    /// <see cref="ConcurrentDictionary"/>, and a concurrent dictionary only
    /// makes the *collection* safe. Mutable properties on the values would
    /// still be read and written without synchronization. Replacing an
    /// immutable entry atomically is what makes widget state thread-safe.
    /// </summary>
    /// <typeparam name="TSize">
    /// The widget size type. Generic so the registry can be exercised by the
    /// test project, which does not reference the Windows App SDK.
    /// </typeparam>
    public sealed record WidgetEntry<TSize> where TSize : struct
    {
        public string WidgetId { get; init; } = string.Empty;

        public string WidgetName { get; init; } = string.Empty;

        public TSize Size { get; init; }

        public bool IsActive { get; init; }

        /// <summary>
        /// The GPU this widget presents, as the device's per-instance
        /// identity.
        ///
        /// <para>
        /// This is a device identity, never an enumeration index: the
        /// enumeration index shifts whenever the device set changes, so a
        /// selection stored against it would silently start pointing at a
        /// different card. The per-instance identity (the Windows PNP device
        /// id) names one specific adapter and stays valid across reboots.
        /// </para>
        ///
        /// <para>
        /// Null means "no device was selected", in which case the documented
        /// default policy chooses the device.
        /// </para>
        /// </summary>
        public string? SelectedGpuInstanceId { get; init; }
    }

    /// <summary>
    /// Thread-safe registry of the widgets this provider is serving.
    ///
    /// All mutations replace the immutable <see cref="WidgetEntry{TSize}"/>
    /// stored under the widget id, so a reader that has obtained an entry
    /// always observes a consistent, complete snapshot of that widget's
    /// state — never a half-updated one.
    /// </summary>
    public sealed class WidgetRegistry<TSize> where TSize : struct
    {
        private readonly ConcurrentDictionary<string, WidgetEntry<TSize>> _entries =
            new(StringComparer.Ordinal);

        /// <summary>Total number of registered widgets, active or not.</summary>
        public int Count => _entries.Count;

        /// <summary>True when no widgets are registered.</summary>
        public bool IsEmpty => _entries.IsEmpty;

        /// <summary>True when at least one registered widget is active.</summary>
        public bool HasActiveWidgets => _entries.Values.Any(e => e.IsActive);

        /// <summary>
        /// Adds or replaces a widget entry.
        /// </summary>
        public void Set(WidgetEntry<TSize> entry)
        {
            if (entry is null)
                throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.WidgetId))
                throw new ArgumentException("A widget entry must have an id.", nameof(entry));

            _entries[entry.WidgetId] = entry;
        }

        /// <summary>
        /// Removes a widget. Returns true when an entry was removed.
        /// </summary>
        public bool Remove(string widgetId)
        {
            if (string.IsNullOrEmpty(widgetId))
                return false;
            return _entries.TryRemove(widgetId, out _);
        }

        /// <summary>
        /// Looks up a widget entry.
        /// </summary>
        public bool TryGet(string widgetId, out WidgetEntry<TSize>? entry)
        {
            if (string.IsNullOrEmpty(widgetId))
            {
                entry = null;
                return false;
            }
            return _entries.TryGetValue(widgetId, out entry);
        }

        /// <summary>
        /// Updates the activation state (and optionally the size) of an
        /// existing widget. Returns false when the widget is not registered.
        ///
        /// The entry is replaced atomically; readers see either the old state
        /// or the new state, never a mixture.
        /// </summary>
        public bool TrySetActive(string widgetId, bool isActive, TSize? size = default)
        {
            if (string.IsNullOrEmpty(widgetId))
                return false;

            // Compare-and-swap loop: the entry is replaced atomically, so a
            // concurrent reader never observes a partially updated state.
            while (true)
            {
                if (!_entries.TryGetValue(widgetId, out var existing) || existing is null)
                    return false;

                var updated = existing with
                {
                    IsActive = isActive,
                    Size = size.HasValue ? size.Value : existing.Size,
                };

                if (_entries.TryUpdate(widgetId, updated, existing))
                    return true;
            }
        }

        /// <summary>
        /// Updates the size of an existing widget. Returns false when the
        /// widget is not registered.
        /// </summary>
        public bool TrySetSize(string widgetId, TSize size)
        {
            if (string.IsNullOrEmpty(widgetId))
                return false;

            while (true)
            {
                if (!_entries.TryGetValue(widgetId, out var existing) || existing is null)
                    return false;

                if (_entries.TryUpdate(widgetId, existing with { Size = size }, existing))
                    return true;
            }
        }

        /// <summary>
        /// Updates the selected GPU of an existing widget. Returns false when
        /// the widget is not registered.
        ///
        /// <para>
        /// The value is the device's per-instance identity. Passing null
        /// clears the selection, which returns the widget to the default
        /// selection policy.
        /// </para>
        ///
        /// <para>
        /// The entry is replaced atomically, so a reader never observes a
        /// partially updated state.
        /// </para>
        /// </summary>
        public bool TrySetSelectedGpu(string widgetId, string? instanceId)
        {
            if (string.IsNullOrEmpty(widgetId))
                return false;

            var normalized = string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();

            while (true)
            {
                if (!_entries.TryGetValue(widgetId, out var existing) || existing is null)
                    return false;

                if (string.Equals(existing.SelectedGpuInstanceId, normalized, StringComparison.Ordinal))
                    return true;

                var updated = existing with { SelectedGpuInstanceId = normalized };

                if (_entries.TryUpdate(widgetId, updated, existing))
                    return true;
            }
        }

        /// <summary>
        /// A snapshot of the currently active widgets.
        /// </new_string>
        /// The returned list is a private copy, and each entry is immutable,
        /// so the caller can iterate it while widgets are being activated or
        /// deactivated without any locking.
        /// </summary>
        public IReadOnlyList<WidgetEntry<TSize>> GetActive()
        {
            var active = new List<WidgetEntry<TSize>>();
            foreach (var entry in _entries.Values)
            {
                if (entry.IsActive)
                    active.Add(entry);
            }
            return active;
        }

        /// <summary>
        /// A snapshot of every registered widget.
        /// </summary>
        public IReadOnlyList<WidgetEntry<TSize>> GetAll()
        {
            var all = new List<WidgetEntry<TSize>>(_entries.Values);
            return all;
        }
    }
}
