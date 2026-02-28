using UnityEngine;

namespace TMKOC.Games.TilingGame
{
    public class InputManager : MonoBehaviour
    {
        [Header("References")]
        public GridManager gridManager;
        public Camera mainCamera;

        [Header("Swipe Settings")]
        public float minSwipeDistance = 50f;
        
        private Piece currentDraggedPiece;
        private Vector2 dragStartPos;
        private bool isSwiping = false;

        void Update()
        {
            // --- NEW: Scroll Wheel Rotation (Frictionless for PC) ---
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.01f)
            {
                HandleScrollRotation(scroll > 0 ? 1 : -1);
            }

            // --- NEW: Right Click to Rotate (Frictionless for PC) ---
            if (Input.GetMouseButtonDown(1))
            {
                HandleRightClickRotation();
            }

            if (Input.GetMouseButtonDown(0))
            {
                HandlePointerDown();
            }
            else if (Input.GetMouseButton(0) && currentDraggedPiece != null)
            {
                HandlePointerDrag();
            }
            else if (Input.GetMouseButtonUp(0))
            {
                HandlePointerUp();
            }
        }

        private float dragStartTime;
        private bool isCurrentlyDragging = false;
        private float holdToDragDelay = 0.15f; 
        
        private Vector2 dragOffset;

        private void HandlePointerDown()
        {
            dragStartPos = Input.mousePosition;
            dragStartTime = Time.time;
            isCurrentlyDragging = false;

            Vector2 worldPos = mainCamera.ScreenToWorldPoint(Input.mousePosition);
            Collider2D hit = Physics2D.OverlapPoint(worldPos);

            if (hit != null)
            {
                Piece piece = hit.GetComponentInParent<Piece>();
                if (piece != null)
                {
                    // If the piece is already part of the static core building, DO NOT PICK IT UP
                    if (piece.transform.parent != null && piece.transform.parent.GetComponent<CoreCluster>() != null)
                    {
                        return;
                    }

                    currentDraggedPiece = piece;
                    currentDraggedPiece.isCurrentlyDragged = true;
                    currentDraggedPiece.isFloating = false; // Pause floating while dragging
                    
                    // Store the offset from the mouse to the piece center
                    dragOffset = (Vector2)currentDraggedPiece.transform.position - worldPos;
                    
                    hit.enabled = false;
                }
            }
        }

        private void HandlePointerDrag()
        {
            Vector2 worldPos = mainCamera.ScreenToWorldPoint(Input.mousePosition);

            if (currentDraggedPiece == null) return;

            // Phase Separation: Wait before physical dragging
            if (Time.time - dragStartTime > holdToDragDelay)
            {
                isCurrentlyDragging = true;
                
                // Move the piece following the finger, maintaining the original offset
                currentDraggedPiece.transform.position = worldPos + dragOffset;
            }
        }

        private void HandlePointerUp()
        {
            if (currentDraggedPiece != null)
            {
                Vector2 mouseDelta = (Vector2)Input.mousePosition - dragStartPos;
                
                // If they never triggered the movement phase AND covered the distance, it's a swipe!
                if (!isCurrentlyDragging && mouseDelta.magnitude > minSwipeDistance)
                {
                    if (Mathf.Abs(mouseDelta.x) > Mathf.Abs(mouseDelta.y))
                    {
                        currentDraggedPiece.ToggleMirrorHorizontal();
                    }
                    else
                    {
                        currentDraggedPiece.ToggleMirrorVertical();
                    }
                }
                // --- NEW: Tap-to-Rotate ---
                // If they released quickly and didn't move the mouse much, it's a TAP.
                else if (!isCurrentlyDragging && mouseDelta.magnitude <= minSwipeDistance)
                {
                    currentDraggedPiece.RotateManual(1);
                }
                else if (isCurrentlyDragging)
                {
                    // It was an intentional drag and drop. 
                    if (CoreCluster.Instance != null && currentDraggedPiece != null)
                    {
                        bool snapped = CoreCluster.Instance.TryPlacePiece(currentDraggedPiece);
                        if (!snapped)
                        {
                            currentDraggedPiece.isFloating = true; // Resume floating if snap failed
                        }
                        else
                        {
                            // If it successfully snapped, lock its internal rotation target so it doesn't spin back!
                            currentDraggedPiece.ForceSnapRotation(currentDraggedPiece.transform.rotation);
                        }
                    }
                }

                if (currentDraggedPiece != null)
                {
                    Collider2D col = currentDraggedPiece.GetComponentInChildren<Collider2D>();
                    if (col != null) col.enabled = true;
                    // Ensure floating resumes if they didn't snap it or drag it
                    currentDraggedPiece.isCurrentlyDragged = false;
                    currentDraggedPiece.isFloating = true;
                }

                currentDraggedPiece = null;
                isCurrentlyDragging = false;
            }
        }

        private void HandleScrollRotation(int dir)
        {
            // If dragging, rotate that piece. Otherwise, try to find a piece under the mouse.
            if (currentDraggedPiece != null)
            {
                currentDraggedPiece.RotateManual(dir);
            }
            else
            {
                Vector2 worldPos = mainCamera.ScreenToWorldPoint(Input.mousePosition);
                Collider2D hit = Physics2D.OverlapPoint(worldPos);
                if (hit != null)
                {
                    Piece p = hit.GetComponentInParent<Piece>();
                    if (p != null) p.RotateManual(dir);
                }
            }
        }

        private void HandleRightClickRotation()
        {
            Vector2 worldPos = mainCamera.ScreenToWorldPoint(Input.mousePosition);
            Collider2D hit = Physics2D.OverlapPoint(worldPos);
            if (hit != null)
            {
                Piece p = hit.GetComponentInParent<Piece>();
                if (p != null) p.RotateManual(1);
            }
        }
    }
}
