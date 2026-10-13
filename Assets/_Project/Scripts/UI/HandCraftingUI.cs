using ThePitt.Items;
using ThePitt.Production;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The crafting sub-panel beside the inventory: recipes that need no station, crafted instantly.
    /// Lives inside the inventory panel, so it shows whenever the inventory is open.
    /// </summary>
    public class HandCraftingUI : MonoBehaviour
    {
        [SerializeField] private Inventory inventory;
        [SerializeField] private RecipeDefinition[] recipes;
        [SerializeField] private RectTransform list;
        [Tooltip("Inactive row with a Button, an Image and a child Text.")]
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text statusLabel;
        [SerializeField] private Color haveColor = new Color(0.6f, 0.85f, 0.5f);
        [SerializeField] private Color missingColor = new Color(0.85f, 0.35f, 0.3f);

        private void Awake()
        {
            rowTemplate.SetActive(false);
        }

        private void OnEnable()
        {
            inventory.Changed += Refresh;
            if (statusLabel != null) statusLabel.text = "";
            Refresh();
        }

        private void OnDisable()
        {
            inventory.Changed -= Refresh;
        }

        private void Refresh()
        {
            for (int i = list.childCount - 1; i >= 0; i--)
            {
                var child = list.GetChild(i).gameObject;
                if (child != rowTemplate) Destroy(child);
            }

            // What the player has had every ingredient for, then (greyed out, as "???") what they've found
            // some of the ingredients for.
            int shown = 0;
            foreach (var recipe in RecipeUnlocks.Visible(recipes))
            {
                shown++;
                var row = Instantiate(rowTemplate, list);
                row.SetActive(true);
                if (!RecipeUnlocks.IsUnlocked(recipe))
                {
                    row.GetComponentInChildren<Text>().text =
                        $"<color=#77736c><b>???</b>\n<size=21>{RecipeUnlocks.MissingText(recipe)}</size></color>";
                    row.GetComponent<Button>().interactable = false;
                    continue;
                }

                var parts = new System.Text.StringBuilder();
                foreach (var i in recipe.Ingredients)
                {
                    if (i.item == null) continue;
                    int have = inventory.Count(i.item);
                    var color = ColorUtility.ToHtmlStringRGB(have >= i.amount ? haveColor : missingColor);
                    parts.Append($"{i.item.DisplayName} <color=#{color}>{have}/{i.amount}</color>\n");
                }
                row.GetComponentInChildren<Text>().text = $"<b>{recipe.DisplayName}</b>\n<size=21>{parts}</size>";

                var button = row.GetComponent<Button>();
                button.interactable = recipe.CanAfford(inventory) && inventory.SpaceFor(recipe.Output) >= recipe.OutputAmount;
                var captured = recipe;
                button.onClick.AddListener(() => Craft(captured));
            }
            const string hint = "Nothing to make yet. Pick up materials to discover recipes.";
            if (statusLabel == null) return;
            if (shown == 0 && statusLabel.text == "") statusLabel.text = hint;
            else if (shown > 0 && statusLabel.text == hint) statusLabel.text = "";
        }

        private void Craft(RecipeDefinition recipe)
        {
            if (inventory.SpaceFor(recipe.Output) < recipe.OutputAmount || !recipe.Consume(inventory, out var quality))
            {
                if (statusLabel != null) statusLabel.text = "Can't craft that.";
                return;
            }
            inventory.Add(recipe.Output, Progression.Skills.RollYield(recipe, recipe.OutputAmount), quality);
            Progression.Skills.Crafted(recipe);
            ThePitt.Audio.AudioManager.Play2D(ThePitt.Audio.AudioManager.Library?.craftDone, 0.6f);
            if (statusLabel != null) statusLabel.text = $"Made {recipe.DisplayName}. Click it in your inventory to place it.";
        }
    }
}
