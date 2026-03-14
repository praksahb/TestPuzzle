using TMPro;
using UnityEngine;
using ColorGame.Core;

namespace ColorGame.UI
{
    public class GameOverPanel : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _finalTimeText;
        [SerializeField] private GameObject _panelContent; // The visual root of the panel

        private void OnEnable()
        {
            GameEvents.OnGameComplete += ShowGameOver;
        }

        private void OnDisable()
        {
            GameEvents.OnGameComplete -= ShowGameOver;
        }

        private void Start()
        {
            if (_panelContent != null)
            {
                _panelContent.SetActive(false);
            }
        }

        private void ShowGameOver(float finalTime)
        {
            if (_finalTimeText != null)
            {
                _finalTimeText.text = $"Final Time: {finalTime:F2} seconds";
            }
            
            if (_panelContent != null)
            {
                _panelContent.SetActive(true);
            }
        }
    }
}
