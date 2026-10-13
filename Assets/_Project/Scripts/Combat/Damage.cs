using UnityEngine;

namespace ThePitt.Combat
{
    /// <summary>One hit: how much, where it landed, which way it was travelling, and who dealt it.</summary>
    public struct DamageInfo
    {
        public float amount;
        public Vector3 point;
        public Vector3 direction;
        public GameObject source;
        public bool fromPlayer;
    }

    /// <summary>Anything that can be hurt: the player, NPCs.</summary>
    public interface IDamageable
    {
        bool IsDead { get; }
        void TakeDamage(DamageInfo info);
    }
}

namespace ThePitt.Combat
{
    /// <summary>Loud things (gunshots) that anyone nearby hears.</summary>
    public static class Noise
    {
        /// <summary>Where the noise was and how far it carried.</summary>
        public static event System.Action<Vector3, float> Heard;

        public static void Make(Vector3 position, float radius) => Heard?.Invoke(position, radius);
    }
}
