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
    /// A station that crafts on the in-game clock, working through a queue one item at a time. Queuing
    /// several takes all their ingredients up front and the same time per item as crafting them one by one.
    /// Jobs keep running while the player is away; finished items pile up on the bench and can be collected
    /// at any time, even while the rest of the queue is still being worked.
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
        private int minutesLeft;
        private readonly List<ItemStack> finished = new();

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
        public float Progress => Current == null || MinutesFor(Current) == 0 ? 0f
            : Mathf.Clamp01(1f - minutesLeft / (float)MinutesFor(Current));
        public float Speed => speed;

        /// <summary>In-game minutes one of these takes here: quicker on an upgraded bench and with skill in the work.</summary>
        public int MinutesFor(RecipeDefinition recipe) =>
            recipe == null || recipe.CraftMinutes <= 0 ? 0
                : Mathf.Max(1, Mathf.CeilToInt(recipe.CraftMinutes / (speed * Progression.Skills.SpeedMultiplier(recipe))));
        /// <summary>In-game minutes until the whole queue is done.</summary>
        public int MinutesToClear
        {
            get
            {
                int total = minutesLeft;
                for (int i = 1; i < queue.Count; i++) total += MinutesFor(queue[i].recipe);
                return total;
            }
        }

        /// <summary>Fired whenever a job is queued, advances or finishes, or output is collected.</summary>
        public event Action Changed;

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            OnDisable();
            OnEnable();
            // Setting one up opens up its recipes.
            RecipeUnlocks.AddStation(station);
            UpdateIndicator();
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private void Update()
        {
            // The sound of work while a job runs: hammering, sizzling or bubbling depending on the station.
            if (!IsBusy || Time.time < nextWorkSound) return;
            nextWorkSound = Time.time + UnityEngine.Random.Range(1.1f, 2.3f);
            PlayWorkSound();
        }

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

        private void OnMinute(int _)
        {
            if (!IsBusy) return;
            if (--minutesLeft > 0)
            {
                Changed?.Invoke();
                return;
            }
            FinishCurrent();
        }

        /// <summary>
        /// Takes the ingredients for up to <paramref name="count"/> items from the inventory and queues them
        /// behind whatever is already being made. Returns how many were queued (fewer if the ingredients or
        /// the queue run out).
        /// </summary>
        public int StartCraft(RecipeDefinition recipe, Inventory inventory, int count = 1)
        {
            if (recipe == null || recipe.Station != station || count <= 0) return 0;
            bool wasIdle = !IsBusy;
            int queued = 0;
            while (queued < count && queue.Count < maxQueue)
            {
                var taken = new List<ItemStack>();
                if (!recipe.Consume(inventory, out var quality, taken)) break;
                queue.Add(new Job { recipe = recipe, quality = quality, ingredients = taken });
                queued++;
            }
            if (queued == 0) return 0;
            if (wasIdle)
            {
                minutesLeft = MinutesFor(recipe);
                PlayWorkSound();
                nextWorkSound = Time.time + 1.5f;
                // Instant recipes don't wait for the clock.
                queuing = true;
                while (IsBusy && minutesLeft <= 0) FinishCurrent();
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
            minutesLeft = 0;
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
            Progression.Skills.Crafted(job.recipe);
            minutesLeft = IsBusy ? MinutesFor(queue[0].recipe) : 0;
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
            if (total > 0) Changed?.Invoke();
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
            var save = new SaveState { minutesLeft = minutesLeft };
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

            minutesLeft = IsBusy ? Mathf.Max(1, s.minutesLeft) : 0;
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

        public string GetPrompt(PlayerInteractor interactor)
        {
            string working = IsBusy ? $" · {WorkStatus()}" : "";
            if (HasOutput)
            {
                var first = finished[0];
                string amount = first.quantity > 1 ? $" x{first.quantity}" : "";
                if (first.item.HasQuality) amount += $" ({first.quality})";
                string more = finished.Count > 1 ? $" +{finished.Count - 1} more" : "";
                return CanCollectAny(interactor.Inventory)
                    ? $"Collect {first.item.DisplayName}{amount}{more}{working}"
                    : $"{first.item.DisplayName} ready · inventory full{working}";
            }
            return $"Use {benchName}{working}";
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
