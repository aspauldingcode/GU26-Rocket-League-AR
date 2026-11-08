using CardboardXR.Demos.Ascii;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ProjectBootstrap
{
    // Adds the terminal pass to both URP renderers once, left inactive until the ASCII demo
    // turns AsciiTerminal.Active on. Runs when the editor loads so a build picks it up.
    [InitializeOnLoad]
    static class AsciiTerminalInstaller
    {
        const string MaterialPath = "Assets/_Project/Demos/Ascii/Ascii Terminal.mat";

        static AsciiTerminalInstaller() => EditorApplication.delayCall += Ensure;

        public static void Ensure()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
                return;
            var changed = false;
            changed |= EnsureRenderer("Assets/Settings/Mobile_Renderer.asset", material);
            changed |= EnsureRenderer("Assets/Settings/PC_Renderer.asset", material);
            if (changed)
                AssetDatabase.SaveAssets();
        }

        static bool EnsureRenderer(string path, Material material)
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
            if (data == null)
                return false;
            AsciiTerminalFeature feature = null;
            foreach (var existing in data.rendererFeatures)
            {
                if (existing is AsciiTerminalFeature terminal)
                {
                    feature = terminal;
                    break;
                }
            }
            var added = feature == null;
            if (added)
            {
                feature = ScriptableObject.CreateInstance<AsciiTerminalFeature>();
                feature.name = "Ascii Terminal";
                feature.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(feature, data);
                data.rendererFeatures.Add(feature);
            }
            if (!added && feature.Material == material)
                return false;
            feature.Material = material;
            EditorUtility.SetDirty(feature);
            data.SetDirty();
            EditorUtility.SetDirty(data);
            return true;
        }
    }
}
