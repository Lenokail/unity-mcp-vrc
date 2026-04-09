using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageVRChatUdonTests
    {
        private const string TempRoot = "Assets/Temp/ManageVRChatUdonTests";
        private const string TempScript = TempRoot + "/DoorToggle.cs";

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(TempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            SafeDeleteAsset(TempScript);
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
            CleanupEmptyParentFolders(TempRoot);

            var go = GameObject.Find("VRChatUdonTestGO");
            if (go != null)
                Object.DestroyImmediate(go);
        }

        [Test]
        public void HandleCommand_MissingAction_ReturnsError()
        {
            var result = ToJObject(ManageVRChatUdon.HandleCommand(new JObject()));
            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"]?.ToString(), Does.Contain("required"));
        }

        [Test]
        public void CheckEnvironment_ReturnsSuccessPayload()
        {
            var result = ToJObject(ManageVRChatUdon.HandleCommand(new JObject
            {
                ["action"] = "check_environment"
            }));

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.IsNotNull(result["data"]);
            Assert.IsNotNull(result["data"]["unity_version"]);
            Assert.IsNotNull(result["data"]["recommended_unity_version"]);
        }

        [Test]
        public void CreateUdonSharpScript_CreatesScriptAsset()
        {
            var result = ToJObject(ManageVRChatUdon.HandleCommand(new JObject
            {
                ["action"] = "create_udonsharp_script",
                ["path"] = TempScript,
                ["class_name"] = "DoorToggle",
                ["sync_mode"] = "manual"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(TempScript);
            Assert.IsNotNull(script);
            string source = script.text;
            Assert.That(source, Does.Contain("class DoorToggle"));
            Assert.That(source, Does.Contain("[UdonSynced]"));
        }

        [Test]
        public void ValidateNetworking_MissingPath_ReturnsError()
        {
            var result = ToJObject(ManageVRChatUdon.HandleCommand(new JObject
            {
                ["action"] = "validate_networking"
            }));

            Assert.IsFalse(result.Value<bool>("success"));
            Assert.That(result["error"]?.ToString(), Does.Contain("path"));
        }
    }
}
