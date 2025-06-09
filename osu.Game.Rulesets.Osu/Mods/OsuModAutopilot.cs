﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.StateChanges;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.UI;
using osu.Game.Rulesets.Scoring;
using osuTK;

namespace osu.Game.Rulesets.Osu.Mods
{
    public class OsuModAutopilot : Mod, IUpdatableByPlayfield, IApplicableToDrawableRuleset<OsuHitObject>
    {
        public override string Name => "Autopilot";
        public override string Acronym => "AP";
        public override IconUsage? Icon => OsuIcon.ModAutopilot;
        public override ModType Type => ModType.Automation;
        public override LocalisableString Description => @"Automatic cursor movement - just follow the rhythm.";
        public override double ScoreMultiplier => 0.1;

        public override Type[] IncompatibleMods => new[]
        {
            typeof(OsuModSpunOut),
            typeof(ModRelax),
            typeof(ModAutoplay),
            typeof(OsuModMagnetised),
            typeof(OsuModRepel),
            typeof(ModTouchDevice)
        };

        // When currentTime equals the start of the hitwindow minus the start offset, we start reducing availableTime
        // from this value down to 1 when currentTime equals the end of the hitwindow minus the end offset.
        // This ensures that, if we enter the window late, we still have some room for natural cursor movement.
        private const double hitwindow_start_offset = 40;
        private const double hitwindow_end_offset = 5;

        // The spinner radius value from OsuAutoGeneratorBase
        private const float spinner_radius = 50;

        private OsuInputManager inputManager = null!;
        private Playfield playfield = null!;

        private readonly IBindable<bool> hasReplayLoaded = new Bindable<bool>();

        private (Vector2 Position, double Time) lastHitInfo = (default, 0);
        private (double HitWindowStart, double HitWindowEnd) hitWindow = (0, 0);
        private double currentTimeElapsed = 0;

        public void ApplyToDrawableRuleset(DrawableRuleset<OsuHitObject> drawableRuleset)
        {
            // Grab the input manager to disable the user's cursor, and for future use
            inputManager = ((DrawableOsuRuleset)drawableRuleset).KeyBindingInputManager;
            inputManager.AllowUserCursorMovement = false;

            playfield = drawableRuleset.Playfield;

            hasReplayLoaded.BindTo(drawableRuleset.HasReplayLoaded);

            // subscribe to run exactly once after LoadComplete()
            playfield.OnLoadComplete += _ =>
            {
                // at this point the ruleset, playfield, and cursor are fully initialized
                Vector2 screenStart = inputManager.CurrentState.Mouse.Position;
                Vector2 fieldStart = playfield.ScreenSpaceToGamefield(screenStart);
                double timeStart = playfield.Clock.CurrentTime;

                // store your “initial” hit info
                lastHitInfo = (fieldStart, timeStart);
            };

            // We want to save the position and time when the HitObject was judged for movement calculations.
            playfield.NewResult += (drawableHitObject, result) =>
            {
                Vector2 mousePos = inputManager.CurrentState.Mouse.Position;
                Vector2 fieldPos = playfield.ScreenSpaceToGamefield(mousePos);

                // If it's a slider, we want the cursor position to be at the start or end of the slider
                if (drawableHitObject is DrawableSlider sliderDrawable)
                {
                    var slider = sliderDrawable.HitObject;

                    Vector2 pathEnd = slider.Path.PositionAt(1);

                    fieldPos = (slider.RepeatCount % 2 == 0)
                        ? sliderDrawable.Position + (pathEnd * sliderDrawable.Scale)
                        : slider.HeadCircle.Position;
                }

                lastHitInfo = (fieldPos, result.TimeAbsolute);
            };
        }

        public void Update(Playfield playfield)
        {
            double currentTime = playfield.Clock.CurrentTime;

            var nextObject = playfield.HitObjectContainer.AliveObjects.FirstOrDefault(d => !d.Judged);

            if (nextObject == null)
                return;

            double start = nextObject.HitObject.StartTime;
            currentTimeElapsed = currentTime - start;

            // Reduce calculations during replay.
            if (hasReplayLoaded.Value)
            {
                if (nextObject is DrawableSpinner replaySpinner)
                {
                    var spinner = replaySpinner.HitObject;
                    replaySpinner.HandleUserInput = false;

                    // Don't start spinning until position is reached.
                    if (currentTimeElapsed >= 0)
                    {
                        double calculatedSpeed = 1.01 * (spinner.MaximumBonusSpins + spinner.SpinsRequiredForBonus) / spinner.Duration;
                        double rate = calculatedSpeed / playfield.Clock.Rate;
                        spinSpinner(replaySpinner, rate);
                    }
                }

                return;
            }

            // Sliders do not have hitwindows except for the HeadCircle, so we need to check for sliders.
            double mehWindow = nextObject is DrawableSlider checkForSld
                ? checkForSld.HeadCircle.HitObject.HitWindows.WindowFor(HitResult.Meh)
                : nextObject.HitObject.HitWindows.WindowFor(HitResult.Meh);

            hitWindow = (start - mehWindow - hitwindow_start_offset, start + mehWindow - hitwindow_end_offset);

            // The position of the current alive object.
            var target = nextObject.Position;

            // If the hitobject doesn't appear during the time it was judged, the cursor will teleport.
            // So, we want to save the time when the hitobject first appears so the cursor can travel smoothly.
            lastHitInfo.Time = nextObject.Entry?.LifetimeStart > lastHitInfo.Time
                ? nextObject.Entry.LifetimeStart
                : lastHitInfo.Time;

            // Based on the hit object type, things work differently.
            switch (nextObject)
            {
                case DrawableSpinner spinnerDrawable:
                    handleSpinner(spinnerDrawable, currentTime, start);
                    return;

                case DrawableSlider sliderDrawable:
                    if (!sliderDrawable.HeadCircle.Judged)
                        break;

                    var slider = sliderDrawable.HitObject;

                    if (currentTimeElapsed + mehWindow >= 0 && currentTimeElapsed < slider.Duration)
                    {
                        double prog = Math.Clamp(currentTimeElapsed / slider.Duration, 0, 1);
                        double spans = (prog * (slider.RepeatCount + 1));
                        spans = (spans > 1 && spans % 2 > 1) ? 1 - (spans % 1) : spans % 1;

                        Vector2 pathPos = sliderDrawable.Position + (slider.Path.PositionAt(spans) * sliderDrawable.Scale);

                        applyCursor(pathPos);
                    }

                    return;
            }

            // Compute how many ms remain for cursor movement toward the hit-object
            double availableTime = handleTime();

            moveTowards(target, availableTime);
        }

        private double handleTime()
        {
            // We want the cursor to eventually reach the center of the HitCircle.
            // However, when it's inside the HitWindow, we want to the cursor to be fast enough
            // where the player can't tap it, but slow enough so it doesn't seem like the cursor is teleporting.
            double hitWindowStart = hitWindow.HitWindowStart;
            double hitWindowEnd = hitWindow.HitWindowEnd;
            double lastJudgedTime = lastHitInfo.Time;

            // Compute scale from 0 to 1, then multiply by an offset. This will be used if we are inside between hitWindowStart and hitWindowEnd so we can prevent sudden cursor teleportation.
            double scaledTime = 1 + (Math.Clamp((hitWindowEnd - lastJudgedTime) / (hitWindowEnd - hitWindowStart), 0, 1) * (hitwindow_start_offset - 1));

            // Edge case where the cursor may not reach the hitobject in time, so we set it to which takes less time.
            scaledTime = Math.Min(scaledTime, hitWindowEnd - lastJudgedTime);

            double timeLeft = lastJudgedTime >= hitWindowStart
                ? scaledTime
                : hitWindowStart - lastJudgedTime;

            // Don’t let it go below 1
            return Math.Max(timeLeft, 1);
        }

        private void handleSpinner(DrawableSpinner spinnerDrawable, double currentTime, double start)
        {
            var spinner = spinnerDrawable.HitObject;
            spinnerDrawable.HandleUserInput = false;

            // Before spinner starts, move to position.
            if (currentTimeElapsed < 0)
            {
                Vector2 spinnerTargetPosition = spinner.Position + new Vector2(
                    -(float)Math.Sin(0) * spinner_radius,
                    -(float)Math.Cos(0) * spinner_radius);

                double duration = handleTime();

                moveTowards(spinnerTargetPosition, duration);

                return;
            }

            double calculatedSpeed = 1.01 * (spinner.MaximumBonusSpins + spinner.SpinsRequiredForBonus) / spinner.Duration;
            double rate = calculatedSpeed / playfield.Clock.Rate;

            spinSpinner(spinnerDrawable, rate);

            double angle = 2 * Math.PI * (currentTimeElapsed * rate);
            Vector2 circPos = spinner.Position + new Vector2(
                -(float)Math.Sin(angle) * spinner_radius,
                -(float)Math.Cos(angle) * spinner_radius);

            applyCursor(circPos);
        }

        private void spinSpinner(DrawableSpinner spinnerDrawable, double rate)
        {
            double elapsedTime = playfield.Clock.ElapsedFrameTime;

            // Automatically spin spinner.
            spinnerDrawable.RotationTracker.AddRotation(float.RadiansToDegrees((float)elapsedTime * (float)rate * MathF.PI * 2.0f));
        }

        private void moveTowards(Vector2 target, double timeMs)
        {
            var (lastHitPosition, lastJudgedTime) = lastHitInfo;
            double currentTime = playfield.Clock.CurrentTime;

            double elapsed = currentTime - lastJudgedTime;

            // The percentage of time between the lastJudgedObject and the time to reach the next HitObject's HitWindow.
            // Example: If the percentage of time is around 40%, the cursor should travel atleast 40% of the distance.
            float frac = (float)Math.Clamp(elapsed / timeMs, 0, 1);

            // Compute the new cursor position by Lerp
            Vector2 newPos = Vector2.Lerp(lastHitPosition, target, frac);

            float distanceToCursor = Vector2.Distance(lastHitPosition, newPos);
            float distanceToTarget = Vector2.Distance(lastHitPosition, target);

            // If we’re effectively at (or beyond) the target, snap there
            if (frac >= 1 || distanceToCursor >= distanceToTarget)
                newPos = target;

            applyCursor(newPos);
        }

        private void applyCursor(Vector2 playfieldPosition)
        {
            new MousePositionAbsoluteInput { Position = playfield.ToScreenSpace(playfieldPosition) }.Apply(inputManager.CurrentState, inputManager);
        }
    }
}