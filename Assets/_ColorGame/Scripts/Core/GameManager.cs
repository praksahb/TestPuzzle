using UnityEngine;

namespace ColorGame.Core
{
    public class GameManager : MonoBehaviour
    {
        private float _gameTimer;
        private bool _isGameComplete;

        private void OnEnable()
        {
            GameEvents.OnPlayerColorChanged += HandlePlayerColorChanged;
        }

        private void OnDisable()
        {
            GameEvents.OnPlayerColorChanged -= HandlePlayerColorChanged;
        }

        private void Update()
        {
            if (_isGameComplete) return;

            _gameTimer += Time.deltaTime;
            GameEvents.OnGameTimeUpdated?.Invoke(_gameTimer);
        }

        private void HandlePlayerColorChanged(Color32 newColor)
        {
            if (_isGameComplete) return;

            if (newColor.r <= 0 && newColor.g <= 0 && newColor.b <= 0)
            {
                TriggerGameComplete();
            }
        }

        private void TriggerGameComplete()
        {
            _isGameComplete = true;
            GameEvents.OnGameComplete?.Invoke(_gameTimer);
            Debug.Log($"Game Complete! Final Time: {_gameTimer:F2} seconds.");
        }
    }
}
