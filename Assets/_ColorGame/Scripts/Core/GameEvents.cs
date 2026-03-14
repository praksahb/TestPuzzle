using System;
using UnityEngine;

namespace ColorGame.Core
{
    public static class GameEvents
    {
        public static Action<Color32> OnPlayerColorChanged;
        public static Action<float> OnGameTimeUpdated;
        public static Action<float> OnGameComplete;
    }
}
