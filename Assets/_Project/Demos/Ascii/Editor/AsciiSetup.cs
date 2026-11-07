using CardboardXR.Demos;
using CardboardXR.Demos.Ascii;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ProjectBootstrap
{
    // ASCII Portal: glyph-gate prefab, ASCII surface material + atlas, and the demo root.
    class AsciiSetup : DemoSetup
    {
        const string DepthMaskShaderPath = "Assets/_Project/Shaders/DepthMask.shader";
        const string AsciiShaderPath = "Assets/_Project/Demos/Ascii/Shaders/AsciiSurface.shader";
        const string AtlasPath = "Assets/_Project/Demos/Ascii/Textures/ascii_atlas.png";
        const string AngelModelPath = "Assets/_Project/Demos/Ascii/Models/StoneAngel.obj";
        const string AngelPrefabPath = "Assets/_Project/Demos/Ascii/Weeping Angel.prefab";
        // The source OBJ is ~2.85 m tall; this brings it to roughly human height.
        const float AngelScale = 0.7f;

        public override string Name => "Ascii";
        public override int Order => 60;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "ascii",
            Title = "ASCII Portal",
            Description = "Open a glyph gate on your floor and walk through into an ASCII remapping of your room. Don't blink: weeping angels close in when you look away. Walk back through to return to passthrough.",
            Tagline = "ASCII room with weeping angels that only move unseen",
            Icon = "terminal",
            Tint = new Color(0.10f, 0.45f, 0.22f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, ScanSurfaces = true },
        };

        public override void BuildScene(string folder)
        {
            var unlit = DemoAssets.Unlit;
            var glow = StereoBuilder.CreateMaterial("Ascii Glow", unlit, Color.white, null, 1f, folder);
            DemoAssets.MakeAdditive(glow);

            var atlas = LoadAtlas();
            var asciiShader = AssetDatabase.LoadAssetAtPath<Shader>(AsciiShaderPath);
            var ascii = StereoBuilder.SaveAsset(new Material(asciiShader)
            {
                name = "Ascii Surface",
            }, $"{folder}/Ascii Surface.mat");
            ascii.SetTexture("_GlyphAtlas", atlas);
            ascii.SetFloat("_GlyphCount", AsciiGlyph.Ramp.Length);
            ascii.SetColor("_Color", new Color(0.25f, 1f, 0.45f, 1f));
            ascii.SetColor("_BgColor", new Color(0.01f, 0.01f, 0.01f, 1f));
            ascii.SetFloat("_CellSize", 0.08f);
            ascii.SetFloat("_Extrude", 0.004f);
            ascii.SetFloat("_ColorBoost", 1.5f);
            ascii.SetFloat("_BgFromCamera", 0.12f);
            EditorUtility.SetDirty(ascii);

            var mask = StereoBuilder.SaveAsset(
                new Material(AssetDatabase.LoadAssetAtPath<Shader>(DepthMaskShaderPath)) { name = "Ascii Depth Mask" },
                $"{folder}/Depth Mask.mat");

            var stone = StereoBuilder.CreateMaterial("Angel Stone", unlit, new Color(0.22f, 0.28f, 0.2f), null, 1f, folder);
            var angel = BuildAngelPrefab(folder, stone, ascii);

            var portal = new GameObject("Ascii Gate").AddComponent<AsciiWorld>();
            var serializedPortal = new SerializedObject(portal);
            serializedPortal.FindProperty("m_Glow").objectReferenceValue = glow;
            serializedPortal.FindProperty("m_Mask").objectReferenceValue = mask;
            serializedPortal.ApplyModifiedPropertiesWithoutUndo();
            var prefab = PrefabUtility.SaveAsPrefabAsset(portal.gameObject, $"{folder}/Ascii Gate.prefab");
            Object.DestroyImmediate(portal.gameObject);

            var demo = new GameObject("Ascii Demo").AddComponent<AsciiDemo>();
            var serializedDemo = new SerializedObject(demo);
            serializedDemo.FindProperty("m_PortalPrefab").objectReferenceValue = prefab;
            serializedDemo.FindProperty("m_AsciiMaterial").objectReferenceValue = ascii;
            serializedDemo.FindProperty("m_AngelPrefab").objectReferenceValue = angel;
            serializedDemo.ApplyModifiedPropertiesWithoutUndo();

            // Pull the new "terminal" icon into the bundled Material Icons font.
            MaterialAssets.Ensure();
        }

        // Batch: Unity -executeMethod ProjectBootstrap.AsciiSetup.EnsureIcons
        public static void EnsureIcons()
        {
            MaterialAssets.Ensure();
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        // Batch: Unity -executeMethod ProjectBootstrap.AsciiSetup.EnsureAngels
        // Imports the stone angel OBJ, builds the weeping-angel prefab, and wires Ascii.unity.
        public static void EnsureAngels()
        {
            try
            {
                var folder = "Assets/_Project/Demos/Ascii";
                AssetDatabase.ImportAsset(AngelModelPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var unlit = DemoAssets.Unlit;
                var stone = StereoBuilder.CreateMaterial("Angel Stone", unlit, new Color(0.22f, 0.28f, 0.2f), null, 1f, folder);
                var ascii = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/Ascii Surface.mat");
                var angel = BuildAngelPrefab(folder, stone, ascii);
                WireAngelPrefab(angel);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Ascii] Weeping angel prefab ready at {AngelPrefabPath}");
            }
            finally
            {
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
        }

        static GameObject BuildAngelPrefab(string folder, Material stone, Material ascii)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AngelModelPath);
            if (model == null)
                throw new System.IO.FileNotFoundException("Stone angel OBJ missing at " + AngelModelPath);

            var root = new GameObject("Weeping Angel");
            root.AddComponent<WeepingAngel>();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "Stone Angel";
            instance.transform.SetParent(root.transform, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one * AngelScale;

            var skin = ascii != null ? ascii : stone;
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterial = skin;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            var path = $"{folder}/Weeping Angel.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static void WireAngelPrefab(GameObject angel)
        {
            var scenePath = "Assets/_Project/Demos/Ascii/Ascii.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (var root in scene.GetRootGameObjects())
            {
                var demo = root.GetComponent<AsciiDemo>();
                if (demo == null)
                    continue;
                var serialized = new SerializedObject(demo);
                serialized.FindProperty("m_AngelPrefab").objectReferenceValue = angel;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[Ascii] Ascii.unity now references the weeping angel prefab");
                return;
            }
            Debug.LogError("[Ascii] Ascii Demo not found in Ascii.unity");
        }

        static Texture2D LoadAtlas()
        {
            var importer = AssetImporter.GetAtPath(AtlasPath) as TextureImporter;
            if (importer == null)
                throw new System.IO.FileNotFoundException("Glyph atlas PNG not found at " + AtlasPath);

            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            // Mipmapped and filtered so distant glyphs stay legible instead of breaking into dots.
            importer.mipmapEnabled = true;
            importer.filterMode = FilterMode.Trilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 4096;
            importer.SaveAndReimport();

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);

            // Glyph cells are square, so width must equal height * glyph count.
            if (texture.width != texture.height * AsciiGlyph.Ramp.Length)
                Debug.LogError($"Atlas is {texture.width}x{texture.height} but AsciiGlyph.Ramp has " +
                            $"{AsciiGlyph.Ramp.Length} glyphs. They must match.");
            return texture;
        }

        // Horizontal strip of Ramp glyphs, white on black, one cell each.
        static Texture2D CreateGlyphAtlas()
        {
            const int cell = 32;
            var ramp = AsciiGlyph.Ramp;
            var width = cell * ramp.Length;
            var height = cell;
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(0, 0, 0, 255);

            for (var g = 0; g < ramp.Length; g++)
                StampGlyph(pixels, width, height, g * cell, cell, ramp[g]);

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Glyph Atlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        // Tiny 5x7 bitmap font for the ramp characters, stamped into the atlas cell.
        static void StampGlyph(Color32[] pixels, int width, int height, int originX, int cell, char glyph)
        {
            var rows = GlyphRows(glyph);
            const int gw = 5;
            const int gh = 7;
            var ox = originX + (cell - gw) / 2;
            var oy = (cell - gh) / 2;
            var on = new Color32(255, 255, 255, 255);
            for (var y = 0; y < gh; y++)
            {
                var row = rows[y];
                for (var x = 0; x < gw; x++)
                {
                    if (((row >> (gw - 1 - x)) & 1) == 0)
                        continue;
                    var px = ox + x;
                    var py = oy + (gh - 1 - y);
                    pixels[py * width + px] = on;
                }
            }
        }

        // Bit patterns for each ramp character (MSB left). Space is empty.
        static int[] GlyphRows(char glyph) => glyph switch
        {
            '.' => new[] { 0, 0, 0, 0, 0, 0, 0x04 },
            ':' => new[] { 0, 0x04, 0, 0, 0x04, 0, 0 },
            '-' => new[] { 0, 0, 0, 0x1F, 0, 0, 0 },
            '=' => new[] { 0, 0, 0x1F, 0, 0x1F, 0, 0 },
            '+' => new[] { 0, 0x04, 0x04, 0x1F, 0x04, 0x04, 0 },
            '*' => new[] { 0, 0x15, 0x0E, 0x04, 0x0E, 0x15, 0 },
            '#' => new[] { 0x0A, 0x1F, 0x0A, 0x0A, 0x1F, 0x0A, 0 },
            '%' => new[] { 0x11, 0x12, 0x04, 0x08, 0x09, 0x11, 0 },
            '$' => new[] { 0x04, 0x0F, 0x14, 0x0E, 0x05, 0x1E, 0x04 },
            '@' => new[] { 0x0E, 0x11, 0x17, 0x15, 0x17, 0x10, 0x0E },
            _ => new[] { 0, 0, 0, 0, 0, 0, 0 },
        };
    }
}
