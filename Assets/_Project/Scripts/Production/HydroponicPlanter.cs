using ThePitt.Core;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// Plant a seed, keep it watered and lit, harvest produce whose quality reflects
    /// how well its needs were met over the whole grow. The crop's own model grows in the soil, a ring floats
    /// over it with the crop and how far along it is, and a gauge set into the front of the base shows the tank
    /// (blue for the share that's full). A tank of nothing but clean water grows 10% faster; any dirty water in
    /// it loses that until it runs dry.
    /// </summary>
    public class HydroponicPlanter : MonoBehaviour, IInteractable, IPickupBlocker, ISaveState
    {
        private enum State { Empty, Growing, Ready }

        [Tooltip("Plants this planter accepts: the choices on its seed menu.")]
        [SerializeField] private PlantDefinition[] acceptedPlants;
        [Tooltip("Item poured in to refill the tank.")]
        [SerializeField] private ItemDefinition waterItem;
        [SerializeField, Min(1f)] private float waterPerItem = 50f;
        [Tooltip("Clean water: one fills the tank.")]
        [SerializeField] private ItemDefinition cleanWaterItem;
        [Tooltip("Growth speed with only clean water in the tank (not shown to the player).")]
        [SerializeField, Min(1f)] private float cleanWaterBonus = 1.1f;
        [Tooltip("Where the progress ring floats. Found by name (\"StatusSpot\") if empty; otherwise over the top.")]
        [SerializeField] private Transform statusSpot;
        [SerializeField, Min(1f)] private float tankCapacity = 100f;
        [Tooltip("How fast plants grow here: above 1 for an upgraded vat.")]
        [SerializeField, Min(0.1f)] private float growthSpeed = 1f;
        [Tooltip("Extra crops on every harvest (an upgraded vat).")]
        [SerializeField, Min(0)] private int bonusYield;
        [SerializeField] private GrowLight growLight;
        [Tooltip("Light the plant gets with the grow light off, during the day / at night.")]
        [SerializeField, Range(0f, 1f)] private float ambientDayLight = 0.3f;
        [SerializeField, Range(0f, 1f)] private float ambientNightLight = 0f;

        [Header("Visuals")]
        [SerializeField] private Transform plantVisual;
        [SerializeField] private Renderer plantRenderer;
        [SerializeField] private Color healthyColor = new Color(0.35f, 0.7f, 0.25f);
        [SerializeField] private Color witheredColor = new Color(0.45f, 0.38f, 0.2f);
        [SerializeField] private Color readyColor = new Color(0.55f, 0.85f, 0.3f);
        [Tooltip("A struggling crop's model is tinted toward this.")]
        [SerializeField] private Color witheredTint = new Color(0.7f, 0.58f, 0.38f);
        [Tooltip("The crop's model at full size, longest side in metres; one at each of these points along the soil.")]
        [SerializeField, Min(0.05f)] private float cropSize = 0.45f;
        [SerializeField] private float[] cropSpots = { -0.45f, 0f, 0.45f };

        [Header("Water gauge")]
        [Tooltip("Centre of the gauge on the front of the base, and its size (local).")]
        [SerializeField] private Vector3 gaugeCentre = new(0f, 0.55f, 0.45f);
        [SerializeField] private Vector2 gaugeSize = new(1.2f, 0.12f);

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private State state;
        private PlantDefinition plant;
        private float grownHours;
        private float water;
        /// <summary>How much of what's in the tank is dirty water (it's used up in proportion).</summary>
        private float dirtyWater;
        private UI.StationStatusUI status;
        private PlantDefinition shownCrop;
        private readonly System.Collections.Generic.List<GameObject> crops = new();
        private readonly System.Collections.Generic.List<Renderer> cropRenderers = new();
        private Transform gaugeFill;
        private Renderer gaugeFillRenderer;
        private static Material gaugeDark, gaugeTrack, gaugeWater;
        private float qualityTotal;
        private int qualitySamples;
        private int litSamples;
        private float lastSample = 1f;
        private MaterialPropertyBlock block;

        public float Water => water;
        public float Progress => plant == null ? 0f : Mathf.Clamp01(grownHours / plant.GrowHours);
        /// <summary>Average conditions score so far, 0-1.</summary>
        public float QualityScore => qualitySamples == 0 ? 1f : qualityTotal / qualitySamples;
        /// <summary>Share of the grow so far spent lit.</summary>
        public float LitShare => qualitySamples == 0 ? 0f : litSamples / (float)qualitySamples;
        private bool TooLit => plant != null && plant.TooLit(LitShare);

        /// <summary>What it would come out as now: by its conditions, but no better than Standard if it got too much light.</summary>
        public ItemQuality Quality
        {
            get
            {
                var q = ItemQualityExtensions.FromScore(QualityScore);
                return TooLit && q > ItemQuality.Standard ? ItemQuality.Standard : q;
            }
        }

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            OnDisable();
            OnEnable();
            if (statusSpot == null) statusSpot = transform.Find("StatusSpot");
            status = UI.StationStatusUI.Create(transform, statusSpot != null ? statusSpot.localPosition : AboveTop());
            BuildGauge();
            UpdateVisual();
        }

        /// <summary>
        /// The water gauge: a dark surround with a grey channel set into it (so it reads as recessed into the
        /// base), and a blue bar along the channel from the left for how full the tank is.
        /// </summary>
        private void BuildGauge()
        {
            if (gaugeDark == null) gaugeDark = GaugeMaterial(new Color(0.06f, 0.06f, 0.07f), Color.black);
            if (gaugeTrack == null) gaugeTrack = GaugeMaterial(new Color(0.42f, 0.43f, 0.45f), Color.black);
            if (gaugeWater == null) gaugeWater = GaugeMaterial(new Color(0.2f, 0.55f, 1f), new Color(0.1f, 0.35f, 0.9f) * 1.5f);
            var root = new GameObject("WaterGauge").transform;
            root.SetParent(transform, false);
            root.localPosition = gaugeCentre;
            GaugePart(root, "Surround", new Vector3(gaugeSize.x + 0.06f, gaugeSize.y + 0.06f, 0.012f), 0.006f, gaugeDark);
            GaugePart(root, "Channel", new Vector3(gaugeSize.x, gaugeSize.y, 0.012f), 0.008f, gaugeTrack);
            gaugeFill = GaugePart(root, "Water", new Vector3(gaugeSize.x, gaugeSize.y, 0.012f), 0.01f, gaugeWater);
            gaugeFillRenderer = gaugeFill.GetComponent<Renderer>();
        }

        private static Transform GaugePart(Transform parent, string name, Vector3 size, float forward, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, forward);
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        private static Material GaugeMaterial(Color colour, Color emission)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = colour };
            m.SetFloat("_Smoothness", 0.4f);
            if (emission != Color.black)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
            }
            return m;
        }

        private void UpdateGauge()
        {
            if (gaugeFill == null) return;
            float f = Mathf.Clamp01(water / tankCapacity);
            gaugeFillRenderer.enabled = f > 0.005f;
            // Filling from the left end of the channel as seen from the front, which is the vat's +x end.
            gaugeFill.localScale = new Vector3(gaugeSize.x * f, gaugeSize.y, 0.012f);
            gaugeFill.localPosition = new Vector3(gaugeSize.x * (1f - f) / 2f, 0f, gaugeFill.localPosition.z);
        }

        /// <summary>Just over the vat's top, in its own space.</summary>
        private Vector3 AboveTop()
        {
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return Vector3.up * 2.8f;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            return transform.InverseTransformPoint(new Vector3(transform.position.x, bounds.max.y + 0.4f, transform.position.z));
        }

        private void Update()
        {
            if (status == null) return;
            bool show = state != State.Empty && plant != null;
            status.SetVisible(show);
            if (show) status.Set(plant.Produce != null ? plant.Produce.Icon : null, state == State.Ready ? 1f : Progress, UI.StationStatusUI.DiscColour);
            status.Tick();
        }

        /// <summary>The hidden boost for a tank of nothing but clean water.</summary>
        private float WaterBonus => water > 0f && dirtyWater <= 0.001f ? cleanWaterBonus : 1f;

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private float CurrentLight()
        {
            float lamp = growLight != null ? growLight.Level : 0f;
            bool night = TimeManager.Instance != null && TimeManager.Instance.IsNight;
            return Mathf.Max(lamp, night ? ambientNightLight : ambientDayLight);
        }

        private void OnMinute(int _)
        {
            if (state != State.Growing) return;
            const float hours = 1f / 60f;

            if (water > 0f)
            {
                float bonus = WaterBonus;
                float before = water;
                water = Mathf.Max(0f, water - plant.WaterPerHour * hours);
                // Dirty water goes in proportion: it's mixed in.
                dirtyWater = water <= 0f ? 0f : dirtyWater * (water / before);
                grownHours += hours * growthSpeed * bonus * Progression.Skills.Multiplier(Progression.Skill.Growing, Progression.Perk.Speed);
                lastSample = plant.LightScore(CurrentLight());
            }
            else
            {
                // Dry plants stop growing and their quality suffers.
                lastSample = 0f;
            }

            qualityTotal += lastSample;
            qualitySamples++;
            if (CurrentLight() > plant.LitAbove) litSamples++;

            if (grownHours >= plant.GrowHours) state = State.Ready;
            UpdateVisual();
        }

        /// <summary>Where seeds and water come from: the player's inventory, then storage in the same property.</summary>
        private System.Collections.Generic.List<Inventory> Sources(Inventory player) => StorageBox.Sources(player, transform.position);

        /// <summary>What can go in the tank: dirty water half fills it, clean water fills it.</summary>
        private System.Collections.Generic.IEnumerable<(ItemDefinition item, float amount)> Waters()
        {
            if (waterItem != null) yield return (waterItem, waterPerItem);
            if (cleanWaterItem != null) yield return (cleanWaterItem, tankCapacity);
        }

        private bool CanWater(PlayerInteractor interactor, ItemDefinition item) =>
            item != null && water < tankCapacity - 1f && ItemSources.Count(Sources(interactor.Inventory), item) > 0;

        private bool CanWater(PlayerInteractor interactor)
        {
            foreach (var (item, _) in Waters())
                if (CanWater(interactor, item)) return true;
            return false;
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            switch (state)
            {
                case State.Empty:
                    return $"Plant · Water {water / tankCapacity:P0}";

                case State.Growing:
                    // The ring over the vat shows the crop and its progress; the tank and any problems are spelt out.
                    string warning = water <= 0f ? " · DRY" : TooLit ? " · too much light" : lastSample < 0.5f ? " · poor light" : "";
                    string tank = $" · Water {water / tankCapacity:P0}";
                    return (CanWater(interactor) ? "Add water" : $"{plant.DisplayName} growing") + tank + warning;

                default:
                    return $"Harvest {plant.DisplayName} ({Quality})";
            }
        }

        public bool CanPickUp(out string reason)
        {
            reason = state == State.Empty ? null : state == State.Ready ? "harvest it first" : "something is growing in it";
            return reason == null;
        }

        public bool CanInteract(PlayerInteractor interactor) => state switch
        {
            State.Empty => RadialMenu.Instance != null,
            State.Growing => CanWater(interactor),
            _ => interactor.Inventory.SpaceFor(plant.Produce, Quality) > 0,
        };

        public void Interact(PlayerInteractor interactor)
        {
            var inventory = interactor.Inventory;
            switch (state)
            {
                case State.Empty:
                    OpenSeedMenu(interactor);
                    return;

                case State.Growing:
                    WaterFromHand(interactor);
                    break;

                case State.Ready:
                    Harvest(inventory);
                    break;
            }
            UpdateVisual();
        }

        /// <summary>Every seed this planter takes, greyed out where there are none to hand, and filling the tank.</summary>
        private void OpenSeedMenu(PlayerInteractor interactor)
        {
            var inventory = interactor.Inventory;
            var sources = Sources(inventory);
            var options = new System.Collections.Generic.List<RadialOption>();
            if (acceptedPlants != null)
                foreach (var p in acceptedPlants)
                {
                    if (p == null || p.Seed == null) continue;
                    var plant = p;
                    int carried = inventory.Count(p.Seed);
                    int stored = ItemSources.Count(sources, p.Seed) - carried;
                    options.Add(new RadialOption
                    {
                        label = p.Seed.DisplayName,
                        detail = stored > 0 ? $"{carried} carried · {stored} stored" : carried > 0 ? $"{carried} carried" : "None carried",
                        icon = p.Seed.Icon,
                        enabled = carried + stored > 0,
                        chosen = () =>
                        {
                            if (state != State.Empty || ItemSources.Count(sources, plant.Seed) <= 0) return;
                            Plant(plant, sources);
                            UpdateVisual();
                        },
                    });
                }
            AddWaterOptions(options, interactor, sources);
            RadialMenu.Instance.Open("PLANT", options, interactor);
        }

        private void Plant(PlantDefinition seed, System.Collections.Generic.IReadOnlyList<Inventory> sources)
        {
            ItemSources.Remove(sources, seed.Seed, 1);
            Progression.Skills.Add(Progression.Skill.Growing, 3);
            plant = seed;
            state = State.Growing;
            grownHours = 0f;
            qualityTotal = 0f;
            qualitySamples = 0;
            litSamples = 0;
            lastSample = 1f;
        }

        /// <summary>One option per kind of water, greyed out where there's none or the tank is full.</summary>
        private void AddWaterOptions(System.Collections.Generic.List<RadialOption> options, PlayerInteractor interactor,
            System.Collections.Generic.IReadOnlyList<Inventory> sources)
        {
            foreach (var (item, amount) in Waters())
            {
                var kind = item;
                float fill = amount;
                options.Add(new RadialOption
                {
                    label = fill >= tankCapacity ? $"Fill tank · {item.DisplayName}" : $"Top up · {item.DisplayName}",
                    detail = $"Tank {water / tankCapacity:P0} · +{Mathf.Min(1f, fill / tankCapacity):P0} each · {ItemSources.Count(sources, item)} to hand",
                    icon = item.Icon,
                    enabled = CanWater(interactor, item),
                    chosen = () =>
                    {
                        if (CanWater(interactor, kind)) AddWater(sources, kind, fill);
                        UpdateVisual();
                    },
                });
            }
        }

        /// <summary>Watering a growing plant: straight in with the only kind there is, or a choice of both.</summary>
        private void WaterFromHand(PlayerInteractor interactor)
        {
            var sources = Sources(interactor.Inventory);
            var usable = new System.Collections.Generic.List<(ItemDefinition item, float amount)>();
            foreach (var w in Waters())
                if (CanWater(interactor, w.item)) usable.Add(w);
            if (usable.Count == 0) return;
            if (usable.Count == 1 || RadialMenu.Instance == null)
            {
                AddWater(sources, usable[0].item, usable[0].amount);
                return;
            }
            var options = new System.Collections.Generic.List<RadialOption>();
            AddWaterOptions(options, interactor, sources);
            RadialMenu.Instance.Open("WATER", options, interactor);
        }

        private void AddWater(System.Collections.Generic.IReadOnlyList<Inventory> sources, ItemDefinition item, float amount)
        {
            if (ItemSources.Remove(sources, item, 1) <= 0) return;
            float added = Mathf.Min(tankCapacity, water + amount) - water;
            water += added;
            if (item != cleanWaterItem) dirtyWater += added;
        }

        private void Harvest(Inventory inventory)
        {
            var quality = Quality;
            // Better-kept plants also yield more.
            int amount = Progression.Skills.RollYield(Progression.Skill.Growing,
                Mathf.RoundToInt(Mathf.Lerp(plant.MinYield, plant.MaxYield, QualityScore))) + bonusYield;
            int added = inventory.Add(plant.Produce, amount, quality);
            // Better-kept crops teach more: 3 xp each at Poor, up to 11 at Premium.
            Progression.Skills.Add(Progression.Skill.Growing, amount * (3 + 2 * (int)quality));
            if (added < amount)
                Debug.Log($"Inventory full: {amount - added} {plant.Produce.DisplayName} lost.");

            state = State.Empty;
            plant = null;
        }

        [System.Serializable]
        private class SaveState
        {
            public int state;
            public string plant;
            public float grownHours;
            public float water;
            /// <summary>-1 in saves from before clean water: all of it counts as dirty.</summary>
            public float dirtyWater = -1f;
            public float qualityTotal;
            public int qualitySamples;
            public int litSamples;
            public float lastSample;
        }

        public string CaptureState() => JsonUtility.ToJson(new SaveState
        {
            state = (int)state,
            plant = SaveRegistry.IdOf(plant),
            grownHours = grownHours,
            water = water,
            dirtyWater = dirtyWater,
            qualityTotal = qualityTotal,
            qualitySamples = qualitySamples,
            litSamples = litSamples,
            lastSample = lastSample,
        });

        public void RestoreState(string json)
        {
            var s = JsonUtility.FromJson<SaveState>(json);
            plant = SaveRegistry.Lookup<PlantDefinition>(s.plant);
            state = plant != null ? (State)s.state : State.Empty;
            grownHours = s.grownHours;
            water = s.water;
            dirtyWater = s.dirtyWater < 0f ? s.water : Mathf.Min(s.dirtyWater, s.water);
            qualityTotal = s.qualityTotal;
            qualitySamples = s.qualitySamples;
            litSamples = s.litSamples;
            lastSample = s.lastSample;
            UpdateVisual();
        }

        private void UpdateVisual()
        {
            UpdateGauge();
            if (plantVisual == null) return;
            bool hasPlant = state != State.Empty && plant != null;
            plantVisual.gameObject.SetActive(hasPlant);
            if (!hasPlant) return;

            float size = Mathf.Lerp(0.15f, 1f, Progress);
            plantVisual.localScale = new Vector3(size, size, size);
            ShowCrop();

            block ??= new MaterialPropertyBlock();
            if (cropRenderers.Count > 0)
            {
                // The crop's own colours, browning as it struggles.
                var tint = state == State.Ready ? Color.white : Color.Lerp(witheredTint, Color.white, lastSample);
                foreach (var r in cropRenderers)
                {
                    if (r == null) continue;
                    var materials = r.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null) continue;
                        block.Clear();
                        block.SetColor(BaseColor, materials[i].color * tint);
                        r.SetPropertyBlock(block, i);
                    }
                }
                return;
            }
            if (plantRenderer == null) return;
            Color color = state == State.Ready ? readyColor : Color.Lerp(witheredColor, healthyColor, lastSample);
            plantRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColor, color);
            plantRenderer.SetPropertyBlock(block);
        }

        /// <summary>
        /// The crop's own model, a few along the soil, in place of the plain green box (kept for crops with no
        /// model). Sits on the soil, so growing the visual scales it up from there.
        /// </summary>
        private void ShowCrop()
        {
            if (shownCrop == plant) return;
            shownCrop = plant;
            foreach (var c in crops)
                if (c != null) Destroy(c);
            crops.Clear();
            cropRenderers.Clear();
            var model = plant != null && plant.Produce != null ? plant.Produce.WorldModel : null;
            if (plantRenderer != null) plantRenderer.enabled = model == null;
            if (model == null) return;

            // Models are baked centred with their longest side 1 unit long.
            var filter = model.GetComponentInChildren<MeshFilter>();
            float halfHeight = filter != null ? filter.sharedMesh.bounds.extents.y * cropSize : cropSize / 2f;
            foreach (float x in cropSpots)
            {
                var crop = Instantiate(model, plantVisual, false);
                crop.name = "Crop";
                crops.Add(crop);
                foreach (var c in crop.GetComponentsInChildren<Collider>()) Destroy(c);
                crop.transform.localScale = Vector3.one * cropSize;
                crop.transform.localPosition = new Vector3(x, halfHeight, 0f);
                // Each turned a little differently, so they don't look stamped out.
                crop.transform.localRotation = Quaternion.Euler(0f, Mathf.Abs(x * 997f) % 360f, 0f);
                cropRenderers.AddRange(crop.GetComponentsInChildren<Renderer>());
            }
        }
    }
}
