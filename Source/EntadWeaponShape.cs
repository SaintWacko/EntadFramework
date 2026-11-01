using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // What a weapon texture looks like, read back from the GPU once per texture: spots on its opaque pixels for flames,
    // flecks and arcs to sit on, and a blurred copy of its alpha for the glow. Textures loaded by the game aren't
    // readable from the CPU, so the texture is blitted into a small render texture and read from there.
    public class EntadWeaponShape
    {
        private const int Size = 48;
        private const int Pad = 8;
        // The halo texture is padded so the blur has room past the weapon's edges; drawn this much larger to line up
        public const float HaloScale = (Size + 2f * Pad) / Size;
        private const int MaxPoints = 64;

        public Vector2[] points;
        public Material HaloMaterial { get; private set; }

        private static readonly Dictionary<Texture, EntadWeaponShape> cache = new Dictionary<Texture, EntadWeaponShape>();

        public static EntadWeaponShape For(Texture tex)
        {
            if (tex == null) return null;
            if (cache.TryGetValue(tex, out var shape)) return shape;
            try { shape = Read(tex); }
            catch (System.Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] couldn't read the shape of weapon texture {tex.name}: {e}", tex.GetInstanceID());
                shape = null;
            }
            cache[tex] = shape;
            return shape;
        }

        private static EntadWeaponShape Read(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Color32[] pixels;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply();
                pixels = read.GetPixels32();
                Object.Destroy(read);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }

            var spots = new List<Vector2>();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (pixels[y * Size + x].a > 128) spots.Add(new Vector2((x + 0.5f) / Size - 0.5f, (y + 0.5f) / Size - 0.5f));
            if (spots.Count == 0) spots.Add(Vector2.zero);
            // An even spread over the weapon, not the first rows of it
            var chosen = new Vector2[Mathf.Min(MaxPoints, spots.Count)];
            for (int i = 0; i < chosen.Length; i++) chosen[i] = spots[(int)((long)i * spots.Count / chosen.Length)];
            for (int i = chosen.Length - 1; i > 0; i--)
            {
                int j = (i * 7919 + 13) % (i + 1);
                (chosen[i], chosen[j]) = (chosen[j], chosen[i]);
            }

            return new EntadWeaponShape { points = chosen, HaloMaterial = MakeHalo(pixels, tex.name) };
        }

        private static Material MakeHalo(Color32[] pixels, string name)
        {
            const int Full = Size + 2 * Pad;
            var alpha = new float[Full * Full];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    alpha[(y + Pad) * Full + x + Pad] = pixels[y * Size + x].a / 255f;
            // Two box blurs each way spread the silhouette a few pixels out with a soft edge
            var tmp = new float[alpha.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                Blur(alpha, tmp, Full, 1, 0);
                Blur(tmp, alpha, Full, 0, 1);
            }
            var colors = new Color32[alpha.Length];
            for (int i = 0; i < alpha.Length; i++) colors[i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha[i] * 1.8f) * 255f));
            var halo = new Texture2D(Full, Full, TextureFormat.ARGB32, false) { name = "EntadHalo_" + name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            halo.SetPixels32(colors);
            halo.Apply(false, true);
            return new Material(ShaderDatabase.MoteGlow) { mainTexture = halo, name = halo.name };
        }

        private static void Blur(float[] from, float[] to, int full, int dx, int dy)
        {
            const int R = 3;
            for (int y = 0; y < full; y++)
                for (int x = 0; x < full; x++)
                {
                    float sum = 0f;
                    for (int k = -R; k <= R; k++)
                    {
                        int sx = x + dx * k, sy = y + dy * k;
                        if (sx >= 0 && sx < full && sy >= 0 && sy < full) sum += from[sy * full + sx];
                    }
                    to[y * full + x] = sum / (2 * R + 1);
                }
        }
    }
}
