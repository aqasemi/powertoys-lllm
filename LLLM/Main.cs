#nullable enable
// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Wox.Plugin;
using Wox.Plugin.Logger;
using Clipboard = System.Windows.Clipboard;

namespace Community.PowerToys.Run.Plugin.LLLM
{
    public class Main : IPlugin, IDelayedExecutionPlugin, ISettingProvider, IContextMenu
    {
        public static string PluginID => "1CDE310EA37046F797117442344A72F2";

        public string Name => "LLLM";

        public string Description => "Uses LLLM to output answer";

        private static readonly HttpClient Client = new();
        private static DateTime _lastCopyTime = DateTime.MinValue; // Added for rate limiting
        private static readonly TimeSpan _copyCooldown = TimeSpan.FromSeconds(1); // Added for rate limiting

        private string? IconPath { get; set; }

        private PluginInitContext? Context { get; set; }

        private string endpoint = string.Empty;
        private string model = string.Empty;
        private string apiKey = string.Empty;
        private string sendTriggerKeyword = string.Empty;
        private readonly string defaultSystemPrompt = "You're a helpful assistant that provides concise and accurate answers to user queries. Your answer should be short and to the point. Respond in plain text. Do not include any code blocks or markdown formatting. Use google search if needed.";
        private string systemPrompt = string.Empty;

        private bool googleSearchEnabled = true;

        public IEnumerable<PluginAdditionalOption> AdditionalOptions =>
        [

            new()
            {
                Key = "LLMEndpoint",
                DisplayLabel = "LLM Endpoint Base URL",
                DisplayDescription = "Enter the base endpoint for your LLM model (e.g., https://generativelanguage.googleapis.com/v1beta/models/). The model name will be appended. Default is for Gemini API.",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
                TextValue = "https://generativelanguage.googleapis.com/v1beta/models/",
            },
            new()
            {
                Key = "LLMModel",
                DisplayLabel = "LLM Model",
                DisplayDescription = "Enter the model name (for Gemini API this is included in the endpoint URL)",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
                TextValue = "gemini-2.0-flash-lite",
            },
            new()
            {
                Key = "APIKey",
                DisplayLabel = "API Key",
                DisplayDescription = "Enter your Gemini API key",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
                TextValue = "",
            },
            new()
            {
                Key = "SendTriggerKeyword",
                DisplayLabel = "Send Trigger Keyword",
                DisplayDescription = "Enter keyword which will trigger the query to be sent to Ollama.",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
                TextValue = "[", // Default value
            },
            new() // Added SystemPrompt setting
            {
                Key = "SystemPrompt",
                DisplayLabel = "System Prompt",
                DisplayDescription = "Enter the system prompt to guide the LLM's responses. (Optional)",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
                TextValue = defaultSystemPrompt,
            },
            new()
            {
                Key = "GoogleSearch",
                DisplayLabel = "Google Search",
                DisplayDescription = "Enable Google Search",
                PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
                Value = true,
            }
        ];
        public void UpdateSettings(PowerLauncherPluginSettings settings)
        {
            Log.Info($"[{Name}] Updating settings...", GetType());
            // Default values
            string defaultEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/";
            string defaultModel = "gemini-2.5-flash-preview-04-17";
            string defaultApiKey = "";
            string defaultSendTrigger = "~";

            if (settings != null && settings.AdditionalOptions != null)
            {
                endpoint = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "LLMEndpoint")?.TextValue ?? defaultEndpoint;
                Log.Info($"[{Name}] Endpoint set to: {endpoint}", GetType());
                model = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "LLMModel")?.TextValue ?? defaultModel;
                Log.Info($"[{Name}] Model set to: {model}", GetType());
                apiKey = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "APIKey")?.TextValue ?? defaultApiKey;
                Log.Info($"[{Name}] APIKey is {(string.IsNullOrEmpty(apiKey) ? "not set" : "set")}", GetType());
                sendTriggerKeyword = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "SendTriggerKeyword")?.TextValue ?? defaultSendTrigger;
                Log.Info($"[{Name}] SendTriggerKeyword set to: {sendTriggerKeyword}", GetType());
                systemPrompt = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "SystemPrompt")?.TextValue ?? defaultSystemPrompt; // Read SystemPrompt
                Log.Info($"[{Name}] SystemPrompt set to: '{systemPrompt}'", GetType());
                googleSearchEnabled = settings.AdditionalOptions.FirstOrDefault(x => x.Key == "GoogleSearch")?.Value ?? false;
                Log.Info($"[{Name}] GoogleSearch is {(googleSearchEnabled ? "enabled" : "disabled")}", GetType());
            }
            else
            {
                Log.Info($"[{Name}] No settings provided or AdditionalOptions is null, using default values.", GetType());
                endpoint = defaultEndpoint;
                model = defaultModel;
                apiKey = defaultApiKey;
                Log.Info($"[{Name}] APIKey set to default: {(string.IsNullOrEmpty(apiKey) ? "not set" : "set")}", GetType());
                sendTriggerKeyword = defaultSendTrigger;
                systemPrompt = defaultSystemPrompt; // Set default SystemPrompt
            }
            Log.Info($"[{Name}] Settings update complete.", GetType());
        }

        public void Init(PluginInitContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(Context.API.GetCurrentTheme());
            Log.Info($"[{Name}] Plugin initialized.", GetType());
        }

        private void OnThemeChanged(Theme currentTheme, Theme newTheme) => UpdateIconPath(newTheme);

        private void UpdateIconPath(Theme theme) => IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite ? Context?.CurrentPluginMetadata.IcoPathLight : Context?.CurrentPluginMetadata.IcoPathDark;

        public List<Result> Query(Query query, bool delayedExecution)
        {
            Log.Info($"[{Name}] Query received: '{query?.Search}'", GetType());
            var input = query?.Search ?? string.Empty;

            var response = "End input with: '" + sendTriggerKeyword + "'";
            if (input.EndsWith(sendTriggerKeyword, StringComparison.Ordinal))
            {
                input = input[..^sendTriggerKeyword.Length];
                Log.Info($"[{Name}] Input for LLM: '{input}'", GetType());
                response = QueryLLMStreamAsync(input).Result;
                Log.Info($"[{Name}] Response from LLM: '{response}'", GetType());
            }

            return
            [
                new()
                {
                    Title = model,
                    SubTitle = response,
                    IcoPath = IconPath,
                    Action = _ => CopyToClipboard(response.ToString()),
                    ContextData = new Dictionary<string, string> { { "copy", response } }, // Store the response text in context data for context menu which allows copying
                },
            ];
        }

        public async Task<string> QueryLLMStreamAsync(string input)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Log.Warn($"[{Name}] API Key is not set. LLM query will likely fail.", GetType());
                return "API Key is not configured. Please set it in the plugin settings.";
            }
            try
            {
                // Construct the proper endpoint URL with API key and model
                var endpointUrl = $"{endpoint}{model}:generateContent?key={apiKey}";
                Log.Info($"[{Name}] Querying LLM", GetType());

                // Base request body
                var requestBodyData = new Dictionary<string, object>
                {
                    ["contents"] = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new
                                {
                                    text = input
                                }
                            }
                        }
                    }
                };

                // Add systemInstruction if systemPrompt is provided
                if (!string.IsNullOrEmpty(systemPrompt))
                {
                    requestBodyData["systemInstruction"] = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = systemPrompt
                            }
                        }
                    };
                    Log.Info($"[{Name}] Using system prompt: '{systemPrompt}'", GetType());
                }

                if (googleSearchEnabled)
                {
                    Log.Info($"[{Name}] Google Search is enabled.", GetType());
                    requestBodyData["tools"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["google_search"] = new Dictionary<string, object>()
                        }
                    };
                }

                var response = await Client.PostAsync(endpointUrl, new StringContent(
                    JsonSerializer.Serialize(requestBodyData), // Use the dictionary
                    System.Text.Encoding.UTF8,
                    "application/json"));

                response.EnsureSuccessStatusCode();

                // Parse the JSON response from Gemini API
                var jsonResponse = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<JsonElement>(jsonResponse);

                string finalResponse = string.Empty;

                // Navigate the JSON structure to find the response
                if (responseObj.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0)
                    {
                        var firstPart = parts[0];
                        if (firstPart.TryGetProperty("text", out var text))
                        {
                            finalResponse = text.GetString() ?? string.Empty;
                        }
                    }
                }
                Log.Info($"[{Name}] Parsed final response from LLM: {finalResponse}", GetType());
                return finalResponse;
            }
            catch (Exception ex)
            {
                Log.Error($"[{Name}] Error querying LLM: {ex.Message}", GetType());
                Log.Exception($"[{Name}] LLM query exception", ex, GetType());
                return $"Error querying LLM: {ex.Message}";
            }
        }

        public List<Result> Query(Query query)
        {
            List<Result> results = [
                new()
                {
                    Title = model,
                    SubTitle = "End input with: '" + sendTriggerKeyword + "'",
                    IcoPath = IconPath,
                    Action = _ => false,
                }
            ];
            return results;
        }

        public System.Windows.Controls.Control CreateSettingPanel()
        {
            throw new NotImplementedException();
        }

        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            // this method is called when the user right-clicks on a result
            List<ContextMenuResult> results = [];

            if (selectedResult?.ContextData is Dictionary<string, string> contextData)
            {
                if (contextData.TryGetValue("copy", out string? value))
                {
                    results.Add(
                        new ContextMenuResult
                        {
                            PluginName = Name,
                            Title = "Copy (Enter)",
                            FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                            Glyph = "\xE8C8",
                            AcceleratorKey = Key.Enter,
                            Action = _ => CopyToClipboard(value?.ToString())
                        });
                }
            }

            return results;
        }

        private static bool CopyToClipboard(string? value)
        {
            // rate limiting to prevent spamming the clipboard which can cause issues
            // when the user hits enter, this will be called twice (once for the action and once for the context menu)
            // so we need to check if the last copy was within the cooldown period
            if (DateTime.Now - _lastCopyTime < _copyCooldown)
            {
                Log.Info("CopyToClipboard called too frequently. Skipping.", typeof(Main));
                return false; // cooldown active
            }

            if (value != null)
            {
                Log.Info($"Copying to clipboard: '{value}'", typeof(Main));
                Clipboard.SetDataObject(value);
                _lastCopyTime = DateTime.Now;
            }

            return true;
        }
    }
}
