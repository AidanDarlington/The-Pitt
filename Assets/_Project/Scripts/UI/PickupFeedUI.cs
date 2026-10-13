using System.Collections.Generic;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Short-lived "+2 Scrap Metal" lines when items enter the inventory, and "+12 Harvesting xp" when skills train.</summary>
    public class PickupFeedUI : MonoBehaviour
    {
        [SerializeField] private Inventory inventory;
        [SerializeField] private Text label;
        [SerializeField, Min(0.1f)] private float lineDuration = 3f;
        [SerializeField, Min(1)] private int maxLines = 5;

        private readonly List<(string text, float expires)> lines = new();

        private void OnEnable()
        {
            if (inventory != null) inventory.ItemAdded += OnItemAdded;
            Progression.Skills.Gained += OnXp;
            Redraw();
        }

        private void OnDisable()
        {
            if (inventory != null) inventory.ItemAdded -= OnItemAdded;
            Progression.Skills.Gained -= OnXp;
        }

        private readonly Dictionary<Progression.Skill, int> xpShown = new();

        /// <summary>"+12 Harvesting xp" in the skill's colour; gains while that line is still up add to it and bring it to the bottom.</summary>
        private void OnXp(Progression.Skill skill, int amount)
        {
            string tag = $" {Progression.Skills.Name(skill)} xp</color>";
            int index = lines.FindIndex(l => l.text.EndsWith(tag));
            int total = amount;
            if (index >= 0 && xpShown.TryGetValue(skill, out int shown))
            {
                total += shown;
                lines.RemoveAt(index);
            }
            xpShown[skill] = total;
            lines.Add(($"<color=#{ColorUtility.ToHtmlStringRGB(Progression.Skills.Colour(skill))}>+{total}{tag}", Time.time + lineDuration));
            while (lines.Count > maxLines) lines.RemoveAt(0);
            Redraw();
        }

        private void OnItemAdded(ItemDefinition item, int amount, ItemQuality quality)
        {
            string color = ColorUtility.ToHtmlStringRGB(quality.DisplayColor());
            string tier = item.HasQuality ? $" <color=#{color}>({quality})</color>" : "";
            lines.Add(($"+{amount} {item.DisplayName}{tier}", Time.time + lineDuration));
            while (lines.Count > maxLines) lines.RemoveAt(0);
            Redraw();
        }

        private void Update()
        {
            if (lines.Count > 0 && lines[0].expires <= Time.time)
            {
                lines.RemoveAll(l => l.expires <= Time.time);
                Redraw();
            }
        }

        private void Redraw()
        {
            if (label == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var line in lines) sb.AppendLine(line.text);
            label.text = sb.ToString();
        }
    }
}
