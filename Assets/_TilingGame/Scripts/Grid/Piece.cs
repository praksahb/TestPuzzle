using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    [RequireComponent(typeof(Rigidbody2D))]
    public class Piece : MonoBehaviour
    {
        [Header("Piece Definition")]
        public bool isMirrored = false;
        
        // Degrees to rotate when the player clicks the piece manually
        [Tooltip("Degrees to rotate when the player clicks the piece manually")]
        public float manualRotationStep = 30f;

        // The 8 kite coordinates that make up this Hat piece at base rotation.
        // These are RELATIVE to the piece's pivot center.
        // We no longer track discrete rotationSteps or isMirrored here for visuals,
        // we just maintain the absolute 3D orientation of the tile.
        private Coroutine visualTween;
        private Quaternion targetRotation = Quaternion.identity;
        private Rigidbody2D rb;

        [Header("Floating Settings")]
        [Tooltip("Assign the 4 corner points (or more) of the Kite here in clockwise/counter-clockwise order")]
        public Transform[] edgeSockets;
        
        public bool isFloating = true;
        public float floatSpeed = 0.5f;
        
        private Vector2 currentDriftDir;

        private void Start()
        {
            targetRotation = transform.rotation;
            rb = GetComponent<Rigidbody2D>();
            rb.gravityScale = 0f;
            rb.bodyType = RigidbodyType2D.Kinematic;
            
            // Pick a random starting direction for the DVD screensaver effect
            float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            currentDriftDir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)).normalized;
        }

        private void FixedUpdate()
        {
            if (isFloating && rb != null && !isCurrentlyDragged)
            {
                // DVD Screensaver Bounce Logic
                if (Camera.main != null)
                {
                    Vector3 viewPos = Camera.main.WorldToViewportPoint(rb.position);
                    
                    // Bounce off horizontal edges
                    if ((viewPos.x < 0.05f && currentDriftDir.x < 0) || (viewPos.x > 0.95f && currentDriftDir.x > 0))
                    {
                        currentDriftDir.x *= -1;
                    }
                    
                    // Bounce off vertical edges
                    if ((viewPos.y < 0.05f && currentDriftDir.y < 0) || (viewPos.y > 0.95f && currentDriftDir.y > 0))
                    {
                        currentDriftDir.y *= -1;
                    }
                }

                // Kinematics don't use Force, they use direct Velocity
                rb.linearVelocity = currentDriftDir * floatSpeed;

                // Add a very slight constant spin
                rb.angularVelocity = 15f; 
            }
            else if (rb != null)
            {
                // If it's being dragged, or has snapped into the grid, stop its physical drift entirely
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;

                if (!isCurrentlyDragged)
                {
                    // If it's snapped permanently, freeze it completely from the physics simulation
                    rb.bodyType = RigidbodyType2D.Static;
                }
            }
        }

        // To track dragging state independently of floating
        public bool isCurrentlyDragged = false;

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (isFloating && !isCurrentlyDragged)
            {
                Piece otherPiece = collision.gameObject.GetComponentInParent<Piece>();
                // If we bumped into a piece that is already locked into the grid...
                if (otherPiece != null && !otherPiece.isFloating)
                {
                    // Attempt to automatically snap into the core!
                    if (CoreCluster.Instance != null)
                    {
                        bool snapped = CoreCluster.Instance.TryPlacePiece(this);
                        if (snapped)
                        {
                            // TryPlacePiece already sets isFloating to false and makes it Kinematic,
                            // so it will instantly stick to the cluster!
                        }
                    }
                }
            }
        }

        public void RotateManual(int direction = 1)
        {
            // Rotate the piece by exactly the manual step defined in the Inspector
            Quaternion zRot = Quaternion.Euler(0, 0, manualRotationStep * direction);
            targetRotation = targetRotation * zRot;
            
            if (!Application.isPlaying || !gameObject.activeInHierarchy)
            {
                transform.rotation = targetRotation;
                return;
            }
            
            if (visualTween != null) StopCoroutine(visualTween);
            visualTween = StartCoroutine(SmoothRotate(targetRotation));
        }

        public void ToggleMirrorHorizontal()
        {
            // Flip the entire piece over the World Y axis (like turning a page in a book)
            Quaternion yFlip = Quaternion.Euler(0, 180f, 0);
            targetRotation = yFlip * targetRotation;

            if (!Application.isPlaying || !gameObject.activeInHierarchy)
            {
                transform.rotation = targetRotation;
                return;
            }

            if (visualTween != null) StopCoroutine(visualTween);
            visualTween = StartCoroutine(SmoothRotate(targetRotation));
        }

        public void ToggleMirrorVertical()
        {
            // Flip the entire piece over the World X axis (like flipping a card top-to-bottom)
            Quaternion xFlip = Quaternion.Euler(180f, 0, 0);
            targetRotation = xFlip * targetRotation;

            if (!Application.isPlaying || !gameObject.activeInHierarchy)
            {
                transform.rotation = targetRotation;
                return;
            }

            if (visualTween != null) StopCoroutine(visualTween);
            visualTween = StartCoroutine(SmoothRotate(targetRotation));
        }

        private System.Collections.IEnumerator SmoothRotate(Quaternion targetRot)
        {
            Quaternion startRot = transform.rotation;
            float elapsed = 0f;
            float duration = 0.15f;

            while (elapsed < duration)
            {
                transform.rotation = Quaternion.Slerp(startRot, targetRot, elapsed / duration);
                elapsed += Time.deltaTime;
                yield return null;
            }
            transform.rotation = targetRot;
        }

        public void ForceSnapRotation(Quaternion finalRot)
        {
            if (visualTween != null) StopCoroutine(visualTween);
            
            targetRotation = finalRot;
            transform.rotation = finalRot;
            
            // Just double checking physics lock
            if (rb != null)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.constraints = RigidbodyConstraints2D.FreezeAll; // Total lockdown
            }
        }

        private void OnDrawGizmos()
        {
            if (edgeSockets == null || edgeSockets.Length < 3) return;

            int count = edgeSockets.Length;
            for (int i = 0; i < count; i++)
            {
                Transform t1 = edgeSockets[i];
                Transform t2 = edgeSockets[(i + 1) % count];

                if (t1 == null || t2 == null) continue;

                Vector2 wp1 = t1.position;
                Vector2 wp2 = t2.position;

                // Draw edge line
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(wp1, wp2);

                // Draw outward normal tick at P1
                Vector2 edgeDir = wp2 - wp1;
                // Clockwise outward normal is (-y, x). Counter-clockwise is (y, -x). 
                // We are now mathematically forcing a Clockwise sort, so we expect (-y, x) to point outward!
                Vector2 outwardNormal = new Vector2(-edgeDir.y, edgeDir.x).normalized;
                
                Gizmos.color = Color.red;
                Gizmos.DrawRay(wp1, outwardNormal * 0.3f);
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Extract Sockets from Collider")]
        public void ExtractSocketsFromCollider()
        {
            PolygonCollider2D col = GetComponentInChildren<PolygonCollider2D>();
            if (col == null)
            {
                Debug.LogError("No PolygonCollider2D found on this Piece!");
                return;
            }

            Vector2[] pts = col.points;
            
            // SORT THE POINTS GEOMETRICALLY
            // PolygonCollider2D sometimes returns points in an intersecting hourglass shape if drawn manually
            // We sort them by their angle around the center to guarantee a clean perimeter ring
            Vector2 center = Vector2.zero;
            foreach (var p in pts) center += p;
            center /= pts.Length;
            
            System.Array.Sort(pts, (a, b) => 
            {
                float aAngle = Mathf.Atan2(a.y - center.y, a.x - center.x);
                float bAngle = Mathf.Atan2(b.y - center.y, b.x - center.x);
                // Compare B to A forces a Clockwise vertex winding order
                return bAngle.CompareTo(aAngle);
            });
            
            // 1. Clean up old sockets
            Transform oldSockets = transform.Find("Sockets");
            if (oldSockets != null)
            {
                DestroyImmediate(oldSockets.gameObject);
            }

            // 2. Create parent
            GameObject socketParentObj = new GameObject("Sockets");
            socketParentObj.transform.SetParent(this.transform);
            socketParentObj.transform.localPosition = Vector3.zero;
            socketParentObj.transform.localRotation = Quaternion.identity;

            // 3. Create children
            edgeSockets = new Transform[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                GameObject p = new GameObject($"P{i+1}");
                p.transform.SetParent(socketParentObj.transform);
                // The points are strictly defined in the collider's local space
                p.transform.position = col.transform.TransformPoint(pts[i]);
                
                edgeSockets[i] = p.transform;
            }

            // Mark the object as dirty to save the prefab
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"Successfully extracted {pts.Length} precise edge sockets from the PolygonCollider2D!");
        }
#endif
    }
}
