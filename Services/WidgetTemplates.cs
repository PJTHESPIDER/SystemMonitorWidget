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
              "type": "TextBlock",
              "text": "System Monitor",
              "weight": "Bolder",
              "size": "Small",
              "spacing": "None"
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
                      "text": "CPU",
                      "size": "Small",
                      "isSubtle": true,
                      "spacing": "None"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${cpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "color": "Accent",
                      "spacing": "None"
                    },
                    {
                      "type": "TextBlock",
                      "text": "RAM",
                      "size": "Small",
                      "isSubtle": true,
                      "spacing": "Small"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${ram}",
                      "size": "Small",
                      "weight": "Bolder",
                      "wrap": true,
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
                      "text": "GPU (i|d)",
                      "size": "Small",
                      "isSubtle": true,
                      "spacing": "None"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${igpu} | ${dgpu}",
                      "size": "Medium",
                      "weight": "Bolder",
                      "spacing": "None"
                    },
                    {
                      "type": "TextBlock",
                      "text": "DISK",
                      "size": "Small",
                      "isSubtle": true,
                      "spacing": "Small"
                    },
                    {
                      "type": "TextBlock",
                      "text": "${disk}",
                      "size": "Small",
                      "weight": "Bolder",
                      "wrap": true,
                      "spacing": "None"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        public const string MediumTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "System Monitor",
                      "weight": "Bolder",
                      "size": "Medium"
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "auto",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${lastUpdated}",
                      "size": "Small",
                      "isSubtle": true,
                      "horizontalAlignment": "Right"
                    }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "Medium",
              "separator": true,
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "FactSet",
                      "facts": [
                        { "title": "CPU:", "value": "${cpu}" },
                        { "title": "iGPU:", "value": "${igpu}" },
                        { "title": "dGPU:", "value": "${dgpu}" },
                        { "title": "RAM:", "value": "${ram}" }
                      ]
                    }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "FactSet",
                      "facts": [
                        { "title": "Disk:", "value": "${disk}" },
                        { "title": "Net ↓:", "value": "${netDown}" },
                        { "title": "Net ↑:", "value": "${netUp}" }
                      ]
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
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "System Monitor",
                      "weight": "Bolder",
                      "size": "Large"
                    },
                    {
                      "type": "TextBlock",
                      "text": "Real-time system performance metrics",
                      "size": "Small",
                      "isSubtle": true,
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
                      "text": "${lastUpdated}",
                      "size": "Small",
                      "isSubtle": true,
                      "horizontalAlignment": "Right"
                    }
                  ]
                }
              ]
            },
            {
              "type": "Container",
              "spacing": "Medium",
              "separator": true,
              "items": [
                {
                  "type": "ColumnSet",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Compute & Memory",
                          "weight": "Bolder",
                          "size": "Medium"
                        },
                        {
                          "type": "FactSet",
                          "spacing": "Small",
                          "facts": [
                            { "title": "Processor (CPU):", "value": "${cpu}" },
                            { "title": "Intel GPU:", "value": "${igpu}" },
                            { "title": "NVIDIA GPU:", "value": "${dgpu}" },
                            { "title": "Memory (RAM):", "value": "${ram}" }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "Storage & Network",
                          "weight": "Bolder",
                          "size": "Medium"
                        },
                        {
                          "type": "FactSet",
                          "spacing": "Small",
                          "facts": [
                            { "title": "Storage (Disk):", "value": "${disk}" },
                            { "title": "Network Download:", "value": "${netDown}" },
                            { "title": "Network Upload:", "value": "${netUp}" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "title": "🔄 Refresh Metrics",
              "verb": "refresh"
            }
          ]
        }
        """;
    }
}

