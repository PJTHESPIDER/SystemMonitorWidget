using System;
using System.Collections.Generic;

namespace System_Monitor.Services
{
    /// <summary>
    /// The Adaptive Card templates the widget renders.
    ///
    /// <para><b>Template rules</b></para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// Every template binds only to names the payload actually produces. A
    /// name that is not in the payload would render as an empty string,
    /// which is indistinguishable from a real value; the payload therefore
    /// always carries every name a template may reference.
    /// </description></item>
    /// <item><description>
    /// Each size has its own template, so the layout can differ by size
    /// rather than being one card scaled down. The provider maps the
    /// host's size onto <see cref="WidgetLayoutSize"/> and publishes the
    /// matching template.
    /// </description></item>
    /// <item><description>
    /// A block that only makes sense when there is a device to show is
    /// guarded by a <c>$when</c> condition on a payload boolean, so a
    /// machine with no GPU does not render an empty GPU section.
    /// </description></item>
    /// <item><description>
    /// No template carries a root-level <c>actions</c> array. The host
    /// renders it as a button row after the card body, and the board does
    /// not scroll, so the row competes with the figures for the card's
    /// height and the overflow is clipped.
    /// </description></item>
    /// </list>
    /// </summary>
    public static class WidgetTemplates
    {
        public static string GetTemplateForSize(WidgetLayoutSize size)
        {
            return size switch
            {
                WidgetLayoutSize.Small => SmallTemplate,
                WidgetLayoutSize.Large => LargeTemplate,
                _ => MediumTemplate
            };
        }

        /// <summary>
        /// Builds the template for a size.
        ///
        /// <para>
        /// The templates carry no root-level <c>actions</c> array. The host
        /// renders that array as a row of buttons <i>after</i> the card
        /// body, and the widget board does not scroll: a card whose body
        /// plus its action row is taller than the card has the overflow
        /// clipped, which is what pushed the action buttons off the bottom
        /// of every size. Removing the actions gives the body the whole
        /// card, which is what the figures need.
        /// </para>
        ///
        /// <para>
        /// The device picker went with them. It was an action per device,
        /// and it cost the same row. The presented device is still named on
        /// the card, and the other devices are still listed, so the card
        /// still says what it is showing.
        /// </para>
        /// </summary>
        public static string BuildTemplate(WidgetLayoutSize size) => GetTemplateForSize(size);

        /// <summary>
        /// Escapes a string for inclusion in a JSON string literal.
        /// </summary>
        private static string EscapeJson(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// The small layout: a compact summary of the figures that fit,
        /// with no additional devices.
        ///
        /// <para>
        /// The card is 148 pixels tall and its body has room for three
        /// rows. It shows the CPU row, the GPU row and the RAM/disk row,
        /// each headed by the name of what it reports. The GPU row does not
        /// name the device and the figures carry no sub-headings: the
        /// small card has no room for them, and the units — "%", "°C" and
        /// a byte figure — say what each figure is.
        /// </para>
        /// </summary>
        public const string SmallTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "CPU",
                      "weight": "Bolder",
                      "size": "Small",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpu}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpuClock}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpuTemp}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "GPU",
                      "weight": "Bolder",
                      "size": "Small",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpu}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpuTemp}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpuVramUsed}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "RAM",
                      "weight": "Bolder",
                      "size": "Small",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${ramCompact}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Disk",
                      "weight": "Bolder",
                      "size": "Small",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${diskCompact}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        /// <summary>
        /// The medium layout: the CPU and the selected GPU are named, and one
        /// additional device is listed when there is one.
        /// </summary>
        public const string MediumTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "TextBlock",
              "text": "CPU — ${cpuName}",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small",
              "wrap": true
            },
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Usage",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Speed",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpuClock}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Temp",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpuTemp}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "GPU — ${gpuLabel}",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small",
              "wrap": true,
              "$when": "${gpuDevicePresent}"
            },
            {
              "type": "TextBlock",
              "text": "${gpuSelectionNotice}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuSelectionMissing}"
            },
            {
              "type": "ColumnSet",
              "spacing": "None",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Usage",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Temp",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuTemp}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpuVramLabel}",
                      "size": "Small",
                      "isSubtle": true,
                      "wrap": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuVram}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None",
                      "wrap": true
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Other GPU — ${gpuExtra1}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuExtra1Available}"
            },
            {
              "type": "TextBlock",
              "text": "Memory (RAM)",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "${ramCompact}",
              "size": "Medium",
              "weight": "Bolder",
              "spacing": "None",
              "wrap": true
            },
            {
              "type": "TextBlock",
              "text": "Storage (Disk)",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small"
            },
            {
              "type": "TextBlock",
              "text": "${diskCompact}",
              "size": "Medium",
              "weight": "Bolder",
              "spacing": "None",
              "wrap": true
            }
          ]
        }
        """;

        /// <summary>
        /// The large layout: everything the medium layout shows, plus the
        /// network figures, the sample time, and up to three additional
        /// devices.
        /// </summary>
        public const string LargeTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "TextBlock",
              "text": "Processor (CPU) — ${cpuName}",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium",
              "separator": true,
              "wrap": true
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "CPU Load",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Clock Speed",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpuClock}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Temperature",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpuTemp}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Graphics (GPU) — ${gpuLabel}",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium",
              "wrap": true,
              "$when": "${gpuDevicePresent}"
            },
            {
              "type": "TextBlock",
              "text": "${gpuSelectionNotice}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuSelectionMissing}"
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "GPU Load",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "GPU Temp",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuTemp}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpuVramLabel}",
                      "size": "Small",
                      "isSubtle": true,
                      "wrap": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuVram}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None",
                      "wrap": true
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Other GPU — ${gpuExtra1}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuExtra1Available}"
            },
            {
              "type": "TextBlock",
              "text": "Other GPU — ${gpuExtra2}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuExtra2Available}"
            },
            {
              "type": "TextBlock",
              "text": "Other GPU — ${gpuExtra3}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None",
              "wrap": true,
              "$when": "${gpuExtra3Available}"
            },
            {
              "type": "TextBlock",
              "text": "Memory (RAM)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium"
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Used / Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ram}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Storage (Disk)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium"
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Used / Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${disk}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "Network",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium"
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Download",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${netDown}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Upload",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${netUp}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Sample",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${sampleTime}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
    }
}
