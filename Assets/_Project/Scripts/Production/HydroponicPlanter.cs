using ThePitt.Core;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// Plant a seed, keep it watered and lit, harvest produce whose quality reflects
    /// how well its needs were met over the whole grow.
    /// </summary>
    public class HydroponicPlanter : MonoBehaviour, IInteractable, IPickupBlocker, ISaveState
    {
        private enum State { Empty, Growing, Ready }

        [Tooltip("Plants this planter accepts: the choices on its seed menu.")]
        [SerializeField] private PlantDefinition[] acceptedPlants;
        [Tooltip("Item poured in to refill the tank.")]
        [SerializeField] private ItemDefinition waterItem;
        [SerializeField, Min(1f)] private float waterPerItem = 50f;
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

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private State state;
        private PlantDefinition plant;
        private float grownHours;
        private float water;
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
            UpdateVisual();
        }

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
                water = Mathf.Max(0f, water - plant.WaterPerHour * hours);
                grownHours += hours * growthSpeed * Progression.Skills.Multiplier(Progression.Skill.Growing, Progression.Perk.Speed);
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

        private bool CanWater(PlayerInteractor interactor) =>
            waterItem != null && water < tankCapacity - 1f && interactor.Inventory.Count(waterItem) > 0;

        public string GetPrompt(PlayerInteractor interactor)
        {
            switch (state)
            {
                case State.Empty:
                    return $"Plant · Water {water / tankCapacity:P0}";

                case State.Growing:
                    string status = $"{plant.DisplayName} {Progress:P0} · Water {water / tankCapacity:P0}";
                    if (grownHours > 0f) status += $" · {Quality}";
                    if (water <= 0f) status += " · DRY";
                    else if (TooLit) status += " · too much light";
                    else if (lastSample < 0.5f) status += " · poor light";
                    return CanWater(interactor) ? $"Add water — {status}" : status;

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
                    if (CanWater(interactor)) AddWater(inventory);
                    break;

                case State.Ready:
                    Harvest(inventory);
                    break;
            }
            UpdateVisual();
        }

        /// <summary>Every seed this planter takes, greyed out where none are carried, and filling the tank.</summary>
        private void OpenSeedMenu(PlayerInteractor interactor)
        {
            var inventory = interactor.Inventory;
            var options = new System.Collections.Generic.List<RadialOption>();
            if (acceptedPlants != null)
                foreach (var p in acceptedPlants)
                {
                    if (p == null || p.Seed == null) continue;
                    var plant = p;
                    int carried = inventory.Count(p.Seed);
                    options.Add(new RadialOption
                    {
                        label = p.Seed.DisplayName,
                        detail = carried > 0 ? $"{carried} carried" : "None carried",
                        icon = p.Seed.Icon,
                        enabled = carried > 0,
                        chosen = () =>
                        {
                            if (state != State.Empty || inventory.Count(plant.Seed) <= 0) return;
                            Plant(plant, inventory);
                            UpdateVisual();
                        },
                    });
                }
            if (waterItem != null)
                options.Add(new RadialOption
                {
                    label = "Fill tank",
                    detail = $"Tank {water / tankCapacity:P0} · {inventory.Count(waterItem)} {waterItem.DisplayName}",
                    icon = waterItem.Icon,
                    enabled = CanWater(interactor),
                    chosen = () =>
                    {
                        if (CanWater(interactor)) AddWater(inventory);
                    },
                });
            RadialMenu.Instance.Open("PLANT", options, interactor);
        }

        private void Plant(PlantDefinition seed, Inventory inventory)
        {
            inventory.Remove(seed.Seed, 1);
            Progression.Skills.Add(Progression.Skill.Growing, 3);
            plant = seed;
            state = State.Growing;
            grownHours = 0f;
            qualityTotal = 0f;
            qualitySamples = 0;
            litSamples = 0;
            lastSample = 1f;
        }

        private void AddWater(Inventory inventory)
        {
            if (inventory.Remove(waterItem, 1) > 0) water = Mathf.Min(tankCapacity, water + waterPerItem);
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
            qualityTotal = s.qualityTotal;
            qualitySamples = s.qualitySamples;
            litSamples = s.litSamples;
            lastSample = s.lastSample;
            UpdateVisual();
        }

        private void UpdateVisual()
        {
            if (plantVisual == null) return;
            bool hasPlant = state != State.Empty;
            plantVisual.gameObject.SetActive(hasPlant);
            if (!hasPlant) return;

            float size = Mathf.Lerp(0.15f, 1f, Progress);
            plantVisual.localScale = new Vector3(size, size, size);

            if (plantRenderer == null) return;
            Color color = state == State.Ready ? readyColor : Color.Lerp(witheredColor, healthyColor, lastSample);
            block ??= new MaterialPropertyBlock();
            plantRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColor, color);
            plantRenderer.SetPropertyBlock(block);
        }
    }
}
