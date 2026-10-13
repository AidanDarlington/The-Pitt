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

        public int Quantity => quantity;

        private int startQuantity;
        private float respawnAt = -1f;
        private bool hidden;

        private void Awake() => startQuantity = quantity;

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
            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = !value;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = !value;
        }

        public void Setup(ItemDefinition newItem, int newQuantity, ItemQuality newQuality)
        {
            item = newItem;
            quantity = newQuantity;
            quality = newQuality;
            ShowModel();
        }

        /// <summary>Swaps the placeholder box for the item's own model, resting on whatever is underneath.</summary>
        private void ShowModel()
        {
            if (item == null || item.WorldModel == null) return;
            if (TryGetComponent<MeshRenderer>(out var box))
            {
                box.enabled = false;
                Destroy(box);
            }
            if (TryGetComponent<MeshFilter>(out var filter)) Destroy(filter);

            transform.localScale = Vector3.one;
            var model = Instantiate(item.WorldModel, transform, false);
            model.transform.localScale = Vector3.one * modelSize;
            // Models are baked centred with their longest side 1 unit long.
            var bounds = model.GetComponentInChildren<MeshFilter>().sharedMesh.bounds;
            var size = bounds.size * modelSize;

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
