using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;

namespace SystemMonitor.Tests
{
    public static class DebugRunner
    {
        public static async Task Main()
        {
            var clock = new FakeTimeProvider();
            var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
            var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
            var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
            var service = CreateService(source);

            var publications = new ConcurrentQueue<string>();

            var pipeline = new UpdatePipeline<TestSize>(
                service, OneActiveWidget(),
                snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); Console.WriteLine($"PUB: {p}"); publications.Enqueue(p); return p; },
                (payload, widget) => Task.CompletedTask,
                TimeSpan.FromMinutes(5), clock);

            // --- Cycle 1: Both GPUs readable, gpu1 becomes tracked secondary ---
            pipeline.Start();
            await WaitForPublicationCountAsync(publications, 1);
            var payload1 = publications.First();
            Console.WriteLine($"Payload 1: {payload1}");
            var extra1 = GetExtraAvailable(payload1, 1);
            Console.WriteLine($"GetExtraAvailable(payload1, 1) = {extra1}");

            if (extra1)
            {
                Console.WriteLine("SUCCESS: Extra device is available!");
            }
            else
            {
                Console.WriteLine("FAIL: Extra device is NOT available!");
                // Print all flags to debug
                var payloadObj = WidgetPayloadPresenter.Present(null);
                var flags = payloadObj.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in flags)
                {
                    if (f.FieldType == typeof(Dictionary<string, bool>))
                    {
                        var dict = (Dictionary<string, bool>)f.GetValue(payloadObj)!;
                        foreach (var kvp in dict)
                        {
                            if (kvp.Key.StartsWith("gpuExtra") && kvp.Key.EndsWith("Available"))
                                Console.WriteLine($"  Flag: {kvp.Key} = {kvp.Value}");
                        }
                    }
                }
            }
        }

        private static async Task WaitForPublicationCountAsync(ConcurrentQueue<string> queue, int expectedCount)
        {
            for (int i = 0; i < 100; i++)
            {
                if (queue.Count >= expectedCount) return;
                await Task.Delay(10);
            }
            throw new TimeoutException($"Publication count did not reach {expectedCount}");
        }

        /// <summary>
        /// Builds a readable NVIDIA GPU fixture.
        /// </summary>
        private static LhmDeviceDescriptor NvidiaGpu(string deviceId, string displayName, string instanceId, bool readable = true)
        {
            var sensors = readable
                ? new List<LhmSensorDescriptor>
                {
                    LhmFixtures.Temp("GPU Core", 61.0),
                    LhmFixtures.Clock("GPU Core", 330.0),
                    LhmFixtures.Load("GPU Core", 25.0),
                    LhmFixtures.Load("GPU Memory Controller", 18.0),
                    LhmFixtures.Load("GPU Video Engine", 0.0),
                    LhmFixtures.Memory("GPU Memory Used", 1233.0),
                    LhmFixtures.Memory("GPU Memory Total", 8192.0),
                }
                : new List<LhmSensorDescriptor>();

            return new LhmDeviceDescriptor
            {
                DeviceId = deviceId,
                DisplayName = displayName,
                Vendor = "NVIDIA",
                IsCpu = false,
                IsIntegratedGpu = false,
                PciDeviceKey = "VEN_10DE&DEV_1E91",
                InstanceId = instanceId,
                Sensors = sensors,
            };
        }

        /// <summary>
        /// Creates a SystemMetricsService with an injected fake sensor source.
        /// </summary>
        private static SystemMetricsService CreateService(ILhmSensorSource sensorSource)
        {
            var model = new FakeModel();
            var nvidiaFallback = new NvidiaGpuFallback();
            return new SystemMetricsService(model, nvidiaFallback, sensorSource);
        }

        private sealed class FakeModel : SystemMetrics { }

        private static WidgetRegistry<TestSize> OneActiveWidget()
        {
            var widgets = new WidgetRegistry<TestSize>();
            widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w1", IsActive = true });
            return widgets;
        }

        /// <summary>
        /// Helper to get the extra-available flag from a JSON payload.
        /// </summary>
        private static bool GetExtraAvailable(string payload, int index)
        {
            var key = $"gpuExtra{index}Available";
            // Check for true/false in JSON
            return payload.Contains($"\"{key}\":true") || payload.Contains($"\"{key}\": true");
        }
    }
}