using ThePitt.Items;
using ThePitt.Production;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// Makes the items, upgraded stations and recipes that skill milestones teach (see Progression.Skills).
    /// Safe to run again: anything already there is updated in place, nothing is duplicated.
    /// </summary>
    public static class SkillContentBuilder
    {
        private const string Items = "Assets/_Project/Items";
        private const string Recipes = "Assets/_Project/Recipes";
        private const string Stations = "Assets/_Project/Prefabs/Stations";
        private const string Materials = "Assets/_Project/Materials";

        [MenuItem("The Pitt/Build Skill Milestone Content")]
        public static void Build()
        {
            // Tools, weapons, food and drugs.
            var pickaxe = CopyItem("ScrapPickaxe", "ReinforcedPickaxe", so =>
            {
                Set(so, "displayName", "Reinforced Pickaxe");
                Set(so, "description", "A scrap pickaxe capped with forged slag. Tears into scrap heaps far faster.");
                Set(so, "gatherPower", 18f);
                Set(so, "damage", 16f);
                Set(so, "baseValue", 60);
            });
            var shovel = CopyItem("ScrapShovel", "ReinforcedShovel", so =>
            {
                Set(so, "displayName", "Reinforced Shovel");
                Set(so, "description", "A scrap shovel with a slag-steel edge. Cuts through corpse piles far faster.");
                Set(so, "gatherPower", 18f);
                Set(so, "damage", 14f);
                Set(so, "baseValue", 60);
            });
            var club = CopyItem("RebarClub", "HeavyRebarClub", so =>
            {
                Set(so, "displayName", "Heavy Rebar Club");
                Set(so, "description", "Rebar with a lump of slag welded to the end. Slow to stop once it's swinging.");
                Set(so, "damage", 33f);
                Set(so, "baseValue", 50);
            });
            var stew = CopyItem("PitStew", "HeartyStew", so =>
            {
                Set(so, "displayName", "Hearty Stew");
                Set(so, "description", "Thick with meat and pitcap. Fills you up for hours, and buyers pay well for it.");
                Set(so, "nutrition", 80f);
                Set(so, "hydration", 20f);
                Set(so, "healthEffect", 12f);
                Set(so, "baseValue", 70);
            });
            var greyPrime = CopyItem("Grey", "GreyPrime", so =>
            {
                Set(so, "displayName", "Grey Prime");
                Set(so, "description", "Grey cut cleaner. A longer, stronger high that sells for much more.");
                Set(so, "healthEffect", 25f);
                Set(so, "effectMinutes", 150);
                Set(so, "baseValue", 48);
            });

            // Upgraded stations: the same station, faster, with steel plating and an amber stripe to tell them apart.
            var forge = UpgradedStation("Forge", "ReinforcedForge", "Reinforced Forge", "StationForge", "StationReinforcedForge",
                "A forge plated in steel with a hotter fire. Works half again as fast.", 1.5f, 0f, 0, 90);
            var stove = UpgradedStation("Stove", "RangeStove", "Range Stove", "StationStove", "StationRangeStove",
                "A big range with more burners. Cooks half again as fast.", 1.5f, 0f, 0, 70);
            var lab = UpgradedStation("DrugLab", "RefinedDrugLab", "Refined Drug Lab", "StationDrugLab", "StationRefinedDrugLab",
                "Cleaner glass and better seals. Works half again as fast.", 1.5f, 0f, 0, 100);
            var tended = UpgradedStation("GrowVat", "TendedGrowVat", "Tended Grow Vat", "StationGrowVat", "StationTendedGrowVat",
                "A grow vat with a drip feed and reflectors. Crops grow 30% faster.", 0f, 1.3f, 0, 70);
            var hydro = UpgradedStation("GrowVat", "HydroVat", "Hydro Vat", "StationGrowVat", "StationHydroVat",
                "Circulating nutrient water and a full canopy. Crops grow 60% faster, with one more on every harvest.", 0f, 1.6f, 1, 120);

            var slag = Item("SlagIngot");
            var spring = Item("Spring");
            var barrel = Item("PipeBarrel");
            var chems = Item("Chemicals");

            Recipe("ReinforcedPickaxe", "Reinforced Pickaxe", "Caps your scrap pickaxe with forged slag.", CraftingStation.Workbench, pickaxe, 1, 45,
                (Item("ScrapPickaxe"), 1), (slag, 3));
            Recipe("ReinforcedShovel", "Reinforced Shovel", "Edges your scrap shovel with slag steel.", CraftingStation.Workbench, shovel, 1, 45,
                (Item("ScrapShovel"), 1), (slag, 3));
            Recipe("HeavyRebarClub", "Heavy Rebar Club", "Welds a lump of slag to a rebar club.", CraftingStation.Workbench, club, 1, 35,
                (Item("RebarClub"), 1), (slag, 2));
            Recipe("HeartyStew", "Hearty Stew", "Meat, bitterleaf and pitcap, stewed down slow.", CraftingStation.Stove, stew, 2, 60,
                (Item("MysteryMeat"), 2), (Item("Bitterleaf"), 2), (Item("Pitcap"), 1), (Item("CleanWater"), 1));
            Recipe("GreyPrime", "Grey Prime", "Pitcap and chemicals, cut cleaner than street Grey.", CraftingStation.ChemLab, greyPrime, 3, 75,
                (Item("Pitcap"), 4), (chems, 2));
            Recipe("BuildReinforcedForge", "Reinforced Forge", "Plates a forge in steel and stokes it hotter.", CraftingStation.Workbench, forge, 1, 90,
                (Item("StationForge"), 1), (slag, 6), (spring, 5));
            Recipe("BuildRangeStove", "Range Stove", "Turns a stove into a proper range.", CraftingStation.Workbench, stove, 1, 60,
                (Item("StationStove"), 1), (slag, 4), (barrel, 5));
            Recipe("BuildRefinedDrugLab", "Refined Drug Lab", "Refits a drug lab with better glass and seals.", CraftingStation.Workbench, lab, 1, 75,
                (Item("StationDrugLab"), 1), (slag, 5), (chems, 3));
            Recipe("BuildTendedGrowVat", "Tended Grow Vat", "Adds a drip feed and reflectors to a grow vat.", CraftingStation.Workbench, tended, 1, 45,
                (Item("StationGrowVat"), 1), (slag, 2), (spring, 3));
            Recipe("BuildHydroVat", "Hydro Vat", "Rebuilds a tended vat with circulating nutrient water.", CraftingStation.Workbench, hydro, 1, 60,
                (Item("StationTendedGrowVat"), 1), (slag, 4), (barrel, 5));

            AssetDatabase.SaveAssets();
            Debug.Log("Skill milestone content built.");
        }

        // ---------------------------------------------------------------- items

        internal static ItemDefinition Item(string key) => AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{Items}/Item_{key}.asset");

        private static ItemDefinition CopyItem(string from, string to, System.Action<SerializedObject> change)
        {
            string path = $"{Items}/Item_{to}.asset";
            if (AssetDatabase.LoadAssetAtPath<ItemDefinition>(path) == null) AssetDatabase.CopyAsset($"{Items}/Item_{from}.asset", path);
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            var so = new SerializedObject(item);
            change(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            return item;
        }

        internal static void Set(SerializedObject so, string field, object value)
        {
            var p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogWarning($"SkillContentBuilder: no field '{field}' on {so.targetObject.name}.");
                return;
            }
            switch (value)
            {
                case string s: p.stringValue = s; break;
                case float f: p.floatValue = f; break;
                case int i: p.intValue = i; break;
                case Object o: p.objectReferenceValue = o; break;
                case Vector3Int v: p.vector3IntValue = v; break;
            }
        }

        // ---------------------------------------------------------------- stations

        private static ItemDefinition UpgradedStation(string fromPrefab, string toPrefab, string name, string fromItem, string toItem,
            string description, float benchSpeed, float growthSpeed, int bonusYield, int value)
        {
            string prefabPath = $"{Stations}/{toPrefab}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) AssetDatabase.CopyAsset($"{Stations}/{fromPrefab}.prefab", prefabPath);
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            root.name = toPrefab;

            var bench = root.GetComponentInChildren<CraftingBench>(true);
            if (bench != null)
            {
                var so = new SerializedObject(bench);
                Set(so, "benchName", name);
                Set(so, "speed", benchSpeed);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            var planter = root.GetComponentInChildren<HydroponicPlanter>(true);
            if (planter != null)
            {
                var so = new SerializedObject(planter);
                Set(so, "growthSpeed", growthSpeed);
                Set(so, "bonusYield", bonusYield);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Plate(root);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var source = Item(fromItem);
            return CopyItem(fromItem, toItem, so =>
            {
                Set(so, "displayName", name);
                Set(so, "description", description);
                Set(so, "placeablePrefab", prefab);
                Set(so, "baseValue", value);
                // A bigger station draws a little more power.
                var draw = source != null ? source.UtilityDraw : Vector3Int.zero;
                Set(so, "utilityDraw", draw + new Vector3Int(0, 1, 0));
            });
        }

        /// <summary>Steel plating round the base and an amber stripe: how an upgraded station looks different.</summary>
        private static void Plate(GameObject root)
        {
            var old = root.transform.Find("Upgrade");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) bounds = r.bounds;
                else bounds.Encapsulate(r.bounds);
                any = true;
            }
            if (!any) return;

            var group = new GameObject("Upgrade").transform;
            group.SetParent(root.transform, false);
            var steel = Material("Station_UpgradePlate", new Color(0.32f, 0.34f, 0.37f), 0.8f, 0f);
            var stripe = Material("Station_UpgradeStripe", new Color(1f, 0.65f, 0.2f), 0f, 2.5f);
            var local = root.transform.InverseTransformPoint(bounds.center);
            var size = bounds.size;
            float baseY = root.transform.InverseTransformPoint(bounds.min).y;
            Piece(group, "Plate", new Vector3(local.x, baseY + 0.12f, local.z), new Vector3(size.x + 0.06f, 0.24f, size.z + 0.06f), steel);
            Piece(group, "Stripe", new Vector3(local.x, baseY + 0.27f, local.z), new Vector3(size.x + 0.07f, 0.04f, size.z + 0.07f), stripe);
        }

        private static void Piece(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        internal static Material Material(string name, Color colour, float metallic, float emission)
        {
            string path = $"{Materials}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.color = colour;
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", 0.45f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", colour * emission);
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        // ---------------------------------------------------------------- recipes

        internal static RecipeDefinition Recipe(string key, string name, string description, CraftingStation station, ItemDefinition output, int amount,
            int minutes, params (ItemDefinition item, int amount)[] ingredients)
        {
            string path = $"{Recipes}/Recipe_{key}.asset";
            var recipe = AssetDatabase.LoadAssetAtPath<RecipeDefinition>(path);
            if (recipe == null)
            {
                recipe = ScriptableObject.CreateInstance<RecipeDefinition>();
                AssetDatabase.CreateAsset(recipe, path);
            }
            var so = new SerializedObject(recipe);
            Set(so, "displayName", name);
            Set(so, "description", description);
            so.FindProperty("station").enumValueIndex = (int)station;
            Set(so, "output", output);
            Set(so, "outputAmount", amount);
            Set(so, "craftMinutes", minutes);
            var list = so.FindProperty("ingredients");
            list.arraySize = ingredients.Length;
            for (int i = 0; i < ingredients.Length; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("item").objectReferenceValue = ingredients[i].item;
                e.FindPropertyRelative("amount").intValue = ingredients[i].amount;
                if (ingredients[i].item == null) Debug.LogWarning($"SkillContentBuilder: missing ingredient {i} for {key}.");
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(recipe);
            return recipe;
        }
    }
}
