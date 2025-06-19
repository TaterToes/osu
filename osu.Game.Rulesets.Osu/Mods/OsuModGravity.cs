// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.UI;
using osu.Framework.Localisation;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osuTK;

namespace osu.Game.Rulesets.Osu.Mods
{
    public class OsuModGravity : Mod, IUpdatableByPlayfield
    {
        public override string Name => "Gravity";
        public override string Acronym => "GP";
        public override LocalisableString Description => "Hit circles fall under gravity!";
        public override double ScoreMultiplier => 1.0;
        public override ModType Type => ModType.Fun;
        public override Type[] IncompatibleMods => base.IncompatibleMods;
        // Dictionary to track per-circle velocity
        private Dictionary<DrawableHitCircle, Vector2> velocities = new Dictionary<DrawableHitCircle, Vector2>();
        private const float elasticity = 0.8f;

        // Apply gravity and move circles
        private const float gravity = 150;

        // Called every frame with the playfield context (from IUpdatableByPlayfield)
        public void Update(Playfield playfield)
        {
            float dt = (float)(playfield.Clock.ElapsedFrameTime / 1000);

            // Gather active circles
            var circles = playfield.HitObjectContainer
                                  .AliveObjects
                                  .OfType<DrawableHitCircle>()
                                  .ToList();

            // Initialize velocity entries for new circles
            foreach (var circle in circles)
                if (!velocities.ContainsKey(circle))
                    velocities[circle] = Vector2.Zero;

            foreach (var circle in circles)
            {
                // Apply gravity
                var vel = velocities[circle];
                vel.Y += gravity * dt;
                velocities[circle] = vel;

                circle.X += vel.X * dt;
                circle.Y += vel.Y * dt;
            }

            // Boundary collisions for each circle
            if (circles.Count > 0)
            {
                float r = circles[0].CornerRadius;
                float minX = r;
                float maxX = playfield.DrawWidth - r;
                float maxY = playfield.DrawHeight - r;

                foreach (var circle in circles)
                {
                    var vel = velocities[circle];

                    if (circle.X < minX)
                    {
                        circle.X = minX;
                        vel.X = -vel.X * elasticity;
                    }
                    else if (circle.X > maxX)
                    {
                        circle.X = maxX;
                        vel.X = -vel.X * elasticity;
                    }

                    if (circle.Y > maxY)
                    {
                        circle.Y = maxY;
                        vel.Y = -vel.Y * elasticity;
                    }

                    velocities[circle] = vel;
                }
            }
        }
    }
}