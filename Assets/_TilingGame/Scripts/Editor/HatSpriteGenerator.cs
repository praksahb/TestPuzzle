using UnityEngine;
using UnityEditor;
using System.IO;

namespace TMKOC.Games.TilingGame.Editor
{
    public class HatSpriteGenerator
    {
        // The Hat monotile has 13 vertices. These SVG coordinates come from a
        // verified Einstein Hat construction. We scale them so the short edge
        // (R/2 = 104px at 256 PPU) matches the KiteSpriteGenerator exactly.
        //
        // SVG source edge lengths: short ≈ 54px, long ≈ 93.5px
        // Target edge lengths:     short = 104px, long = 180px (= apothem)
        // Scale factor = 104 / 54 ≈ 1.926

        [MenuItem("Tools/Generate Hat Sprite")]
        public static void GenerateHatPNG()
        {
            // Raw SVG coordinates (Y-down) for the Einstein Hat monotile
            Vector2[] svgPoints = new Vector2[]
            {
                new Vector2(89.3f,  146.9f),
                new Vector2(8.5f,   194.0f),
                new Vector2(35.3f,  240.5f),
                new Vector2(143.3f, 240.5f),
                new Vector2(170.2f, 193.6f),
                new Vector2(250.8f, 240.7f),
                new Vector2(331.7f, 193.9f),
                new Vector2(305.0f, 146.9f),
                new Vector2(251.0f, 146.9f),
                new Vector2(251.7f, 53.4f),
                new Vector2(170.1f, 6.5f),
                new Vector2(143.3f, 53.4f),
                new Vector2(89.3f,  53.4f),
            };

            // Scale to match Kite edge lengths (short edge = R/2 = 104px)
            float scale = 104f / 54f;

            // Flip Y (SVG is Y-down, Unity is Y-up) and scale
            float maxY = 240.7f;
            Vector2[] points = new Vector2[13];
            for (int i = 0; i < 13; i++)
            {
                points[i] = new Vector2(
                    svgPoints[i].x * scale,
                    (maxY - svgPoints[i].y) * scale
                );
            }

            // Find bounding box
            float minX = float.MaxValue, minY2 = float.MaxValue;
            float maxX2 = float.MinValue, maxY2 = float.MinValue;
            foreach (var p in points)
            {
                minX = Mathf.Min(minX, p.x); maxX2 = Mathf.Max(maxX2, p.x);
                minY2 = Mathf.Min(minY2, p.y); maxY2 = Mathf.Max(maxY2, p.y);
            }

            int margin = 20;
            int texWidth = Mathf.CeilToInt(maxX2 - minX) + margin * 2;
            int texHeight = Mathf.CeilToInt(maxY2 - minY2) + margin * 2;
            Vector2 offset = new Vector2(-minX + margin, -minY2 + margin);

            // Apply offset so all coords are positive
            for (int i = 0; i < 13; i++)
                points[i] += offset;

            // Create texture
            Texture2D tex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
            Color[] colors = new Color[texWidth * texHeight];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.clear;
            tex.SetPixels(colors);

            // Fill: test each pixel against the 13-gon
            for (int y = 0; y < texHeight; y++)
            {
                for (int x = 0; x < texWidth; x++)
                {
                    if (IsPointInPolygon(new Vector2(x, y), points))
                        tex.SetPixel(x, y, Color.white);
                }
            }
            tex.Apply();

            // Draw thick black outline
            for (int i = 0; i < 13; i++)
            {
                DrawThickLine(tex, points[i], points[(i + 1) % 13], Color.black, 6);
            }
            tex.Apply();

            // Save
            byte[] bytes = tex.EncodeToPNG();
            string dir = Application.dataPath + "/_TilingGame/Sprites";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string relativePath = "Assets/_TilingGame/Sprites/HatMonotile.png";
            File.WriteAllBytes(dir + "/HatMonotile.png", bytes);
            AssetDatabase.Refresh();

            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(relativePath);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 256; // Same as Kite!
                importer.filterMode = FilterMode.Bilinear;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }

            Debug.Log($"Generated Hat sprite ({texWidth}x{texHeight}) at: {relativePath}");
        }

        private static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if ((polygon[i].y > point.y) != (polygon[j].y > point.y) &&
                    point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) /
                    (polygon[j].y - polygon[i].y) + polygon[i].x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static void DrawThickLine(Texture2D tex, Vector2 p1, Vector2 p2, Color col, int thickness)
        {
            int dist = Mathf.CeilToInt(Vector2.Distance(p1, p2));
            for (int i = 0; i <= dist; i++)
            {
                Vector2 t = Vector2.Lerp(p1, p2, (float)i / dist);
                for (int x = -thickness / 2; x <= thickness / 2; x++)
                {
                    for (int y = -thickness / 2; y <= thickness / 2; y++)
                    {
                        if (x * x + y * y <= (thickness / 2) * (thickness / 2))
                        {
                            int px = (int)t.x + x;
                            int py = (int)t.y + y;
                            if (px >= 0 && px < tex.width && py >= 0 && py < tex.height)
                                tex.SetPixel(px, py, col);
                        }
                    }
                }
            }
        }
    }
}
