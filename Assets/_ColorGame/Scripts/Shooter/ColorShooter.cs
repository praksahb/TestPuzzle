using System.Collections;
using UnityEngine;

namespace ColorGame.Shooter
{
    public enum ColorChannel
    {
        Red,
        Green,
        Blue
    }

    public class ColorShooter : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject _colorBallPrefab;
        [SerializeField] private Transform _playerTransform;

        [Header("Settings (Overrides Config if assigned)")]
        [SerializeField] private float _fireRate = 1.0f;
        [SerializeField] private float _ballSpeed = 3.0f;
        [SerializeField] private byte _subtractionAmount = 10;
        [SerializeField] private ColorChannel _shooterChannel;

        private void Start()
        {
            SetShooterVisuals();

            if (_playerTransform == null)
            {
                Debug.LogWarning($"ColorShooter {gameObject.name} lacks a player reference. Shooting disabled.");
                return;
            }

            StartCoroutine(ShootingRoutine());
        }

        private void SetShooterVisuals()
        {
            var spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer != null)
            {
                spriteRenderer.color = _shooterChannel switch
                {
                    ColorChannel.Red => Color.red,
                    ColorChannel.Green => Color.green,
                    ColorChannel.Blue => Color.blue,
                    _ => Color.white
                };
            }
        }

        private IEnumerator ShootingRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(_fireRate);
                FireBall();
            }
        }

        private void FireBall()
        {
            if (_colorBallPrefab == null || _playerTransform == null) return;

            GameObject ballObj = Instantiate(_colorBallPrefab, transform.position, Quaternion.identity);
            ColorBall colorBall = ballObj.GetComponent<ColorBall>();

            if (colorBall != null)
            {
                Color32 subtractionData = GetSubtractionData();
                Color visualColor = GetVisualColor();
                Vector2 direction = (_playerTransform.position - transform.position).normalized;
                Vector2 velocity = direction * _ballSpeed;

                colorBall.Initialize(subtractionData, visualColor, velocity);
            }
        }

        private Color32 GetSubtractionData()
        {
            return _shooterChannel switch
            {
                ColorChannel.Red => new Color32(_subtractionAmount, 0, 0, 255),
                ColorChannel.Green => new Color32(0, _subtractionAmount, 0, 255),
                ColorChannel.Blue => new Color32(0, 0, _subtractionAmount, 255),
                _ => new Color32(0, 0, 0, 255)
            };
        }

        private Color GetVisualColor()
        {
            return _shooterChannel switch
            {
                ColorChannel.Red => Color.red,
                ColorChannel.Green => Color.green,
                ColorChannel.Blue => Color.blue,
                _ => Color.white
            };
        }
    }
}
