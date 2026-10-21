using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Interaction
{
    /// <summary>An item lying in the world that the player can pick up.</summary>
    public class WorldItem : MonoBehaviour, IInteractable
    {
        [SerializeField] private ItemDefinition item;
        [SerializeField, Min(1)] private int quantity = 1;
        [SerializeField] private ItemQuality quality = ItemQuality.Standard;
        [Tooltip("In-game hours before it reappears after being picked up. 0 = never.")]
        [SerializeField, Min(0f)] private float respawnHours;
        [Tooltip("Longest side of the item's ground model, in metres.")]
        [SerializeField, Min(0.05f)] private float modelSize = 0.45f;
        [Tooltip("Longest side of a packed-up station (a dropped workbench, forge...), in metres.")]
        [SerializeField, Min(0.05f)] private float stationModelSize = 0.7f;

        public int Quantity => quantity;
        public ItemDefinition Item => item;
        public ItemQuality Quality => quality;

        /// <summary>Everything the player has dropped that's still lying around (what gets saved).</summary>
        public static readonly System.Collections.Generic.List<WorldItem> Dropped = new();

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDropped()
        {
            Dropped.Clear();
            watched = null;
        }

        /// <summary>In-game hours a dropped stack lies around before it's cleared away.</summary>
        public const float DroppedLifetimeHours = 48f;
        /// <summary>Most dropped stacks in the world at once; past this the oldest goes, so mass-dropping can't bog the game down.</summary>
        public const int MaxDropped = 150;
        /// <summary>A drop this close to a stack of the same thing joins it rather than adding another object.</summary>
        private const float MergeRadius = 1.5f;

        /// <summary>When it was dropped, in total in-game hours. Only meaningful for dropped stacks.</summary>
        public float DroppedAt { get; private set; }

        /// <summary>
        /// Puts a stack on the ground at <paramref name="position"/>, to be picked up again. Null if the city has no pickup prefab.
        /// <paramref name="droppedAt"/> is when it was dropped (total in-game hours), for restoring saves; negative means now.
        /// </summary>
        public static WorldItem Drop(ItemDefinition item, int quantity, ItemQuality quality, Vector3 position, float yaw, float droppedAt = -1f)
        {
            var prefab = World.PittGreybox.Current != null ? World.PittGreybox.Current.worldItemPrefab : null;
            if (prefab == null || item == null || quantity <= 0) return null;
            float now = TimeManagerHours();
            if (droppedAt < 0f) droppedAt = now;
            if (now - droppedAt >= DroppedLifetimeHours) return null;

            float mergeSq = MergeRadius * MergeRadius;
            foreach (var near in Dropped)
                if (near != null && near.item == item && near.quality == quality && (near.transform.position - position).sqrMagnitude < mergeSq)
                {
                    near.quantity += quantity;
                    near.DroppedAt = Mathf.Max(near.DroppedAt, droppedAt);
                    return near;
                }

            while (Dropped.Count >= MaxDropped)
            {
                var oldest = Dropped[0];
                Dropped.RemoveAt(0);
                if (oldest != null) Destroy(oldest.gameObject);
            }

            var go = Instantiate(prefab, position, Quaternion.Euler(0f, yaw, 0f));
            go.name = "Dropped_" + item.name.Replace("Item_", "");
            var dropped = go.GetComponent<WorldItem>();
            dropped.respawnHours = 0f;
            dropped.DroppedAt = droppedAt;
            dropped.Setup(item, quantity, quality);
            Dropped.Add(dropped);
            WatchClock();
            return dropped;
        }

        private void OnDestroy() => Dropped.Remove(this);

        private static Core.TimeManager watched;

        // One hourly sweep for every dropped stack, rather than each one checking every frame.
        private static void WatchClock()
        {
            var clock = Core.TimeManager.Instance;
            if (clock == watched) return;
            if (watched != null) watched.HourChanged -= ClearExpired;
            watched = clock;
            if (clock != null) clock.HourChanged += ClearExpired;
        }

        private static void ClearExpired(int hour)
        {
            float now = TimeManagerHours();
            for (int i = Dropped.Count - 1; i >= 0; i--)
            {
                var d = Dropped[i];
                if (d == null) Dropped.RemoveAt(i);
                else if (now - d.DroppedAt >= DroppedLifetimeHours)
                {
                    Dropped.RemoveAt(i);
                    Destroy(d.gameObject);
                }
            }
        }

        private int startQuantity;
        private float respawnAt = -1f;
        private bool hidden;

        private void Awake()
        {
            startQuantity = quantity;
            // Update only does respawns, so it's off until there's one to wait for.
            enabled = false;
        }

        private void Update()
        {
            if (!hidden || TimeManagerHours() < respawnAt) return;
            quantity = startQuantity;
            SetHidden(false);
        }

        // Total in-game hours elapsed, so respawns survive midnight.
        private static float TimeManagerHours()
        {
            var t = ThePitt.Core.TimeManager.Instance;
            return t == null ? 0f : (t.Day - 1) * 24f + t.TimeOfDay;
        }

        private void SetHidden(bool value)
        {
            hidden = value;
            enabled = value;
            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = !value;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = !value;
        }

        /// <summary>In-game hours before it reappears after being picked up. 0 = never.</summary>
        public void SetRespawn(float hours) => respawnHours = Mathf.Max(0f, hours);

        public void Setup(ItemDefinition newItem, int newQuantity, ItemQuality newQuality)
        {
            item = newItem;
            quantity = startQuantity = newQuantity;
            quality = newQuality;
            ShowModel();
        }

        /// <summary>Swaps the placeholder box for the item's own model, resting on whatever is underneath.</summary>
        private void ShowModel()
        {
            if (item == null) return;
            GameObject model;
            Vector3 unitSize;
            float scale = modelSize;
            if (item.WorldModel != null)
            {
                model = Instantiate(item.WorldModel, transform, false);
                // Models are baked centred with their longest side 1 unit long.
                unitSize = model.GetComponentInChildren<MeshFilter>().sharedMesh.bounds.size;
            }
            else if (item.IsPlaceable && (model = Miniature(item.PlaceablePrefab, out unitSize)) != null)
            {
                model.transform.SetParent(transform, false);
                scale = stationModelSize;
            }
            else return;
            if (TryGetComponent<MeshRenderer>(out var box))
            {
                box.enabled = false;
                Destroy(box);
            }
            if (TryGetComponent<MeshFilter>(out var filter)) Destroy(filter);

            transform.localScale = Vector3.one;
            model.transform.localScale = Vector3.one * scale;
            var size = unitSize * scale;

            var collider = GetComponent<BoxCollider>();
            if (collider != null) collider.enabled = false;
            float drop = 0f;
            if (Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, out var hit, 2f, ~0, QueryTriggerInteraction.Ignore))
                drop = hit.point.y + size.y * 0.5f - transform.position.y;
            model.transform.localPosition = new Vector3(0f, drop, 0f);
            if (collider == null) return;
            collider.enabled = true;
            collider.center = model.transform.localPosition;
            // A little bigger than the model, so small things are easy to look at.
            collider.size = Vector3.Max(size, Vector3.one * 0.2f);
        }

        /// <summary>
        /// A station's look without its workings: copies of the prefab's meshes only (no scripts, lights or colliders),
        /// centred and scaled so the longest side is 1 unit, like a baked item model. Null if it has no meshes.
        /// </summary>
        private static GameObject Miniature(GameObject prefab, out Vector3 unitSize)
        {
            unitSize = Vector3.zero;
            var root = prefab.transform;
            var toRoot = root.worldToLocalMatrix;
            var parts = new System.Collections.Generic.List<(Mesh mesh, Material[] materials, Matrix4x4 matrix)>();
            var bounds = new Bounds();
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || !filter.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled) continue;
                if (!ActiveUnder(filter.transform, root)) continue;
                var matrix = toRoot * filter.transform.localToWorldMatrix;
                var b = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = matrix.MultiplyPoint3x4(corner);
                    if (parts.Count == 0 && i == 0) bounds = new Bounds(p, Vector3.zero);
                    else bounds.Encapsulate(p);
                }
                parts.Add((filter.sharedMesh, renderer.sharedMaterials, matrix));
            }
            float longest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (parts.Count == 0 || longest <= 0f) return null;

            var model = new GameObject(prefab.name + "_Packed");
            var inner = new GameObject("Model").transform;
            inner.SetParent(model.transform, false);
            inner.localScale = Vector3.one / longest;
            inner.localPosition = -bounds.center / longest;
            foreach (var (mesh, materials, matrix) in parts)
            {
                var go = new GameObject(mesh.name, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(inner, false);
                go.transform.SetLocalPositionAndRotation(matrix.GetPosition(), matrix.rotation);
                go.transform.localScale = matrix.lossyScale;
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = materials;
            }
            unitSize = bounds.size / longest;
            return model;
        }

        private static bool ActiveUnder(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) return true;
            }
            return true;
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (item == null) return null;
            string amount = quantity > 1 ? $" x{quantity}" : "";
            string tier = item.HasQuality ? $" ({quality})" : "";
            return CanInteract(interactor) ? $"Pick up {item.DisplayName}{amount}{tier}" : "Inventory full";
        }

        public bool CanInteract(PlayerInteractor interactor) =>
            item != null && interactor.Inventory != null && interactor.Inventory.SpaceFor(item, quality) > 0;

        public void Interact(PlayerInteractor interactor)
        {
            quantity -= interactor.Inventory.Add(item, quantity, quality);
            if (quantity > 0) return;
            if (respawnHours > 0f)
            {
                respawnAt = TimeManagerHours() + respawnHours;
                SetHidden(true);
            }
            else Destroy(gameObject);
        }
    }
}
