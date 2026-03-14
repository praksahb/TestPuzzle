using TMPro;
using UnityEngine;
using ColorGame.Core;

namespace ColorGame.UI
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class TimerDisplay : MonoBehaviour
    {
        private TextMeshProUGUI _timerText;

        private void Awake()
        {
            _timerText = GetComponent<TextMeshProUGUI>();
        }

        private void OnEnable()
        {
            GameEvents.OnGameTimeUpdated += UpdateTimerDisplay;
        }

        private void OnDisable()
        {
            GameEvents.OnGameTimeUpdated -= UpdateTimerDisplay;
        }

        private void UpdateTimerDisplay(float currentTime)
        {
            _timerText.text = $"Time: {currentTime:F1}s";
        }
    }
}
