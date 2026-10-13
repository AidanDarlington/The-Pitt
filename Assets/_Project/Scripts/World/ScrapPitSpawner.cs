using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// Keeps the scrap pit stocked: scrap washes down into it over time, appearing at random spots on
    /// the pit floor until a cap is reached. Picked-up scrap is replaced on the in-game clock. It also keeps
    /// a few heaps of scrap (<see cref="HarvestNode"/>) on the floor to be broken up with a pickaxe; worked-out
    /// heaps build up again over time.
    /// Uses the greybox's loot table and pit shape.
    /// </summary>
    public class ScrapPitSpawner : MonoBehaviour
    {
        [SerializeField] private PittGreybox greybox;
        [SerializeField] private GameObject pickupPrefab;
        [Tooltip("Most pickups lying in the pit at once.")]
        [SerializeField, Min(1)] private int maxPickups = 18;
        [Tooltip("How many are there when the game starts.")]
        [SerializeField, Min(0)] private int startingPickups = 12;
        [Tooltip("In-game minutes between new scrap appearing (while under the cap).")]
        [SerializeField, Min(1)] private int minutesPerSpawn = 20;
        [Tooltip("Keep spawns away from the pit wall.")]
        [SerializeField, Min(0f)] private float wallMargin = 3f;

        [Header("Scrap Heaps (mined with a pickaxe)")]
        [SerializeField, Min(0)] private int maxNodes = 6;
        [Tooltip("In-game minutes for a new heap to build up after one is broken.")]
        [SerializeField, Min(1)] private int minutesPerNode = 90;
        [Tooltip("Heaps keep at least this far apart.")]
        [SerializeField, Min(0f)] private float nodeSpacing = 6f;
        [SerializeField] private ItemDefinition nodeScrap;
        [SerializeField] private ItemDefinition nodeSpring;
        [SerializeField] private ItemDefinition nodePipe;

        private readonly List<GameObject> live = new();
        private readonly List<HarvestNode> nodes = new();
        private int minutesUntilNode;
        private readonly Dictionary<GameObject, int> lootOf = new();
        private int minutesUntilSpawn;

        public int LiveCount
        {
            get
            {
                live.RemoveAll(g => g == null);
                return live.Count;
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
            for (int i = 0; i < startingPickups; i++) Spawn();
            minutesUntilSpawn = minutesPerSpawn;
            for (int i = 0; i < maxNodes; i++) SpawnNode();
            minutesUntilNode = minutesPerNode;
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private void OnMinute(int _)
        {
            nodes.RemoveAll(n => n == null);
            if (--minutesUntilNode <= 0)
            {
                minutesUntilNode = minutesPerNode;
                if (nodes.Count < maxNodes) SpawnNode();
            }
            if (--minutesUntilSpawn > 0) return;
            minutesUntilSpawn = minutesPerSpawn;
            if (LiveCount < maxPickups) Spawn();
        }

        public void Spawn()
        {
            if (greybox == null || pickupPrefab == null || greybox.pitLoot == null || greybox.pitLoot.Length == 0) return;
            int loot = PickLoot();
            var entry = greybox.pitLoot[loot];
            if (entry.item == null) return;

            var p = RandomFloorPoint();
            SpawnAt(loot, new Vector3(p.x, greybox.HeightAt(p) + 0.2f, p.y), Random.Range(0f, 360f),
                Random.Range(entry.minQuantity, Mathf.Max(entry.minQuantity, entry.maxQuantity) + 1));
        }

        private void SpawnAt(int loot, Vector3 position, float yaw, int quantity)
        {
            var entry = greybox.pitLoot[loot];
            var go = Instantiate(pickupPrefab, position, Quaternion.Euler(0f, yaw, 0f), transform);
            go.name = "Scrap_" + entry.item.name.Replace("Item_", "");
            if (entry.material != null && go.TryGetComponent<Renderer>(out var r)) r.sharedMaterial = entry.material;
            // Whatever washes down into the pit is battered junk.
            go.GetComponent<WorldItem>().Setup(entry.item, quantity, Items.ItemQuality.Poor);
            live.Add(go);
            lootOf[go] = loot;
        }

        public ThePitt.Core.ScrapPitSave Capture()
        {
            var save = new ThePitt.Core.ScrapPitSave { minutesUntilSpawn = minutesUntilSpawn, minutesUntilNode = minutesUntilNode };
            // Heaps broken this frame are still around until the frame ends; leave them out.
            nodes.RemoveAll(n => n == null || n.Health <= 0f);
            foreach (var n in nodes)
                save.nodes.Add(new ThePitt.Core.ScrapNodeSave { position = n.transform.position, yaw = n.transform.eulerAngles.y, fraction = n.Fraction });
            live.RemoveAll(g => g == null);
            foreach (var go in live)
            {
                if (!lootOf.TryGetValue(go, out int loot)) continue;
                save.pieces.Add(new ThePitt.Core.ScrapSave
                {
                    loot = loot,
                    quantity = go.GetComponent<WorldItem>().Quantity,
                    position = go.transform.position,
                    yaw = go.transform.eulerAngles.y,
                });
            }
            return save;
        }

        /// <summary>Replaces whatever is lying in the pit with the saved pieces.</summary>
        public void Restore(ThePitt.Core.ScrapPitSave save)
        {
            foreach (var go in live)
                if (go != null) Destroy(go);
            live.Clear();
            lootOf.Clear();
            minutesUntilSpawn = Mathf.Max(1, save.minutesUntilSpawn);
            // Heaps: saves from before heaps existed keep the fresh ones.
            if (save.nodes != null && (save.nodes.Count > 0 || save.minutesUntilNode > 0))
            {
                foreach (var n in nodes)
                    if (n != null) Destroy(n.gameObject);
                nodes.Clear();
                minutesUntilNode = Mathf.Max(1, save.minutesUntilNode);
                foreach (var n in save.nodes)
                    if (n.fraction > 0f) SpawnNodeAt(OnFloor(n.position, 0f), n.yaw, n.fraction);
            }
            if (greybox == null || greybox.pitLoot == null) return;
            foreach (var piece in save.pieces)
                if (piece.loot >= 0 && piece.loot < greybox.pitLoot.Length && greybox.pitLoot[piece.loot].item != null)
                    SpawnAt(piece.loot, OnFloor(piece.position, 0.2f), piece.yaw, piece.quantity);
        }

        /// <summary>A saved spot put back on the pit floor as it is now (the floor moves if the city is regenerated).</summary>
        private Vector3 OnFloor(Vector3 saved, float lift) =>
            greybox == null ? saved : new Vector3(saved.x, greybox.HeightAt(new Vector2(saved.x, saved.z)) + lift, saved.z);

        private Vector2 RandomFloorPoint(float clearance = 0.4f)
        {
            TryFloorPoint(clearance, 30, out var p);
            return p;
        }

        /// <summary>A random spot on the pit floor with nothing solid around it at body height.</summary>
        private bool TryFloorPoint(float clearance, int attempts, out Vector2 point)
        {
            float radius = greybox.PitFloorRadius - wallMargin;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var p = greybox.pitCenter + Random.insideUnitCircle * radius;
                // Stay off the ramp, and away from the piles (anything solid at waist height). The probe sits
                // clear of the floor so it only finds things standing on it.
                if (greybox.InRampCorridor(p, 0.5f + clearance)) continue;
                var probe = new Vector3(p.x, greybox.HeightAt(p) + 0.5f + clearance, p.y);
                if (Physics.CheckSphere(probe, clearance, ~0, QueryTriggerInteraction.Ignore)) continue;
                point = p;
                return true;
            }
            point = greybox.pitCenter;
            return false;
        }

        // ---------------------------------------------------------------- scrap heaps

        /// <summary>
        /// A new heap at a clear spot, as far from the other heaps as can be found (best of several tries),
        /// and never closer than <see cref="nodeSpacing"/>. No spot, no heap; it tries again next time.
        /// </summary>
        private void SpawnNode()
        {
            if (greybox == null) return;
            nodes.RemoveAll(n => n == null);
            Physics.SyncTransforms();
            float best = -1f;
            var bestPoint = Vector2.zero;
            for (int candidate = 0; candidate < 16; candidate++)
            {
                if (!TryFloorPoint(1.2f, 8, out var p)) continue;
                float nearest = float.MaxValue;
                foreach (var n in nodes)
                {
                    var o = n.transform.position;
                    nearest = Mathf.Min(nearest, Vector2.Distance(p, new Vector2(o.x, o.z)));
                }
                if (nearest < nodeSpacing || nearest <= best) continue;
                best = nearest;
                bestPoint = p;
            }
            if (best < 0f) return;
            SpawnNodeAt(new Vector3(bestPoint.x, greybox.HeightAt(bestPoint), bestPoint.y), Random.Range(0f, 360f), 1f);
        }

        private void SpawnNodeAt(Vector3 position, float yaw, float fraction)
        {
            if (greybox == null) return;
            var node = HarvestNode.BuildScrapHeap(transform, position, yaw, greybox.rust, greybox.metal);
            node.Setup(ToolType.Pickaxe, nodeScrap, nodeSpring, nodePipe, fraction);
            nodes.Add(node);
        }

        /// <summary>Index of a weighted random entry in the loot table.</summary>
        private int PickLoot()
        {
            float total = 0f;
            foreach (var e in greybox.pitLoot) total += e.weight;
            float roll = Random.Range(0f, total);
            for (int i = 0; i < greybox.pitLoot.Length; i++)
            {
                roll -= greybox.pitLoot[i].weight;
                if (roll <= 0f) return i;
            }
            return greybox.pitLoot.Length - 1;
        }
    }
}
