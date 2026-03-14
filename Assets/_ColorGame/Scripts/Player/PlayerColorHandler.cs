using UnityEngine;
using ColorGame.Core;

namespace ColorGame.Player
{
    [RequireComponent(typeof(SpriteRenderer))]
    public class PlayerColorHandler : MonoBehaviour, IColorAbsorbable
    {
        [SerializeField] private Color32 _startingColor = new Color32(255, 255, 255, 255);
        
        private SpriteRenderer _spriteRenderer;
        private Color32 _currentColor;

        private void Awake()
        {
            _spriteRenderer = GetComponent<SpriteRenderer>();
            ResetColor();
        }

        private void ResetColor()
        {
            _currentColor = _startingColor;
            UpdateVisuals();
        }

        public void AbsorbColor(Color32 incomingColor)
        {
            // Subtract color values and clamp at 0 to prevent underflow
            _currentColor.r = (byte)Mathf.Max(0, _currentColor.r - incomingColor.r);
            _currentColor.g = (byte)Mathf.Max(0, _currentColor.g - incomingColor.g);
            _currentColor.b = (byte)Mathf.Max(0, _currentColor.b - incomingColor.b);

            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            _spriteRenderer.color = _currentColor;
            GameEvents.OnPlayerColorChanged?.Invoke(_currentColor);
        }
    }
}
