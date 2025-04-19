// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.StateChanges;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using osu.Game.Graphics;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Replays;
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

        // Assuming that it is impossible to tap MinHitWindowStartDuration in ms without raking or an autoclicker.
        private static readonly double MinHitWindowStartDuration = 20;
        private static readonly double MinHitWindowEndOffset = 5;

        private OsuInputManager inputManager = null!;
        private List<OsuReplayFrame> replayFrames = null!;
        private int currentReplayFrameIndex;
        private int frameCountMinusOne;
        private Func<HitWindows, double> hitWindowLookup = null!;
        private bool ifReplay;

        // Keep relative position if no replay frames are going.
        private Vector2 lastPlayfieldCursorPos;

        public void Update(Playfield playfield)
        {
            // Prevent updating based on these conditions
            if (ifReplay || replayFrames.Count == 0)
                return;

            double currentTime = playfield.Clock.CurrentTime;

            // First alive, unjudged object.
            var active = playfield.HitObjectContainer.AliveObjects
                                .OfType<DrawableOsuHitObject>()
                                .FirstOrDefault(x => !x.Judged);

            if (active != null) 
            {
                // Timing of the HitObject's hit window.
                double window = active is DrawableSlider ds
                    ? hitWindowLookup(ds.HeadCircle.HitObject.HitWindows)
                    : hitWindowLookup(active.HitObject.HitWindows);

                // It's alot easier to just let replay frames handle spinners.
                bool useReplayFrames = active.HitObject is Spinner;

                if (!useReplayFrames)
                {
                    // Makes sliders appliable to more mods (ex. Depth).
                    if (active is DrawableSlider ds2 && ds2.HeadCircle.Judged)
                    {
                        var slider = ds2.HitObject;
                        double elapsed = currentTime - slider.StartTime;

                        if (elapsed + window < 0 || elapsed >= slider.Duration)
                            return;
                        double trueProgress = Math.Clamp(elapsed / (slider.Duration), 0, 1);
                        double repeatCount = slider.RepeatCount + 1;
                        double span = trueProgress * repeatCount;

                        span = (span > 1 && span % 2 > 1) 
                            ? 1 - (span % 1)
                            : (span % 1);

                        // Takes the original sliderpath offset multiplied by a scale.
                        Vector2 pathPos = ds2.Position + (slider.Path.PositionAt(span) * ds2.Scale);

                        ApplyCursor(pathPos, playfield);

                        return;
                    }
    
                    // Get the timing of a HitObject and add given offsets.
                    double hitStart = active.HitObject.StartTime - window - MinHitWindowStartDuration;
                    double hitEnd = active.HitObject.StartTime + window - MinHitWindowEndOffset;

                    // To make the cursor movement somewhat visually pleasing, we move the cursor from last judgement
                    // to the next hit circle IF current < hitStart. If not, we gradually decrease avaliableTime
                    // from MinHitWindowStartDuration to 1 ms based on where the currentTime stands from start to end.
                    double availableTime = currentTime >= hitStart
                        ? 1 + Math.Clamp((hitEnd - currentTime) / (hitEnd - hitStart), 0, 1) * (MinHitWindowStartDuration - 1)
                        : (active.HitObject.StartTime - window - currentTime);

                    Vector2 currentCursorPos = playfield.ToLocalSpace(inputManager.CurrentState.Mouse.Position);
                    Vector2 targetPos = active.Position;

                    // Compute velocity such that the cursor moves to the HitObject in the available time.
                    float distance = Vector2.Distance(currentCursorPos, targetPos);
                    float velocity = distance / (float)availableTime;
                    float displacement = velocity * (float)playfield.Clock.ElapsedFrameTime;

                    // If the displacement value is greater then the distance between the cursor and HitObject, we don't
                    // want to overshoot it, so go straight to target position.
                    Vector2 newCursorPos = displacement >= distance
                        ? targetPos
                        : currentCursorPos + (targetPos - currentCursorPos).Normalized() * displacement;

                    ApplyCursor(newCursorPos, playfield);
                }
                else
                {
                    ApplyCursor(InterpolateReplayCursorPosition(currentTime), playfield);
                }
            }
            else
            {
                ApplyCursor(lastPlayfieldCursorPos, playfield);
            }
            AdvanceFrame(currentTime);

            // TODO: Implement the functionality to automatically spin spinners
        }

        public void ApplyToDrawableRuleset(DrawableRuleset<OsuHitObject> drawableRuleset)
        {
            // Grab the input manager to disable the user's cursor, and for future use
            inputManager = ((DrawableOsuRuleset)drawableRuleset).KeyBindingInputManager;
            inputManager.AllowUserCursorMovement = false;

            // Without this, replay starts having a little seizure when rewinding
            // due to how Update calculates mouse positions.
            ifReplay = drawableRuleset.HasReplayLoaded.Value;
            drawableRuleset.HasReplayLoaded.BindValueChanged(
                e => ifReplay = e.NewValue,
                runOnceImmediately: true
            );

            // Generate the replay frames the cursor should follow
            replayFrames = new OsuAutoGenerator(
                    drawableRuleset.Beatmap,
                drawableRuleset.Mods
            ).Generate().Frames.Cast<OsuReplayFrame>().ToList();

            frameCountMinusOne = Math.Max(0, replayFrames.Count - 1);

            // HitWindow lookup setup for future HitObjects.
            hitWindowLookup = hw => hw.WindowFor(HitResult.Meh);
        }

        private Vector2 InterpolateReplayCursorPosition(double time)
        {
            var currentFrame = replayFrames[currentReplayFrameIndex];
            var nextFrame = replayFrames[Math.Min(currentReplayFrameIndex + 1, frameCountMinusOne)];
            return Interpolation.ValueAt(
                time, currentFrame.Position, nextFrame.Position, currentFrame.Time, nextFrame.Time);
        }

        private void ApplyCursor(Vector2 playfieldPosition, Playfield playfield)
        {
            lastPlayfieldCursorPos = playfieldPosition;

            new MousePositionAbsoluteInput
            {
                Position = playfield.ToScreenSpace(playfieldPosition)
            }.Apply(inputManager.CurrentState, inputManager);
        }

        private void AdvanceFrame(double currentTime)
        {
            double nextTime = replayFrames[Math.Min(currentReplayFrameIndex + 1, frameCountMinusOne)].Time;
            if (currentTime >= nextTime)
                currentReplayFrameIndex = Math.Min(currentReplayFrameIndex + 1, frameCountMinusOne);
        }
    }
}