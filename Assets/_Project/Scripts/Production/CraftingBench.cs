using System;
using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Core;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// A station that crafts in real time (paused with the game), working through a queue one item at a time.
    /// Queuing several takes all their ingredients up front and the same time per item as crafting them one by
    /// one. Jobs keep running while the player is away; finished items pile up on the bench (the last one made
    /// sits on it to see) and can be collected at any time, even while the rest of the queue is still being
    /// worked. While it works, a ring floats over it showing what's being made and how far along it is.
    /// </summary>
    public class CraftingBench : MonoBehaviour, IInteractable, IPickupBlocker, ISaveState
    {
        [SerializeField] private string benchName = "Workbench";
        [SerializeField] private CraftingStation station = CraftingStation.Workbench;
        [SerializeField] private RecipeDefinition[] recipes;
        [Tooltip("Optional: shown while a job is running.")]
        [SerializeField] private GameObject workingIndicator;
        [Tooltip("Most items waiting in the queue at once, counting the one being made.")]
        [SerializeField, Min(1)] private int maxQueue = 50;
        [Tooltip("How fast this bench works: 1.5 is an upgraded station, taking two thirds of the time.")]
        [SerializeField, Min(0.1f)] private float speed = 1f;
        [Tooltip("Where the last finished item sits until it's collected. Found by name (\"OutputSpot\") if empty.")]
        [SerializeField] private Transform outputSpot;
        [Tooltip("Where the progress ring floats. Found by name (\"StatusSpot\") if empty; otherwise over the top.")]
        [SerializeField] private Transform statusSpot;
        [Tooltip("The finished item's model, longest side in metres.")]
        [SerializeField, Min(0.05f)] private float outputModelSize = 0.35f;

        private struct Job
        {
            public RecipeDefinition recipe;
            public ItemQuality quality;
            /// <summary>What it took from the inventory, given back if it's cancelled.</summary>
            public List<ItemStack> ingredients;
        }

        private float nextWorkSound;
        // Set while a craft is being queued: instant recipes finish with the player standing at the bench.
        private bool queuing;

        private readonly List<Job> queue = new();
        private float secondsLeft;
        private readonly List<ItemStack> finished = new();
        private ItemDefinition lastMade;
        private ItemDefinition shownOutput;
        private GameObject outputModel;
        private UI.StationStatusUI status;

        public string BenchName => benchName;
        public CraftingStation Station => station;
        /// <summary>This station's own recipes plus any gang recipes the player has learned for it.</summary>
        public RecipeDefinition[] Recipes => RecipeBook.With(recipes, station);
        /// <summary>The recipe being worked right now.</summary>
        public RecipeDefinition Current => queue.Count > 0 ? queue[0].recipe : null;
        public bool IsBusy => queue.Count > 0;
        /// <summary>Items waiting behind the current one.</summary>
        public int Waiting => Mathf.Max(0, queue.Count - 1);
        /// <summary>How many more items can be queued.</summary>
        public int QueueSpace => Mathf.Max(0, maxQueue - queue.Count);
        public bool HasOutput => finished.Count > 0;
        public IReadOnlyList<ItemStack> Finished => finished;
        public float Progress => Current == null || SecondsFor(Current) <= 0f ? 0f
            : Mathf.Clamp01(1f - secondsLeft / SecondsFor(Current));
        public float Speed => speed;

        /// <summary>Real seconds one of these takes here: quicker on an upgraded bench and with skill in the work.</summary>
        public float SecondsFor(RecipeDefinition recipe) =>
            recipe == null || recipe.CraftSeconds <= 0f ? 0f
                : Mathf.Max(0.5f, recipe.CraftSeconds / (speed * Progression.Skills.SpeedMultiplier(recipe)));
        /// <summary>Real seconds until the whole queue is done.</summary>
        public float SecondsToClear
        {
            get
            {
                float total = secondsLeft;
                for (int i = 1; i < queue.Count; i++) total += SecondsFor(queue[i].recipe);
                return total;
            }
        }

        /// <summary>"45 s", "2m 05s", "1h 10m".</summary>
        public static string Duration(float seconds)
        {
            int s = Mathf.CeilToInt(seconds);
            if (s < 60) return $"{s} s";
            if (s < 3600) return $"{s / 60}m {s % 60:00}s";
            return $"{s / 3600}h {s / 60 % 60:00}m";
        }

        /// <summary>Where crafting here takes ingredients from: the player's inventory, then storage in the same property.</summary>
        public List<Inventory> Sources(Inventory player) => StorageBox.Sources(player, transform.position);

        /// <summary>Fired whenever a job is queued, advances or finishes, or output is collected.</summary>
        public event Action Changed;

        private void Start()
        {
            // Setting one up opens up its recipes.
            RecipeUnlocks.AddStation(station);
            if (outputSpot == null) outputSpot = transform.Find("OutputSpot");
            if (statusSpot == null) statusSpot = transform.Find("StatusSpot");
            status = UI.StationStatusUI.Create(transform, statusSpot != null ? statusSpot.localPosition : AboveTop());
            UpdateIndicator();
        }

        /// <summary>Just over the station's top, in its own space.</summary>
        private Vector3 AboveTop()
        {
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return Vector3.up * 2f;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            return transform.InverseTransformPoint(new Vector3(transform.position.x, bounds.max.y + 0.4f, transform.position.z));
        }

        private void Update()
        {
            if (IsBusy)
            {
                secondsLeft -= Time.deltaTime;
                if (secondsLeft <= 0f) FinishCurrent();
                else if (Time.time >= nextProgressEvent)
                {
                    // The menu's queue line, a few times a second rather than every frame.
                    nextProgressEvent = Time.time + 0.25f;
                    Changed?.Invoke();
                }
            }
            if (status != null)
            {
                status.SetVisible(IsBusy);
                if (IsBusy) status.Set(Current.Output != null ? Current.Output.Icon : null, Progress, UI.StationStatusUI.DiscColour);
                status.Tick();
            }

            // The sound of work while a job runs: hammering, sizzling or bubbling depending on the station.
            if (!IsBusy || Time.time < nextWorkSound) return;
            nextWorkSound = Time.time + UnityEngine.Random.Range(1.1f, 2.3f);
            PlayWorkSound();
        }

        private float nextProgressEvent;

        private void PlayWorkSound()
        {
            var lib = AudioManager.Library;
            if (lib == null) return;
            var at = transform.position + Vector3.up;
            switch (station)
            {
                case CraftingStation.Stove: AudioManager.Play(lib.craftSizzle, at, 0.5f, 14f); break;
                case CraftingStation.ChemLab: AudioManager.Play(lib.craftBubble, at, 0.55f, 14f); break;
                default: AudioManager.PlayRandom(lib.craftMetal, at, 0.6f, 18f); break;
            }
        }

        /// <summary>
        /// Takes the ingredients for up to <paramref name="count"/> items from the inventory (and storage in the
        /// same property) and queues them
        /// behind whatever is already being made. Returns how many were queued (fewer if the ingredients or
        /// the queue run out).
        /// </summary>
        public int StartCraft(RecipeDefinition recipe, Inventory inventory, int count = 1)
        {
            if (recipe == null || recipe.Station != station || count <= 0) return 0;
            bool wasIdle = !IsBusy;
            var sources = Sources(inventory);
            int queued = 0;
            while (queued < count && queue.Count < maxQueue)
            {
                var taken = new List<ItemStack>();
                if (!recipe.Consume(sources, out var quality, taken)) break;
                queue.Add(new Job { recipe = recipe, quality = quality, ingredients = taken });
                queued++;
            }
            if (queued == 0) return 0;
            if (wasIdle)
            {
                secondsLeft = SecondsFor(recipe);
                PlayWorkSound();
                nextWorkSound = Time.time + 1.5f;
                // Instant recipes don't wait for the clock.
                queuing = true;
                while (IsBusy && secondsLeft <= 0f) FinishCurrent();
                queuing = false;
            }
            UpdateIndicator();
            Changed?.Invoke();
            return queued;
        }

        /// <summary>
        /// Stops the bench: the item being made and everything queued behind it are dropped, and their
        /// ingredients go back to the inventory. Anything that doesn't fit waits on the bench to be collected.
        /// Finished items are untouched. Returns how many items were cancelled.
        /// </summary>
        public int CancelQueue(Inventory inventory)
        {
            int cancelled = queue.Count;
            if (cancelled == 0) return 0;
            foreach (var job in queue)
            {
                if (job.ingredients == null) continue;
                foreach (var stack in job.ingredients)
                {
                    int back = inventory != null ? inventory.Add(stack.item, stack.quantity, stack.quality) : 0;
                    if (back < stack.quantity) AddFinished(stack.item, stack.quantity - back, stack.quality);
                }
            }
            queue.Clear();
            secondsLeft = 0f;
            UpdateIndicator();
            Changed?.Invoke();
            return cancelled;
        }

        private void FinishCurrent()
        {
            var job = queue[0];
            queue.RemoveAt(0);
            AudioManager.Play(AudioManager.Library?.craftDone, transform.position + Vector3.up, 0.7f, 18f);
            AddFinished(job.recipe.Output, Progression.Skills.RollYield(job.recipe, job.recipe.OutputAmount), job.quality);
            if (job.recipe.Output != null) lastMade = job.recipe.Output;
            Progression.Skills.Crafted(job.recipe);
            // Any time overshot carries into the next one.
            secondsLeft = IsBusy ? secondsLeft + SecondsFor(queue[0].recipe) : 0f;
            UpdateIndicator();
            Changed?.Invoke();
            if (!IsBusy && !queuing) AnnounceDone();
        }

        /// <summary>The whole queue is through: a card in the corner saying where, and what's waiting.</summary>
        private void AnnounceDone()
        {
            var parts = new List<string>();
            foreach (var s in finished)
                parts.Add(s.quantity > 1 ? $"{s.item.DisplayName} ×{s.quantity}" : s.item.DisplayName);
            var property = Properties.Property.At(transform.position);
            string where = property != null ? $"{benchName} · {property.DisplayName}" : benchName;
            UnlockPopupUI.Notify("CRAFTING DONE", where, parts.Count > 0 ? $"Ready to collect: {string.Join(", ", parts)}" : null,
                new Color(0.55f, 0.85f, 0.45f), 8f);
        }

        private void AddFinished(ItemDefinition item, int amount, ItemQuality quality)
        {
            if (item == null || amount <= 0) return;
            if (!item.HasQuality) quality = ItemQuality.Standard;
            for (int i = 0; i < finished.Count; i++)
            {
                if (finished[i].item != item || finished[i].quality != quality) continue;
                var s = finished[i];
                s.quantity += amount;
                finished[i] = s;
                return;
            }
            finished.Add(new ItemStack(item, amount, quality));
        }

        /// <summary>Moves everything finished that fits into the inventory. Returns how many items were taken.</summary>
        public int Collect(Inventory inventory)
        {
            int total = 0;
            for (int i = finished.Count - 1; i >= 0; i--)
            {
                var s = finished[i];
                int taken = inventory.Add(s.item, s.quantity, s.quality);
                total += taken;
                s.quantity -= taken;
                if (s.quantity <= 0) finished.RemoveAt(i);
                else finished[i] = s;
            }
            if (total > 0)
            {
                UpdateIndicator();
                Changed?.Invoke();
            }
            return total;
        }

        private bool CanCollectAny(Inventory inventory)
        {
            foreach (var s in finished)
                if (inventory.SpaceFor(s.item, s.quality) > 0) return true;
            return false;
        }

        [Serializable]
        private class JobSave
        {
            public string recipe;
            public int quality;
            public List<StackSave> ingredients = new();
        }

        [Serializable]
        private class StackSave
        {
            public string item;
            public int quantity;
            public int quality;
        }

        [Serializable]
        private class SaveState
        {
            public List<JobSave> queue = new();
            public float secondsLeft = -1f;
            public string lastMade;
            // From before craft times were in real seconds.
            public int minutesLeft;
            public List<StackSave> finished = new();
            // From before queues: a single job and a single finished stack.
            public string recipe;
            public int quality;
            public string finishedItem;
            public int finishedQuantity;
            public int finishedQuality;
        }

        public string CaptureState()
        {
            var save = new SaveState { secondsLeft = secondsLeft, lastMade = SaveRegistry.IdOf(lastMade) };
            foreach (var j in queue)
            {
                var js = new JobSave { recipe = SaveRegistry.IdOf(j.recipe), quality = (int)j.quality };
                if (j.ingredients != null)
                    foreach (var i in j.ingredients) js.ingredients.Add(new StackSave { item = SaveRegistry.IdOf(i.item), quantity = i.quantity, quality = (int)i.quality });
                save.queue.Add(js);
            }
            foreach (var f in finished) save.finished.Add(new StackSave { item = SaveRegistry.IdOf(f.item), quantity = f.quantity, quality = (int)f.quality });
            return JsonUtility.ToJson(save);
        }

        public void RestoreState(string json)
        {
            var s = JsonUtility.FromJson<SaveState>(json);
            queue.Clear();
            finished.Clear();
            if (s.queue != null)
                foreach (var j in s.queue)
                {
                    var r = SaveRegistry.Lookup<RecipeDefinition>(j.recipe);
                    if (r == null) continue;
                    queue.Add(new Job { recipe = r, quality = (ItemQuality)j.quality, ingredients = Refund(r, j) });
                }
            if (s.finished != null)
                foreach (var f in s.finished)
                    AddFinished(SaveRegistry.Lookup<ItemDefinition>(f.item), f.quantity, (ItemQuality)f.quality);
            // Older saves.
            var old = SaveRegistry.Lookup<RecipeDefinition>(s.recipe);
            if (old != null && queue.Count == 0) queue.Add(new Job { recipe = old, quality = (ItemQuality)s.quality, ingredients = Refund(old, new JobSave { quality = s.quality }) });
            AddFinished(SaveRegistry.Lookup<ItemDefinition>(s.finishedItem), s.finishedQuantity, (ItemQuality)s.finishedQuality);

            lastMade = SaveRegistry.Lookup<ItemDefinition>(s.lastMade);
            // Older saves counted in-game minutes (0.625 s each).
            float left = s.secondsLeft >= 0f ? s.secondsLeft : s.minutesLeft * 0.625f;
            secondsLeft = IsBusy ? Mathf.Max(0.5f, left) : 0f;
            UpdateIndicator();
            Changed?.Invoke();
        }

        /// <summary>A saved job's ingredients; older saves didn't record them, so the recipe's at the job's quality.</summary>
        private static List<ItemStack> Refund(RecipeDefinition recipe, JobSave job)
        {
            var list = new List<ItemStack>();
            if (job.ingredients != null && job.ingredients.Count > 0)
            {
                foreach (var i in job.ingredients)
                {
                    var item = SaveRegistry.Lookup<ItemDefinition>(i.item);
                    if (item != null && i.quantity > 0) list.Add(new ItemStack(item, i.quantity, (ItemQuality)i.quality));
                }
                return list;
            }
            if (recipe.Ingredients != null)
                foreach (var i in recipe.Ingredients)
                    if (i.item != null) list.Add(new ItemStack(i.item, i.amount, i.item.HasQuality ? (ItemQuality)job.quality : ItemQuality.Standard));
            return list;
        }

        private void UpdateIndicator()
        {
            if (workingIndicator != null) workingIndicator.SetActive(IsBusy);
            ShowOutput();
        }

        /// <summary>The last thing made sits on the bench while anything's waiting to be collected.</summary>
        private void ShowOutput()
        {
            ItemDefinition show = null;
            if (finished.Count > 0)
            {
                show = finished[finished.Count - 1].item;
                foreach (var f in finished)
                    if (f.item == lastMade) show = lastMade;
            }
            if (show == shownOutput && (show == null || outputModel != null)) return;
            shownOutput = show;
            if (outputModel != null) Destroy(outputModel);
            outputModel = null;
            if (show == null || show.WorldModel == null) return;

            var spot = outputSpot != null ? outputSpot : transform;
            outputModel = Instantiate(show.WorldModel, spot, false);
            outputModel.name = "FinishedItem";
            foreach (var c in outputModel.GetComponentsInChildren<Collider>()) Destroy(c);
            // Models are baked centred with their longest side 1 unit long: scale it, then sit it on the spot.
            var lossy = spot.lossyScale;
            outputModel.transform.localScale = new Vector3(outputModelSize / Mathf.Max(0.0001f, lossy.x),
                outputModelSize / Mathf.Max(0.0001f, lossy.y), outputModelSize / Mathf.Max(0.0001f, lossy.z));
            var filter = outputModel.GetComponentInChildren<MeshFilter>();
            float halfHeight = filter != null ? filter.sharedMesh.bounds.extents.y * outputModelSize : 0.05f;
            outputModel.transform.position = spot.position + Vector3.up * halfHeight;
        }

        public bool CanPickUp(out string reason)
        {
            reason = HasOutput ? "collect what's on it first" : IsBusy ? "it's still working" : null;
            return reason == null;
        }

        /// <summary>"Spring 45% · 3 queued", or null when idle.</summary>
        public string WorkStatus()
        {
            if (!IsBusy) return null;
            string waiting = Waiting > 0 ? $" · {Waiting} queued" : "";
            return $"crafting {Current.DisplayName} {Progress:P0}{waiting}";
        }

        // The ring over the bench shows the work, so the prompt is just what pressing does.
        public string GetPrompt(PlayerInteractor interactor)
        {
            if (HasOutput)
            {
                var first = finished[0];
                string amount = first.quantity > 1 ? $" x{first.quantity}" : "";
                if (first.item.HasQuality) amount += $" ({first.quality})";
                string more = finished.Count > 1 ? $" +{finished.Count - 1} more" : "";
                return CanCollectAny(interactor.Inventory)
                    ? $"Collect {first.item.DisplayName}{amount}{more}"
                    : $"{first.item.DisplayName} ready · inventory full";
            }
            return $"Use {benchName}";
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            if (HasOutput) return CanCollectAny(interactor.Inventory);
            return CraftingUI.Instance != null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (HasOutput) Collect(interactor.Inventory);
            else CraftingUI.Instance.Open(this, interactor);
        }
    }
}
