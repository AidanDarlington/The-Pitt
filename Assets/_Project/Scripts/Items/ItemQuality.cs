using UnityEngine;

namespace ThePitt.Items
{
    /// <summary>Quality tiers. Stacks only merge with the same item at the same tier.</summary>
    public enum ItemQuality
    {
        Poor,
        Standard,
        Good,
        Excellent,
        Premium,
    }

    public static class ItemQualityExtensions
    {
        public static float ValueMultiplier(this ItemQuality quality) => quality switch
        {
            ItemQuality.Poor => 0.5f,
            ItemQuality.Standard => 1f,
            ItemQuality.Good => 1.5f,
            ItemQuality.Excellent => 2.25f,
            ItemQuality.Premium => 3.5f,
            _ => 1f,
        };

        /// <summary>How hard a tool of this quality works a scrap heap or corpse pile.</summary>
        public static float GatherMultiplier(this ItemQuality quality) => quality switch
        {
            ItemQuality.Poor => 0.6f,
            ItemQuality.Standard => 1f,
            ItemQuality.Good => 1.25f,
            ItemQuality.Excellent => 1.5f,
            ItemQuality.Premium => 1.8f,
            _ => 1f,
        };

        /// <summary>Maps a 0-1 score (e.g. how well a plant's needs were met) to a tier.</summary>
        public static ItemQuality FromScore(float score)
        {
            score = Mathf.Clamp01(score);
            if (score < 0.2f) return ItemQuality.Poor;
            if (score < 0.5f) return ItemQuality.Standard;
            if (score < 0.75f) return ItemQuality.Good;
            if (score < 0.92f) return ItemQuality.Excellent;
            return ItemQuality.Premium;
        }

        public static Color DisplayColor(this ItemQuality quality) => quality switch
        {
            ItemQuality.Poor => new Color(0.6f, 0.55f, 0.5f),
            ItemQuality.Standard => new Color(0.88f, 0.86f, 0.82f),
            ItemQuality.Good => new Color(0.55f, 0.85f, 0.45f),
            ItemQuality.Excellent => new Color(0.4f, 0.65f, 1f),
            ItemQuality.Premium => new Color(1f, 0.72f, 0.25f),
            _ => Color.white,
        };
    }
}
