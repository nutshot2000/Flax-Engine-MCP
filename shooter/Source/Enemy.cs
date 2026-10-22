using System;
using FlaxEngine;

namespace Game
{
    /// <summary>Ground drone: paths to the player over the navmesh, keeps its distance and fires inaccurate shots.</summary>
    public class Enemy : Script
    {
        public float Speed = 300, StopDistance = 700, AttackRange = 1800, FireInterval = 1.3f, Accuracy = 0.5f, ShotDamage = 8, Gravity = 1800;

        public bool Dead => health == null || health.Dead;
        public int PathPoints => path?.Length ?? 0;
        public bool CanSeePlayer { get; private set; }

        static readonly Random random = new Random();
        CharacterController controller;
        Health health;
        PointLight eye;
        Vector3[] path;
        int pathIndex;
        float repath, cooldown, vertical, flash, strafe;

        public override void OnStart()
        {
            controller = Actor as CharacterController;
            Actor.Layer = ShooterGame.EnemyLayer;
            health = Actor.GetScript<Health>();
            health.Damaged += (amount, from) => flash = 0.08f;
            health.Died += () => ShooterGame.Instance?.EnemyKilled(this);
            eye = Actor.GetChild<PointLight>();
            cooldown = 0.8f + (float)random.NextDouble();
            strafe = random.Next(2) == 0 ? -1 : 1;
            ShooterGame.Instance?.Register(this);
        }

        public override void OnDestroy() => ShooterGame.Instance?.Unregister(this);

        public override void OnUpdate()
        {
            var game = ShooterGame.Instance;
            if (game == null || Dead || game.Phase != "playing" || game.Player == null) return;
            float dt = Math.Min(Time.DeltaTime, 0.05f);
            flash -= dt;
            if (eye != null) eye.Brightness = flash > 0 ? 14 : 4;

            var position = Actor.Position;
            var target = game.Player.Position;
            float distance = Vector3.Distance(position, target);
            CanSeePlayer = distance < AttackRange && HasLineOfSight(game, out _);

            repath -= dt;
            if (repath <= 0)
            {
                repath = 0.5f;
                path = Navigation.FindPath(position, target, out var points) && points != null && points.Length > 1 ? points : new[] { position, target };
                pathIndex = 1;
            }

            var move = Vector3.Zero;
            if (!CanSeePlayer || distance > StopDistance)
            {
                while (pathIndex < path.Length - 1 && FlatDistance(position, path[pathIndex]) < 60) pathIndex++;
                var to = path[Math.Min(pathIndex, path.Length - 1)] - position;
                to.Y = 0;
                if (to.LengthSquared > 1) move = Vector3.Normalize(to) * Speed;
            }
            else
            {
                // In range with a clear shot: circle-strafe instead of standing still.
                var toPlayer = target - position;
                toPlayer.Y = 0;
                if (toPlayer.LengthSquared > 1) move = Vector3.Cross(Vector3.Up, Vector3.Normalize(toPlayer)) * strafe * Speed * 0.5f;
            }
            if (controller.IsGrounded && vertical < 0) vertical = -30;
            vertical -= Gravity * dt;
            controller.Move(new Vector3(move.X, vertical, move.Z) * dt);

            var look = target - Actor.Position;
            look.Y = 0;
            if (look.LengthSquared > 1) Actor.Orientation = Quaternion.LookRotation(Vector3.Normalize(look), Vector3.Up);

            cooldown -= dt;
            if (CanSeePlayer && cooldown <= 0)
            {
                cooldown = FireInterval * (0.8f + 0.4f * (float)random.NextDouble());
                Fire(game);
            }
        }

        static float FlatDistance(Vector3 a, Vector3 b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

        Vector3 Muzzle => Actor.Position + Actor.Direction * 60 + new Vector3(0, 30, 0);

        bool HasLineOfSight(ShooterGame game, out RayCastHit hit)
        {
            var origin = Muzzle;
            var chest = game.Player.Position + new Vector3(0, 40, 0);
            var direction = Vector3.Normalize(chest - origin);
            return Physics.RayCast(origin, direction, out hit, AttackRange, ~(1u << ShooterGame.EnemyLayer), false) && Health.Of(hit.Collider) == game.PlayerHealth;
        }

        void Fire(ShooterGame game)
        {
            var origin = Muzzle;
            var chest = game.Player.Position + new Vector3(0, 40, 0);
            bool onTarget = random.NextDouble() < Accuracy;
            // Misses aim at a point beside the player so the tracer visibly flies past.
            var aim = onTarget ? chest : chest + new Vector3((float)(random.NextDouble() - 0.5) * 260, (float)random.NextDouble() * 120, (float)(random.NextDouble() - 0.5) * 260);
            var direction = Vector3.Normalize(aim - origin);
            var end = origin + direction * AttackRange;
            if (Physics.RayCast(origin, direction, out RayCastHit hit, AttackRange, ~(1u << ShooterGame.EnemyLayer), false))
            {
                end = hit.Point;
                Health.Of(hit.Collider)?.Damage(ShotDamage, origin);
            }
            flash = 0.06f;
            game.Tracer(origin, end, true);
            game.Sound("enemyshot");
        }
    }
}
