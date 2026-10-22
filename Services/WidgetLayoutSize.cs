namespace System_Monitor.Services
{
    /// <summary>
    /// The size of a widget layout.
    ///
    /// <para>
    /// This is the project's own size enum rather than the Windows App SDK's
    /// <c>WidgetSize</c>, so that the templates can be exercised by the test
    /// project without referencing the Windows App SDK. The provider maps
    /// the host's size onto this one.
    /// </para>
    /// </summary>
    public enum WidgetLayoutSize
    {
        Small,
        Medium,
        Large,
    }
}
