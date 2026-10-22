using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace System_Monitor.Services
{
    /// <summary>
    /// The data payload a widget template binds against.
    ///
    /// <para>
    /// A widget template refers to payload values with <c>${name}</c>, and to
    /// payload booleans with <c>$when</c> conditions. The payload keeps the
    /// two apart, because a value bound into a text block and a value used
    /// as a condition are not the same kind of thing: a text value is
    /// always a string, while a condition must be a real boolean.
    /// </para>
    ///
    /// <para>
    /// The payload is deliberately a flat map rather than an object graph,
    /// because the widget host binds by property name.
    /// </para>
    /// </summary>
    public sealed class WidgetPayload
    {
        private readonly Dictionary<string, string> _text;
        private readonly Dictionary<string, bool> _flags;

        public WidgetPayload(
            Dictionary<string, string> text,
            Dictionary<string, bool> flags)
        {
            _text = text ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _flags = flags ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The text values, bound with <c>${name}</c>.</summary>
        public IReadOnlyDictionary<string, string> Text => _text;

        /// <summary>The boolean values, used by <c>$when</c> conditions.</summary>
        public IReadOnlyDictionary<string, bool> Flags => _flags;

        /// <summary>
        /// Every name the payload can be referenced by, text and flags
        /// together. A template that references a name outside this set is
        /// binding against a property the payload does not have.
        /// </summary>
        public IReadOnlyCollection<string> Keys =>
            _text.Keys.Concat(_flags.Keys).ToList();

        /// <summary>True when the payload carries the named property.</summary>
        public bool Contains(string key) =>
            !string.IsNullOrEmpty(key)
            && (_text.ContainsKey(key) || _flags.ContainsKey(key));

        /// <summary>
        /// The text value for a name, or the not-available placeholder when
        /// the payload does not carry it.
        /// </summary>
        public string GetText(string key) =>
            _text.TryGetValue(key, out var value) ? value : WidgetPayloadPresenter.NotAvailable;

        /// <summary>
        /// The boolean value for a name. A name the payload does not carry is
        /// false: an absent condition must not enable anything.
        /// </summary>
        public bool GetFlag(string key) =>
            _flags.TryGetValue(key, out var value) && value;

        /// <summary>
        /// Serializes the payload to the JSON the widget host expects.
        ///
        /// Text values are JSON strings and flags are JSON booleans, so a
        /// template's <c>$when</c> condition sees a real boolean rather than
        /// a non-empty string.
        /// </summary>
        public string ToJson()
        {
            var buffer = new JsonObject();

            foreach (var pair in _text)
                buffer[pair.Key] = JsonValue.Create(pair.Value);

            foreach (var pair in _flags)
                buffer[pair.Key] = JsonValue.Create(pair.Value);

            return buffer.ToJsonString();
        }
    }
}
