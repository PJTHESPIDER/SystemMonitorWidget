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
    /// Medium and Large use supported stretch-height containers to share
    /// available vertical slack between leading, peer-section and trailing
    /// gaps. This avoids fixed-pixel assumptions and preset-only spacing.
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
        /// Gets the customization template for CPU temperature setup and GPU selection.
        /// This template is used when the host requests the widget's customization view
        /// via <see cref="IWidgetProvider2.OnCustomizationRequested"/>.
        /// </summary>
        public static string GetCustomizationTemplate() => CustomizationTemplate;

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
        /// The card is 148 pixels tall and its body has room for four
        /// rows. It shows the CPU row, the GPU row, the RAM row and the
        /// Disk row, each headed by the name of what it reports. The GPU
        /// row does not name the device and the figures carry no
        /// sub-headings: the small card has no room for them, and the
        /// units — "%", "°C" and a byte figure — say what each figure is.
        /// </para>
        /// </summary>
        public const string SmallTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "verticalContentAlignment": "Center",
          "body": [
            { "type": "TextBlock", "text": "Telemetry is stale", "size": "ExtraSmall", "isSubtle": true, "spacing": "None", "$when": "${sampleIsStale}" },
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
                  ],
                  "$when": "${cpuTempShow}"
                }
              ]
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
                      "text": "${gpuMemoryCapacityCompact}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ],
              "$when": "${gpuDevicePresent}"
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
                      "text": "RAM",
                      "weight": "Bolder",
                      "size": "Small",
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
                      "text": "${ramCapacityCompact}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None",
                      "wrap": true
                    }
                  ]
                }
              ]
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
                      "text": "Disk",
                      "weight": "Bolder",
                      "size": "Small",
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
                      "text": "${diskCapacityCompact}",
                      "size": "ExtraSmall",
                      "weight": "Bolder",
                      "spacing": "None",
                      "wrap": true
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        /// <summary>
        /// The medium layout: the CPU and the selected GPU are named with
        /// normalized compact names, and one additional device is listed
        /// when there is one. RAM and disk show compact used/total capacities.
        /// GPU memory uses the compact field with its semantic label.
        /// </summary>
        public const string MediumTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "verticalContentAlignment": "Center",
          "body": [
            { "type": "TextBlock", "text": "Telemetry is stale", "size": "ExtraSmall", "isSubtle": true, "spacing": "None", "$when": "${sampleIsStale}" },
            {
              "type": "Container",
              "id": "layoutGapLeading",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "CPU — ${cpuNameCompact}",
                  "weight": "Bolder",
                  "size": "Small",
                  "spacing": "None",
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
                      ],
                      "$when": "${cpuTempShow}"
                    }
                  ]
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeGpu",
              "height": "stretch",
              "spacing": "None",
              "items": [],
              "$when": "${gpuSectionPresent}"
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "GPU — ${gpuNameCompact}",
                  "weight": "Bolder",
                  "size": "Small",
                  "spacing": "None",
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
                          "text": "${gpuVramLabelCompact}",
                          "size": "Small",
                          "isSubtle": true,
                          "wrap": true
                        },
                        {
                          "type": "TextBlock",
                          "text": "${gpuMemoryCapacityCompact}",
                          "size": "Medium",
                          "weight": "Bolder",
                          "spacing": "None",
                          "wrap": true
                        }
                      ]
                    }
                  ],
                  "$when": "${gpuDevicePresent}"
                },
                {
                  "type": "TextBlock",
                  "text": "Other GPU — ${gpuExtra1}",
                  "size": "Small",
                  "isSubtle": true,
                  "spacing": "None",
                  "wrap": true,
                  "$when": "${gpuExtra1Available}"
                }
              ],
              "$when": "${gpuSectionPresent}"
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeMemory",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "Memory (RAM)",
                  "weight": "Bolder",
                  "size": "Small",
                  "spacing": "None"
                },
                {
                  "type": "TextBlock",
                  "text": "${ramCapacityCompact}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "spacing": "None",
                  "wrap": true
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeStorage",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "Storage (Disk)",
                  "weight": "Bolder",
                  "size": "Small",
                  "spacing": "None"
                },
                {
                  "type": "TextBlock",
                  "text": "${diskCapacityCompact}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "spacing": "None",
                  "wrap": true
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapTrailing",
              "height": "stretch",
              "spacing": "None",
              "items": []
            }
          ]
        }
        """;

        /// <summary>
        /// The large layout: everything the medium layout shows, plus the
        /// network figures and up to three additional devices.
        ///
        /// <para>
        /// The Sample heading and sampleTime value are removed from display.
        /// The Network section is a balanced Download/Upload layout.
        /// Snapshot and measurement timestamps are retained in the payload
        /// for diagnostic provenance and internal freshness logic.
        /// </para>
        /// </summary>
        public const string LargeTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "verticalContentAlignment": "Center",
          "body": [
            { "type": "TextBlock", "text": "Telemetry is stale", "size": "ExtraSmall", "isSubtle": true, "spacing": "None", "$when": "${sampleIsStale}" },
            {
              "type": "Container",
              "id": "layoutGapLeading",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
              "type": "TextBlock",
              "text": "Processor (CPU) — ${cpuNameCompact}",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "None",
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
                  ],
                  "$when": "${cpuTempShow}"
                }
              ]
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeGpu",
              "height": "stretch",
              "spacing": "None",
              "items": [],
              "$when": "${gpuSectionPresent}"
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
              "type": "TextBlock",
              "text": "Graphics (GPU) — ${gpuNameCompact}",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "None",
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
                      "text": "${gpuMemoryCapacityCompact}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None",
                      "wrap": true
                    }
                  ]
                }
              ],
              "$when": "${gpuDevicePresent}"
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
                }
              ],
              "$when": "${gpuSectionPresent}"
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeMemory",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
              "type": "TextBlock",
              "text": "Memory (RAM)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "None"
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
                      "text": "Used / Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramCapacityCompact}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeStorage",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
              "type": "TextBlock",
              "text": "Storage (Disk)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "None"
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
                      "text": "Used / Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskCapacityCompact}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    }
                  ]
                }
              ]
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapBeforeNetwork",
              "height": "stretch",
              "spacing": "None",
              "items": []
            },
            {
              "type": "Container",
              "spacing": "None",
              "items": [
                {
              "type": "TextBlock",
              "text": "Network",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "None"
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
                }
              ]
                }
              ]
            },
            {
              "type": "Container",
              "id": "layoutGapTrailing",
              "height": "stretch",
              "spacing": "None",
              "items": []
            }
          ]
        }
        """;

        /// <summary>
        /// The customization template for CPU temperature setup and GPU selection.
        /// This template is used when the host requests the widget's customization view
        /// via <see cref="IWidgetProvider2.OnCustomizationRequested"/>.
        /// </summary>
        public const string CustomizationTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.6",
          "body": [
            {
              "type": "TextBlock",
              "text": "${cpuTempSetupStatus}",
              "size": "Default",
              "isSubtle": false,
              "spacing": "None",
              "wrap": true
            },
            {
              "type": "TextBlock",
              "text": "CPU Temperature",
              "size": "Small",
              "isSubtle": true,
              "spacing": "Medium",
              "wrap": true
            },
            {
              "type": "ActionSet",
              "spacing": "Small",
              "$when": "${cpuTempShow}",
              "actions": [
                {
                  "type": "Action.Submit",
                  "title": "Hide CPU temperature",
                  "data": { "action": "toggleCpuTemp" }
                }
              ]
            },
            {
              "type": "ActionSet",
              "spacing": "Small",
              "$when": "${cpuTempShow == false}",
              "actions": [
                {
                  "type": "Action.Submit",
                  "title": "Show CPU temperature",
                  "data": { "action": "toggleCpuTemp" }
                }
              ]
            },
            {
              "type": "ActionSet",
              "spacing": "Small",
              "$when": "${cpuTempSetupRequired}",
              "actions": [
                {
                  "type": "Action.Submit",
                  "title": "Set up CPU temperature...",
                  "data": { "action": "setupCpuTemperature" }
                }
              ]
            },
            {
              "type": "ActionSet",
              "spacing": "Small",
              "$when": "${cpuTempSetupNotEnabled}",
              "actions": [
                {
                  "type": "Action.Submit",
                  "title": "Set up CPU temperature...",
                  "data": { "action": "setupCpuTemperature" }
                }
              ]
            }
          ]
        }
        """;
    }
}
