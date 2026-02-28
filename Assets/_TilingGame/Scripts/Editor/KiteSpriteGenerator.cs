using UnityEngine;
using UnityEditor;
using System.IO;

namespace TMKOC.Games.TilingGame.Editor
{
    public class KiteSpriteGenerator
    {
        [MenuItem("Tools/Generate Clean Kite Sprite")]
        
        public static void GenerateKitePNG()
        {
            int width = 256;
            int height = 256;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            
            // Fill with transparency
            Color[] colors = new Color[width * height];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.clear;
            tex.SetPixels(colors);

            // True Mathematical Proportions of the Hexagon-Kite (60/90/120/90 degrees)
            // Left/Right X is 128 ± 90.
            // Bottom Y is 20.
            // Side Y is Bottom Y + 90 * sqrt(3) = 20 + 156 = 176.
            // Top Y is Side Y + 90 / sqrt(3) = 176 + 52 = 228.
            Vector2 top = new Vector2(128f, 228f);
            Vector2 right = new Vector2(218f, 176f);
            Vector2 bottom = new Vector2(128f, 20f);
            Vector2 left = new Vector2(38f, 176f);

            // 1. Draw solid white fill
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (IsPointInPolygon(new Vector2(x, y), top, right, bottom, left))
                    {
                        tex.SetPixel(x, y, Color.white);
                    }
                }
            }

            tex.Apply();

            // 2. Draw thick black outline
            DrawThickLine(tex, top, right, Color.black, 6);
            DrawThickLine(tex, right, bottom, Color.black, 6);
            DrawThickLine(tex, bottom, left, Color.black, 6);
            DrawThickLine(tex, left, top, Color.black, 6);

            tex.Apply();

            // 3. Save to PNG
            byte[] bytes = tex.EncodeToPNG();
            
            string dir = Application.dataPath + "/_TilingGame/Sprites";
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            string relativePath = "Assets/_TilingGame/Sprites/SingleKite.png";
            string absolutePath = dir + "/SingleKite.png";
            
            File.WriteAllBytes(absolutePath, bytes);
            AssetDatabase.Refresh();
            
            // 4. Set texture importer settings
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(relativePath);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spritePixelsPerUnit = 256; // 1 unit = 1 kite height roughly
                importer.filterMode = FilterMode.Bilinear;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }
            
            Debug.Log("Successfully generated mathematically perfect Kite Sprite without inner lines at: " + relativePath);
        }

        private static bool IsPointInPolygon(Vector2 p, Vector2 top, Vector2 right, Vector2 bottom, Vector2 left)
        {
            Vector2[] poly = new Vector2[] { top, right, bottom, left };
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                     p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
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
                for (int x = -thickness/2; x <= thickness/2; x++)
                {
                    for (int y = -thickness/2; y <= thickness/2; y++)
                    {
                        if (x*x + y*y <= (thickness/2)*(thickness/2))
                        {
                            tex.SetPixel((int)t.x + x, (int)t.y + y, col);
                        }
                    }
                }
            }
        }
    }
}
