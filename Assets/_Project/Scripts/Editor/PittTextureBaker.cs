using System.IO;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// Generates placeholder surface textures (brick, stained concrete) with matching normal maps,
    /// so greybox walls catch light like real surfaces instead of reading as flat colour.
    /// Each texture covers 2 x 2 metres; generated boxes use UVs in metres.
    /// </summary>
    public static class PittTextureBaker
    {
        private const int Size = 512;
        private const float MetresPerTile = 2f;
        // Bump when the look changes so existing textures are regenerated.
        private const int Version = 3;

        public static (Texture2D albedo, Texture2D normal) Brick(string folder) =>
            Bake(folder, "Brick", BrickHeight, BrickColor, normalStrength: 6f);

        public static (Texture2D albedo, Texture2D normal) Concrete(string folder) =>
            Bake(folder, "Concrete", ConcreteHeight, ConcreteColor, normalStrength: 3f);

        /// <summary>Cracked, oil-stained asphalt with gravel speckle; alpha carries a slight wet sheen.</summary>
        public static (Texture2D albedo, Texture2D normal) Asphalt(string folder) =>
            Bake(folder, "Asphalt", AsphaltHeight, AsphaltColor, normalStrength: 4f);

        /// <summary>Polished white marble in metre slabs, grey veins wandering through it. No grime: it's kept clean.</summary>
        public static (Texture2D albedo, Texture2D normal) Marble(string folder) =>
            Bake(folder, "Marble", MarbleHeight, MarbleColor, normalStrength: 1.5f);

        /// <summary>Material tiling for textures baked here, given UVs in metres.</summary>
        public static Vector2 Tiling => Vector2.one / MetresPerTile;

        private static (Texture2D, Texture2D) Bake(string folder, string name,
            System.Func<float, float, float> height, System.Func<float, float, float, Color> color, float normalStrength)
        {
            string albedoPath = $"{folder}/Tex_{name}_v{Version}_Albedo.png";
            string normalPath = $"{folder}/Tex_{name}_v{Version}_Normal.png";
            var existingAlbedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var existingNormal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (existingAlbedo != null && existingNormal != null) return (existingAlbedo, existingNormal);

            var heights = new float[Size, Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                heights[x, y] = height(x / (float)Size, y / (float)Size);

            var albedo = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var normal = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float u = x / (float)Size, v = y / (float)Size;
                albedo.SetPixel(x, y, color(u, v, heights[x, y]));

                // Normal from height gradient (wrapping, so it tiles).
                float hl = heights[(x - 1 + Size) % Size, y], hr = heights[(x + 1) % Size, y];
                float hd = heights[x, (y - 1 + Size) % Size], hu = heights[x, (y + 1) % Size];
                var n = new Vector3((hl - hr) * normalStrength, (hd - hu) * normalStrength, 1f).normalized;
                normal.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f));
            }

            File.WriteAllBytes(albedoPath, albedo.EncodeToPNG());
            File.WriteAllBytes(normalPath, normal.EncodeToPNG());
            Object.DestroyImmediate(albedo);
            Object.DestroyImmediate(normal);
            AssetDatabase.ImportAsset(albedoPath);
            AssetDatabase.ImportAsset(normalPath);

            var normalImporter = (TextureImporter)AssetImporter.GetAtPath(normalPath);
            normalImporter.textureType = TextureImporterType.NormalMap;
            normalImporter.SaveAndReimport();
            var albedoImporter = (TextureImporter)AssetImporter.GetAtPath(albedoPath);
            albedoImporter.sRGBTexture = true;
            albedoImporter.SaveAndReimport();

            return (AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath), AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath));
        }

        // ---------------------------------------------------------------- brick

        private const int BricksAcross = 8;  // 0.25 m bricks
        private const int BrickRows = 24;    // ~8 cm courses
        private const float Mortar = 0.12f;  // fraction of a course

        private static void BrickCell(float u, float v, out int bx, out int by, out float fx, out float fy)
        {
            float row = v * BrickRows;
            by = Mathf.FloorToInt(row);
            fy = row - by;
            float col = u * BricksAcross + (by % 2 == 0 ? 0f : 0.5f);
            bx = Mathf.FloorToInt(col);
            fx = col - bx;
            bx = ((bx % BricksAcross) + BricksAcross) % BricksAcross;
        }

        private static float BrickHeight(float u, float v)
        {
            BrickCell(u, v, out _, out _, out float fx, out float fy);
            float mortarX = Mortar * BrickRows / BricksAcross / 3f;
            float edge = Mathf.Min(Mathf.Min(fx, 1f - fx) / mortarX, Mathf.Min(fy, 1f - fy) / Mortar);
            float brick = Mathf.Clamp01(edge * 2f - 0.5f);
            return brick * 0.8f + Fbm(u, v, 24, 24, 3) * 0.2f;
        }

        private static Color BrickColor(float u, float v, float h)
        {
            BrickCell(u, v, out int bx, out int by, out _, out _);
            float tone = Hash(bx, by) * 0.35f + 0.65f;
            // Muddier, less saturated brick so light colour, not the albedo, sets the mood.
            var brick = new Color(0.4f, 0.23f, 0.17f) * tone;
            var mortar = new Color(0.27f, 0.25f, 0.23f);
            var c = Color.Lerp(mortar, brick, Mathf.Clamp01(h * 1.6f - 0.3f));
            return ApplyGrime(c, u, v);
        }

        // ---------------------------------------------------------------- concrete

        private static float ConcreteHeight(float u, float v)
        {
            // Formwork panel seams every metre and tie-holes on a grid.
            float su = Mathf.Repeat(u * 2f, 1f), sv = Mathf.Repeat(v * 2f, 1f);
            float seam = Mathf.Min(Mathf.Min(su, 1f - su), Mathf.Min(sv, 1f - sv));
            float h = Mathf.Clamp01(seam / 0.006f);
            float tu = Mathf.Repeat(u * 4f, 1f) - 0.5f, tv = Mathf.Repeat(v * 4f, 1f) - 0.5f;
            if (tu * tu + tv * tv < 0.0025f) h *= 0.3f;
            return h * 0.85f + Fbm(u, v, 16, 16, 4) * 0.15f;
        }

        private static Color ConcreteColor(float u, float v, float h)
        {
            float mottle = Fbm(u, v, 6, 6, 4);
            var c = new Color(0.44f, 0.43f, 0.41f) * (0.8f + mottle * 0.35f);
            c *= Mathf.Lerp(0.6f, 1f, h);
            return ApplyGrime(c, u, v);
        }

        private static float MarbleHeight(float u, float v)
        {
            // Slab joints every metre, otherwise flat and polished.
            float su = Mathf.Repeat(u * 2f, 1f), sv = Mathf.Repeat(v * 2f, 1f);
            float seam = Mathf.Min(Mathf.Min(su, 1f - su), Mathf.Min(sv, 1f - sv));
            return Mathf.Clamp01(seam / 0.004f);
        }

        private static Color MarbleColor(float u, float v, float h)
        {
            // Veins: thin dark lines along a sine warped by noise.
            float warp = Fbm(u, v, 4, 4, 5);
            float vein = Mathf.Abs(Mathf.Sin((u * 3f + v * 1.5f + warp * 2.2f) * Mathf.PI * 2f));
            float fine = Mathf.Abs(Mathf.Sin((u * 7f - v * 4f + Fbm(u + 0.3f, v, 8, 8, 4) * 3f) * Mathf.PI * 2f));
            float darkness = Mathf.Pow(1f - vein, 18f) * 0.45f + Mathf.Pow(1f - fine, 30f) * 0.2f;
            var c = Color.Lerp(new Color(0.93f, 0.93f, 0.95f), new Color(0.5f, 0.52f, 0.58f), darkness);
            c *= 0.97f + Fbm(u, v, 12, 12, 3) * 0.06f;
            c *= Mathf.Lerp(0.75f, 1f, h);
            c.a = 1f;
            return c;
        }

        // ---------------------------------------------------------------- shared

        /// <summary>Dark stains, water streaks running down, soot near the bottom, and fine speckle.</summary>
        private static Color ApplyGrime(Color c, float u, float v)
        {
            float stain = Mathf.SmoothStep(0.4f, 0.75f, Fbm(u, v, 3, 3, 5));
            float streak = Mathf.SmoothStep(0.5f, 0.85f, Fbm(u + 0.37f, v, 18, 2, 3)) * (0.4f + 0.6f * (1f - v));
            float soot = Mathf.SmoothStep(0.35f, 0f, v) * 0.5f;
            float grime = Mathf.Clamp01(stain * 0.75f + streak * 0.6f + soot);
            c = Color.Lerp(c, c * new Color(0.25f, 0.23f, 0.2f), grime);
            // High-frequency speckle gives the crunchy, gritty read.
            float speckle = Hash(Mathf.FloorToInt(u * Size), Mathf.FloorToInt(v * Size));
            c *= 0.82f + speckle * 0.3f;
            c.a = 1f;
            return c;
        }

        // ---------------------------------------------------------------- asphalt

        private static float Crack(float u, float v)
        {
            // Thin dark lines where a noise field crosses its midpoint.
            float n = Fbm(u, v, 4, 4, 4);
            return 1f - Mathf.SmoothStep(0f, 0.012f, Mathf.Abs(n - 0.5f));
        }

        private static float AsphaltHeight(float u, float v)
        {
            float gravel = Hash(Mathf.FloorToInt(u * Size), Mathf.FloorToInt(v * Size)) * 0.25f;
            return 0.7f + gravel - Crack(u, v) * 0.6f + Fbm(u, v, 8, 8, 3) * 0.15f;
        }

        private static Color AsphaltColor(float u, float v, float h)
        {
            // Dusty, worn grey rather than fresh black asphalt, so the ground reads in dim light.
            var c = new Color(0.34f, 0.32f, 0.29f) * (0.75f + Fbm(u, v, 5, 5, 4) * 0.5f);
            // Pale patches of worn concrete and dark oil stains.
            float worn = Mathf.SmoothStep(0.6f, 0.75f, Fbm(u + 0.5f, v, 2, 2, 4));
            c = Color.Lerp(c, new Color(0.46f, 0.43f, 0.38f), worn * 0.6f);
            float oil = Mathf.SmoothStep(0.62f, 0.78f, Fbm(u, v + 0.3f, 3, 3, 4));
            c = Color.Lerp(c, new Color(0.1f, 0.09f, 0.08f), oil * 0.7f);
            c *= Mathf.Lerp(0.85f, 1.1f, Hash(Mathf.FloorToInt(u * Size), Mathf.FloorToInt(v * Size)));
            c = Color.Lerp(c, Color.black, Crack(u, v) * 0.8f);
            // URP terrain reads alpha as smoothness: oily patches shine a little, cracks are dull.
            c.a = Mathf.Clamp01(0.18f + oil * 0.35f - Crack(u, v) * 0.18f);
            return c;
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }

        /// <summary>Tileable value noise with separate periods per axis.</summary>
        private static float Noise(float x, float y, int periodX, int periodY)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int Wx(int i) => ((i % periodX) + periodX) % periodX;
            int Wy(int i) => ((i % periodY) + periodY) % periodY;
            float a = Hash(Wx(x0), Wy(y0)), b = Hash(Wx(x0 + 1), Wy(y0));
            float c = Hash(Wx(x0), Wy(y0 + 1)), d = Hash(Wx(x0 + 1), Wy(y0 + 1));
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// <summary>
        /// Fractal noise over the unit square that tiles: u, v in 0..1, with integer base
        /// frequencies per axis doubling each octave.
        /// </summary>
        private static float Fbm(float u, float v, int freqX, int freqY, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                int px = freqX << i, py = freqY << i;
                sum += Noise(u * px, v * py, px, py) * amp;
                norm += amp;
                amp *= 0.5f;
            }
            return sum / norm;
        }
    }
}
