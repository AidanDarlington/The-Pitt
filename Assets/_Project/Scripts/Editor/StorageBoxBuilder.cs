using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.Production;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// Makes the storage box: its prefab, item, icon and workbench recipe. Safe to run again: anything
    /// already there is updated in place, nothing is duplicated.
    /// </summary>
    public static class StorageBoxBuilder
    {
        private const string PrefabPath = "Assets/_Project/Prefabs/Stations/StorageBox.prefab";
        private const string ItemPath = "Assets/_Project/Items/Item_StorageBox.asset";
        private const string WorkbenchPath = "Assets/_Project/Prefabs/Stations/Workbench.prefab";
        private const int Slots = 6;

        [MenuItem("The Pitt/Build Storage Box")]
        public static void Build()
        {
            var prefab = BuildPrefab();

            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(ItemPath);
            if (item == null)
            {
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(item, ItemPath);
            }
            var so = new SerializedObject(item);
            SkillContentBuilder.Set(so, "displayName", "Storage Box");
            SkillContentBuilder.Set(so, "description", $"A slag-steel footlocker with a hasp. Holds {Slots} stacks.");
            so.FindProperty("category").enumValueIndex = (int)ItemCategory.Structure;
            SkillContentBuilder.Set(so, "maxStack", 3);
            SkillContentBuilder.Set(so, "baseValue", 20);
            so.FindProperty("hasQuality").boolValue = false;
            SkillContentBuilder.Set(so, "placeablePrefab", prefab);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(item);

            var recipe = SkillContentBuilder.Recipe("BuildStorageBox", "Storage Box", "Hammers scrap sheet round a slag-ingot frame into a lockable box.",
                CraftingStation.Workbench, item, 1, 30, (SkillContentBuilder.Item("SlagIngot"), 2), (SkillContentBuilder.Item("ScrapMetal"), 40));
            AddToWorkbench(recipe);

            AssetDatabase.SaveAssets();
            PittIconBaker.Bake(item);
            Debug.Log("Storage box built.");
        }

        private static GameObject BuildPrefab()
        {
            var root = new GameObject("StorageBox");
            var inventory = root.AddComponent<Inventory>();
            var inv = new SerializedObject(inventory);
            inv.FindProperty("slotCount").intValue = Slots;
            inv.FindProperty("slots").arraySize = Slots;
            inv.ApplyModifiedPropertiesWithoutUndo();
            root.AddComponent<StorageBox>();

            var paint = SkillContentBuilder.Material("StorageBox_Paint", new Color(0.24f, 0.27f, 0.22f), 0.55f, 0f);
            var steel = SkillContentBuilder.Material("StorageBox_Steel", new Color(0.32f, 0.3f, 0.29f), 0.75f, 0f);
            var rust = SkillContentBuilder.Material("StorageBox_Rust", new Color(0.5f, 0.27f, 0.14f), 0.3f, 0f);

            // Footlocker: a body, an overhanging lid, two straps round it, a hasp on the front and handles at the ends.
            Piece(root.transform, "Body", PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), new Vector3(0.9f, 0.46f, 0.55f), paint, true);
            Piece(root.transform, "Lid", PrimitiveType.Cube, new Vector3(0f, 0.52f, 0f), new Vector3(0.94f, 0.09f, 0.59f), paint, true);
            foreach (float x in new[] { -0.28f, 0.28f })
                Piece(root.transform, "Strap", PrimitiveType.Cube, new Vector3(x, 0.28f, 0f), new Vector3(0.07f, 0.56f, 0.61f), steel, false);
            Piece(root.transform, "Hasp", PrimitiveType.Cube, new Vector3(0f, 0.45f, -0.3f), new Vector3(0.1f, 0.14f, 0.03f), rust, false);
            foreach (float x in new[] { -0.47f, 0.47f })
                Piece(root.transform, "Handle", PrimitiveType.Cube, new Vector3(x, 0.36f, 0f), new Vector3(0.04f, 0.04f, 0.22f), steel, false);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void Piece(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, bool solid)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (!solid) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static void AddToWorkbench(RecipeDefinition recipe)
        {
            var root = PrefabUtility.LoadPrefabContents(WorkbenchPath);
            var bench = root.GetComponentInChildren<CraftingBench>(true);
            var so = new SerializedObject(bench);
            var list = so.FindProperty("recipes");
            bool has = false;
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == recipe) has = true;
            if (!has)
            {
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = recipe;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, WorkbenchPath);
            }
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
