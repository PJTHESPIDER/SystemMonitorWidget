using Microsoft.Windows.Widgets;

namespace System_Monitor.Services
{
    internal static class WidgetTemplates
    {
        public static string GetTemplateForSize(WidgetSize size)
        {
            return size switch
            {
                WidgetSize.Small => SmallTemplate,
                WidgetSize.Large => LargeTemplate,
                _ => MediumTemplate
            };
        }

                public const string SmallTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "separator": true,
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "CPU Temp",
                      "weight": "Bolder",
                      "size": "Small"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpuTemp}",
                      "weight": "Bolder",
                      "size": "Small",
                      "horizontalAlignment": "Center"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "separator": true,
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "VRAM",
                      "weight": "Bolder",
                      "size": "Small"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${gpuVramTotal}",
                      "weight": "Bolder",
                      "size": "Small",
                      "horizontalAlignment": "Center"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "separator": true,
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "RAM",
                      "weight": "Bolder",
                      "size": "Small"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${ramTotal}",
                      "weight": "Bolder",
                      "size": "Small",
                      "horizontalAlignment": "Center"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Small",
              "separator": true,
              "columns": [
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "Storage",
                      "weight": "Bolder",
                      "size": "Small"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${diskTotal}",
                      "weight": "Bolder",
                      "size": "Small",
                      "horizontalAlignment": "Center"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;public const string MediumTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "TextBlock",
              "text": "CPU",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small"
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
              "text": "GPU",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small",
              "separator": true
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
                      "text": "${gpu}",
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
                      "text": "VRAM Used",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuVramUsed}",
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
              "text": "Memory (RAM)",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "Small",
              "separator": true
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
                      "text": "Used",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramUsed}",
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
                      "text": "Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramTotal}",
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
                      "text": "Usage",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramPercent}",
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
              "size": "Small",
              "spacing": "Small",
              "separator": true
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
                      "text": "Used",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskUsed}",
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
                      "text": "Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskTotal}",
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
                      "text": "Usage",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskPercent}",
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

        public const string LargeTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "TextBlock",
              "text": "Processor (CPU)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium",
              "separator": true
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
              "text": "Graphics (GPU)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium",
              "separator": true
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
                    },
                    {
                      "type": "TextBlock",
                      "text": "Intel: ${igpu} | NVIDIA: ${dgpu}",
                      "size": "Small",
                      "isSubtle": true,
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
                      "text": "Dedicated VRAM",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuVram}",
                      "size": "Small",
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
              "text": "Memory (RAM)",
              "weight": "Bolder",
              "size": "Medium",
              "spacing": "Medium",
              "separator": true
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
                      "text": "Memory Used",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramUsed}",
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
                      "text": "Total Memory",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramTotal}",
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
                      "text": "Memory Load",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ramPercent}",
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
              "spacing": "Medium",
              "separator": true
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
                      "text": "Disk Used",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskUsed}",
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
                      "text": "Disk Total",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskTotal}",
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
                      "text": "Disk Load",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${diskPercent}",
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
              "spacing": "Medium",
              "separator": true
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
                }
              ]
            }
          ]
        }
        """;
    }
}


