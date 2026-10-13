using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>What a seed grows into and what it needs. Create via Assets > Create > The Pitt > Plant.</summary>
    [CreateAssetMenu(menuName = "The Pitt/Plant", fileName = "Plant_")]
    public class PlantDefinition : ScriptableObject
    {
        [SerializeField] private string displayName = "New Plant";
        [SerializeField] private ItemDefinition seed;
        [SerializeField] private ItemDefinition produce;

        [Tooltip("In-game hours from planting to harvest while fully watered. One day = 24.")]
        [SerializeField, Min(0.1f)] private float growHours = 24f;
        [SerializeField, Min(1)] private int minYield = 2;
        [SerializeField, Min(1)] private int maxYield = 4;

        [Header("Needs")]
        [Tooltip("Light level (0-1) the plant does best at.")]
        [SerializeField, Range(0f, 1f)] private float idealLight = 0.8f;
        [Tooltip("How far from ideal light can be before quality falls to zero.")]
        [SerializeField, Range(0.05f, 1f)] private float lightTolerance = 0.6f;
        [Tooltip("Water used per in-game hour of growth (tank holds 100).")]
        [SerializeField, Min(0f)] private float waterPerHour = 5f;
        [Tooltip("Light level above which the plant counts as lit (a grow light, not daylight).")]
        [SerializeField, Range(0f, 1f)] private float litAbove = 0.5f;
        [Tooltip("Most of its growth it can spend lit and still come out Good or better. 1 = no limit.")]
        [SerializeField, Range(0f, 1f)] private float maxLitShare = 1f;

        public string DisplayName => displayName;
        public ItemDefinition Seed => seed;
        public ItemDefinition Produce => produce;
        public float GrowHours => growHours;
        public int MinYield => minYield;
        public int MaxYield => Mathf.Max(minYield, maxYield);
        public float WaterPerHour => waterPerHour;
        public float LitAbove => litAbove;

        /// <summary>Whether spending this share of its growth lit keeps it from coming out Good or better.</summary>
        public bool TooLit(float litShare) => maxLitShare < 1f && litShare > maxLitShare;

        /// <summary>0-1 score for how well a light level suits this plant.</summary>
        public float LightScore(float light) => Mathf.Clamp01(1f - Mathf.Abs(light - idealLight) / lightTolerance);
    }
}
