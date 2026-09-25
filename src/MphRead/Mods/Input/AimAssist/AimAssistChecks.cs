using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MphRead.Mods.Input.AimAssist
{
    internal static class AimAssistChecks
    {
        public static void Run()
        {
            void Check(bool ok, string name) => GamepadChecks.Check(ok, "aim assist: " + name);
            var state = new AimAssistState();
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false);
            var targets = new[] { new AimAssistTarget(1, 1, new(1, .2f), new(.3f, .4f), 15, true, true) };
            AimAssistResult Apply(float stick = .5f, float move = 0, bool eligible = true, Vector2? raw = null,
                AimAssistWeaponProfile? overrideProfile = null)
                => AimAssist.Apply(state, targets, raw ?? new(.1f, .01f), stick, move, 1f / 60,
                    eligible, overrideProfile ?? profile);

            Check(Apply(0, 0, raw: Vector2.Zero) == new AimAssistResult(0, 0), "untouched pad never moves camera");
            Check(Apply(eligible: false).TargetSlot == -1, "mouse/menu/death eligibility bypasses assist");

            state.Reset();
            var weakProfile = profile with { Rotation = profile.Rotation / 4 };
            var weak = Apply(overrideProfile: weakProfile);
            state.Reset();
            var result = Apply();
            Check(Math.Abs(result.RotationStrength - weak.RotationStrength * 4) < .0001f,
                "requested four-times rotational assistance is explicit and testable");
            Check(result.TargetSlot == 1 && result.Friction >= AimAssistTuning.MinimumFriction && result.Friction < 1,
                "visible body gets bounded friction");

            Check(result.HeadBlend == 0, "head cannot acquire on the first target frame");
            for (int i = 0; i < 60; i++) result = Apply();
            Check(result.HeadBlend > 0 && result.HeadBlend <= AimAssistTuning.IntentionalMaxHeadBlend,
                "head refinement ramps after retained torso acquisition");
            targets[0] = targets[0] with { HeadError = new(.15f, .15f) };
            result = Apply();
            targets[0] = targets[0] with { HeadError = new(.05f, .08f) };
            result = Apply();
            Check(result.HeadPrediction >= 0 && result.HeadPrediction <= AimAssistTuning.MaxHeadPrediction + .0001f,
                "head prediction is bounded");

            targets[0] = targets[0] with { HeadVisible = false };
            Check(Apply().HeadBlend == 0, "head LOS loss drops refinement immediately");

            targets[0] = targets[0] with { BodyVisible = false };
            var hidden = Apply();
            Check(hidden.TargetSlot == 1 && hidden.Occluded && hidden.Friction == 1 && hidden.RotationStrength == 0,
                "brief occlusion retains identity without tracking through walls");
            for (int i = 0; i < 4; i++) hidden = Apply();
            Check(hidden.TargetSlot == -1 && state.TargetSlot == -1, "occlusion grace expires and clears the target");

            targets[0] = targets[0] with { BodyVisible = true, HeadVisible = true, Eligible = false };
            Check(Apply().TargetSlot == -1, "team/dead/spectator filtering");
            targets[0] = targets[0] with { Eligible = true, BodyError = new(float.NaN, 0) };
            Check(Apply().TargetSlot == -1, "nonfinite target rejected");

            targets[0] = targets[0] with { BodyError = new(1, .2f), HeadError = new(.3f, .4f), HeadVisible = true };
            Apply();
            targets[0] = targets[0] with { Life = 2 };
            Check(Apply().HeadBlend == 0 && state.TargetLife == 2, "respawn cannot inherit target history");

            var opposed = Apply(raw: new(-2, -2));
            Check(Math.Abs(opposed.X + 2) < .00001f && Math.Abs(opposed.Y + 2) < .00001f,
                "strong opposing input overrides both axes");
            Check(Apply(0, .5f, raw: Vector2.Zero).RotationStrength == 0,
                "movement stick cannot rotate a neutral aim camera");
            Check(Apply(0, 0, raw: Vector2.Zero).RotationStrength == 0,
                "no intent clears rotation");

            targets = new[] {
                new AimAssistTarget(1, 1, new(1, 0), new(1, 1), 15, true, false),
                new AimAssistTarget(2, 1, new(1.1f, 0), new(1, 1), 15, true, false)
            };
            state.Reset();
            Check(Apply().TargetSlot == 1, "best angular score wins");
            targets[1] = targets[1] with { BodyError = new(.9f, 0) };
            Check(Apply().TargetSlot == 1, "small challenger improvement does not oscillate");
            targets[0] = targets[0] with { BodyError = new(8, 0) };
            targets[1] = targets[1] with { BodyError = new(.1f, 0) };
            Check(Apply().TargetSlot == 2, "decisive challenger releases old target");
            Check(Apply(raw: new(.2f, 0)).InputAlignment > 0, "target score records player input alignment");

            float Simulate(int hz)
            {
                var memory = new AimAssistState();
                float angle = 2;
                var input = new AimAssistTarget[1];
                for (int i = 0; i < hz; i++)
                {
                    input[0] = new(1, 1, new(angle, 0), new(angle, 3), 15, true, false);
                    angle -= AimAssist.Apply(memory, input, Vector2.Zero, .5f, 0, 1f / hz, true, profile).X;
                }
                return angle;
            }
            Check(Math.Abs(Simulate(30) - Simulate(120)) < .08f,
                "rotation is stable across 30/120 Hz integration");

            AimInputSourceTracker.Reset();
            AimInputSourceTracker.Stick(.5f, 0, 1000);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "initial stick owns aim");
            AimInputSourceTracker.Pointer(1, 0, false, 1001);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "mouse revokes immediately");
            AimInputSourceTracker.Stick(.5f, 0, 1002);
            AimInputSourceTracker.Stick(.5f, 0, 1050);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "medium stick still confirms takeover");
            AimInputSourceTracker.Stick(.5f, 0, 1062);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "medium stick uses faster confirmation");
            AimInputSourceTracker.Pointer(1, 0, false, 1063);
            AimInputSourceTracker.Stick(.7f, 0, 1064);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "strong intentional stick reclaims aim immediately");
            AimInputSourceTracker.Pointer(0, 1, true, 1065);
            Check(AimInputSourceTracker.Current == AimInputSource.Touch, "touch revokes immediately");

            RegionAndIntentChecks();
            TrackingChecks();
            V3Checks();
            AimAssistCameraChecks.Run();

            AimInputSourceTracker.Reset();
            state.Reset();
            MeasureCoreAllocations(state, targets, profile);
            long allocated = MeasureCoreAllocations(state, targets, profile);
            Check(allocated == 0, $"steady-state assist core allocates no managed memory ({allocated} bytes)");
        }

        private static void RegionAndIntentChecks()
        {
            void Check(bool ok, string name) => GamepadChecks.Check(ok, "aim regions: " + name);
            var region = new AimAssistRegion(-2, 2, -.3f, .3f);
            Check(AimAssistMath.InsideRegion(region), "center is contained");
            Check(AimAssistMath.RegionError(new Vector2(1, -.3f), region) == Vector2.Zero,
                "lower boundary and lateral width are valid");
            Check(AimAssistMath.RegionError(new Vector2(-1, .3f), region) == Vector2.Zero,
                "upper boundary is valid");
            Check(AimAssistMath.RegionError(new Vector2(3, 0), region) == new Vector2(-1, 0),
                "outside width uses nearest boundary, not center");
            var edgeHead = new AimAssistRegion(-1, 1, -.05f, .35f);
            Vector2 safeHead = AimAssistMath.SafeRegionError(edgeHead, AimAssistTuning.HeadSafeInset);
            Check(AimAssistMath.InsideRegion(edgeHead) && safeHead.Y > 0 && safeHead.Y < .1f,
                "headshot band gets a weak safe-interior correction before the real edge");
            float edgeGuard = AimAssistMath.EdgeFrictionFactor(.2f, .2f, -.5f, .03f, .5f);
            float deliberateExit = AimAssistMath.EdgeFrictionFactor(.2f, .95f, -.5f, .03f, .5f);
            Check(edgeGuard < 1 && deliberateExit > edgeGuard,
                "edge friction guards accidental overshoot but releases deliberate exit");
            foreach (float distance in new[] { 14f, 15f, 16f, 30f })
            {
                Check(AimAssistMath.CanHeadshotAtDistance(BeamType.PowerBeam, distance) == (distance <= 15), "standard range " + distance);
                Check(AimAssistMath.CanHeadshotAtDistance(BeamType.Imperialist, distance), "Imperialist range " + distance);
                Check(!AimAssistMath.CanHeadshotAtDistance(BeamType.ShockCoil, distance), "coil never head-refines");
            }
            var state = new AimAssistState();
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Precision, false);
            var targets = new[] { new AimAssistTarget(1, 1, new(0, -2), new(0, .4f), 30, true, true,
                BodyRegion: new(-1, 1, -3, -.5f), HeadRegion: new(-1, 1, .25f, .55f)) };
            AimAssistResult Step(Vector2 stick, float move = 0, Vector2 raw = default)
                => AimAssist.Apply(state, targets, raw, stick, move, 1f / 60, true, profile);
            Check(Step(Vector2.Zero, 1).TargetSlot == -1, "movement never acquires");
            var flick = Step(new(0, .8f));
            Check(flick.TrackingState == AimAssistTrackingState.FlickCapturingHead && flick.Y > 0 && flick.Y <= .3f,
                "aligned physical flick finishes a tiny region error");
            Check(flick.HeadPrediction == 0, "Imperialist positional lead is zero");
            state.Reset();
            state.PreviousStick = new(.7f, 0);
            var directionalFlick = Step(new(.2f, .7f));
            Check(directionalFlick.FlickActive
                && directionalFlick.TrackingState == AimAssistTrackingState.FlickCapturingHead,
                "fast direction-change flick is recognized without a magnitude spike");
            state.Reset();
            Check(Step(new(0, -.8f)).TrackingState != AimAssistTrackingState.FlickCapturingHead, "chest flick cannot capture head");
            state.Reset();
            targets[0] = targets[0] with { HeadRegion = region, HeadError = new(.5f, 0) };
            for (int i = 0; i < 12; i++) Step(new(.3f, 0));
            Check(state.HeadBlend == 1, "lateral position inside head band has no chest pull");
            Check(state.TrackingConfidence >= AimAssistTuning.TrackingConfidenceMin,
                "deliberate tracking builds continuous retention confidence");
            targets[0] = targets[0] with { HeadError = new(.6f, 0) };
            var retained = Step(Vector2.Zero, .5f);
            Check(retained.StrafeTracking && retained.PositionCorrection == Vector2.Zero
                && retained.TrackingCorrection.X > 0,
                "confidence retains motion tracking while strafing without position magnetism");
            for (int i = 0; i < 32; i++) retained = Step(Vector2.Zero, .5f);
            Check(retained.TargetSlot == -1, "neutral strafe confidence expires smoothly");
            state.Reset();
            targets[0] = targets[0] with { HeadVisible = false, BodyRegion = new(1, 2, -1, 1) };
            Step(new(.5f, 0));
            Check(Step(new(-.8f, 0)).OpposingBreak, "opposition cancels retention immediately");
            state.Reset();
            var normal = Step(new(.4f, .2f), raw: new(.01f, .3f));
            state.Reset();
            var asymmetric = Step(new(.4f, .2f), raw: new(.3f, .01f));
            Check(normal.TargetSlot == asymmetric.TargetSlot && normal.InputAlignment == asymmetric.InputAlignment,
                "physical selection ignores asymmetric camera sensitivity");
            Check(GamepadAnalog.FilterAim(new(.1f, 0), new(-.1f, 0), 1f / 60) == new Vector2(-.1f, 0),
                "filter bypasses reversals");
            Check(GamepadAnalog.FilterAim(new(.1f, 0), new(.8f, 0), 1f / 60) == new Vector2(.8f, 0),
                "filter preserves flicks");
            state.Reset();
            var headTarget = new AimAssistTarget(1, 1, new(0, -2), new(0, .4f), 14, true, true,
                BodyRegion: new(-1, 1, -3, -.5f), HeadRegion: new(-1, 1, .25f, .55f));
            targets[0] = headTarget with { Eligible = false };
            for (int i = 0; i < 12; i++) Step(new(0, .8f));
            targets[0] = headTarget;
            Check(Step(new(0, .8f)).TrackingState != AimAssistTrackingState.FlickCapturingHead,
                "enemy entering after a held stick cannot synthesize a flick");
            state.Reset();
            targets[0] = headTarget with { HeadVisible = false };
            Check(Step(new(0, .8f)).TrackingState != AimAssistTrackingState.FlickCapturingHead,
                "occluded head cannot capture");
            state.Reset();
            targets[0] = headTarget with { Distance = 16 };
            var invalidRange = AimAssist.Apply(state, targets, Vector2.Zero, new Vector2(0, .8f), 0, 1f / 60,
                true, AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false));
            Check(invalidRange.TrackingState != AimAssistTrackingState.FlickCapturingHead,
                "standard flick respects mechanical range");
            state.Reset();
            targets = new[] {
                headTarget with { HeadError = new(-.35f, .35f),
                    HeadRegion = new(-.5f, -.2f, .2f, .5f),
                    BodyRegion = new(-.7f, -.1f, -3, -.5f) },
                headTarget with { Slot = 2, HeadError = new(.35f, .35f),
                    HeadRegion = new(.2f, .5f, .2f, .5f),
                    BodyRegion = new(.1f, .7f, -3, -.5f) }
            };
            state.TargetSlot = 1; state.TargetLife = 1; state.RetainedSeconds = .25f;
            state.TrackingConfidence = 1; state.PreviousStick = new(-.7f, 0);
            var aligned = Step(new(.5f, .5f));
            Check(aligned.TargetSlot == 2 && state.FlickTarget == 2,
                "new flick trajectory may deliberately choose a different head");
            int captured = aligned.TargetSlot;
            targets[0] = targets[0] with { HeadRegion = new(-.01f, .01f, -.01f, .01f) };
            Check(Step(new(.5f, .5f)).TargetSlot == captured,
                "active flick locks its selected target for the capture window");
            state.Reset();
            targets = new[] { headTarget with { HeadVisible = false, BodyRegion = new(.5f, 1, -.1f, .1f) },
                headTarget with { Slot = 2, HeadVisible = false, BodyRegion = new(.6f, 1.1f, -.1f, .1f) } };
            var firingFirst = AimAssist.Apply(state, targets, Vector2.Zero, new Vector2(.4f, 0), 0, 1f / 60, true, profile, true);
            targets[1] = targets[1] with { BodyRegion = new(.45f, 1, -.1f, .1f) };
            var firingNext = AimAssist.Apply(state, targets, Vector2.Zero, new Vector2(.4f, 0), 0, 1f / 60, true, profile, true);
            Check(firingNext.TargetSlot == firingFirst.TargetSlot, "crossing firing targets retain identity");
            foreach (GamepadCurve curve in Enum.GetValues<GamepadCurve>())
            {
                float previous = 0;
                for (int i = 0; i <= 100; i++)
                {
                    float value = GamepadAnalog.ApplyResponseCurve(i / 100f, curve);
                    Check(value >= previous && value <= 1, "monotonic bounded " + curve);
                    previous = value;
                }
            }
        }

        private static void TrackingChecks()
        {
            void Check(bool ok, string name) => GamepadChecks.Check(ok, "aim tracking: " + name);
            var state = new AimAssistState();
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false);
            var targets = new[] { new AimAssistTarget(1, 1, new(1, -3), new(.1f, .2f), 15, true, true) };
            AimAssistResult Step(Vector2 raw = default, float stick = .5f, float dt = 1f / 60,
                AimAssistWeaponProfile? weapon = null)
                => AimAssist.Apply(state, targets, raw, stick, 0, dt, true, weapon ?? profile);

            // A head above cover remains selectable, but no correction may use the hidden chest.
            targets[0] = targets[0] with { BodyVisible = false };
            var result = Step();
            Check(result.TargetSlot == 1 && result.PointType == AimAssistPointType.Head && result.Y > 0,
                "visible head acquires above an occluded chest");
            targets[0] = targets[0] with { HeadVisible = false };
            result = Step(new(.05f, .02f));
            Check(result.Occluded && result.X == .05f && result.Y == .02f,
                "complete cover passes raw input without rotation or friction");
            targets[0] = targets[0] with { BodyVisible = true, HeadVisible = true };
            Step();
            Check(state.AngularVelocity == Vector2.Zero && state.HeadAngularVelocity == Vector2.Zero,
                "reappearing target cannot inherit hidden motion");

            state.Reset();
            targets[0] = targets[0] with { BodyError = new(0, -8), HeadError = new(.1f, .1f) };
            Check(Step().TargetSlot == 1, "head stays selectable when nearby chest is outside acquire cone");
            for (int i = 0; i < 60; i++) Step();
            Check(state.HeadBlend > .99f, "aim already on head converges fully without chest bias");
            targets[0] = targets[0] with { HeadError = new(float.NaN, 0) };
            // Move the torso into range to exercise the fallback rather than target rejection.
            targets[0] = targets[0] with { BodyError = new(0, -1) };
            result = Step();
            Check(float.IsFinite(result.X) && float.IsFinite(result.Y) && result.HeadBlend == 0,
                "invalid head data falls back to a finite visible torso");
            targets[0] = targets[0] with { HeadError = new(.1f, .1f) };
            result = Step();
            Check(state.HeadAngularVelocity == Vector2.Zero && result.HeadBlend == 0,
                "restored head starts fresh velocity and dwell history");

            state.Reset();
            targets[0] = targets[0] with { BodyError = new(1, 0), HeadVisible = false };
            var low = Step(stick: .041f);
            state.Reset();
            var full = Step(stick: .2f);
            Check(low.X > 0 && low.X < full.X * .01f && low.Friction > .999f,
                "assistance enters smoothly above the intent threshold");
            Check(Step(stick: float.NaN).TargetSlot == -1, "invalid stick intent never enables assistance");
            Check(Step(dt: float.NaN).TargetSlot == -1, "invalid timing clears history");

            state.Reset();
            result = Step(new(-.03f, 0), stick: 1,
                weapon: AimAssistWeaponProfile.For(AimAssistWeaponClass.Precision, true));
            Check(Math.Abs(result.X + .03f) < .00001f,
                "full opposing stick escapes even at very low scoped sensitivity");

            state.Reset();
            targets[0] = targets[0] with { BodyError = new(.1f, 0) };
            result = Step(new(.16f, 0));
            Check(result.X <= .16001f, "assist cannot add to deliberate stick overshoot");
            state.Reset();
            result = Step(new(2, 0));
            Check(result.X <= 2 && result.X >= 2 * result.Friction,
                "deliberate stick overshoot gets no extra push");
            state.Reset();
            targets[0] = targets[0] with { BodyError = new(1, 0), HeadVisible = false };
            var independentCap = profile with
            {
                Head = false, PositionGain = 0, MaxPositionSpeed = .1f,
                MaxTrackingSpeed = 18, MaxSpeed = .1f
            };
            state.TargetSlot = 1; state.TargetLife = 1; state.RetainedSeconds = .25f;
            state.TrackingConfidence = 1; state.PreviousBodyVisible = true;
            state.PreviousError = new(.5f, 0); state.PreviousOutput = Vector2.Zero;
            state.PreviousDeltaTime = 1f / 60;
            result = Step(weapon: independentCap);
            Check(result.TrackingCorrection.Length() > independentCap.MaxSpeed / 60
                && result.TrackingCorrection.Length() <= independentCap.MaxTrackingSpeed / 60 + .00001f,
                "tracking speed is no longer re-clamped by the legacy combined cap");
            Check(state.AngularAcceleration.Length() > 0
                && state.AngularAcceleration.Length() <= AimAssistTuning.MaxTrackedAcceleration + .0001f,
                "motion servo learns bounded angular acceleration");

            // Model a stationary target and a camera already clamped at its pitch limit.
            state.Reset();
            targets[0] = targets[0] with { BodyError = new(0, 1) };
            for (int i = 0; i < 30; i++)
            {
                Step();
                state.PreviousOutput = Vector2.Zero; // actual applied rotation at the limit
            }
            Check(state.AngularVelocity.Length() < .00001f,
                "clamped camera does not invent target motion");

            // Feed camera-compensated observations with alternating frame lengths.
            state.Reset();
            Vector2 angle = new(1, 0);
            float previousDt = 1f / 60;
            Vector2 previousOutput = default;
            for (int i = 0; i < 90; i++)
            {
                float dt = i % 2 == 0 ? 1f / 30 : 1f / 120;
                if (i > 0) angle += new Vector2(4 * previousDt, 0) - previousOutput;
                targets[0] = targets[0] with { BodyError = angle };
                result = Step(dt: dt);
                previousOutput = new(result.X, result.Y);
                previousDt = dt;
            }
            Check(Math.Abs(state.AngularVelocity.X - 4) < .01f,
                "velocity uses the observation interval under variable timing");

            state.Reset();
            targets[0] = new(1, 1, new(0, -2), new(.1f, .1f), 40, false, true,
                HeadRadiusDegrees: .15f);
            for (int i = 0; i < 60; i++) result = Step();
            Check(result.HeadBlend == 0 && result.HeadPrediction == 0,
                "standard weapon cannot refine heads beyond mechanical range");
            result = Step(weapon: AimAssistWeaponProfile.For(AimAssistWeaponClass.Splash, false));
            Check(result.HeadBlend == 0 && result.RotationStrength == 0,
                "non-head weapon cannot track an exposed head through a hidden torso");

            Vector2 scoped = AimAssistMath.CameraDelta(new(2, -1), .25f, true, false);
            Check(scoped == new Vector2(-.5f, -.25f), "scope and inversion convert input to camera degrees once");
            Check(AimAssistMath.AngularDelta(new(-179, 1), new(179, 0)) == new Vector2(2, 1),
                "yaw wrap does not become a velocity spike");
            AimInputSourceTracker.Reset();
            AimInputSourceTracker.Pointer(1, 0, false, 2000);
            AimInputSourceTracker.Stick(1, 0, 2000);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse,
                "pointer owns a mixed input frame even with full stick deflection");
            AimInputSourceTracker.Stick(1, 0, 2001);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad,
                "intentional controller input can reclaim the following frame");

            foreach (int hz in new[] { 30, 60, 120 })
            {
                float stationary = SimulateTracking(hz, false, false);
                float moving = SimulateTracking(hz, true, false);
                float head = SimulateTracking(hz, true, true);
                Check(stationary < .1f, $"stationary convergence at {hz} Hz ({stationary:F3} deg)");
                Check(moving < 1f, $"moving torso tracking at {hz} Hz ({moving:F3} deg)");
                Check(head < 1f, $"jumping/reversing head tracking at {hz} Hz ({head:F3} deg)");
            }
        }

        private static void V3Checks()
        {
            void Check(bool ok, string name) => GamepadChecks.Check(ok, "aim v3: " + name);
            const float dt = 1f / 60;
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false);

            var broad = new AimAssistRegion(-1, 1, -1, 1);
            var exact = new AimAssistTarget(1, 1, new(.1f, 0), new(0, .2f), 10, true, false,
                BodyRegion: broad, BodySurface: new(new(.4f, 0), false));
            Check(AimAssistMath.InsideRegion(broad) && !AimAssistMath.InsideBody(exact)
                && AimAssistMath.BodyError(exact) == new Vector2(.4f, 0),
                "exact hit surface overrides enclosing rectangle");

            AimAssistRegion stationarySafe = AimAssistMath.MotionSafeRegion(
                new(-1, 1, -.3f, .3f), Vector2.Zero);
            AimAssistRegion movingSafe = AimAssistMath.MotionSafeRegion(
                new(-1, 1, -.3f, .3f), new(40, 0));
            Check(movingSafe.Center.X > stationarySafe.Center.X
                && movingSafe.MaxYaw <= 1 && movingSafe.MinYaw >= -1,
                "head safe pocket biases with motion but stays inside hit band");

            Check(AimAssistMath.RelativeTrackingVelocity(new(10, 0), new(7, 0)) == new Vector2(3, 0)
                && AimAssistMath.RelativeTrackingVelocity(new(10, 0), new(12, 0)) == Vector2.Zero
                && AimAssistMath.RelativeTrackingVelocity(new(10, 0), new(-4, 0)) == new Vector2(10, 0),
                "tracking supplies only target velocity the player is not already matching");

            float onPath = AimAssistMath.TrajectoryRegionScore(new(.6f, .9f, -.1f, .1f), new(1, 0));
            float offPath = AimAssistMath.TrajectoryRegionScore(new(.2f, .4f, .6f, .8f), new(1, 0));
            Check(onPath > .99f && onPath > offPath,
                "trajectory score prefers a region the current aim path will cross");

            float slowRadius = AimAssistMath.DynamicFlickRadius(.5f,
                AimAssistTuning.FlickDirectionalSpeed, scoped: false);
            float fastRadius = AimAssistMath.DynamicFlickRadius(.5f, 45, scoped: false);
            float scopedRadius = AimAssistMath.DynamicFlickRadius(.5f, 45, scoped: true);
            Check(fastRadius > slowRadius && scopedRadius < fastRadius
                && AimAssistMath.FlickLandingHorizon(45)
                    < AimAssistMath.FlickLandingHorizon(AimAssistTuning.FlickDirectionalSpeed),
                "flick speed widens finishing envelope while shortening landing horizon");

            Vector2 filteredNormal = GamepadAnalog.FilterAim(new(.1f, .1f), new(.16f, .14f), dt, 0);
            Vector2 filteredPrecision = GamepadAnalog.FilterAim(new(.1f, .1f), new(.16f, .14f), dt, 1);
            Check((new Vector2(.16f, .14f) - filteredPrecision).Length()
                    < (new Vector2(.16f, .14f) - filteredNormal).Length(),
                "near-target precision context releases stick smoothing");

            AimAssistState Seed(Vector2 previousError)
            {
                return new AimAssistState
                {
                    TargetSlot = 1, TargetLife = 1, RetainedSeconds = .3f,
                    BodyTrackingConfidence = 1, PreviousBodyVisible = true,
                    PreviousError = previousError, PreviousOutput = Vector2.Zero,
                    PreviousDeltaTime = dt, PreviousStick = new(.4f, 0)
                };
            }

            var movingTarget = new AimAssistTarget(1, 1, new(.6f, 0), new(.6f, 2), 12, true, false,
                BodyRegion: new(.4f, .8f, -.4f, .4f),
                BodySurface: new(new(.6f, 0), false), BodyVisibility: 1);
            var fullState = Seed(new(.3f, 0));
            var full = AimAssist.Apply(fullState, new[] { movingTarget }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            var peekState = Seed(new(.3f, 0));
            var peek = AimAssist.Apply(peekState,
                new[] { movingTarget with { BodyVisibility = .15f } }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            Check(full.TrackingCorrection.Length() > peek.TrackingCorrection.Length()
                && full.VisibilityCoverage > peek.VisibilityCoverage,
                "partial-cover exposure scales retained tracking");

            var approachState = Seed(new(.6f, 0));
            var approachTarget = movingTarget with { BodyError = new(.6f, 0),
                BodySurface = new(new(.6f, 0), false) };
            var approaching = AimAssist.Apply(approachState, new[] { approachTarget },
                new Vector2(.2f, 0), new Vector2(.4f, 0), 0, dt, true, profile);
            Check(approaching.MotionPhase == AimAssistMotionPhase.Approaching,
                "control phase identifies rapid approach");

            var brakeState = Seed(new(.6f, 0));
            brakeState.PreviousClosingSpeed = 10;
            var braking = AimAssist.Apply(brakeState, new[] { approachTarget },
                new Vector2(.05f, 0), new Vector2(.4f, 0), 0, dt, true, profile);
            Check(braking.MotionPhase == AimAssistMotionPhase.Braking,
                "control phase identifies player braking before target");

            var overState = Seed(new(.1f, 0));
            var overTarget = movingTarget with { BodyError = new(1.2f, 0),
                BodySurface = new(new(1.2f, 0), false) };
            var overshoot = AimAssist.Apply(overState, new[] { overTarget }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            Check(overshoot.MotionPhase == AimAssistMotionPhase.Overshooting,
                "control phase identifies target error opening again");

            var matchedState = Seed(Vector2.Zero);
            var matchedTarget = movingTarget with { BodyError = Vector2.Zero,
                BodyRegion = broad, BodySurface = new(Vector2.Zero, true) };
            var matched = AimAssist.Apply(matchedState, new[] { matchedTarget }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            Check(matched.MotionPhase == AimAssistMotionPhase.Matched,
                "exact surface containment enters matched phase");

            var confidenceState = new AimAssistState();
            var confidenceTarget = new AimAssistTarget(1, 1, new(.5f, 0), new(.2f, 0), 12,
                true, false, BodyRegion: new(.3f, .7f, -.4f, .4f));
            for (int i = 0; i < 12; i++)
                AimAssist.Apply(confidenceState, new[] { confidenceTarget }, Vector2.Zero,
                    new Vector2(.4f, 0), 0, dt, true, profile);
            Check(confidenceState.BodyTrackingConfidence > .3f
                && confidenceState.HeadTrackingConfidence == 0,
                "body confidence builds without granting head confidence");
            confidenceTarget = confidenceTarget with
            {
                HeadVisible = true, HeadRegion = new(.05f, .35f, -.15f, .15f),
                HeadSurface = new(new(.2f, 0), false), HeadVisibility = 1
            };
            for (int i = 0; i < 12; i++)
                AimAssist.Apply(confidenceState, new[] { confidenceTarget }, Vector2.Zero,
                    new Vector2(.4f, 0), 0, dt, true, profile);
            Check(confidenceState.HeadTrackingConfidence > .1f,
                "head confidence builds only after real head engagement");

            var hiddenState = Seed(new(.4f, 0));
            hiddenState.AngularVelocity = new(10, 0);
            hiddenState.AngularAcceleration = new(20, 0);
            var hiddenTarget = movingTarget with { BodyVisible = false, HeadVisible = false };
            var hidden = AimAssist.Apply(hiddenState, new[] { hiddenTarget }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            float remembered = hiddenState.AngularVelocity.Length();
            Check(hidden.Occluded && hidden.TrackingCorrection == Vector2.Zero
                && remembered > 0 && remembered < 10,
                "brief cover outputs no tracking but preserves decayed motion memory");
            var returned = AimAssist.Apply(hiddenState, new[] { movingTarget }, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile);
            Check(hiddenState.AngularVelocity.Length() > 0 && !returned.Occluded,
                "reappearing target resumes from decayed motion instead of zero");

            var commitState = Seed(new(.5f, 0));
            commitState.PreviousInsideBody = true;
            var commitTargets = new[] {
                movingTarget with { Slot = 1, BodyError = new(.5f, 0),
                    BodySurface = new(new(.5f, 0), false) },
                movingTarget with { Slot = 2, BodyError = new(.05f, 0),
                    BodyRegion = new(-.05f, .15f, -.2f, .2f),
                    BodySurface = new(new(.05f, 0), false) }
            };
            var committed = AimAssist.Apply(commitState, commitTargets, Vector2.Zero,
                new Vector2(.4f, 0), 0, dt, true, profile, firing: true);
            Check(committed.TargetSlot == 1 && committed.ShotCommitted,
                "shot commitment prevents a last-moment challenger steal");

            var trajectoryState = new AimAssistState();
            var trajectoryTargets = new[] {
                new AimAssistTarget(1, 1, new(.3f, .4f), new(3, 3), 12, true, false,
                    BodyRegion: new(.2f, .4f, .35f, .55f)),
                new AimAssistTarget(2, 1, new(.6f, 0), new(3, 3), 12, true, false,
                    BodyRegion: new(.5f, .8f, -.1f, .1f))
            };
            var trajectoryChoice = AimAssist.Apply(trajectoryState, trajectoryTargets,
                new Vector2(.14f, 0), new Vector2(.8f, 0), 0, dt, true, profile);
            Check(trajectoryChoice.TargetSlot == 2,
                "normal target selection prefers the hunter on the current aim trajectory");

            var flickState = new AimAssistState { PreviousStick = new(.7f, 0),
                StickHistory0 = new(.7f, 0), StickHistory1 = new(.7f, 0) };
            var flickTarget = new AimAssistTarget(1, 1, new(0, -1), new(.15f, .45f), 12,
                true, true, BodyRegion: new(-.5f, .5f, -1.5f, -.5f),
                HeadRegion: new(-.25f, .25f, .25f, .55f),
                HeadSurface: new(new(0, .25f), false), HeadVisibility: 1);
            AimAssist.Apply(flickState, new[] { flickTarget }, new Vector2(.03f, .18f),
                new Vector2(.2f, .7f), 0, dt, true, profile);
            var landing = AimAssist.Apply(flickState, new[] { flickTarget }, new Vector2(.02f, .08f),
                new Vector2(.2f, .7f), 0, dt, true, profile);
            Check(landing.FlickActive && landing.FlickBraking
                && float.IsFinite(landing.FlickLandingError),
                "flick braking phase carries a predicted landing error");

            var coupledState = Seed(new(.1f, .1f));
            var diagonalTarget = movingTarget with { BodyError = new(.5f, .4f),
                BodySurface = new(new(.5f, .4f), false) };
            AimAssist.Apply(coupledState, new[] { diagonalTarget }, Vector2.Zero,
                new Vector2(.4f, .3f), 0, dt, true, profile);
            Check(coupledState.MotionDirection.LengthSquared() > .9f
                && Math.Abs(coupledState.MotionDirection.X) > .1f
                && Math.Abs(coupledState.MotionDirection.Y) > .1f,
                "target motion direction couples yaw and pitch for diagonal tracking");
        }

        private static float SimulateTracking(int hz, bool moving, bool head)
        {
            var state = new AimAssistState();
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false);
            var targets = new AimAssistTarget[1];
            Vector2 camera = head ? Vector2.Zero : new(-2, 0);
            float total = 0;
            for (int i = 0; i < hz * 4; i++)
            {
                float time = i / (float)hz;
                Vector2 position = moving ? new(1.2f * MathF.Sin(time * 3), .7f * MathF.Sin(time * 4)) : Vector2.Zero;
                Vector2 error = position - camera;
                targets[0] = new(1, 1, head ? error - new Vector2(0, 2) : error,
                    error, 15, !head, head);
                var result = AimAssist.Apply(state, targets, Vector2.Zero, error.LengthSquared() > .000001f ? Vector2.Normalize(error) * .5f : new Vector2(.05f, 0), 0, 1f / hz, true, profile);
                camera += new Vector2(result.X, result.Y);
                if (i >= hz) total += (position - camera).Length();
            }
            return total / (hz * 3);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long MeasureCoreAllocations(AimAssistState state, AimAssistTarget[] targets,
            AimAssistWeaponProfile profile)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                AimAssist.Apply(state, targets, new(.1f, .01f), .5f, 0, 1f / 60, true, profile);
            return GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
    }
}
