using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    public class ToolDiscoveryService : IToolDiscoveryService
    {
        private Dictionary<string, ToolMetadata> _cachedTools;


        public List<ToolMetadata> DiscoverAllTools()
        {
            if (_cachedTools != null)
            {
                return _cachedTools.Values.ToList();
            }

            _cachedTools = new Dictionary<string, ToolMetadata>();

            var toolTypes = TypeCache.GetTypesWithAttribute<McpForUnityToolAttribute>();
            foreach (var type in toolTypes)
            {
                TryRegisterToolType(type, allowOverwrite: true);
            }

            // TypeCache can lag behind new scripts until the script type hash refreshes.
            // Scan our Editor assembly so tools like manage_vrchat_udon are never omitted.
            DiscoverToolsFromExecutingAssemblySupplement();

            McpLog.Info($"Discovered {_cachedTools.Count} MCP tools via reflection", false);
            return _cachedTools.Values.ToList();
        }

        public ToolMetadata GetToolMetadata(string toolName)
        {
            if (_cachedTools == null)
            {
                DiscoverAllTools();
            }

            return _cachedTools.TryGetValue(toolName, out var metadata) ? metadata : null;
        }

        public List<ToolMetadata> GetEnabledTools()
        {
            return DiscoverAllTools()
                .Where(tool => IsToolEnabled(tool.Name))
                .ToList();
        }

        public bool IsToolEnabled(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return false;
            }

            string key = GetToolPreferenceKey(toolName);
            if (EditorPrefs.HasKey(key))
            {
                return EditorPrefs.GetBool(key, true);
            }

            var metadata = GetToolMetadata(toolName);
            return metadata?.AutoRegister ?? false;
        }

        public void SetToolEnabled(string toolName, bool enabled)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return;
            }

            string key = GetToolPreferenceKey(toolName);
            EditorPrefs.SetBool(key, enabled);
        }

        private ToolMetadata ExtractToolMetadata(Type type, McpForUnityToolAttribute toolAttr)
        {
            try
            {
                // Get tool name
                string toolName = toolAttr.Name;
                if (string.IsNullOrEmpty(toolName))
                {
                    // Derive from class name: CaptureScreenshotTool -> capture_screenshot
                    toolName = ConvertToSnakeCase(type.Name.Replace("Tool", ""));
                }

                // Get description
                string description = toolAttr.Description ?? $"Tool: {toolName}";

                // Extract parameters
                var parameters = ExtractParameters(type);

                var metadata = new ToolMetadata
                {
                    Name = toolName,
                    Description = description,
                    StructuredOutput = toolAttr.StructuredOutput,
                    Parameters = parameters,
                    ClassName = type.Name,
                    Namespace = type.Namespace ?? "",
                    AssemblyName = type.Assembly.GetName().Name,
                    AutoRegister = toolAttr.AutoRegister,
                    RequiresPolling = toolAttr.RequiresPolling,
                    PollAction = string.IsNullOrEmpty(toolAttr.PollAction) ? "status" : toolAttr.PollAction,
                    MaxPollSeconds = toolAttr.MaxPollSeconds,
                    Group = toolAttr.Group ?? "core"
                };

                metadata.IsBuiltIn = StringCaseUtility.IsBuiltInMcpType(
                    type, metadata.AssemblyName, "MCPForUnity.Editor.Tools");

                return metadata;

            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to extract metadata for {type.Name}: {ex.Message}");
                return null;
            }
        }

        private List<ParameterMetadata> ExtractParameters(Type type)
        {
            var parameters = new List<ParameterMetadata>();

            // Look for nested Parameters class
            var parametersType = type.GetNestedType("Parameters");
            if (parametersType == null)
            {
                return parameters;
            }

            // Get all properties with [ToolParameter]
            var properties = parametersType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in properties)
            {
                var paramAttr = prop.GetCustomAttribute<ToolParameterAttribute>();
                if (paramAttr == null)
                    continue;

                string paramName = prop.Name;
                string paramType = GetParameterType(prop.PropertyType);

                parameters.Add(new ParameterMetadata
                {
                    Name = paramName,
                    Description = paramAttr.Description,
                    Type = paramType,
                    Required = paramAttr.Required,
                    DefaultValue = paramAttr.DefaultValue
                });
            }

            return parameters;
        }

        private string GetParameterType(Type type)
        {
            // Handle nullable types
            if (Nullable.GetUnderlyingType(type) != null)
            {
                type = Nullable.GetUnderlyingType(type);
            }

            // Map C# types to JSON schema types
            if (type == typeof(string))
                return "string";
            if (type == typeof(int) || type == typeof(long))
                return "integer";
            if (type == typeof(float) || type == typeof(double))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return "array";

            return "object";
        }

        private string ConvertToSnakeCase(string input) => StringCaseUtility.ToSnakeCase(input);

        private void TryRegisterToolType(Type type, bool allowOverwrite)
        {
            McpForUnityToolAttribute toolAttr;
            try
            {
                toolAttr = type.GetCustomAttribute<McpForUnityToolAttribute>();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to read [McpForUnityTool] for {type.FullName}: {ex.Message}");
                return;
            }

            if (toolAttr == null)
            {
                return;
            }

            var metadata = ExtractToolMetadata(type, toolAttr);
            if (metadata == null)
            {
                return;
            }

            if (_cachedTools.ContainsKey(metadata.Name))
            {
                if (!allowOverwrite)
                {
                    return;
                }

                McpLog.Warn($"Duplicate tool name '{metadata.Name}' from {type.FullName}; overwriting previous registration.");
            }

            _cachedTools[metadata.Name] = metadata;
            EnsurePreferenceInitialized(metadata);
        }

        /// <summary>
        /// TypeCache can omit newly added tools until the editor script type hash updates.
        /// Merge any [McpForUnityTool] types from this assembly that are not already registered.
        /// </summary>
        private void DiscoverToolsFromExecutingAssemblySupplement()
        {
            Assembly asm;
            try
            {
                asm = Assembly.GetExecutingAssembly();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"MCP tool discovery: could not get executing assembly: {ex.Message}");
                return;
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"MCP tool discovery: GetTypes failed for {asm.GetName().Name}: {ex.Message}");
                return;
            }

            foreach (var type in types)
            {
                if (!type.IsClass)
                {
                    continue;
                }

                TryRegisterToolType(type, allowOverwrite: false);
            }

            bool vrchatHandlerPresent = types.Any(t =>
                string.Equals(t.Name, "ManageVRChatUdon", StringComparison.Ordinal));
            if (vrchatHandlerPresent && !_cachedTools.ContainsKey("manage_vrchat_udon"))
            {
                McpLog.Warn(
                    "MCP: ManageVRChatUdon exists in assembly but was not registered (TypeCache/metadata issue). Try Assets > Reimport All or restart the Editor.",
                    false);
            }
        }

        public void InvalidateCache()
        {
            _cachedTools = null;
        }

        private void EnsurePreferenceInitialized(ToolMetadata metadata)
        {
            if (metadata == null || string.IsNullOrEmpty(metadata.Name))
            {
                return;
            }

            string key = GetToolPreferenceKey(metadata.Name);
            if (!EditorPrefs.HasKey(key))
            {
                bool defaultValue = metadata.AutoRegister || metadata.IsBuiltIn;
                EditorPrefs.SetBool(key, defaultValue);
                return;
            }

            // Older package wrote false when AutoRegister was false; prefs persist after AutoRegister=true.
            if (string.Equals(metadata.Name, "manage_vrchat_udon", StringComparison.Ordinal)
                && metadata.AutoRegister
                && !EditorPrefs.GetBool(key, true)
                && !EditorPrefs.HasKey(EditorPrefKeys.MigrationManageVrchatUdonAutoRegisterV1))
            {
                EditorPrefs.SetBool(key, true);
                EditorPrefs.SetBool(EditorPrefKeys.MigrationManageVrchatUdonAutoRegisterV1, true);
                McpLog.Info(
                    "MCP: enabled manage_vrchat_udon (default is now on). Disable it in MCP > Tools if you do not need it.",
                    false);
                ScheduleReregisterToolsAfterPreferenceMigration();
            }
        }

        private static void ScheduleReregisterToolsAfterPreferenceMigration()
        {
            EditorApplication.delayCall += () =>
            {
                _ = ReregisterToolsAfterPreferenceMigrationAsync();
            };
        }

        private static async Task ReregisterToolsAfterPreferenceMigrationAsync()
        {
            try
            {
                var tm = MCPServiceLocator.TransportManager;
                foreach (TransportMode mode in new[] { TransportMode.Http, TransportMode.Stdio })
                {
                    if (!tm.IsRunning(mode))
                    {
                        continue;
                    }

                    var client = tm.GetClient(mode);
                    if (client != null)
                    {
                        await client.ReregisterToolsAsync().ConfigureAwait(true);
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"MCP: could not re-register tools after preference migration: {ex.Message}");
            }
        }

        private static string GetToolPreferenceKey(string toolName)
        {
            return EditorPrefKeys.ToolEnabledPrefix + toolName;
        }

    }
}
