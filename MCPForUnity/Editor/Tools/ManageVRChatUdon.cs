#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool(
        "manage_vrchat_udon",
        AutoRegister = true,
        Group = "core",
        Description = "VRChat UdonSharp helper: check environment, create Udon# script with ProgramAsset, attach UdonBehaviour with Program Source, generate sync/network templates, and validate networking anti-patterns."
    )]
    public static class ManageVRChatUdon
    {
        private const string RecommendedUnityVersion = "2022.3.22f1";
        private static readonly string[] ExpectedPackages =
        {
            "com.vrchat.worlds",
            "com.vrchat.avatars",
            "com.vrchat.base",
            "com.vrchat.udonsharp",
        };

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "check_environment":
                        return CheckEnvironment();
                    case "create_udonsharp_script":
                        return CreateUdonSharpScript(p);
                    case "attach_udon_behaviour":
                        return AttachUdonBehaviour(p);
                    case "configure_sync":
                        return ConfigureSync(p);
                    case "generate_network_pattern":
                        return GenerateNetworkPattern(p);
                    case "validate_networking":
                        return ValidateNetworking(p);
                    default:
                        return new ErrorResponse(
                            $"Unknown action: '{action}'. Supported actions: check_environment, create_udonsharp_script, attach_udon_behaviour, configure_sync, generate_network_pattern, validate_networking.");
                }
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
            }
        }

        private static object CheckEnvironment()
        {
            string unityVersion = Application.unityVersion;
            bool unityVersionMatches = string.Equals(unityVersion, RecommendedUnityVersion, StringComparison.OrdinalIgnoreCase);

            var installed = PackageInfo.GetAllRegisteredPackages()
                .Select(pkg => pkg.name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var packageState = ExpectedPackages.ToDictionary(
                key => key,
                key => installed.Contains(key)
            );

            bool hasAnyVrchatPackage = packageState.Any(kv => kv.Value);
            bool hasUdonSharpPackage = packageState.TryGetValue("com.vrchat.udonsharp", out bool hasUdon) && hasUdon;

            bool hasUdonSharpBehaviourType = FindType("UdonSharp.UdonSharpBehaviour") != null;
            bool hasVrcPlayerApiType = FindType("VRC.SDKBase.VRCPlayerApi") != null;

            var issues = new List<string>();
            if (!unityVersionMatches)
                issues.Add($"Recommended Unity version for VRChat is {RecommendedUnityVersion}, current is {unityVersion}.");
            if (!hasAnyVrchatPackage)
                issues.Add("No VRChat SDK package detected (expected com.vrchat.* packages).");
            if (!hasUdonSharpPackage)
                issues.Add("UdonSharp package not detected (com.vrchat.udonsharp).");
            if (!hasUdonSharpBehaviourType)
                issues.Add("UdonSharpBehaviour type was not found in loaded assemblies.");
            if (!hasVrcPlayerApiType)
                issues.Add("VRCPlayerApi type was not found in loaded assemblies.");

            return new SuccessResponse(
                issues.Count == 0 ? "VRChat/Udon environment looks ready." : "VRChat/Udon environment check completed with warnings.",
                new
                {
                    unity_version = unityVersion,
                    recommended_unity_version = RecommendedUnityVersion,
                    unity_version_matches = unityVersionMatches,
                    package_state = packageState,
                    has_any_vrchat_package = hasAnyVrchatPackage,
                    has_udonsharp_package = hasUdonSharpPackage,
                    has_udonsharp_behaviour_type = hasUdonSharpBehaviourType,
                    has_vrc_player_api_type = hasVrcPlayerApiType,
                    issues,
                }
            );
        }

        private static object CreateUdonSharpScript(ToolParams p)
        {
            string path = p.Get("path");
            string className = p.Get("class_name");
            string namespaceName = p.Get("namespace");
            string syncMode = (p.Get("sync_mode") ?? "manual").ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(path))
            {
                if (string.IsNullOrWhiteSpace(className))
                    return new ErrorResponse("'path' or 'class_name' is required for create_udonsharp_script.");
                path = $"Assets/Scripts/Udon/{className}.cs";
            }

            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("'path' must point to a .cs file under Assets/.");

            path = path.Replace("\\", "/");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("'path' must be under Assets/.");

            if (string.IsNullOrWhiteSpace(className))
                className = Path.GetFileNameWithoutExtension(path);

            if (!Regex.IsMatch(className, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                return new ErrorResponse($"Invalid class_name '{className}'. Expected a valid C# identifier.");

            string fileContents = BuildScriptTemplate(className, namespaceName, syncMode);
            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), path);
            string directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(absolutePath, fileContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();

            string programAssetPath;
            string programAssetNote;
            bool ensuredProgramAsset = EnsureUdonSharpProgramAsset(path, className, out programAssetPath, out programAssetNote);

            return new SuccessResponse(
                $"Created UdonSharp script '{className}' at '{path}'.",
                new
                {
                    path,
                    class_name = className,
                    namespace_name = namespaceName,
                    sync_mode = syncMode,
                    ensured_program_asset = ensuredProgramAsset,
                    udon_sharp_program_asset_path = programAssetPath,
                    udon_sharp_program_asset_note = programAssetNote,
                }
            );
        }

        private static object AttachUdonBehaviour(ToolParams p)
        {
            string target = p.Get("target");
            if (string.IsNullOrWhiteSpace(target))
                return new ErrorResponse("'target' is required for attach_udon_behaviour.");

            string className = p.Get("class_name");
            if (string.IsNullOrWhiteSpace(className))
            {
                string path = p.Get("path");
                if (!string.IsNullOrWhiteSpace(path))
                    className = Path.GetFileNameWithoutExtension(path);
            }

            if (string.IsNullOrWhiteSpace(className))
                return new ErrorResponse("'class_name' (or 'path') is required for attach_udon_behaviour.");

            string searchMethod = p.Get("search_method");
            GameObject targetGo = ResolveTargetGameObject(target, searchMethod);
            if (targetGo == null)
                return new ErrorResponse($"Target GameObject ('{target}') not found using method '{searchMethod ?? "default"}'.");

            ScriptableObject programAsset = ResolveProgramAssetForAttach(p, className);
            if (programAsset == null)
            {
                return new ErrorResponse(
                    $"Could not resolve UdonSharpProgramAsset for class '{className}'. " +
                    "Create the script first with create_udonsharp_script or provide a valid 'path'.");
            }

            Type udonBehaviourType = FindType("VRC.Udon.UdonBehaviour");
            if (udonBehaviourType == null || !typeof(Component).IsAssignableFrom(udonBehaviourType))
                return new ErrorResponse("VRC.Udon.UdonBehaviour type not found. Ensure VRChat SDK is installed.");

            Component udonBehaviour = targetGo.GetComponent(udonBehaviourType);
            if (udonBehaviour == null)
            {
                udonBehaviour = Undo.AddComponent(targetGo, udonBehaviourType);
            }
            if (udonBehaviour == null)
                return new ErrorResponse("Failed to add UdonBehaviour component.");

            if (!SetUdonProgramSource(udonBehaviour, programAsset, out string assignError))
                return new ErrorResponse($"Failed to assign Program Source: {assignError}");

            EditorUtility.SetDirty(udonBehaviour);
            EditorUtility.SetDirty(targetGo);
            EditorSceneManager.MarkSceneDirty(targetGo.scene);

            return new SuccessResponse(
                $"UdonBehaviour attached to '{targetGo.name}' with Program Source '{programAsset.name}'.",
                new
                {
                    target_name = targetGo.name,
                    target_path = GameObjectLookup.GetGameObjectPath(targetGo),
                    target_instance_id = targetGo.GetInstanceID(),
                    udon_sharp_program_asset = AssetDatabase.GetAssetPath(programAsset),
                    class_name = className,
                });
        }

        private static object ConfigureSync(ToolParams p)
        {
            string syncMode = (p.Get("sync_mode") ?? "manual").ToLowerInvariant();
            string[] variables = p.GetStringArray("variables") ?? Array.Empty<string>();
            bool includeExample = p.GetBool("include_example", true);

            if (syncMode != "manual" && syncMode != "continuous")
                return new ErrorResponse("sync_mode must be 'manual' or 'continuous'.");

            string modeAttribute = syncMode == "manual"
                ? "[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]"
                : "[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]";

            var variableLines = new List<string>();
            foreach (string variable in variables.Where(v => !string.IsNullOrWhiteSpace(v)))
                variableLines.Add($"[UdonSynced] private int {variable};");

            string snippet = string.Join("\n", variableLines);
            if (string.IsNullOrWhiteSpace(snippet))
                snippet = "[UdonSynced] private int syncedValue;";

            string example = includeExample ? BuildSyncExample(syncMode) : null;

            return new SuccessResponse(
                $"Generated {syncMode} sync configuration guidance.",
                new
                {
                    sync_mode = syncMode,
                    class_attribute = modeAttribute,
                    synced_variable_snippet = snippet,
                    example,
                    recommendations = new[]
                    {
                        "Only owner should mutate [UdonSynced] values.",
                        "Prefer ownership checks over isMaster checks.",
                        syncMode == "manual"
                            ? "Call RequestSerialization() only after meaningful state changes."
                            : "Keep continuous sync values small and low-frequency."
                    }
                }
            );
        }

        private static object GenerateNetworkPattern(ToolParams p)
        {
            string pattern = (p.Get("network_pattern") ?? "ownership_gate").ToLowerInvariant();
            bool includeExample = p.GetBool("include_example", true);

            string title;
            string snippet;

            switch (pattern)
            {
                case "ownership_gate":
                    title = "Ownership Gate";
                    snippet = "if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);";
                    break;
                case "manual_sync_toggle":
                    title = "Manual Sync Toggle";
                    snippet = "isOpen = !isOpen; RequestSerialization();";
                    break;
                case "event_after_sync":
                    title = "Event After Sync";
                    snippet = "RequestSerialization(); SendCustomEventDelayedSeconds(nameof(NotifyStateChanged), 0.2f);";
                    break;
                case "late_joiner_state":
                    title = "Late Joiner State";
                    snippet = "public override void OnDeserialization() { ApplyState(); }";
                    break;
                default:
                    return new ErrorResponse(
                        $"Unknown network_pattern '{pattern}'. Valid: ownership_gate, manual_sync_toggle, event_after_sync, late_joiner_state.");
            }

            string example = includeExample ? BuildPatternExample(pattern) : null;
            return new SuccessResponse(
                $"Generated networking pattern '{pattern}'.",
                new
                {
                    pattern,
                    title,
                    snippet,
                    example,
                }
            );
        }

        private static object ValidateNetworking(ToolParams p)
        {
            string path = p.Get("path");
            if (string.IsNullOrWhiteSpace(path))
                return new ErrorResponse("'path' is required for validate_networking.");

            path = path.Replace("\\", "/");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse("'path' must be under Assets/.");

            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), path);
            if (!File.Exists(absolutePath))
                return new ErrorResponse($"Script not found: '{path}'.");

            string code = File.ReadAllText(absolutePath);
            var issues = new List<object>();

            if (Regex.IsMatch(code, @"\bisMaster\b", RegexOptions.IgnoreCase))
            {
                issues.Add(new
                {
                    severity = "warning",
                    rule = "avoid_master_gating",
                    message = "Avoid gating authority by instance master; prefer ownership checks.",
                });
            }

            bool hasUdonSynced = Regex.IsMatch(code, @"\[UdonSynced\]", RegexOptions.Multiline);
            bool usesRequestSerialization = Regex.IsMatch(code, @"\bRequestSerialization\s*\(", RegexOptions.Multiline);
            if (hasUdonSynced && !usesRequestSerialization)
            {
                issues.Add(new
                {
                    severity = "warning",
                    rule = "synced_without_serialization",
                    message = "Detected [UdonSynced] fields without RequestSerialization() usage.",
                });
            }

            if (Regex.IsMatch(code, @"\bUpdate\s*\(", RegexOptions.Multiline) &&
                Regex.IsMatch(code, @"\bRequestSerialization\s*\(", RegexOptions.Multiline))
            {
                issues.Add(new
                {
                    severity = "warning",
                    rule = "serialization_in_update",
                    message = "RequestSerialization() appears together with Update(). Consider throttling.",
                });
            }

            if (Regex.IsMatch(code, @"Networking\.SetOwner\s*\(", RegexOptions.Multiline) &&
                Regex.IsMatch(code, @"SendCustomNetworkEvent\s*\(", RegexOptions.Multiline))
            {
                issues.Add(new
                {
                    severity = "info",
                    rule = "owner_then_event_timing",
                    message = "Ownership transfer and network events are both used; ensure ordering handles network delay.",
                });
            }

            return new SuccessResponse(
                issues.Count == 0 ? "No obvious networking issues detected." : $"Found {issues.Count} potential networking issue(s).",
                new
                {
                    path,
                    issue_count = issues.Count,
                    issues,
                }
            );
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type found = assembly.GetType(fullName, throwOnError: false);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static string BuildScriptTemplate(string className, string namespaceName, string syncMode)
        {
            string modeAttribute = syncMode == "continuous"
                ? "[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]"
                : "[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]";

            string classBody =
$@"using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

{modeAttribute}
public class {className} : UdonSharpBehaviour
{{
    [UdonSynced] private bool syncedState;

    public override void Interact()
    {{
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);

        syncedState = !syncedState;
        {(syncMode == "manual" ? "RequestSerialization();" : string.Empty)}
        ApplyState();
    }}

    public override void OnDeserialization()
    {{
        ApplyState();
    }}

    private void ApplyState()
    {{
        // Apply syncedState to your world objects here.
    }}
}}";

            if (string.IsNullOrWhiteSpace(namespaceName))
                return classBody + "\n";

            return
$@"namespace {namespaceName}
{{
{Indent(classBody, 1)}
}}
";
        }

        private static string BuildSyncExample(string syncMode)
        {
            if (syncMode == "continuous")
            {
                return
@"[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
[UdonSynced] private float progress;";
            }

            return
@"[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
[UdonSynced] private int score;
public void AddScore(int delta)
{
    if (!Networking.IsOwner(gameObject)) return;
    score += delta;
    RequestSerialization();
}";
        }

        private static string BuildPatternExample(string pattern)
        {
            switch (pattern)
            {
                case "manual_sync_toggle":
                    return
@"public void Toggle()
{
    if (!Networking.IsOwner(gameObject))
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
    isOpen = !isOpen;
    RequestSerialization();
}";
                case "event_after_sync":
                    return
@"RequestSerialization();
SendCustomEventDelayedSeconds(nameof(AfterStateSync), 0.2f);";
                case "late_joiner_state":
                    return
@"public override void OnDeserialization()
{
    ApplyState();
}";
                default:
                    return
@"if (!Networking.IsOwner(gameObject))
    Networking.SetOwner(Networking.LocalPlayer, gameObject);";
            }
        }

        private static string Indent(string content, int level)
        {
            string indent = new string(' ', level * 4);
            return string.Join("\n", content.Split('\n').Select(line => line.Length == 0 ? string.Empty : indent + line));
        }

        private static GameObject ResolveTargetGameObject(string target, string searchMethod)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;

            // Prefer explicit search_method behavior from GameObjectLookup.
            if (!string.IsNullOrWhiteSpace(searchMethod))
                return GameObjectLookup.FindByTarget(JToken.FromObject(target), searchMethod, includeInactive: true);

            // Default strategy: instance ID -> exact path -> exact name.
            if (int.TryParse(target, out int targetId))
            {
                var byId = GameObjectLookup.FindById(targetId);
                if (byId != null)
                    return byId;
            }

            var byPath = GameObjectLookup.FindByTarget(JToken.FromObject(target), "by_path", includeInactive: true);
            if (byPath != null)
                return byPath;

            return GameObjectLookup.FindByTarget(JToken.FromObject(target), "by_name", includeInactive: true);
        }

        private static ScriptableObject ResolveProgramAssetForAttach(ToolParams p, string className)
        {
            string path = p.Get("path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Replace("\\", "/");
                string scriptDir = Path.GetDirectoryName(path)?.Replace("\\", "/");
                MonoScript sourceScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (sourceScript != null && !string.IsNullOrWhiteSpace(scriptDir))
                {
                    if (TryFindMatchingProgramAsset(scriptDir, sourceScript, out string foundPath))
                        return AssetDatabase.LoadAssetAtPath<ScriptableObject>(foundPath);
                }
            }

            // Fallback: look up by source script name globally.
            string[] scriptGuids = AssetDatabase.FindAssets($"t:MonoScript {className}");
            foreach (string guid in scriptGuids)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(scriptPath) || !scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                MonoScript sourceScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                string scriptDir = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");
                if (sourceScript == null || string.IsNullOrWhiteSpace(scriptDir))
                    continue;
                if (TryFindMatchingProgramAsset(scriptDir, sourceScript, out string foundPath))
                    return AssetDatabase.LoadAssetAtPath<ScriptableObject>(foundPath);
            }

            return null;
        }

        private static bool SetUdonProgramSource(Component udonBehaviour, ScriptableObject programAsset, out string error)
        {
            error = null;
            if (udonBehaviour == null)
            {
                error = "UdonBehaviour is null.";
                return false;
            }
            if (programAsset == null)
            {
                error = "Program asset is null.";
                return false;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 1) SerializedProperty path first (most robust with Unity serialization)
            string[] propertyCandidates =
            {
                "programSource",
                "serializedProgramAsset",
                "m_ProgramSource",
                "m_SerializedProgramAsset",
                "_programSource",
                "_serializedProgramAsset",
                "m_ProgramAsset",
            };

            var so = new SerializedObject(udonBehaviour);
            foreach (string candidate in propertyCandidates)
            {
                SerializedProperty prop = so.FindProperty(candidate);
                if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                    continue;

                prop.objectReferenceValue = programAsset;
                so.ApplyModifiedPropertiesWithoutUndo();
                so.Update();

                SerializedProperty verify = so.FindProperty(candidate);
                if (verify != null && ReferenceEquals(verify.objectReferenceValue, programAsset))
                    return true;
            }

            // 2) Reflection fallback
            Type compType = udonBehaviour.GetType();
            foreach (string candidate in propertyCandidates)
            {
                PropertyInfo pInfo = compType.GetProperty(candidate, flags);
                if (pInfo != null && pInfo.CanWrite && pInfo.PropertyType.IsAssignableFrom(programAsset.GetType()))
                {
                    pInfo.SetValue(udonBehaviour, programAsset);
                    return true;
                }

                FieldInfo fInfo = compType.GetField(candidate, flags);
                if (fInfo != null && fInfo.FieldType.IsAssignableFrom(programAsset.GetType()))
                {
                    fInfo.SetValue(udonBehaviour, programAsset);
                    return true;
                }
            }

            error = "Compatible Program Source field/property was not found on UdonBehaviour.";
            return false;
        }

        private static bool EnsureUdonSharpProgramAsset(
            string scriptAssetPath,
            string className,
            out string programAssetPath,
            out string note)
        {
            programAssetPath = null;
            note = null;

            string scriptDir = Path.GetDirectoryName(scriptAssetPath)?.Replace("\\", "/");
            if (string.IsNullOrEmpty(scriptDir))
            {
                note = "Could not resolve script directory for UdonSharpProgramAsset lookup.";
                return false;
            }

            MonoScript sourceScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
            if (sourceScript == null)
            {
                note = "Created script but failed to load MonoScript asset.";
                return false;
            }

            if (TryFindMatchingProgramAsset(scriptDir, sourceScript, out programAssetPath))
            {
                note = "UdonSharpProgramAsset already exists.";
                return true;
            }

            Type programAssetType = FindType("UdonSharpEditor.UdonSharpProgramAsset") ??
                                    FindType("UdonSharp.UdonSharpProgramAsset");
            if (programAssetType == null)
            {
                note = "UdonSharpProgramAsset type not found. Ensure VRChat UdonSharp is installed.";
                return false;
            }

            if (!typeof(ScriptableObject).IsAssignableFrom(programAssetType))
            {
                note = "UdonSharpProgramAsset type is not a ScriptableObject.";
                return false;
            }

            ScriptableObject assetInstance = ScriptableObject.CreateInstance(programAssetType);
            if (assetInstance == null)
            {
                note = "Failed to instantiate UdonSharpProgramAsset.";
                return false;
            }

            if (!SetSourceScriptOnProgramAsset(assetInstance, sourceScript))
            {
                UnityEngine.Object.DestroyImmediate(assetInstance);
                note = "Could not bind source script field on UdonSharpProgramAsset.";
                return false;
            }

            string desiredPath = $"{scriptDir}/{className}.asset";
            string uniquePath = AssetDatabase.GenerateUniqueAssetPath(desiredPath);
            AssetDatabase.CreateAsset(assetInstance, uniquePath);
            AssetDatabase.ImportAsset(uniquePath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            if (TryFindMatchingProgramAsset(scriptDir, sourceScript, out programAssetPath))
            {
                note = "UdonSharpProgramAsset was created and linked to the script.";
                return true;
            }

            programAssetPath = uniquePath;
            note = "UdonSharpProgramAsset asset file was created, but source link could not be verified.";
            return true;
        }

        private static bool TryFindMatchingProgramAsset(string scriptDir, MonoScript sourceScript, out string programAssetPath)
        {
            programAssetPath = null;
            string[] guids = AssetDatabase.FindAssets("t:UdonSharpProgramAsset", new[] { scriptDir });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                ScriptableObject asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
                if (asset == null)
                    continue;
                if (ReferencesSourceScript(asset, sourceScript))
                {
                    programAssetPath = assetPath;
                    return true;
                }
            }
            return false;
        }

        private static bool ReferencesSourceScript(ScriptableObject asset, MonoScript sourceScript)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type assetType = asset.GetType();

            PropertyInfo sourceProperty = assetType.GetProperty("sourceCsScript", flags);
            if (sourceProperty != null && sourceProperty.PropertyType == typeof(MonoScript))
            {
                object value = sourceProperty.GetValue(asset);
                if (ReferenceEquals(value, sourceScript))
                    return true;
            }

            FieldInfo sourceField = assetType.GetField("sourceCsScript", flags);
            if (sourceField != null && sourceField.FieldType == typeof(MonoScript))
            {
                object value = sourceField.GetValue(asset);
                if (ReferenceEquals(value, sourceScript))
                    return true;
            }

            var serialized = new SerializedObject(asset);
            SerializedProperty prop = serialized.FindProperty("sourceCsScript");
            if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
                return ReferenceEquals(prop.objectReferenceValue, sourceScript);

            return false;
        }

        private static bool SetSourceScriptOnProgramAsset(ScriptableObject asset, MonoScript sourceScript)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type assetType = asset.GetType();

            PropertyInfo sourceProperty = assetType.GetProperty("sourceCsScript", flags);
            if (sourceProperty != null && sourceProperty.CanWrite && sourceProperty.PropertyType == typeof(MonoScript))
            {
                sourceProperty.SetValue(asset, sourceScript);
                return true;
            }

            FieldInfo sourceField = assetType.GetField("sourceCsScript", flags);
            if (sourceField != null && sourceField.FieldType == typeof(MonoScript))
            {
                sourceField.SetValue(asset, sourceScript);
                return true;
            }

            var serialized = new SerializedObject(asset);
            SerializedProperty prop = serialized.FindProperty("sourceCsScript");
            if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
            {
                prop.objectReferenceValue = sourceScript;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                return true;
            }

            return false;
        }
    }
}
