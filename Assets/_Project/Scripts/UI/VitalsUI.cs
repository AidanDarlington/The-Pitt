using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Health / hunger / thirst bars, shown only while the inventory is open. Each fill Image should be
    /// Image.Type.Filled.
    /// </summary>
    public class VitalsUI : MonoBehaviour
    {
        [SerializeField] private PlayerVitals vitals;
        [SerializeField] private Image healthFill;
        [SerializeField] private Image hungerFill;
        [SerializeField] private Image thirstFill;
        [Tooltip("Bars flash this colour when below the warning threshold.")]
        [SerializeField] private Color warningColor = new Color(0.9f, 0.25f, 0.2f);
        [SerializeField, Range(0f, 1f)] private float warningThreshold = 0.2f;

        [Tooltip("The bars and their labels, hidden while the inventory is shut. Defaults to the \"Vitals\" child.")]
        [SerializeField] private GameObject group;

        private Color healthColor, hungerColor, thirstColor;
        private InventoryUI inventoryUI;

        private void Awake()
        {
            healthColor = healthFill.color;
            hungerColor = hungerFill.color;
            thirstColor = thirstFill.color;
            if (group == null)
            {
                // The "Vitals" object the bars sit in, wherever the player menu has moved it.
                var t = healthFill.transform;
                while (t.parent != null && t.name != "Vitals") t = t.parent;
                group = t.name == "Vitals" ? t.gameObject : healthFill.transform.parent.parent.gameObject;
            }
            inventoryUI = GetComponent<InventoryUI>();
            if (inventoryUI == null) inventoryUI = FindFirstObjectByType<InventoryUI>();
            if (inventoryUI != null) inventoryUI.OpenChanged += Show;
        }

        // Not in Awake: the inventory panel may not have closed itself yet.
        private void Start() => Show(inventoryUI != null && inventoryUI.IsOpen);

        private void OnDestroy()
        {
            if (inventoryUI != null) inventoryUI.OpenChanged -= Show;
        }

        private void Show(bool open)
        {
            group.SetActive(open);
            // Snap rather than slide in from wherever they were last seen.
            if (open && vitals != null)
            {
                healthFill.fillAmount = vitals.Health / vitals.MaxHealth;
                hungerFill.fillAmount = vitals.Hunger / vitals.MaxHunger;
                thirstFill.fillAmount = vitals.Thirst / vitals.MaxThirst;
            }
        }

        private void Update()
        {
            if (vitals == null) return;
            // Smooth the bars toward their value; cheap enough to do every frame for three images.
            Apply(healthFill, vitals.Health / vitals.MaxHealth, healthColor);
            Apply(hungerFill, vitals.Hunger / vitals.MaxHunger, hungerColor);
            Apply(thirstFill, vitals.Thirst / vitals.MaxThirst, thirstColor);
        }

        private void Apply(Image fill, float value, Color normal)
        {
            fill.fillAmount = Mathf.MoveTowards(fill.fillAmount, value, Time.deltaTime);
            bool warn = value <= warningThreshold;
            fill.color = warn ? Color.Lerp(normal, warningColor, Mathf.PingPong(Time.time * 2f, 1f)) : normal;
        }
    }
}
