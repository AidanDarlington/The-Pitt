using UnityEngine;

namespace ThePitt.Audio
{
    /// <summary>
    /// The game's sound effects in one place. The defaults are synthesised placeholders
    /// (The Pitt > Bake Default Sounds); drop a recorded clip into any slot to replace one.
    /// </summary>
    [CreateAssetMenu(menuName = "The Pitt/Sound Library", fileName = "SoundLibrary")]
    public class SoundLibrary : ScriptableObject
    {
        [Header("Combat")]
        public AudioClip gunshot;
        public AudioClip[] punches;
        [Tooltip("Melee weapons landing.")]
        public AudioClip[] meleeHits;
        public AudioClip[] swings;
        public AudioClip reload;
        public AudioClip dryFire;

        [Header("Movement")]
        public AudioClip[] footsteps;

        [Header("Crafting")]
        [Tooltip("Hammering and clanking: workbench, forge, firearms bench.")]
        public AudioClip[] craftMetal;
        [Tooltip("Stove.")]
        public AudioClip craftSizzle;
        [Tooltip("Drug lab.")]
        public AudioClip craftBubble;
        public AudioClip craftDone;

        [Header("UI")]
        public AudioClip click;
    }
}
