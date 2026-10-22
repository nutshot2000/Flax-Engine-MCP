using System;
using System.IO;
using FlaxEngine;
using FlaxEngine.GUI;

namespace Game
{
    /// <summary>Crosshair, health, ammo, wave/score and the title/defeat screens.</summary>
    public class ShooterHud : Control
    {
        readonly ShooterGame game;
        readonly Font small, normal, big, title;
        static readonly Color Panel = new Color(0.03f, 0.04f, 0.06f, 0.82f), Accent = new Color(1f, 0.55f, 0.12f), White = new Color(0.94f, 0.96f, 1f), Muted = new Color(0.6f, 0.66f, 0.74f), Danger = new Color(1f, 0.2f, 0.18f);

        public ShooterHud(ShooterGame state)
        {
            game = state;
            var font = Content.LoadAsync<FontAsset>(Path.Combine(Globals.EngineContentFolder, "Editor/Fonts/Roboto-Regular.flax"));
            font.WaitForLoaded();
            small = font.CreateFont(14);
            normal = font.CreateFont(22);
            big = font.CreateFont(40);
            title = font.CreateFont(56);
        }

        static void Text(Font f, string text, float x, float y, Color c) => Render2D.DrawText(f, text, c, new Float2(x, y));

        public override void Draw()
        {
            if (Parent != null) Size = Parent.Size;
            float w = Width, h = Height, cx = w / 2, cy = h / 2;
            var player = game.PlayerControl;

            if (game.DamageFlash > 0) Render2D.FillRectangle(new Rectangle(0, 0, w, h), new Color(0.8f, 0, 0, game.DamageFlash * 0.9f));

            if (game.Phase == "playing" && player != null)
            {
                // Crosshair, with a hit marker after landing a shot.
                var cross = player.HitMarker > 0 ? Danger : White;
                Render2D.FillRectangle(new Rectangle(cx - 12, cy - 1, 8, 2), cross);
                Render2D.FillRectangle(new Rectangle(cx + 4, cy - 1, 8, 2), cross);
                Render2D.FillRectangle(new Rectangle(cx - 1, cy - 12, 2, 8), cross);
                Render2D.FillRectangle(new Rectangle(cx - 1, cy + 4, 2, 8), cross);
                if (player.HitMarker > 0) Render2D.DrawRectangle(new Rectangle(cx - 9, cy - 9, 18, 18), Danger, 2);

                float hp = game.PlayerHp / Math.Max(1, game.PlayerHealth.Max);
                Render2D.FillRectangle(new Rectangle(24, h - 74, 300, 50), Panel);
                Text(small, "HEALTH", 40, h - 68, Muted);
                Render2D.FillRectangle(new Rectangle(40, h - 46, 268, 10), new Color(0.2f, 0.22f, 0.26f));
                Render2D.FillRectangle(new Rectangle(40, h - 46, 268 * hp, 10), hp > 0.3f ? new Color(0.35f, 0.9f, 0.45f) : Danger);

                Render2D.FillRectangle(new Rectangle(w - 224, h - 74, 200, 50), Panel);
                Text(small, player.Reloading ? "RELOADING" : "AMMO", w - 208, h - 68, player.Reloading ? Accent : Muted);
                if (player.Reloading) Render2D.FillRectangle(new Rectangle(w - 208, h - 46, 168 * player.ReloadProgress, 10), Accent);
                else Text(normal, $"{player.Ammo} / {player.MagazineSize}", w - 208, h - 52, player.Ammo <= 5 ? Danger : White);

                Render2D.FillRectangle(new Rectangle(24, 22, 280, 64), Panel);
                Render2D.FillRectangle(new Rectangle(24, 22, 4, 64), Accent);
                Text(small, $"WAVE {game.Wave}   ·   {game.EnemiesAlive + game.PendingSpawns} HOSTILE", 42, 30, Accent);
                Text(normal, $"SCORE  {game.Score}", 42, 52, White);
            }
            else
            {
                Render2D.FillRectangle(new Rectangle(0, 0, w, h), new Color(0.01f, 0.01f, 0.02f, 0.7f));
                float left = cx - 320, top = cy - 170;
                Render2D.FillRectangle(new Rectangle(left, top, 640, 340), Panel);
                Render2D.FillRectangle(new Rectangle(left, top, 640, 3), game.Phase == "lost" ? Danger : Accent);
                Text(title, game.Phase == "lost" ? "YOU DIED" : "HOLD THE LINE", left + 36, top + 28, White);
                Text(normal, game.Phase == "lost" ? $"Wave {game.Wave}  ·  {game.Kills} kills  ·  {game.Score} points" : "Survive endless drone waves.", left + 38, top + 122, Muted);
                Text(small, "WASD  move     SHIFT  sprint     SPACE  jump", left + 38, top + 176, Muted);
                Text(small, "MOUSE  aim / fire     R  reload     ESC  release mouse", left + 38, top + 200, Muted);
                Render2D.FillRectangle(new Rectangle(left + 38, top + 254, 564, 50), Accent);
                Text(normal, game.Phase == "lost" ? "PRESS ENTER OR R TO RETRY" : "PRESS ENTER TO START", left + 38 + (game.Phase == "lost" ? 140 : 168), top + 266, new Color(0.05f, 0.04f, 0.03f));
            }
        }
    }
}
