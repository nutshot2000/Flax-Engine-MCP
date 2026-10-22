using System;
using FlaxEngine;

namespace Game
{
    /// <summary>First-person movement, mouse look and a hitscan rifle. Attach to the player's CharacterController.</summary>
    public class PlayerController : Script
    {
        public Camera View;
        public float WalkSpeed = 520, SprintSpeed = 820, JumpSpeed = 620, Gravity = 1800, LookSensitivity = 0.12f;
        public int MagazineSize = 24;
        public float ShotDamage = 34, FireInterval = 0.11f, ReloadTime = 1.4f, Range = 8000;

        public int Ammo { get; private set; }
        public bool Reloading => reloadLeft > 0;
        public float ReloadProgress => Reloading ? 1 - reloadLeft / ReloadTime : 1;
        public int ShotsFired { get; private set; }
        public int Hits { get; private set; }
        public float HitMarker { get; private set; }
        public float Yaw => yaw;
        public float Pitch => pitch;
        public bool Grounded => controller != null && controller.IsGrounded;
        public Vector3 EyePosition => View != null ? View.Position : Actor.Position;

        CharacterController controller;
        PointLight muzzle;
        float yaw, pitch, vertical, cooldown, reloadLeft, muzzleLeft;
        // Bot input (automated playtests) goes through the same movement and firing paths as keyboard and mouse.
        float botX, botZ, botTime;
        bool botJump;
        int botShots;

        public override void OnStart()
        {
            controller = Actor as CharacterController;
            Actor.Layer = ShooterGame.PlayerLayer;
            yaw = Actor.EulerAngles.Y;
            if (View != null)
            {
                muzzle = View.AddChild<PointLight>();
                muzzle.Name = "MuzzleFlash";
                muzzle.LocalPosition = new Vector3(18, -14, 70);
                muzzle.Color = new Color(1f, 0.75f, 0.35f);
                muzzle.Radius = 400;
                muzzle.Brightness = 0;
                muzzle.ShadowsMode = ShadowsCastingMode.None;
            }
            ResetState();
        }

        public void ResetState()
        {
            Ammo = MagazineSize;
            reloadLeft = cooldown = muzzleLeft = HitMarker = 0;
            vertical = 0;
            pitch = 0;
            botTime = botShots = 0;
            ShotsFired = Hits = 0;
        }

        public void BotMove(float x, float z, float seconds, bool jump = false)
        {
            botX = Mathf.Clamp(x, -1, 1);
            botZ = Mathf.Clamp(z, -1, 1);
            botTime = Mathf.Clamp(seconds, 0, 5);
            botJump = jump;
        }
        public void BotLook(float yawDegrees, float pitchDegrees)
        {
            yaw = yawDegrees;
            pitch = Mathf.Clamp(pitchDegrees, -85, 85);
        }
        public void BotAimAt(Vector3 target)
        {
            var d = target - EyePosition;
            if (d.LengthSquared < 1) return;
            d.Normalize();
            BotLook((float)Math.Atan2(d.X, d.Z) * Mathf.RadiansToDegrees, -(float)Math.Asin(d.Y) * Mathf.RadiansToDegrees);
        }
        public void BotFire(int shots = 1) => botShots += Math.Max(0, shots);
        public void Reload()
        {
            if (!Reloading && Ammo < MagazineSize) { reloadLeft = ReloadTime; ShooterGame.Instance?.Sound("reload"); }
        }

        public override void OnUpdate()
        {
            var game = ShooterGame.Instance;
            float dt = Math.Min(Time.DeltaTime, 0.05f);
            muzzleLeft -= dt;
            HitMarker = Math.Max(0, HitMarker - dt);
            if (muzzle != null) muzzle.Brightness = muzzleLeft > 0 ? 9 : 0;
            if (game == null || game.Phase != "playing") return;

            // Game-view focus, not app focus: in the editor the app is focused while the game view may not be.
            bool humanInput = Engine.HasGameViewportFocus;
            if (humanInput && Screen.CursorLock == CursorLockMode.Locked)
            {
                // A locked cursor is re-centred every frame, so the raw position delta reads zero in the editor;
                // the "Mouse X"/"Mouse Y" axes (Content/Settings/InputSettings.json) report the actual movement.
                float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
                if (mx == 0 && my == 0) { var delta = Input.MousePositionDelta; mx = delta.X; my = delta.Y; }
                yaw += mx * LookSensitivity;
                pitch = Mathf.Clamp(pitch + my * LookSensitivity, -85, 85);
            }
            Actor.Orientation = Quaternion.Euler(0, yaw, 0);
            if (View != null) View.LocalOrientation = Quaternion.Euler(pitch, 0, 0);

            float x = 0, z = 0;
            bool jump = false, sprint = false, fire = false;
            if (botTime > 0)
            {
                botTime -= dt;
                x = botX; z = botZ; jump = botJump; botJump = false;
            }
            else if (humanInput)
            {
                x = (Input.GetKey(KeyboardKeys.D) ? 1 : 0) - (Input.GetKey(KeyboardKeys.A) ? 1 : 0);
                z = (Input.GetKey(KeyboardKeys.W) ? 1 : 0) - (Input.GetKey(KeyboardKeys.S) ? 1 : 0);
                jump = Input.GetKeyDown(KeyboardKeys.Spacebar);
                sprint = Input.GetKey(KeyboardKeys.Shift);
                fire = Input.GetMouseButton(MouseButton.Left);
                if (Input.GetKeyDown(KeyboardKeys.R)) Reload();
            }
            if (botShots > 0) fire = true;

            var facing = Quaternion.Euler(0, yaw, 0);
            var move = Vector3.Transform(new Vector3(x, 0, z), facing);
            if (move.LengthSquared > 1) move.Normalize();
            move *= sprint ? SprintSpeed : WalkSpeed;
            if (controller.IsGrounded)
            {
                if (vertical < 0) vertical = -30;
                if (jump) { vertical = JumpSpeed; game.Sound("jump"); }
            }
            vertical -= Gravity * dt;
            controller.Move(new Vector3(move.X, vertical, move.Z) * dt);

            UpdateWeapon(game, dt, fire);
        }

        void UpdateWeapon(ShooterGame game, float dt, bool fire)
        {
            cooldown -= dt;
            if (Reloading)
            {
                reloadLeft -= dt;
                if (reloadLeft <= 0) { reloadLeft = 0; Ammo = MagazineSize; }
                return;
            }
            if (!fire || cooldown > 0) return;
            if (Ammo <= 0) { Reload(); botShots = 0; return; }
            cooldown = FireInterval;
            Ammo--;
            ShotsFired++;
            if (botShots > 0) botShots--;
            muzzleLeft = 0.05f;
            game.Sound("rifle");

            var origin = EyePosition;
            var direction = View != null ? View.Direction : Actor.Direction;
            var end = origin + direction * Range;
            if (Physics.RayCast(origin, direction, out RayCastHit hit, Range, ~(1u << ShooterGame.PlayerLayer), false))
            {
                end = hit.Point;
                var target = Health.Of(hit.Collider);
                if (target != null && !target.Dead)
                {
                    target.Damage(ShotDamage, origin);
                    Hits++;
                    HitMarker = 0.18f;
                    game.Sound("hit");
                }
                game.Impact(hit.Point, target != null);
            }
            var muzzlePosition = muzzle != null ? muzzle.Position : origin;
            game.Tracer(muzzlePosition, end, false);
            if (Ammo == 0) Reload();
        }
    }
}
