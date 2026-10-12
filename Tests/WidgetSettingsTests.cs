using Microsoft.VisualStudio.TestTools.UnitTesting;
using System_Monitor.Models;

namespace System_Monitor.Tests
{
    [TestClass]
    public class WidgetSettingsTests
    {
        [TestMethod]
        public void Defaults_AreExpected()
        {
            var settings = new WidgetSettings();
            Assert.IsTrue(settings.ShowCpu);
            Assert.IsTrue(settings.ShowGpu);
            Assert.IsTrue(settings.ShowRam);
            Assert.IsTrue(settings.ShowDisk);
            Assert.IsTrue(settings.ShowNetwork);
            Assert.AreEqual(5, settings.RefreshIntervalSeconds);
        }

        [TestMethod]
        public void PropertyChanged_Raised_OnChange()
        {
            var settings = new WidgetSettings();
            var saw = false;
            settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(WidgetSettings.ShowCpu))
                    saw = true;
            };

            settings.ShowCpu = false;
            Assert.IsTrue(saw);
        }

        [TestMethod]
        public void RefreshInterval_ClampsToMinimum()
        {
            var settings = new WidgetSettings();
            settings.RefreshIntervalSeconds = 0;
            Assert.AreEqual(1, settings.RefreshIntervalSeconds);

            settings.RefreshIntervalSeconds = 10;
            Assert.AreEqual(10, settings.RefreshIntervalSeconds);
        }
    }
}