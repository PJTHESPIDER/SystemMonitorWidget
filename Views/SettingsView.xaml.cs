using Microsoft.UI.Xaml.Controls;
using System_Monitor.Models;

namespace System_Monitor.Views
{
    public sealed partial class SettingsView : UserControl
    {
        public WidgetSettings Settings { get; }

        public SettingsView(WidgetSettings settings)
        {
            this.InitializeComponent();
            Settings = settings;
            this.DataContext = Settings;
        }
    }
}