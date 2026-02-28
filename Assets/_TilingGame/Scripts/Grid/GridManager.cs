using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    public class GridManager : MonoBehaviour
    {
        [Header("Grid Settings")]
        [Tooltip("The side length of the bounding hexagon containing 6 kites.")]
        public float hexSize = 1f;

        [Tooltip("Distance from the Hexagon Center to the Sprite Pivot. Tweak this if your kites overlap or have gaps!")]
        public float kitePivotOffset = 0.5f;

        [Tooltip("Which direction does your unrotated Kite sprite point? (e.g. 90 if it points UP, 0 if it points RIGHT, -30 if it points DOWN-RIGHT). Tweak until the center cluster forms a perfect pie!")]
        public float startingAngle = 90f;

        // A single Kite coordinate has three properties.
        // In our underlying hexagonal grid, 'q' and 'r' represent the Hexagon in axial coordinates.
        // 'k' (0 to 5) represents the specific Kite within that Hexagon.
        [System.Serializable]
        public struct KiteCoord
        {
            public int q, r, k;

            public KiteCoord(int q, int r, int k)
            {
                this.q = q;
                this.r = r;
                this.k = k;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is KiteCoord)) return false;
                KiteCoord other = (KiteCoord)obj;
                return q == other.q && r == other.r && k == other.k;
            }

            public override int GetHashCode()
            {
                return (q * 397) ^ r ^ (k * 17);
            }
        }

        private HashSet<KiteCoord> filledKites = new HashSet<KiteCoord>();

        public void SetKiteFilled(KiteCoord coord, bool filled)
        {
            if (filled) filledKites.Add(coord);
            else filledKites.Remove(coord);
        }

        public bool IsKiteFilled(KiteCoord coord)
        {
            return filledKites.Contains(coord);
        }

        public void ClearGrid()
        {
            filledKites.Clear();
        }

        [ContextMenu("Apply Generated Sprite Math")]
        public void ApplyGeneratedSpriteMath()
        {
            // These exact numbers are mathematically derived from the KiteSpriteGenerator.cs
            // The hexagon center to corner distance is 208 pixels (at 256 PPU = 0.8125 units)
            // If your Unity Sprite PPU defaulted to 100, then this should be 2.08 instead.
            hexSize = 0.8125f;
            
            // The 60-degree point of the Kite is drawn at the BOTTOM (Y=20) of the image, extending upwards to Y=228.
            // Dist from Hex Center (20) to Sprite Pivot (128) is 108 pixels.
            // 108 / 208 = 0.5192307f relative to hex size.
            kitePivotOffset = 0.5192307f;
            
            // The kite extends UPWARDS from the hex center.
            startingAngle = 90f;
            
            Debug.Log("GridManager: Applied exact mathematical proportions for the generated Kite sprite.");
        }

        /// <summary>
        /// Converts an axial (q,r) hexagon coordinate to a world space position (pointy-top hex).
        /// </summary>
        public Vector2 HexToWorld(int q, int r)
        {
            float x = hexSize * Mathf.Sqrt(3) * (q + r / 2f);
            float y = hexSize * 3f / 2f * r;
            return new Vector2(x, y);
        }

        /// <summary>
        /// Gets the world space center of a specific Kite (k) inside Hexagon (q,r).
        /// </summary>
        public Vector2 KiteToWorld(KiteCoord coord)
        {
            Vector2 hexCenter = HexToWorld(coord.q, coord.r);
            
            // The angle of this specific kite slot. k=0 is the starting angle, then clockwise (-60 per k)
            float angleDeg = startingAngle - 60f * coord.k;
            float angleRad = Mathf.PI / 180f * angleDeg;

            // Offset the pivot by the configured amount along the kite's forward axis
            float dist = kitePivotOffset * hexSize;

            return hexCenter + new Vector2(Mathf.Cos(angleRad) * dist, Mathf.Sin(angleRad) * dist);
        }

        [Header("Debug Visualization")]
        public bool drawHexGrid = true;
        public bool drawMathKiteOutlines = true;

        // --- VISUALIZATION FOR PROTOTYPING ---
        private void OnDrawGizmos()
        {
            if (!drawHexGrid && !drawMathKiteOutlines) return;

            int drawRadius = 2; // Draw a 2-hex radius grid
            
            for (int q = -drawRadius; q <= drawRadius; q++)
            {
                int r1 = Mathf.Max(-drawRadius, -q - drawRadius);
                int r2 = Mathf.Min(drawRadius, -q + drawRadius);
                for (int r = r1; r <= r2; r++)
                {
                    Vector2 hexCenter = HexToWorld(q, r);
                    
                    if (drawHexGrid)
                    {
                        Gizmos.color = Color.gray;
                        Gizmos.DrawWireSphere(hexCenter, 0.1f);
                    }

                    for (int k = 0; k < 6; k++)
                    {
                        KiteCoord coord = new KiteCoord(q, r, k);
                        Vector2 kitePivot = KiteToWorld(coord);
                        
                        // Draw Pivot Points
                        if (drawHexGrid)
                        {
                            if (filledKites != null && filledKites.Contains(coord))
                                Gizmos.color = Color.red;
                            else
                                Gizmos.color = Color.green;

                            Gizmos.DrawWireSphere(kitePivot, 0.05f);
                        }

                        // Draw the TRUE Mathematical Kite Outline to help the user align their sprite
                        if (drawMathKiteOutlines)
                        {
                            Gizmos.color = Color.cyan;

                            // The math kite has 4 corners:
                            // 1. Center of the hex
                            Vector2 pCenter = hexCenter;
                            
                            // 2. The outer corner of the hexagon (Angle = startingAngle - k*60)
                            float cornerAngleDeg = startingAngle - k * 60f;
                            float cornerAngleRad = Mathf.PI / 180f * cornerAngleDeg;
                            Vector2 pOuterCorner = hexCenter + new Vector2(Mathf.Cos(cornerAngleRad) * hexSize, Mathf.Sin(cornerAngleRad) * hexSize);

                            // 3 & 4. The midpoints of the two adjacent edges of the hexagon
                            float mid1AngleDeg = cornerAngleDeg - 30f;
                            float mid1AngleRad = Mathf.PI / 180f * mid1AngleDeg;
                            Vector2 pMid1 = hexCenter + new Vector2(Mathf.Cos(mid1AngleRad) * (Mathf.Sqrt(3f)/2f * hexSize), Mathf.Sin(mid1AngleRad) * (Mathf.Sqrt(3f)/2f * hexSize));

                            float mid2AngleDeg = cornerAngleDeg + 30f;
                            float mid2AngleRad = Mathf.PI / 180f * mid2AngleDeg;
                            Vector2 pMid2 = hexCenter + new Vector2(Mathf.Cos(mid2AngleRad) * (Mathf.Sqrt(3f)/2f * hexSize), Mathf.Sin(mid2AngleRad) * (Mathf.Sqrt(3f)/2f * hexSize));

                            // Draw the outline
                            Gizmos.DrawLine(pCenter, pMid1);
                            Gizmos.DrawLine(pMid1, pOuterCorner);
                            Gizmos.DrawLine(pOuterCorner, pMid2);
                            Gizmos.DrawLine(pMid2, pCenter);
                        }
                    }
                }
            }
        }
    }
}
