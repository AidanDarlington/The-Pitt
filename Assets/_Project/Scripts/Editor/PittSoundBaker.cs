using System;
using System.IO;
using ThePitt.Audio;
using UnityEditor;
using UnityEngine;

namespace ThePitt.EditorTools
{
    /// <summary>
    /// Synthesises the default sound set (gunshots, footsteps, punches, crafting) into WAV files and fills
    /// the sound library with them. Placeholder audio: swap any clip in the library for a recorded one.
    /// </summary>
    public static class PittSoundBaker
    {
        private const string Folder = "Assets/_Project/Audio";
        private const string LibraryPath = Folder + "/SoundLibrary.asset";
        private const int Rate = 44100;
        private const float Tau = Mathf.PI * 2f;

        private static System.Random rng;

        [MenuItem("The Pitt/Bake Default Sounds")]
        public static void BakeAll()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project", "Audio");

            var lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<SoundLibrary>();
                AssetDatabase.CreateAsset(lib, LibraryPath);
            }

            lib.gunshot = Bake("Gunshot", 1, Gunshot)[0];
            lib.footsteps = Bake("Footstep", 5, Footstep);
            lib.punches = Bake("Punch", 3, Punch);
            lib.meleeHits = Bake("ClubHit", 2, ClubHit);
            lib.swings = Bake("Swing", 2, Swing);
            lib.reload = Bake("Reload", 1, Reload)[0];
            lib.dryFire = Bake("DryFire", 1, DryFire)[0];
            lib.click = Bake("Click", 1, Click)[0];
            lib.craftMetal = Bake("CraftMetal", 3, CraftMetal);
            lib.craftSizzle = Bake("CraftSizzle", 1, Sizzle)[0];
            lib.craftBubble = Bake("CraftBubble", 1, Bubbles)[0];
            lib.craftDone = Bake("CraftDone", 1, CraftDone)[0];

            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log("PittSoundBaker: baked default sounds.");
        }

        private static AudioClip[] Bake(string name, int variants, Func<int, float[]> make)
        {
            var clips = new AudioClip[variants];
            for (int v = 0; v < variants; v++)
            {
                rng = new System.Random(name.GetHashCode() * 31 + v);
                var samples = make(v);
                Finish(samples);
                string path = $"{Folder}/{name}{(variants > 1 ? "_" + (v + 1) : "")}.wav";
                WriteWav(path, samples);
                AssetDatabase.ImportAsset(path);
                var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                importer.forceToMono = true;
                var settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
                clips[v] = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            return clips;
        }

        // ---------------------------------------------------------------- sounds

        private static float[] Gunshot(int v)
        {
            var s = Buffer(1.1f);
            var body = new float[s.Length];
            float lp = 0f, lpTail = 0f, phase = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float crack = t < 0.004f ? Noise() : 0f;
                // Muzzle blast: noise darkening fast.
                lp = OnePole(lp, Noise(), 300f + 7000f * Mathf.Exp(-t / 0.04f));
                body[i] = lp * Mathf.Exp(-t / 0.08f);
                // Low thump, pitch dropping.
                phase += Tau * Mathf.Lerp(40f, 95f, Mathf.Exp(-t / 0.05f)) / Rate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t / 0.11f);
                // Rumble off the buildings.
                lpTail = OnePole(lpTail, Noise(), 500f);
                float tail = lpTail * Mathf.Exp(-t / 0.4f) * 0.9f;
                s[i] = crack * 0.9f + body[i] * 1.4f + thump * 0.9f + tail;
            }
            // A couple of slap-back echoes from the walls.
            Echo(s, body, 0.047f, 0.35f);
            Echo(s, body, 0.103f, 0.2f);
            for (int i = 0; i < s.Length; i++) s[i] = (float)Math.Tanh(s[i] * 1.6f);
            return s;
        }

        private static float[] Footstep(int v)
        {
            var s = Buffer(0.22f);
            float heelFreq = 100f + v * 9f, gritCenter = 1600f + v * 350f;
            var grit = new Svf();
            var scuff = new Svf();
            float phase = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                phase += Tau * Mathf.Lerp(60f, heelFreq, Mathf.Exp(-t / 0.02f)) / Rate;
                float heel = Mathf.Sin(phase) * Mathf.Exp(-t / 0.022f) * 0.8f;
                float g = grit.Band(Noise(), gritCenter, 0.8f) * Mathf.Exp(-t / 0.028f) * 1.3f;
                float ts = t - 0.035f - v * 0.004f;
                float sc = ts > 0f ? scuff.Band(Noise(), 3200f, 1.2f) * Mathf.Exp(-ts / 0.025f) * 0.6f : 0f;
                s[i] = heel + g + sc;
            }
            return s;
        }

        private static float[] Punch(int v)
        {
            var s = Buffer(0.28f);
            float lp = 0f, phase = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                phase += Tau * Mathf.Lerp(50f, 150f + v * 15f, Mathf.Exp(-t / 0.03f)) / Rate;
                float thud = Mathf.Sin(phase) * Mathf.Exp(-t / 0.05f);
                lp = OnePole(lp, Noise(), 1400f);
                float body = lp * Mathf.Exp(-t / 0.025f) * 1.5f;
                float slap = Noise() * Mathf.Exp(-t / 0.005f) * 0.5f;
                s[i] = thud + body + slap;
            }
            return s;
        }

        private static float[] ClubHit(int v)
        {
            var s = Buffer(0.45f);
            float lp = 0f, phase = 0f;
            float[] partials = { 430f + v * 40f, 1180f + v * 70f, 2050f + v * 90f };
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                phase += Tau * Mathf.Lerp(45f, 110f, Mathf.Exp(-t / 0.04f)) / Rate;
                float thud = Mathf.Sin(phase) * Mathf.Exp(-t / 0.07f);
                lp = OnePole(lp, Noise(), 2200f);
                float crunch = lp * Mathf.Exp(-t / 0.03f) * 1.4f;
                float ring = 0f;
                for (int k = 0; k < partials.Length; k++)
                    ring += Mathf.Sin(Tau * partials[k] * t) * Mathf.Exp(-t / (0.14f / (k + 1))) * (0.22f / (k + 1));
                s[i] = thud + crunch + ring;
            }
            return s;
        }

        private static float[] Swing(int v)
        {
            float length = 0.3f + v * 0.04f;
            var s = Buffer(length);
            var f = new Svf();
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate, u = t / length;
                float center = 350f + 1500f * Mathf.Sin(u * Mathf.PI);
                float env = Mathf.Pow(Mathf.Sin(u * Mathf.PI), 2f);
                s[i] = f.Band(Noise(), center, 0.6f) * env;
            }
            return s;
        }

        private static float[] Click(int v)
        {
            var s = Buffer(0.06f);
            AddClick(s, 0f, 3200f, 1f);
            return s;
        }

        private static float[] DryFire(int v)
        {
            var s = Buffer(0.18f);
            AddClick(s, 0f, 2600f, 1f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                s[i] += Mathf.Sin(Tau * 1750f * t) * Mathf.Exp(-t / 0.03f) * 0.25f;
            }
            return s;
        }

        private static float[] Reload(int v)
        {
            var s = Buffer(0.9f);
            AddClick(s, 0.04f, 2400f, 0.8f);
            var f = new Svf();
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                // A round sliding into the breech.
                if (t > 0.18f && t < 0.45f) s[i] += f.Band(Noise(), 1500f, 0.9f) * Mathf.Sin((t - 0.18f) / 0.27f * Mathf.PI) * 0.6f;
            }
            AddClick(s, 0.58f, 2900f, 1f);
            AddClick(s, 0.61f, 1900f, 0.6f);
            return s;
        }

        private static float[] CraftMetal(int v)
        {
            var s = Buffer(0.7f);
            float f0 = 380f + v * 95f;
            // Modes of a struck bar.
            float[] ratios = { 1f, 2.76f, 5.40f, 8.93f };
            float[] decays = { 0.28f, 0.16f, 0.09f, 0.05f };
            float[] amps = { 0.8f, 0.55f, 0.3f, 0.18f };
            float phase = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float ring = 0f;
                for (int k = 0; k < ratios.Length; k++) ring += Mathf.Sin(Tau * f0 * ratios[k] * t) * Mathf.Exp(-t / decays[k]) * amps[k];
                phase += Tau * 85f / Rate;
                float knock = Mathf.Sin(phase) * Mathf.Exp(-t / 0.03f) * 0.6f;
                s[i] = ring + knock + Noise() * Mathf.Exp(-t / 0.003f) * 0.8f;
            }
            return s;
        }

        private static float[] Sizzle(int v)
        {
            var s = Buffer(1.8f);
            float lp = 0f, mod = 0f, pop = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate;
                float n = Noise();
                lp = OnePole(lp, n, 2500f);
                mod = OnePole(mod, Noise(), 6f);
                float hiss = (n - lp) * (0.35f + Mathf.Abs(mod) * 2.5f);
                // Fat spitting.
                if (rng.NextDouble() < 0.0012) pop = 1f;
                pop *= 0.996f;
                s[i] = hiss + pop * Noise() * 0.7f;
                s[i] *= Mathf.Min(1f, t / 0.15f) * Mathf.Min(1f, (1.8f - t) / 0.25f);
            }
            return s;
        }

        private static float[] Bubbles(int v)
        {
            var s = Buffer(1.8f);
            float lp = 0f;
            for (int i = 0; i < s.Length; i++)
            {
                lp = OnePole(lp, Noise(), 180f);
                s[i] = lp * 0.5f;
            }
            float at = 0.05f;
            while (at < 1.7f)
            {
                float f0 = 280f + (float)rng.NextDouble() * 500f, length = 0.03f + (float)rng.NextDouble() * 0.05f;
                float amp = 0.3f + (float)rng.NextDouble() * 0.5f, phase = 0f;
                int start = (int)(at * Rate), n = (int)(length * Rate);
                for (int j = 0; j < n && start + j < s.Length; j++)
                {
                    float u = j / (float)n;
                    phase += Tau * f0 * (1f + 2.5f * u) / Rate;
                    s[start + j] += Mathf.Sin(phase) * Mathf.Sin(u * Mathf.PI) * amp;
                }
                at += 0.06f + (float)rng.NextDouble() * 0.2f;
            }
            return s;
        }

        private static float[] CraftDone(int v)
        {
            var s = Buffer(1.1f);
            var a = CraftMetal(1);
            var b = CraftMetal(2);
            Mix(s, a, 0f, 0.6f);
            Mix(s, b, 0.13f, 0.7f);
            for (int i = 0; i < s.Length; i++)
            {
                float t = i / (float)Rate - 0.25f;
                if (t > 0f) s[i] += (Mathf.Sin(Tau * 1320f * t) + 0.4f * Mathf.Sin(Tau * 3090f * t)) * Mathf.Exp(-t / 0.35f) * 0.3f;
            }
            return s;
        }

        // ---------------------------------------------------------------- building blocks

        private static float[] Buffer(float seconds) => new float[Mathf.CeilToInt(seconds * Rate)];

        private static float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

        private static float OnePole(float y, float x, float cutoff) => y + (1f - Mathf.Exp(-Tau * cutoff / Rate)) * (x - y);

        /// <summary>State-variable filter, for band-passed noise.</summary>
        private class Svf
        {
            private float low, band;

            public float Band(float x, float center, float q)
            {
                float f = 2f * Mathf.Sin(Mathf.PI * Mathf.Min(center, Rate / 6f) / Rate);
                low += f * band;
                float high = x - low - q * band;
                band += f * high;
                return band;
            }
        }

        private static void AddClick(float[] s, float at, float ping, float amp)
        {
            int start = (int)(at * Rate);
            for (int i = start; i < s.Length; i++)
            {
                float t = (i - start) / (float)Rate;
                if (t > 0.05f) break;
                s[i] += (Noise() * Mathf.Exp(-t / 0.0015f) + Mathf.Sin(Tau * ping * t) * Mathf.Exp(-t / 0.006f) * 0.6f) * amp;
            }
        }

        private static void Echo(float[] s, float[] source, float delay, float gain)
        {
            int d = (int)(delay * Rate);
            for (int i = d; i < s.Length; i++) s[i] += source[i - d] * gain;
        }

        private static void Mix(float[] s, float[] other, float at, float gain)
        {
            int start = (int)(at * Rate);
            for (int i = 0; i < other.Length && start + i < s.Length; i++) s[start + i] += other[i] * gain;
        }

        /// <summary>Normalise to just under full scale and fade the very end to avoid a click.</summary>
        private static void Finish(float[] s)
        {
            float peak = 0f;
            foreach (float x in s) peak = Mathf.Max(peak, Mathf.Abs(x));
            if (peak > 0f)
                for (int i = 0; i < s.Length; i++) s[i] *= 0.9f / peak;
            int fade = Mathf.Min(s.Length, Rate / 200);
            for (int i = 0; i < fade; i++) s[s.Length - 1 - i] *= i / (float)fade;
        }

        private static void WriteWav(string path, float[] samples)
        {
            using var stream = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(stream);
            int bytes = samples.Length * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + bytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(bytes);
            foreach (float x in samples) w.Write((short)Mathf.RoundToInt(Mathf.Clamp(x, -1f, 1f) * 32767f));
        }
    }
}
