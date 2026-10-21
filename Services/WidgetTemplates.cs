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
                      "text": "${gpuVram}",
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
                      "text": "${ram}",
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
                          "text": "${disk}",
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
                      "text": "VRAM",
                      "size": "Small",
                      "isSubtle": true
                    },
                    {
                      "type": "TextBlock",
                      "text": "${gpuVram}",
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
                      "text": "Integrated: ${igpu} | Discrete: ${dgpu}",
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
                }
              ]
            }
          ]
        }
        """;
    }
}


