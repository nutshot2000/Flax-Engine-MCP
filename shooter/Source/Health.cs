using System;
using FlaxEngine;

namespace Game
{
    /// <summary>Hit points shared by the player and enemies.</summary>
    public class Health : Script
    {
        public float Max = 100;
        public float Current { get; private set; } = 100;
        public bool Dead => Current <= 0;
        public event Action<float, Vector3> Damaged;
        public event Action Died;

        public override void OnStart() => Current = Max;
        public void Restore() => Current = Max;

        public void Damage(float amount, Vector3 from)
        {
            if (Dead || amount <= 0) return;
            Current = Math.Max(0, Current - amount);
            Damaged?.Invoke(amount, from);
            if (Dead) Died?.Invoke();
        }

        /// <summary>Finds the Health on an actor or its nearest ancestor, e.g. from a raycast collider.</summary>
        public static Health Of(Actor actor)
        {
            for (; actor != null; actor = actor.Parent)
            {
                var health = actor.GetScript<Health>();
                if (health != null) return health;
            }
            return null;
        }
    }
}
