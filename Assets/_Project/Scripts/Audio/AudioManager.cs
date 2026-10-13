using UnityEngine;

namespace ThePitt.Audio
{
    /// <summary>
    /// Plays one-shot sound effects from a small pool of audio sources. Positional sounds out of earshot
    /// of the listener are skipped. Use the static helpers; they do nothing if there's no manager.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        [SerializeField] private SoundLibrary library;
        [SerializeField, Min(1)] private int voices = 32;
        [SerializeField, Range(0f, 1f)] private float masterVolume = 1f;

        private AudioSource[] pool;
        private int next;
        private Transform listener;

        public static SoundLibrary Library => Instance != null ? Instance.library : null;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            pool = new AudioSource[voices];
            for (int i = 0; i < voices; i++)
            {
                var go = new GameObject("Voice");
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.dopplerLevel = 0f;
                pool[i] = s;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// A sound in the world, fading with distance. Background sounds (footsteps) only use a free voice and
        /// never cut off another sound.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 position, float volume = 1f, float maxDistance = 30f, float minDistance = 1.5f,
            float pitchJitter = 0.08f, bool background = false)
        {
            if (Instance == null || clip == null) return;
            var l = Instance.Listener;
            if (l != null && (l.position - position).sqrMagnitude > maxDistance * maxDistance) return;
            var s = Instance.Take(background);
            if (s == null) return;
            s.transform.position = position;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            Instance.Fire(s, clip, volume, pitchJitter);
        }

        /// <summary>A sound with no position: the player's own gun, UI.</summary>
        public static void Play2D(AudioClip clip, float volume = 1f, float pitchJitter = 0.05f)
        {
            if (Instance == null || clip == null) return;
            var s = Instance.Take(false);
            s.spatialBlend = 0f;
            Instance.Fire(s, clip, volume, pitchJitter);
        }

        public static void PlayRandom(AudioClip[] clips, Vector3 position, float volume = 1f, float maxDistance = 30f, float minDistance = 1.5f,
            float pitchJitter = 0.08f, bool background = false)
        {
            if (clips != null && clips.Length > 0)
                Play(clips[Random.Range(0, clips.Length)], position, volume, maxDistance, minDistance, pitchJitter, background);
        }

        public static void PlayRandom2D(AudioClip[] clips, float volume = 1f, float pitchJitter = 0.05f)
        {
            if (clips != null && clips.Length > 0) Play2D(clips[Random.Range(0, clips.Length)], volume, pitchJitter);
        }

        private Transform Listener
        {
            get
            {
                if (listener == null)
                {
                    var l = FindFirstObjectByType<AudioListener>();
                    if (l != null) listener = l.transform;
                }
                return listener;
            }
        }

        /// <summary>A free voice, or else (unless it's only a background sound) the one that started longest ago.</summary>
        private AudioSource Take(bool background)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                var s = pool[(next + i) % pool.Length];
                if (!s.isPlaying)
                {
                    next = (next + i + 1) % pool.Length;
                    return s;
                }
            }
            if (background) return null;
            var oldest = pool[next];
            next = (next + 1) % pool.Length;
            return oldest;
        }

        private void Fire(AudioSource s, AudioClip clip, float volume, float pitchJitter)
        {
            s.clip = clip;
            s.volume = volume * masterVolume;
            s.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            s.Play();
        }
    }
}
