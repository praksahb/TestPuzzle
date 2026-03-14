using TMPro;
using UnityEngine;
using ColorGame.Core;

namespace ColorGame.UI
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class ColorDisplay : MonoBehaviour
    {
        private TextMeshProUGUI _colorText;

        private void Awake()
        {
            _colorText = GetComponent<TextMeshProUGUI>();
        }

        private void OnEnable()
        {
            GameEvents.OnPlayerColorChanged += UpdateColorDisplay;
        }

        private void OnDisable()
        {
            GameEvents.OnPlayerColorChanged -= UpdateColorDisplay;
        }

        private void Start()
        {
            // Set initial state
            UpdateColorDisplay(new Color32(255, 255, 255, 255));
        }

        private void UpdateColorDisplay(Color32 newColor)
        {
            _colorText.text = $"R: {newColor.r}  G: {newColor.g}  B: {newColor.b}";
        }
    }
}
