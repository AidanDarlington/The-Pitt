using System.Collections.Generic;
using System.IO;
using ThePitt.Items;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// Renders an inventory icon for every item: a small model built from primitives (or the station's
    /// own prefab), shot over black and over white so a clean alpha can be recovered without relying
    /// on the render pipeline keeping alpha. Menu: The Pitt > Bake Item Icons.
    /// </summary>
    public static class PittIconBaker
    {
        private const string ItemsFolder = "Assets/_Project/Items";
        private const string IconFolder = "Assets/_Project/Icons";
        private const int Size = 256;
        private static readonly Vector3 Stage = new(0f, -2000f, 0f);

        private static readonly Dictionary<Color, Material> mats = new();
        // Building a ground model rather than an icon: keep the vertex count down.
        private static bool lowPoly;

        [MenuItem("The Pitt/Bake Item Icons")]
        public static void BakeAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("PittIconBaker: exit play mode first.");
                return;
            }
            if (!AssetDatabase.IsValidFolder(IconFolder)) AssetDatabase.CreateFolder("Assets/_Project", "Icons");
            mats.Clear();

            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDefinition", new[] { ItemsFolder }))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (BakeIcon(item)) count++;
            }
            foreach (var m in mats.Values) Object.DestroyImmediate(m);
            mats.Clear();
            AssetDatabase.SaveAssets();
            Debug.Log($"PittIconBaker: baked {count} icons.");
        }

        /// <summary>Bakes one item's icon (new content, without re-rendering every other icon).</summary>
        public static void Bake(ItemDefinition item)
        {
            mats.Clear();
            BakeIcon(item);
            foreach (var m in mats.Values) Object.DestroyImmediate(m);
            mats.Clear();
            AssetDatabase.SaveAssets();
        }

        private static bool BakeIcon(ItemDefinition item)
        {
            var model = BuildModel(item);
            if (model == null) return false;
            var sprite = Render(model, item.name.Replace("Item_", ""));
            Object.DestroyImmediate(model);

            var so = new SerializedObject(item);
            so.FindProperty("icon").objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private const string ModelFolder = "Assets/_Project/Prefabs/ItemModels";

        /// <summary>
        /// Saves each item's icon model as a single-mesh prefab (one submesh per material, centred, longest
        /// side 1 unit) and sets it as the item's ground model. Stations are left out: they place their own prefab.
        /// </summary>
        [MenuItem("The Pitt/Bake Item World Models")]
        public static void BakeWorldModels()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("PittIconBaker: exit play mode first.");
                return;
            }
            if (!AssetDatabase.IsValidFolder(ModelFolder)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "ItemModels");
            mats.Clear();

            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDefinition", new[] { ItemsFolder }))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (item.IsPlaceable) continue;
                lowPoly = true;
                var model = BuildModel(item);
                lowPoly = false;
                if (model == null) continue;
                var prefab = SaveWorldModel(model, item.name.Replace("Item_", ""));
                Object.DestroyImmediate(model);

                var so = new SerializedObject(item);
                so.FindProperty("worldModel").objectReferenceValue = prefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                count++;
            }
            foreach (var m in mats.Values) Object.DestroyImmediate(m);
            mats.Clear();
            AssetDatabase.SaveAssets();
            Debug.Log($"PittIconBaker: baked {count} world models.");
        }

        private static GameObject SaveWorldModel(GameObject model, string key)
        {
            // Every piece in the model's own space, grouped by material.
            var toLocal = model.transform.worldToLocalMatrix;
            var groups = new Dictionary<Material, List<CombineInstance>>();
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>())
            {
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                if (!groups.TryGetValue(r.sharedMaterial, out var list)) groups[r.sharedMaterial] = list = new List<CombineInstance>();
                list.Add(new CombineInstance { mesh = filter.sharedMesh, transform = toLocal * r.localToWorldMatrix });
            }

            var parts = new List<CombineInstance>();
            var materials = new List<Material>();
            foreach (var pair in groups)
            {
                var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                part.CombineMeshes(pair.Value.ToArray(), true, true);
                parts.Add(new CombineInstance { mesh = part, transform = Matrix4x4.identity });
                materials.Add(MaterialAsset(pair.Key));
            }
            var mesh = new Mesh { name = $"ItemModel_{key}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(parts.ToArray(), false, true);
            foreach (var p in parts) Object.DestroyImmediate(p.mesh);

            // Centred, longest side 1.
            mesh.RecalculateBounds();
            var b = mesh.bounds;
            float scale = 1f / Mathf.Max(b.size.x, b.size.y, b.size.z, 0.0001f);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = (vertices[i] - b.center) * scale;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            string meshPath = $"{ModelFolder}/ItemModel_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                // Rewritten through the mesh API (not CopySerialized) so the editor stops drawing the old shape.
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.SetVertices(mesh.vertices);
                existing.SetNormals(mesh.normals);
                existing.SetUVs(0, mesh.uv);
                existing.subMeshCount = mesh.subMeshCount;
                for (int i = 0; i < mesh.subMeshCount; i++) existing.SetTriangles(mesh.GetTriangles(i), i);
                existing.RecalculateBounds();
                existing.RecalculateTangents();
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                mesh = existing;
            }
            else AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject($"ItemModel_{key}");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials.ToArray();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{ModelFolder}/ItemModel_{key}.prefab");
            Object.DestroyImmediate(go);
            return prefab;
        }

        /// <summary>A saved copy of one of the baker's materials, shared by every model using the same look.</summary>
        private static Material MaterialAsset(Material source)
        {
            float metallic = source.GetFloat("_Metallic");
            bool glows = source.IsKeywordEnabled("_EMISSION");
            string name = $"ItemModel_{ColorUtility.ToHtmlStringRGB(source.color)}_{Mathf.RoundToInt(metallic * 100f)}{(glows ? "_glow" + ColorUtility.ToHtmlStringRGB(source.GetColor("_EmissionColor")) : "")}";
            string path = $"{ModelFolder}/{name}.mat";
            var asset = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (asset != null) return asset;
            asset = new Material(source) { name = name };
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        // ---------------------------------------------------------------- rendering

        private static Sprite Render(GameObject model, string name)
        {
            var bounds = new Bounds(model.transform.position, Vector3.zero);
            foreach (var r in model.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

            var camGo = new GameObject("IconCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 25f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            // From the front (-Z), a little to the right, looking down at about 35 degrees.
            var offset = new Vector3(0.55f, 0.75f, -1f).normalized;
            float distance = bounds.extents.magnitude / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            camGo.transform.position = bounds.center + offset * distance;
            camGo.transform.LookAt(bounds.center);

            // Studio lighting: warm key, cool fill, rim from behind.
            var lights = new List<GameObject>
            {
                MakeLight(new Vector3(40f, 30f, 0f), new Color(1f, 0.93f, 0.85f), 1.6f),
                MakeLight(new Vector3(20f, 200f, 0f), new Color(0.6f, 0.7f, 0.9f), 0.6f),
                MakeLight(new Vector3(10f, 150f, 0f), new Color(1f, 1f, 1f), 0.8f),
            };
            var ambientMode = RenderSettings.ambientMode;
            var ambientLight = RenderSettings.ambientLight;
            bool fog = RenderSettings.fog;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.38f);
            RenderSettings.fog = false;

            var black = Shoot(cam, Color.black);
            var white = Shoot(cam, Color.white);

            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.fog = fog;
            foreach (var l in lights) Object.DestroyImmediate(l);
            Object.DestroyImmediate(camGo);

            // alpha = 1 - (white - black); colour = black / alpha.
            var outPx = new Color[Size * Size];
            for (int i = 0; i < outPx.Length; i++)
            {
                var b = black[i];
                var w = white[i];
                float a = Mathf.Clamp01(1f - ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f);
                outPx[i] = a > 0.004f ? new Color(b.r / a, b.g / a, b.b / a, a) : Color.clear;
            }
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels(outPx);
            tex.Apply();

            string path = $"{IconFolder}/Icon_{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static GameObject MakeLight(Vector3 euler, Color color, float intensity)
        {
            var go = new GameObject("IconLight");
            go.transform.rotation = Quaternion.Euler(euler);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.color = color;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            return go;
        }

        private static Color[] Shoot(Camera cam, Color background)
        {
            cam.backgroundColor = background;
            var rt = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels();
            Object.DestroyImmediate(tex);
            return px;
        }

        // ---------------------------------------------------------------- models

        private static Material M(Color c, float metallic = 0f, float emission = 0f)
        {
            var key = new Color(c.r, c.g, c.b, metallic + emission * 10f);
            if (mats.TryGetValue(key, out var m)) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.color = c;
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", 0.35f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * emission);
            }
            mats[key] = m;
            return m;
        }

        private static GameObject P(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Material m)
        {
            var g = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos;
            g.transform.localScale = scale;
            g.transform.localEulerAngles = euler;
            g.GetComponent<Renderer>().sharedMaterial = m;
            return g;
        }

        private static GameObject BuildModel(ItemDefinition item)
        {
            var root = new GameObject("IconModel_" + item.name);
            root.transform.position = Stage;
            var t = root.transform;
            var metal = M(new Color(0.55f, 0.56f, 0.6f), 0.7f);
            var rust = M(new Color(0.55f, 0.28f, 0.14f), 0.2f);
            var dark = M(new Color(0.12f, 0.12f, 0.13f), 0.4f);
            var paper = M(new Color(0.75f, 0.66f, 0.48f));

            // Skill milestone items are upgrades of existing ones: the same model, worked in steel (or cut cleaner).
            string key = item.name.Replace("Item_", "");
            string basis = key switch
            {
                "ReinforcedPickaxe" => "ScrapPickaxe",
                "ReinforcedShovel" => "ScrapShovel",
                "HeavyRebarClub" => "RebarClub",
                "HeartyStew" => "PitStew",
                "GreyPrime" => "Grey",
                _ => key,
            };
            bool upgraded = basis != key;
            if (upgraded) rust = M(new Color(0.3f, 0.32f, 0.36f), 0.85f);

            switch (basis)
            {
                case "ScrapMetal":
                    P(t, PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(1f, 0.08f, 0.6f), new Vector3(10, 20, 5), rust);
                    P(t, PrimitiveType.Cube, new Vector3(0.2f, 0.15f, 0.1f), new Vector3(0.7f, 0.06f, 0.45f), new Vector3(-15, -30, 20), metal);
                    P(t, PrimitiveType.Cube, new Vector3(-0.25f, 0.25f, -0.05f), new Vector3(0.5f, 0.05f, 0.3f), new Vector3(25, 50, -10), rust);
                    break;
                case "SlagIngot":
                {
                    // A rough, dull bar: a slab with a narrower top and a lump of slag stuck to one end.
                    var slag = M(new Color(0.32f, 0.3f, 0.29f), 0.25f);
                    P(t, PrimitiveType.Cube, Vector3.zero, new Vector3(1.1f, 0.28f, 0.5f), new Vector3(0, 25, -8), slag);
                    P(t, PrimitiveType.Cube, new Vector3(0f, 0.18f, 0f), new Vector3(0.9f, 0.12f, 0.36f), new Vector3(0, 25, -8), slag);
                    P(t, PrimitiveType.Sphere, new Vector3(0.45f, 0.18f, -0.2f), new Vector3(0.22f, 0.16f, 0.2f), Vector3.zero, rust);
                    break;
                }
                case "Spring":
                    if (lowPoly)
                    {
                        // On the ground: thin boxes joined end to end along the helix, a few thousand vertices
                        // rather than the icon's hundreds of thousands.
                        const int segments = 96;
                        Vector3 Coil(int i) => new(Mathf.Cos(i / (float)segments * Mathf.PI * 12f) * 0.25f, i / (float)segments * 1.1f,
                            Mathf.Sin(i / (float)segments * Mathf.PI * 12f) * 0.25f);
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 a = Coil(i), b = Coil(i + 1);
                            P(t, PrimitiveType.Cube, (a + b) * 0.5f, new Vector3(0.06f, 0.06f, (b - a).magnitude + 0.02f),
                                Quaternion.LookRotation(b - a).eulerAngles, metal);
                        }
                        break;
                    }
                    // Dense beads along a helix read as a coil of wire.
                    for (int i = 0; i < 160; i++)
                    {
                        float a = i / 160f * Mathf.PI * 2f * 6f;
                        P(t, PrimitiveType.Sphere, new Vector3(Mathf.Cos(a) * 0.25f, i / 160f * 1.1f, Mathf.Sin(a) * 0.25f), Vector3.one * 0.075f, Vector3.zero, metal);
                    }
                    break;
                case "PipeBarrel":
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.22f, 0.7f, 0.22f), new Vector3(0, 0, 70), metal);
                    P(t, PrimitiveType.Cylinder, new Vector3(-0.66f, 0.24f, 0f), new Vector3(0.14f, 0.02f, 0.14f), new Vector3(0, 0, 70), dark);
                    break;
                case "Chemicals":
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.55f, 0.5f, 0.55f), Vector3.zero, M(new Color(0.85f, 0.85f, 0.8f)));
                    P(t, PrimitiveType.Cylinder, new Vector3(0f, 0.6f, 0f), new Vector3(0.18f, 0.1f, 0.18f), Vector3.zero, M(new Color(0.3f, 0.75f, 0.25f)));
                    P(t, PrimitiveType.Cube, new Vector3(0f, 0.05f, -0.27f), new Vector3(0.35f, 0.3f, 0.02f), Vector3.zero, M(new Color(0.85f, 0.75f, 0.1f)));
                    P(t, PrimitiveType.Cube, new Vector3(0.3f, 0.35f, 0f), new Vector3(0.06f, 0.35f, 0.15f), Vector3.zero, M(new Color(0.85f, 0.85f, 0.8f)));
                    break;
                case "MysteryMeat":
                    P(t, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.9f, 0.35f, 0.6f), new Vector3(0, 20, 0), M(new Color(0.6f, 0.12f, 0.12f)));
                    P(t, PrimitiveType.Sphere, new Vector3(0.25f, 0.1f, 0.05f), new Vector3(0.35f, 0.2f, 0.3f), Vector3.zero, M(new Color(0.9f, 0.82f, 0.7f)));
                    break;
                case "SearedMeat":
                    P(t, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.9f, 0.3f, 0.6f), new Vector3(0, 20, 0), M(new Color(0.42f, 0.22f, 0.1f)));
                    for (int i = -1; i <= 1; i++)
                        P(t, PrimitiveType.Cube, new Vector3(i * 0.18f, 0.15f, 0f), new Vector3(0.04f, 0.02f, 0.5f), new Vector3(0, 20 + 30, 0), dark);
                    break;
                case "DirtyWater":
                case "CleanWater":
                    bool clean = item.name.Contains("Clean");
                    var glass = M(clean ? new Color(0.45f, 0.7f, 0.95f) : new Color(0.45f, 0.4f, 0.28f), 0.2f);
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.35f, 0.45f, 0.35f), Vector3.zero, glass);
                    P(t, PrimitiveType.Cylinder, new Vector3(0f, 0.6f, 0f), new Vector3(0.14f, 0.15f, 0.14f), Vector3.zero, glass);
                    P(t, PrimitiveType.Cylinder, new Vector3(0f, 0.78f, 0f), new Vector3(0.16f, 0.04f, 0.16f), Vector3.zero, M(new Color(0.8f, 0.2f, 0.15f)));
                    break;
                case "BitterleafSeeds":
                case "PitcapSpores":
                    bool spores = item.name.Contains("Pitcap");
                    P(t, PrimitiveType.Cube, Vector3.zero, new Vector3(0.6f, 0.85f, 0.06f), new Vector3(0, 25, 8), paper);
                    P(t, PrimitiveType.Cube, new Vector3(0f, 0.12f, -0.035f), new Vector3(0.62f, 0.25f, 0.01f), new Vector3(0, 25, 8),
                        M(spores ? new Color(0.55f, 0.55f, 0.6f) : new Color(0.3f, 0.65f, 0.25f)));
                    break;
                case "Bitterleaf":
                    var leaf = M(new Color(0.3f, 0.62f, 0.22f));
                    for (int i = 0; i < 5; i++)
                        P(t, PrimitiveType.Sphere, new Vector3(Mathf.Cos(i * 1.26f) * 0.18f, i * 0.06f, Mathf.Sin(i * 1.26f) * 0.18f),
                            new Vector3(0.25f, 0.05f, 0.6f), new Vector3(-30, i * 72f, 0), leaf);
                    break;
                case "Pitcap":
                    var stem = M(new Color(0.85f, 0.83f, 0.76f));
                    var cap = M(new Color(0.62f, 0.6f, 0.58f));
                    for (int i = 0; i < 3; i++)
                    {
                        var o = new Vector3((i - 1) * 0.3f, 0f, (i % 2) * 0.15f);
                        float s = 1f - i * 0.2f;
                        P(t, PrimitiveType.Cylinder, o + new Vector3(0f, 0.2f * s, 0f), new Vector3(0.1f, 0.2f, 0.1f) * s, Vector3.zero, stem);
                        P(t, PrimitiveType.Sphere, o + new Vector3(0f, 0.42f * s, 0f), new Vector3(0.38f, 0.18f, 0.38f) * s, Vector3.zero, cap);
                    }
                    break;
                case "Grey":
                    P(t, PrimitiveType.Cube, Vector3.zero, new Vector3(0.8f, 0.01f, 0.8f), new Vector3(0, 30, 0), paper);
                    P(t, PrimitiveType.Sphere, new Vector3(0f, 0.08f, 0f), new Vector3(0.55f, 0.18f, 0.5f), Vector3.zero,
                        upgraded ? M(new Color(0.86f, 0.9f, 1f), 0f, 0.25f) : M(new Color(0.6f, 0.6f, 0.62f)));
                    break;
                case "Static":
                    var crystal = M(new Color(0.45f, 0.8f, 0.95f), 0.1f, 0.35f);
                    P(t, PrimitiveType.Cube, Vector3.zero, new Vector3(0.18f, 0.55f, 0.18f), new Vector3(10, 45, 15), crystal);
                    P(t, PrimitiveType.Cube, new Vector3(0.2f, -0.05f, 0.05f), new Vector3(0.14f, 0.38f, 0.14f), new Vector3(-20, 20, -25), crystal);
                    P(t, PrimitiveType.Cube, new Vector3(-0.18f, -0.08f, -0.02f), new Vector3(0.12f, 0.3f, 0.12f), new Vector3(5, 70, 30), crystal);
                    break;
                case "Smalltimer":
                    P(t, PrimitiveType.Cylinder, new Vector3(0.15f, 0.2f, 0f), new Vector3(0.12f, 0.4f, 0.12f), new Vector3(0, 0, 90), metal);
                    P(t, PrimitiveType.Cube, new Vector3(-0.2f, 0.17f, 0f), new Vector3(0.4f, 0.18f, 0.14f), Vector3.zero, rust);
                    P(t, PrimitiveType.Cube, new Vector3(-0.3f, -0.08f, 0f), new Vector3(0.16f, 0.4f, 0.13f), new Vector3(0, 0, -15), M(new Color(0.35f, 0.22f, 0.12f)));
                    P(t, PrimitiveType.Cube, new Vector3(-0.15f, 0.02f, 0f), new Vector3(0.03f, 0.12f, 0.04f), Vector3.zero, dark);
                    break;
                case "RebarClub":
                {
                    // Along the shaft, which is tilted 55 degrees: welded lump at the top, taped grip at the bottom.
                    var axis = Quaternion.Euler(0f, 0f, 55f) * Vector3.up;
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.09f, 0.7f, 0.09f), new Vector3(0, 0, 55), rust);
                    P(t, PrimitiveType.Cube, axis * 0.55f, new Vector3(0.2f, 0.28f, 0.17f) * (upgraded ? 1.5f : 1f), new Vector3(15, 20, 55), M(new Color(0.3f, 0.27f, 0.25f), 0.4f));
                    for (int i = 0; i < 5; i++)
                        P(t, PrimitiveType.Cylinder, axis * (-0.62f + i * 0.07f), new Vector3(0.11f, 0.025f, 0.11f), new Vector3(0, 0, 55), dark);
                    break;
                }
                case "ScrapPickaxe":
                {
                    // Handle tilted 35 degrees; the head lies across its top (along the handle's own x axis).
                    var axis = Quaternion.Euler(0f, 0f, 35f) * Vector3.up;
                    var across = Quaternion.Euler(0f, 0f, 35f) * Vector3.right;
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.08f, 0.6f, 0.08f), new Vector3(0, 0, 35), metal);
                    P(t, PrimitiveType.Cube, axis * 0.62f, new Vector3(0.9f, 0.13f, 0.15f), new Vector3(0, 0, 35), rust);
                    P(t, PrimitiveType.Cube, axis * 0.6f + across * 0.5f, new Vector3(0.14f, 0.09f, 0.11f), new Vector3(0, 0, 20), rust);
                    for (int i = 0; i < 3; i++)
                        P(t, PrimitiveType.Cylinder, axis * (-0.45f + i * 0.08f), new Vector3(0.1f, 0.025f, 0.1f), new Vector3(0, 0, 35), dark);
                    break;
                }
                case "ScrapShovel":
                {
                    // Handle tilted 35 degrees with a flat blade on the end, a grip bar across the top.
                    var axis = Quaternion.Euler(0f, 0f, 35f) * Vector3.up;
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.08f, 0.6f, 0.08f), new Vector3(0, 0, 35), metal);
                    P(t, PrimitiveType.Cube, axis * 0.78f, new Vector3(0.42f, 0.45f, 0.04f), new Vector3(0, 0, 35), rust);
                    P(t, PrimitiveType.Cube, -axis * 0.6f, new Vector3(0.3f, 0.07f, 0.07f), new Vector3(0, 0, 35), dark);
                    break;
                }
                case "Scattergun":
                    for (int i = -1; i <= 1; i += 2)
                        P(t, PrimitiveType.Cylinder, new Vector3(0.2f, 0.2f, i * 0.065f), new Vector3(0.12f, 0.45f, 0.12f), new Vector3(0, 0, 90), metal);
                    P(t, PrimitiveType.Cube, new Vector3(-0.3f, 0.18f, 0f), new Vector3(0.35f, 0.2f, 0.26f), Vector3.zero, rust);
                    P(t, PrimitiveType.Cube, new Vector3(-0.62f, 0.08f, 0f), new Vector3(0.38f, 0.16f, 0.16f), new Vector3(0, 0, -12), M(new Color(0.35f, 0.22f, 0.12f)));
                    P(t, PrimitiveType.Cube, new Vector3(-0.28f, -0.02f, 0f), new Vector3(0.03f, 0.12f, 0.04f), Vector3.zero, dark);
                    break;
                case "Ash":
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.75f, 0.05f, 0.75f), Vector3.zero, M(new Color(0.4f, 0.35f, 0.3f)));
                    P(t, PrimitiveType.Sphere, new Vector3(0f, 0.05f, 0f), new Vector3(0.5f, 0.2f, 0.45f), Vector3.zero, M(new Color(0.12f, 0.11f, 0.11f)));
                    P(t, PrimitiveType.Sphere, new Vector3(0.08f, 0.12f, 0.02f), new Vector3(0.18f, 0.1f, 0.16f), Vector3.zero, M(new Color(0.55f, 0.08f, 0.06f)));
                    break;
                case "RationBrick":
                    P(t, PrimitiveType.Cube, Vector3.zero, new Vector3(0.8f, 0.25f, 0.45f), new Vector3(0, 25, 0), M(new Color(0.62f, 0.6f, 0.55f), 0.3f));
                    P(t, PrimitiveType.Cube, new Vector3(0f, 0.13f, 0f), new Vector3(0.82f, 0.01f, 0.12f), new Vector3(0, 25, 0), M(new Color(0.3f, 0.45f, 0.75f)));
                    break;
                case "NeedleShiv":
                    P(t, PrimitiveType.Cylinder, new Vector3(0.18f, 0.12f, 0f), new Vector3(0.035f, 0.38f, 0.035f), new Vector3(0, 0, -60), metal);
                    P(t, PrimitiveType.Cube, new Vector3(-0.2f, -0.1f, 0f), new Vector3(0.09f, 0.32f, 0.09f), new Vector3(0, 0, -60), M(new Color(0.16f, 0.1f, 0.2f)));
                    P(t, PrimitiveType.Cube, new Vector3(-0.07f, -0.03f, 0f), new Vector3(0.2f, 0.03f, 0.1f), new Vector3(0, 0, -60), dark);
                    break;
                case "PipeRounds":
                    P(t, PrimitiveType.Cube, new Vector3(0f, -0.08f, 0f), new Vector3(0.7f, 0.18f, 0.45f), new Vector3(0, 20, 0), paper);
                    for (int i = 0; i < 5; i++)
                    {
                        var o = new Vector3(-0.24f + i * 0.12f, 0.12f, 0f);
                        P(t, PrimitiveType.Cylinder, o, new Vector3(0.08f, 0.14f, 0.08f), Vector3.zero, M(new Color(0.72f, 0.55f, 0.25f), 0.6f));
                        P(t, PrimitiveType.Sphere, o + new Vector3(0f, 0.14f, 0f), new Vector3(0.08f, 0.08f, 0.08f), Vector3.zero, metal);
                    }
                    break;
                case "GreensBroth":
                case "PitStew":
                    bool stew = item.name.Contains("Stew");
                    P(t, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.8f, 0.14f, 0.8f), Vector3.zero, metal);
                    P(t, PrimitiveType.Cylinder, new Vector3(0f, 0.1f, 0f), new Vector3(0.7f, 0.05f, 0.7f), Vector3.zero,
                        M(stew ? new Color(0.45f, 0.25f, 0.1f) : new Color(0.35f, 0.55f, 0.2f)));
                    if (stew)
                        for (int i = 0; i < (upgraded ? 7 : 4); i++)
                            P(t, PrimitiveType.Sphere, new Vector3(Mathf.Cos(i * 1.6f) * (0.12f + i * 0.02f), 0.16f, Mathf.Sin(i * 1.6f) * (0.12f + i * 0.02f)), Vector3.one * 0.12f, Vector3.zero,
                                M(i % 2 == 0 ? new Color(0.5f, 0.18f, 0.12f) : new Color(0.3f, 0.6f, 0.22f)));
                    break;
                default:
                    if (item.IsPlaceable)
                    {
                        var station = (GameObject)PrefabUtility.InstantiatePrefab(item.PlaceablePrefab);
                        PrefabUtility.UnpackPrefabInstance(station, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                        foreach (var l in station.GetComponentsInChildren<Light>()) l.enabled = false;
                        station.transform.SetParent(t, false);
                        break;
                    }
                    Object.DestroyImmediate(root);
                    return null;
            }
            return root;
        }
    }
}
