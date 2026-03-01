using UnityEngine;

namespace TMKOC.Games.TilingGame
{
    public class PieceSpawner : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("The prefab to spawn (e.g., SingleKite or FullHat).")]
        public Piece piecePrefab;
        
        [Tooltip("The main camera used to calculate screen edges.")]
        public Camera mainCamera;

        [Tooltip("Distance outside the screen to spawn.")]
        public float spawnPadding = 2f;
        
        public void SpawnNewPiece()
        {
            if (piecePrefab == null || mainCamera == null)
            {
                Debug.LogWarning("PieceSpawner: Missing prefab or camera reference!");
                return;
            }
            
            // Calculate a random point inside the camera view
            Vector2 spawnPos = GetRandomViewportPosition();

            // Instantiate with strict Z-only rotation (0 for X and Y)
            float spawnZ = 0f; // Can be randomized later, but must be a multiple of 30
            Piece newPiece = Instantiate(piecePrefab, spawnPos, Quaternion.Euler(0f, 0f, spawnZ));
            
            // Keep the hierarchy clean
            newPiece.transform.SetParent(this.transform);
        }

        private Vector2 GetRandomViewportPosition()
        {
            float orthoSize = mainCamera.orthographicSize;
            float aspect = mainCamera.aspect;
            
            // Keep it 80% constrained within the screen so it doesn't spawn exactly on the edge
            float camWidth = orthoSize * aspect * 0.8f;
            float camHeight = orthoSize * 0.8f;

            Vector2 camPos = mainCamera.transform.position;

            float x = Random.Range(-camWidth, camWidth);
            float y = Random.Range(-camHeight, camHeight);

            return camPos + new Vector2(x, y);
        }
    }
}
