using System.Collections.Generic;
using ThePitt.Items;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// The greybox's assets, made and kept in the editor: surface textures and materials, terrain layers,
    /// pit loot, the NPC agent's size, and the material library the runtime generator picks named materials
    /// from. None of it depends on the seed, so it's set up once and every world the game builds shares it.
    /// </summary>
    public static class PittWorldAssets
    {
        private const string AssetFolder = "Assets/_Project/World";
        private static PittGreybox g;

        /// <summary>Makes or refreshes every asset and fills in the greybox's references to them.</summary>
        public static void Refresh(PittGreybox greybox)
        {
            g = greybox;
            Undo.RecordObject(g, "Refresh Greybox Assets");
            EnsureAssetsInner();
            SetUpAgent();

            // Outposts and doorways ask for these by name.
            var (marbleTex, marbleNormal) = PittTextureBaker.Marble(AssetFolder);
            var marble = Textured(null, "World_Marble", new Color(0.82f, 0.82f, 0.85f), marbleTex, marbleNormal);
            marble.SetFloat("_Smoothness", 0.6f);
            Mat("World_WatcherRobe", new Color(0.09f, 0.1f, 0.12f)).SetFloat("_Smoothness", 0.35f);
            Mat("World_WatcherMask", new Color(0.95f, 0.95f, 0.97f)).SetFloat("_Smoothness", 0.85f);
            Mat("World_DoorVoid", new Color(0.008f, 0.008f, 0.008f));
            g.terminalScreen = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Materials/WatcherTerminal_Screen.mat");
            if (g.itemRegistry == null)
            {
                var found = AssetDatabase.FindAssets("t:SaveRegistry");
                if (found.Length > 0) g.itemRegistry = AssetDatabase.LoadAssetAtPath<Core.SaveRegistry>(AssetDatabase.GUIDToAssetPath(found[0]));
            }

            g.materialLibrary = new List<Material>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { AssetFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Contains("/Glow/")) continue; // glow colours are made per world, in memory
                g.materialLibrary.Add(AssetDatabase.LoadAssetAtPath<Material>(path));
            }
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(g);
        }

        private static void EnsureAssetsInner()
        {
            if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets/_Project", "World");
            if (!AssetDatabase.IsValidFolder($"{AssetFolder}/Glow")) AssetDatabase.CreateFolder(AssetFolder, "Glow");

            // Surfaces with real texture and relief so light has something to catch on.
            var (concreteTex, concreteNormal) = PittTextureBaker.Concrete(AssetFolder);
            var (brickTex, brickNormal) = PittTextureBaker.Brick(AssetFolder);
            g.concrete = Textured(g.concrete, "World_Concrete", new Color(0.85f, 0.83f, 0.8f), concreteTex, concreteNormal);
            g.concreteDark = Textured(g.concreteDark, "World_ConcreteDark", new Color(0.5f, 0.5f, 0.52f), concreteTex, concreteNormal);
            g.brick = Textured(g.brick, "World_Brick", Color.white, brickTex, brickNormal);
            g.wood = g.wood != null ? g.wood : Mat("World_Wood", new Color(0.36f, 0.25f, 0.15f));
            g.metal = g.metal != null ? g.metal : Mat("World_Metal", new Color(0.26f, 0.27f, 0.29f), metallic: 0.6f);
            g.rust = g.rust != null ? g.rust : Mat("World_Rust", new Color(0.4f, 0.21f, 0.11f));
            g.trash = g.trash != null ? g.trash : Mat("World_Trash", new Color(0.13f, 0.12f, 0.1f));
            g.lampGlow = g.lampGlow != null ? g.lampGlow : Emissive("World_LampGlow", new Color(1f, 0.75f, 0.45f), new Color(1f, 0.6f, 0.3f) * 3f);
            g.windowWarm = Emissive("World_WindowWarm", new Color(0.3f, 0.2f, 0.1f), new Color(1f, 0.55f, 0.2f) * 2f);
            g.windowCold = Emissive("World_WindowCold", new Color(0.1f, 0.15f, 0.2f), new Color(0.4f, 0.6f, 1f) * 1.6f);
            if (g.ceiling == null || g.ceiling.name == "World_Ceiling")
            {
                // Flat black that ignores fog: the sky stays black while nearby buildings keep their tint.
                string ceilingPath = $"{AssetFolder}/World_CeilingBlack.mat";
                g.ceiling = AssetDatabase.LoadAssetAtPath<Material>(ceilingPath);
                if (g.ceiling == null)
                {
                    g.ceiling = new Material(Shader.Find("ThePitt/Unlit Emissive (No Fog)"));
                    AssetDatabase.CreateAsset(g.ceiling, ceilingPath);
                }
                g.ceiling.SetColor("_Color", Color.black);
                g.ceiling.SetFloat("_Intensity", 0f);
            }
            if (g.holeGlow == null)
            {
                string path = $"{AssetFolder}/World_HoleGlow.mat";
                g.holeGlow = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (g.holeGlow == null)
                {
                    g.holeGlow = new Material(Shader.Find("ThePitt/Unlit Emissive (No Fog)"));
                    AssetDatabase.CreateAsset(g.holeGlow, path);
                }
            }
            g.holeGlow.SetFloat("_Intensity", g.holeIntensity);
            g.chainLink = g.chainLink != null ? g.chainLink : ChainLinkMaterial();
            if (g.cable == null)
            {
                string path = $"{AssetFolder}/World_Cable.mat";
                g.cable = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (g.cable == null)
                {
                    g.cable = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                    AssetDatabase.CreateAsset(g.cable, path);
                }
                g.cable.color = new Color(0.02f, 0.02f, 0.02f);
            }

            if (g.terrainMaterial == null)
            {
                string path = $"{AssetFolder}/World_Terrain.mat";
                g.terrainMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (g.terrainMaterial == null)
                {
                    g.terrainMaterial = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
                    AssetDatabase.CreateAsset(g.terrainMaterial, path);
                }
            }
            // Our own placeholder layers are refreshed every run; custom layers assigned by hand are kept.
            if (g.groundLayer == null || g.groundLayer.name == "TerrainLayer_Ground")
            {
                g.groundLayer = Layer("Ground", new Color(0.27f, 0.26f, 0.25f));
                // Cracked asphalt rather than flat colour, tiled every 4 m.
                var (asphalt, asphaltNormal) = PittTextureBaker.Asphalt(AssetFolder);
                g.groundLayer.diffuseTexture = asphalt;
                g.groundLayer.normalMapTexture = asphaltNormal;
                g.groundLayer.normalScale = 1f;
                g.groundLayer.tileSize = new Vector2(4f, 4f);
                g.groundLayer.smoothness = 0.25f;
                // The street floor sits in the darkest part of the bake; lift it so it reads like the walls.
                g.groundLayer.diffuseRemapMax = new Vector4(2.5f, 2.5f, 2.5f, 1f);
                EditorUtility.SetDirty(g.groundLayer);
            }
            if (g.dirtLayer == null || g.dirtLayer.name == "TerrainLayer_Dirt")
            {
                g.dirtLayer = Layer("Dirt", new Color(0.24f, 0.19f, 0.14f));
                // Lifted a little for the live ambient; any more and the open pit glows orange.
                g.dirtLayer.diffuseRemapMax = new Vector4(1.2f, 1.2f, 1.2f, 1f);
                EditorUtility.SetDirty(g.dirtLayer);
            }

            if (g.worldItemPrefab == null)
                g.worldItemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/WorldItem.prefab");

            if (g.pitLoot == null || g.pitLoot.Length == 0)
            {
                g.pitLoot = new[]
                {
                    Loot("ScrapMetal", 2, 6, 5f),
                    Loot("Spring", 1, 2, 2f),
                    Loot("PipeBarrel", 1, 1, 1f),
                };
            }
            AssetDatabase.SaveAssets();
        }

        private static Material ChainLinkMaterial()
        {
            string texPath = $"{AssetFolder}/ChainLink.asset";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                const int size = 64;
                tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
                var px = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Two diagonal wire sets make the diamond weave.
                    bool wire = (x + y) % 16 < 2 || (x - y + size) % 16 < 2;
                    px[y * size + x] = wire ? new Color(0.3f, 0.3f, 0.32f, 1f) : new Color(0f, 0f, 0f, 0f);
                }
                tex.SetPixels(px);
                tex.Apply(true);
                AssetDatabase.CreateAsset(tex, texPath);
            }

            var m = Mat("World_ChainLink", Color.white, metallic: 0.5f);
            m.SetTexture("_BaseMap", tex);
            m.mainTextureScale = new Vector2(8f, 4.4f);
            m.SetFloat("_AlphaClip", 1f);
            m.SetFloat("_Cutoff", 0.5f);
            m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            return m;
        }

        private static PittGreybox.LootEntry Loot(string key, int min, int max, float weight) => new()
        {
            item = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"Assets/_Project/Items/Item_{key}.asset"),
            material = AssetDatabase.LoadAssetAtPath<Material>($"Assets/_Project/Materials/Pickup_{key}.mat"),
            minQuantity = min,
            maxQuantity = max,
            weight = weight,
        };

        private static Material Mat(string name, Color color, float metallic = 0f)
        {
            string path = $"{AssetFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.color = color;
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", 0.15f);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Our own placeholder materials get the baked textures; hand-assigned materials are left alone.</summary>
        private static Material Textured(Material current, string name, Color tint, Texture2D albedo, Texture2D normal)
        {
            if (current != null && current.name != name) return current;
            var m = Mat(name, tint);
            m.SetTexture("_BaseMap", albedo);
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            m.EnableKeyword("_NORMALMAP");
            m.SetTextureScale("_BaseMap", PittTextureBaker.Tiling);
            m.SetTextureScale("_BumpMap", PittTextureBaker.Tiling);
            return m;
        }

        private static Material Emissive(string name, Color baseColor, Color emission)
        {
            var m = Mat(name, baseColor);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            // Glowing surfaces light their surroundings in the bake: signs actually light the walls.
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            return m;
        }

        private static TerrainLayer Layer(string name, Color color)
        {
            string texPath = $"{AssetFolder}/TerrainTex_{name}.asset";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            bool created = tex == null;
            if (created) tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color[16];
            for (int i = 0; i < 16; i++)
            {
                var c = color * (0.92f + (i * 7 % 5) * 0.03f);
                // URP terrain reads diffuse alpha as smoothness; keep the ground matte.
                pixels[i] = new Color(c.r, c.g, c.b, 0f);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            if (created) AssetDatabase.CreateAsset(tex, texPath);
            else EditorUtility.SetDirty(tex);

            string layerPath = $"{AssetFolder}/TerrainLayer_{name}.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
            if (layer == null)
            {
                layer = new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(4f, 4f) };
                AssetDatabase.CreateAsset(layer, layerPath);
            }
            return layer;
        }

        /// <summary>Humanoid agent sized for the tight alleys: slim, and able to take stairs but not retaining walls.</summary>
        private static void SetUpAgent()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset");
            if (settings.Length == 0) return;
            var so = new SerializedObject(settings[0]);
            var list = so.FindProperty("m_Settings");
            for (int i = 0; i < list.arraySize; i++)
            {
                var a = list.GetArrayElementAtIndex(i);
                if (a.FindPropertyRelative("agentTypeID").intValue != 0) continue;
                a.FindPropertyRelative("agentRadius").floatValue = 0.3f;
                a.FindPropertyRelative("agentHeight").floatValue = 1.8f;
                a.FindPropertyRelative("agentClimb").floatValue = 0.4f;
                a.FindPropertyRelative("agentSlope").floatValue = 40f;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

    }
}
