using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaxEngine;

namespace Game
{
    /// <summary>Runs waves, score, shared effects and sound. Attach to the GameDirector actor.</summary>
    public class ShooterGame : Script
    {
        public const int PlayerLayer = 1, EnemyLayer = 2;
        public static ShooterGame Instance { get; private set; }

        public Actor Player;
        public Actor SpawnPoints;
        public string EnemyPrefab = "Prefabs/Drone.prefab";
        public float WaveBreak = 3;

        public string Phase { get; private set; } = "ready";
        public int Wave { get; private set; }
        public int Score { get; private set; }
        public int Kills { get; private set; }
        public int EnemiesAlive => enemies.Count(e => !e.Dead);
        public int PendingSpawns => pendingSpawns;
        public float TimeAlive { get; private set; }
        public float DamageFlash { get; private set; }
        public int SoundEvents { get; private set; }
        public float PlayerHp => PlayerHealth?.Current ?? 0;
        public Vector3 PlayerPosition => Player?.Position ?? Vector3.Zero;
        public Health PlayerHealth { get; private set; }
        public PlayerController PlayerControl { get; private set; }

        readonly List<Enemy> enemies = new List<Enemy>();
        readonly List<(StaticModel model, float life)> effects = new List<(StaticModel, float)>();
        readonly Dictionary<string, AudioSource> sounds = new Dictionary<string, AudioSource>();
        Prefab prefab;
        Vector3 playerSpawn;
        int pendingSpawns;
        float spawnTimer, breakTimer;
        Model cube, sphere;
        MaterialBase tracerMaterial, enemyTracerMaterial, impactMaterial;
        ShooterHud hud;

        public override void OnEnable() => Instance = this;
        public override void OnDisable() { if (Instance == this) Instance = null; }

        public override void OnStart()
        {
            Instance = this;
            PlayerHealth = Player.GetScript<Health>();
            PlayerControl = Player.GetScript<PlayerController>();
            PlayerHealth.Damaged += (amount, from) => { DamageFlash = 0.35f; Sound("hurt"); };
            PlayerHealth.Died += () => { Phase = "lost"; Sound("lose"); ReleaseCursor(); };
            playerSpawn = Player.Position;
            prefab = Content.LoadAsync<Prefab>(Path.Combine(Globals.ProjectContentFolder, EnemyPrefab));
            cube = Content.LoadAsync<Model>(Path.Combine(Globals.EngineContentFolder, "Editor/Primitives/Cube.flax"));
            sphere = Content.LoadAsync<Model>(Path.Combine(Globals.EngineContentFolder, "Editor/Primitives/Sphere.flax"));
            tracerMaterial = Content.LoadAsync<MaterialBase>(Path.Combine(Globals.ProjectContentFolder, "Materials/Tracer.flax"));
            enemyTracerMaterial = Content.LoadAsync<MaterialBase>(Path.Combine(Globals.ProjectContentFolder, "Materials/EnemyTracer.flax"));
            impactMaterial = Content.LoadAsync<MaterialBase>(Path.Combine(Globals.ProjectContentFolder, "Materials/Impact.flax"));
            foreach (var name in new[] { "rifle", "hit", "enemyshot", "hurt", "kill", "reload", "wave", "jump", "lose" })
            {
                var clip = Content.LoadAsync<AudioClip>(Path.Combine(Globals.ProjectContentFolder, "Audio", name + ".flax"));
                if (clip == null) continue;
                var source = Actor.AddChild<AudioSource>();
                source.Name = "Sound_" + name;
                source.Clip = clip;
                source.AllowSpatialization = false;
                source.Volume = name == "rifle" ? 0.25f : 0.4f;
                sounds[name] = source;
            }
            var canvas = Actor.AddChild<UICanvas>();
            canvas.Name = "HUD";
            // Camera-space so game-view screenshots include the HUD. The canvas fills the view at this distance; IgnoreDepth keeps walls from covering it.
            canvas.RenderMode = CanvasRenderMode.CameraSpace;
            canvas.RenderCamera = PlayerControl.View;
            canvas.Distance = 700;
            canvas.IgnoreDepth = true;
            hud = new ShooterHud(this) { Parent = canvas.GUI };
        }

        public override void OnDestroy() => hud?.Dispose();

        public void StartGame()
        {
            foreach (var enemy in enemies.ToArray()) if (enemy != null && enemy.Actor != null) Destroy(enemy.Actor);
            enemies.Clear();
            Player.Position = playerSpawn;
            PlayerHealth.Restore();
            PlayerControl.ResetState();
            Wave = Score = Kills = 0;
            TimeAlive = DamageFlash = 0;
            pendingSpawns = 0;
            breakTimer = 1;
            Phase = "playing";
            cursorReleased = false;
        }

        bool cursorReleased;
        void ReleaseCursor() { cursorReleased = true; ApplyCursor(false); }
        static void ApplyCursor(bool locked)
        {
            var mode = locked ? CursorLockMode.Locked : CursorLockMode.None;
            if (Screen.CursorLock != mode) Screen.CursorLock = mode;
            if (Screen.CursorVisible == locked) Screen.CursorVisible = !locked;
        }

        public void Register(Enemy enemy) { if (!enemies.Contains(enemy)) enemies.Add(enemy); }
        public void Unregister(Enemy enemy) => enemies.Remove(enemy);

        public void EnemyKilled(Enemy enemy)
        {
            Kills++;
            Score += 100 * Math.Max(1, Wave);
            Sound("kill");
            Impact(enemy.Actor.Position + new Vector3(0, 40, 0), true, 3);
            Destroy(enemy.Actor, 0.05f);
        }

        public override void OnUpdate()
        {
            float dt = Math.Min(Time.DeltaTime, 0.05f);
            DamageFlash = Math.Max(0, DamageFlash - dt);
            UpdateEffects(dt);
            if (Phase != "playing")
            {
                ApplyCursor(false);
                if (Input.GetKeyDown(KeyboardKeys.Return) || (Phase == "lost" && Input.GetKeyDown(KeyboardKeys.R))) StartGame();
                return;
            }
            // Keep the mouse captured while the game view has focus; Esc releases it until the next click.
            if (Input.GetKeyDown(KeyboardKeys.Escape)) cursorReleased = true;
            if (Input.GetMouseButtonDown(MouseButton.Left) && Engine.HasGameViewportFocus) cursorReleased = false;
            ApplyCursor(Engine.HasGameViewportFocus && !cursorReleased);
            TimeAlive += dt;
            if (Player.Position.Y < -500) PlayerHealth.Damage(1000, Player.Position);

            if (pendingSpawns > 0)
            {
                spawnTimer -= dt;
                if (spawnTimer <= 0) { SpawnEnemy(); pendingSpawns--; spawnTimer = 0.6f; }
            }
            else if (EnemiesAlive == 0)
            {
                breakTimer -= dt;
                if (breakTimer <= 0) { Wave++; pendingSpawns = Math.Min(2 + Wave * 2, 12); spawnTimer = 0; breakTimer = WaveBreak; Sound("wave"); }
            }
        }

        void SpawnEnemy()
        {
            if (prefab == null || SpawnPoints == null || SpawnPoints.ChildrenCount == 0) return;
            // Prefer spawn points away from the player.
            var points = SpawnPoints.Children.OrderByDescending(p => Vector3.Distance(p.Position, Player.Position)).Take(Math.Max(1, SpawnPoints.ChildrenCount / 2)).ToArray();
            var point = points[(Kills + pendingSpawns + Wave) % points.Length];
            var actor = PrefabManager.SpawnPrefab(prefab, Actor.Scene, new Transform(point.Position));
            actor.Name = "Drone_" + Wave + "_" + pendingSpawns;
        }

        public void Sound(string name)
        {
            if (sounds.TryGetValue(name, out var source)) { source.Stop(); source.Play(); }
            SoundEvents++;
        }

        public void Tracer(Vector3 from, Vector3 to, bool enemy)
        {
            var length = Vector3.Distance(from, to);
            if (length < 1 || cube == null) return;
            var tracer = Effect(cube, enemy ? enemyTracerMaterial : tracerMaterial, 0.06f);
            tracer.Position = (from + to) * 0.5f;
            tracer.Orientation = Quaternion.LookRotation(Vector3.Normalize(to - from), Vector3.Up);
            tracer.Scale = new Vector3(0.025f, 0.025f, length / 100f);
        }

        public void Impact(Vector3 point, bool flesh, float size = 1)
        {
            if (sphere == null) return;
            var spark = Effect(sphere, flesh ? enemyTracerMaterial : impactMaterial, 0.15f);
            spark.Position = point;
            spark.Scale = new Vector3(0.18f * size);
        }

        StaticModel Effect(Model model, MaterialBase material, float life)
        {
            var effect = Actor.Scene.AddChild<StaticModel>();
            effect.Name = "Fx";
            effect.Model = model;
            if (material != null) effect.SetMaterial(0, material);
            effects.Add((effect, life));
            return effect;
        }

        void UpdateEffects(float dt)
        {
            for (int i = effects.Count - 1; i >= 0; i--)
            {
                var (model, life) = effects[i];
                life -= dt;
                if (life <= 0 || model == null) { if (model != null) Destroy(model); effects.RemoveAt(i); }
                else effects[i] = (model, life);
            }
        }
    }
}
