using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// Keeps piles of corpses (<see cref="HarvestNode"/>) dumped deep in the city's small alleys, at the closed
    /// ends of dead ends, to be dug through with a shovel. They're spread across the map, kept clear of NPC
    /// doorways, and new bodies pile up on the in-game clock after one is cleared.
    /// </summary>
    public class CorpsePileSpawner : MonoBehaviour
    {
        [SerializeField] private PittGreybox greybox;
        [Tooltip("Most piles in the city at once.")]
        [SerializeField, Min(0)] private int maxPiles = 20;
        [Tooltip("In-game minutes for a new pile to build up after one is cleared.")]
        [SerializeField, Min(1)] private int minutesPerPile = 60;
        [Tooltip("Piles keep at least this far apart.")]
        [SerializeField, Min(0f)] private float pileSpacing = 20f;
        [Tooltip("Keep clear of NPC doorways, so nobody walks into a pile.")]
        [SerializeField, Min(0f)] private float doorwayClearance = 3f;

        [Header("Yield")]
        [SerializeField] private ItemDefinition meat;
        [Tooltip("Turned out of pockets now and then.")]
        [SerializeField] private ItemDefinition pocketA;
        [SerializeField] private ItemDefinition pocketB;
        [Tooltip("Thrown over the pile.")]
        [SerializeField] private Material tarp;

        private readonly List<HarvestNode> piles = new();
        private int minutesUntilPile;

        public int Count
        {
            get
            {
                piles.RemoveAll(n => n == null);
                return piles.Count;
            }
        }

        private void OnEnable()
        {
            // The city lives in the World scene; no reference can reach across.
            if (greybox == null) greybox = PittGreybox.Current;
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            OnDisable();
            OnEnable();
            for (int i = 0; i < maxPiles; i++) SpawnPile();
            minutesUntilPile = minutesPerPile;
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private void OnMinute(int _)
        {
            if (--minutesUntilPile > 0) return;
            minutesUntilPile = minutesPerPile;
            if (Count < maxPiles) SpawnPile();
        }

        /// <summary>A new pile at a free dead end, away from the others. No free spot, no pile.</summary>
        private void SpawnPile()
        {
            if (greybox == null || greybox.alleySpots.Count == 0) return;
            piles.RemoveAll(n => n == null);
            Physics.SyncTransforms();
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var spot = greybox.alleySpots[Random.Range(0, greybox.alleySpots.Count)];
                if (!Fits(spot, out var position, out float yaw)) continue;
                SpawnPileAt(position, yaw, 1f);
                return;
            }
        }

        private bool Fits(PittGreybox.AlleySpot spot, out Vector3 position, out float yaw)
        {
            // Lengthways along the alley, its far end against the wall.
            var outward = new Vector3(spot.outward.x, 0f, spot.outward.y);
            position = spot.position + outward * 0.2f;
            yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;
            if (spot.width < 1.1f) return false;

            var flat = new Vector2(position.x, position.z);
            foreach (var n in piles)
            {
                var o = n.transform.position;
                if (Vector2.Distance(flat, new Vector2(o.x, o.z)) < pileSpacing) return false;
            }
            foreach (var d in greybox.doorways)
                if (Vector2.Distance(flat, new Vector2(d.position.x, d.position.z)) < doorwayClearance) return false;

            // Nothing already lying there (trash, crates, fences).
            var half = new Vector3(Mathf.Min(0.45f, spot.width / 2f - 0.1f), 0.45f, 0.85f);
            return !Physics.CheckBox(position + Vector3.up * 0.6f, half, Quaternion.Euler(0f, yaw, 0f), ~0, QueryTriggerInteraction.Ignore);
        }

        private void SpawnPileAt(Vector3 position, float yaw, float fraction)
        {
            var node = HarvestNode.BuildCorpsePile(transform, position, yaw, tarp != null ? tarp : greybox.rust);
            node.Setup(ToolType.Shovel, meat, pocketA, pocketB, fraction);
            piles.Add(node);
        }

        public CorpsePileSave Capture()
        {
            var save = new CorpsePileSave { minutesUntilPile = minutesUntilPile };
            // Piles cleared this frame are still around until the frame ends; leave them out.
            piles.RemoveAll(n => n == null || n.Health <= 0f);
            foreach (var n in piles)
                save.piles.Add(new ScrapNodeSave { position = n.transform.position, yaw = n.transform.eulerAngles.y, fraction = n.Fraction });
            return save;
        }

        /// <summary>Replaces the piles with the saved ones. Saves from before piles existed keep the fresh ones.</summary>
        public void Restore(CorpsePileSave save)
        {
            if (save == null || (save.piles.Count == 0 && save.minutesUntilPile <= 0)) return;
            foreach (var n in piles)
                if (n != null) Destroy(n.gameObject);
            piles.Clear();
            minutesUntilPile = Mathf.Max(1, save.minutesUntilPile);
            foreach (var n in save.piles)
                if (n.fraction > 0f && OnSpot(n.position, out var position)) SpawnPileAt(position, n.yaw, n.fraction);
        }

        /// <summary>
        /// Where a saved pile lies now: at its dead end, if that's still one. A save from before the city was
        /// regenerated can name places that are inside buildings now; those piles are dropped.
        /// </summary>
        private bool OnSpot(Vector3 saved, out Vector3 position)
        {
            position = saved;
            if (greybox == null) return true;
            foreach (var spot in greybox.alleySpots)
            {
                var at = spot.position + new Vector3(spot.outward.x, 0f, spot.outward.y) * 0.2f;
                if (Vector2.Distance(new Vector2(at.x, at.z), new Vector2(saved.x, saved.z)) > 1f) continue;
                position = at;
                return true;
            }
            return false;
        }
    }
}
