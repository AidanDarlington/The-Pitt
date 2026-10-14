using System;
using ThePitt.Combat;
using ThePitt.Core;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Player
{
    /// <summary>Health, hunger and thirst. Drains on the in-game clock, not real time.</summary>
    public class PlayerVitals : MonoBehaviour, IDamageable
    {
        [SerializeField, Min(1f)] private float maxHealth = 100f;
        [SerializeField, Min(1f)] private float maxHunger = 100f;
        [SerializeField, Min(1f)] private float maxThirst = 100f;

        [Header("Rates per in-game hour")]
        [Tooltip("100 over 72 hours = empty in three days.")]
        [SerializeField, Min(0f)] private float hungerDrain = 100f / 72f;
        [Tooltip("100 over 48 hours = empty in two days.")]
        [SerializeField, Min(0f)] private float thirstDrain = 100f / 48f;
        [SerializeField, Min(1f)] private float sprintDrainMultiplier = 2f;
        [Tooltip("Health lost per hour for each of hunger/thirst that is empty.")]
        [SerializeField, Min(0f)] private float starvingDamage = 6f;
        [Tooltip("Health regained per hour while hunger and thirst are both above the threshold.")]
        [SerializeField, Min(0f)] private float regenRate = 3f;
        [SerializeField, Range(0f, 1f)] private float regenThreshold = 0.5f;

        [Header("Death")]
        [SerializeField] private Transform respawnPoint;
        [SerializeField, Range(0f, 1f)] private float respawnFill = 0.5f;
        [Tooltip("Falling below this height (off the edge of the pillar) is fatal.")]
        [SerializeField] private float killHeight = -40f;

        [SerializeField] private FirstPersonController controller;

        public float Health { get; private set; }
        public float Hunger { get; private set; }
        public float Thirst { get; private set; }
        public float MaxHealth => maxHealth;
        public float MaxHunger => maxHunger;
        public float MaxThirst => maxThirst;

        public event Action Changed;
        public event Action Died;
        /// <summary>Fired when hurt by an attack, with the hit.</summary>
        public event Action<DamageInfo> Hurt;

        public bool IsDead => Health <= 0f;

        public void TakeDamage(DamageInfo info)
        {
            if (info.amount <= 0f || IsDead) return;
            Hurt?.Invoke(info);
            Damage(info.amount);
        }

        private Vector3 spawnPosition;
        private Quaternion spawnRotation;

        private void Awake()
        {
            Health = maxHealth;
            Hunger = maxHunger;
            Thirst = maxThirst;
            if (controller == null) controller = GetComponent<FirstPersonController>();
        }

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            // Here rather than Awake: the world loader moves the spawn point to the city's start as this scene loads.
            spawnPosition = respawnPoint != null ? respawnPoint.position : transform.position;
            spawnRotation = respawnPoint != null ? respawnPoint.rotation : transform.rotation;
            // TimeManager may not have existed during OnEnable.
            OnDisable();
            OnEnable();
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private void Update()
        {
            if (transform.position.y < killHeight) Damage(Health);
        }

        private void OnMinute(int _)
        {
            const float hours = 1f / 60f;
            float multiplier = controller != null && controller.IsSprinting ? sprintDrainMultiplier : 1f;

            Hunger = Mathf.Max(0f, Hunger - hungerDrain * multiplier * hours);
            Thirst = Mathf.Max(0f, Thirst - thirstDrain * multiplier * hours);

            int emptyStats = (Hunger <= 0f ? 1 : 0) + (Thirst <= 0f ? 1 : 0);
            if (emptyStats > 0) Damage(starvingDamage * emptyStats * hours);
            else if (Hunger >= maxHunger * regenThreshold && Thirst >= maxThirst * regenThreshold)
                Health = Mathf.Min(maxHealth, Health + regenRate * hours);

            Changed?.Invoke();
        }

        public void Consume(ItemDefinition item)
        {
            if (item == null || !item.IsConsumable) return;
            Hunger = Mathf.Clamp(Hunger + item.Nutrition, 0f, maxHunger);
            Thirst = Mathf.Clamp(Thirst + item.Hydration, 0f, maxThirst);
            if (item.HealthEffect < 0f) Damage(-item.HealthEffect);
            else Health = Mathf.Min(maxHealth, Health + item.HealthEffect);
            if (item.HasLastingEffect && TryGetComponent<PlayerEffects>(out var effects)) effects.Apply(item);
            Changed?.Invoke();
        }

        /// <summary>Adds (or with negatives, removes) health, hunger and thirst.</summary>
        public void Adjust(float health, float hunger, float thirst)
        {
            Hunger = Mathf.Clamp(Hunger + hunger, 0f, maxHunger);
            Thirst = Mathf.Clamp(Thirst + thirst, 0f, maxThirst);
            if (health < 0f) Damage(-health);
            else Health = Mathf.Min(maxHealth, Health + health);
            Changed?.Invoke();
        }

        /// <summary>Sets the stats outright (loading a save).</summary>
        public void Set(float health, float hunger, float thirst)
        {
            Health = Mathf.Clamp(health, 1f, maxHealth);
            Hunger = Mathf.Clamp(hunger, 0f, maxHunger);
            Thirst = Mathf.Clamp(thirst, 0f, maxThirst);
            Changed?.Invoke();
        }

        public void Damage(float amount)
        {
            if (amount <= 0f || Health <= 0f) return;
            Health = Mathf.Max(0f, Health - amount);
            Changed?.Invoke();
            if (Health <= 0f) Die();
        }

        private void Die()
        {
            Debug.Log("Player died. Respawning.");
            Died?.Invoke();

            var cc = GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            if (cc != null) cc.enabled = true;

            Health = maxHealth * respawnFill;
            Hunger = maxHunger * respawnFill;
            Thirst = maxThirst * respawnFill;
            Changed?.Invoke();
        }
    }
}
