﻿using Celeste.Mod.Entities;
using Celeste.Mod.MaxHelpingHand.Module;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.MaxHelpingHand.Entities {
    [CustomEntity("MaxHelpingHand/SeekerBarrierColorController")]
    public class SeekerBarrierColorController : Entity {
        public static void Load() {
            On.Celeste.Level.LoadLevel += onLoadLevel;
        }

        public static void Unload() {
            On.Celeste.Level.LoadLevel -= onLoadLevel;
        }

        private static void onLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level self, Player.IntroTypes playerIntro, bool isFromLoader) {
            if (MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors != null
                && self.Session.LevelData != null // happens if we are loading a save in a room that got deleted
                && !self.Session.LevelData.Entities.Any(entity =>
                    entity.Name == "MaxHelpingHand/SeekerBarrierColorController" || entity.Name == "MaxHelpingHand/SeekerBarrierColorControllerDisabler")) {

                // we have a barrier color, and are entering a room with no controller: spawn one.
                EntityData restoredData = new EntityData();
                restoredData.Values = new Dictionary<string, object>() {
                    { "color", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.Color },
                    { "particleColor", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.ParticleColor },
                    { "transparency", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.Transparency },
                    { "particleTransparency", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.ParticleTransparency },
                    { "particleDirection", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.ParticleDirection },
                    { "depth", MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors.Depth?.ToString() ?? "" },
                    { "persistent", true }
                };

                self.Add(new SeekerBarrierColorController(restoredData, Vector2.Zero));
            }

            orig(self, playerIntro, isFromLoader);
        }


        private static bool seekerBarrierRendererHooked = false;

        // the seeker controller on the current screen.
        private static SeekerBarrierColorController controllerOnScreen;

        // during transitions: the seeker controller on the next screen, and the progress between both screens.
        // transitionProgress = -1 means no transition is ongoing.
        private static SeekerBarrierColorController nextController;
        private static float transitionProgress = -1f;

        // the parameters for this seeker controller.
        private Color color;
        private Color particleColor;
        private float transparency;
        private float particleTransparency;
        private float particleDirection;
        private int? depth;
        private bool persistent;

        private VirtualRenderTarget levelRenderTarget;

        internal static bool HasControllerOnNextScreen() {
            return nextController != null;
        }

        public SeekerBarrierColorController(EntityData data, Vector2 offset) : base(data.Position + offset) {
            color = Calc.HexToColor(data.Attr("color", "FFFFFF"));
            particleColor = Calc.HexToColor(data.Attr("particleColor", "FFFFFF"));
            transparency = data.Float("transparency", 0.15f);
            particleTransparency = data.Float("particleTransparency", 0.5f);
            particleDirection = data.Float("particleDirection", 0f);
            persistent = data.Bool("persistent");

            if (int.TryParse(data.Attr("depth"), out int depthInt)) {
                depth = depthInt;

                // we are going to need this for bloom rendering.
                levelRenderTarget = VirtualContent.CreateRenderTarget("helping-hand-seeker-barrier-color-controller-" + data.ID, 320, 180);
            }

            Add(new TransitionListener {
                OnIn = progress => transitionProgress = progress,
                OnOut = progress => transitionProgress = progress,
                OnInBegin = () => transitionProgress = 0f,
                OnInEnd = () => transitionProgress = -1f
            });

            // update session
            if (persistent) {
                MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors = new MaxHelpingHandSession.SeekerBarrierColorState() {
                    Color = data.Attr("color", "FFFFFF"),
                    ParticleColor = data.Attr("particleColor", "FFFFFF"),
                    Transparency = data.Float("transparency", 0.15f),
                    ParticleTransparency = data.Float("particleTransparency", 0.5f),
                    ParticleDirection = data.Float("particleDirection", 0f),
                    Depth = depth
                };
            } else {
                MaxHelpingHandModule.Instance.Session.SeekerBarrierCurrentColors = null;
            }
        }

        public override void Added(Scene scene) {
            base.Added(scene);

            // this is the controller for the next screen.
            nextController = this;

            // enable the hooks on barrier rendering.
            if (!seekerBarrierRendererHooked) {
                hookSeekerBarrierRenderer();
                seekerBarrierRendererHooked = true;
            }
        }

        public override void Awake(Scene scene) {
            base.Awake(scene);

            // apply depth to all barriers and to the renderer.
            if (depth.HasValue) {
                foreach (SeekerBarrier barrier in scene.Tracker.GetEntities<SeekerBarrier>()) {
                    barrier.Depth = depth.Value;
                }
                foreach (SeekerBarrierRenderer renderer in scene.Tracker.GetEntities<SeekerBarrierRenderer>()) {
                    renderer.Depth = depth.Value + 1;
                }
            }
        }

        public override void Removed(Scene scene) {
            base.Removed(scene);

            // the "current" color controller is now the one from the next screen.
            controllerOnScreen = nextController;
            nextController = null;

            // the transition (if any) is over.
            transitionProgress = -1f;

            // if there is none, clean up the hooks.
            if (controllerOnScreen == null && seekerBarrierRendererHooked) {
                unhookSeekerBarrierRenderer();
                seekerBarrierRendererHooked = false;
            }

            levelRenderTarget?.Dispose();
            levelRenderTarget = null;
        }

        public override void SceneEnd(Scene scene) {
            base.SceneEnd(scene);

            // leaving level: forget about all controllers and clean up the hooks if present.
            controllerOnScreen = null;
            nextController = null;
            if (seekerBarrierRendererHooked) {
                unhookSeekerBarrierRenderer();
                seekerBarrierRendererHooked = false;
            };

            levelRenderTarget?.Dispose();
            levelRenderTarget = null;
        }

        public override void Update() {
            base.Update();

            if (transitionProgress == -1f && controllerOnScreen == null) {
                // no transition is ongoing.
                // if only nextController is defined, move it into controllerOnScreen.
                controllerOnScreen = nextController;
                nextController = null;
            }
        }

        private void hookSeekerBarrierRenderer() {
            IL.Celeste.SeekerBarrierRenderer.OnRenderBloom += hookBarrierColor;
            IL.Celeste.SeekerBarrierRenderer.Render += hookBarrierColor;
            IL.Celeste.SeekerBarrier.Render += hookParticleColors;
            On.Celeste.SeekerBarrier.Update += hookSeekerBarrierParticles;

            On.Celeste.SeekerBarrierRenderer.OnRenderBloom += onSeekerBarrierRendererRenderBloom;
            IL.Celeste.BloomRenderer.Apply += modBloomRendererApply;
            On.Celeste.SeekerBarrierRenderer.Render += onSeekerBarrierRendererRender;
        }

        private static void unhookSeekerBarrierRenderer() {
            IL.Celeste.SeekerBarrierRenderer.OnRenderBloom -= hookBarrierColor;
            IL.Celeste.SeekerBarrierRenderer.Render -= hookBarrierColor;
            IL.Celeste.SeekerBarrier.Render -= hookParticleColors;
            On.Celeste.SeekerBarrier.Update -= hookSeekerBarrierParticles;

            On.Celeste.SeekerBarrierRenderer.OnRenderBloom -= onSeekerBarrierRendererRenderBloom;
            IL.Celeste.BloomRenderer.Apply -= modBloomRendererApply;
            On.Celeste.SeekerBarrierRenderer.Render -= onSeekerBarrierRendererRender;
        }

        private static void hookSeekerBarrierParticles(On.Celeste.SeekerBarrier.orig_Update orig, SeekerBarrier self) {
            float particleDirection = controllerOnScreen?.particleDirection ?? 0f;
            if (self is CustomSeekerBarrier customBarrier) {
                particleDirection = customBarrier.particleDirection;
            }

            // no need to account for screen transitions: particles are frozen during them.
            if (particleDirection == 0f) {
                // default settings: do nothing
                orig(self);
                return;
            }

            // save all particles
            DynData<SeekerBarrier> selfData = new DynData<SeekerBarrier>(self);
            List<Vector2> particles = new List<Vector2>(selfData.Get<List<Vector2>>("particles"));
            float[] speeds = selfData.Get<float[]>("speeds");

            // run vanilla code
            orig(self);

            // move particles again ourselves, except on the direction we want.
            for (int i = 0; i < particles.Count; i++) {
                // compute new position
                Vector2 newPosition = particles[i] + Vector2.UnitY.Rotate((float) (particleDirection * Math.PI / 180)) * speeds[i % speeds.Length] * Engine.DeltaTime;

                // make sure it stays in bounds
                while (newPosition.X < 0) newPosition.X += self.Width;
                while (newPosition.Y < 0) newPosition.Y += self.Height;
                newPosition.X %= self.Width;
                newPosition.Y %= self.Height;

                // replace the particle position
                particles[i] = newPosition;
            }

            // replace them.
            selfData["particles"] = particles;
        }

        private static void hookBarrierColor(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // replace colors (vanilla is white)...
            while (cursor.TryGotoNext(MoveType.AfterLabel, instr => instr.MatchCall<Color>("get_White"))) {
                Logger.Log("MaxHelpingHand/SeekerBarrierColorController", $"Injecting seeker barrier color at {cursor.Index} in IL for {il.Method.Name}");
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Next.OpCode = OpCodes.Call;
                cursor.Next.Operand = typeof(SeekerBarrierColorController).GetMethod("GetCurrentBarrierColor");
            }

            // reset the cursor...
            cursor.Index = 0;

            // ... and replace opacity (vanilla is 0.15).
            if (cursor.TryGotoNext(MoveType.AfterLabel, instr => instr.MatchLdcR4(0.15f))) {
                Logger.Log("MaxHelpingHand/SeekerBarrierColorController", $"Injecting seeker barrier transparency at {cursor.Index} in IL for {il.Method.Name}");
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Next.OpCode = OpCodes.Call;
                cursor.Next.Operand = typeof(SeekerBarrierColorController).GetMethod("GetCurrentBarrierTransparency");
            }
        }

        private static void hookParticleColors(ILContext il) {
            ILCursor cursor = new ILCursor(il);

            // replace colors (vanilla is white)...
            if (cursor.TryGotoNext(MoveType.AfterLabel, instr => instr.MatchCall<Color>("get_White"))) {
                Logger.Log("MaxHelpingHand/SeekerBarrierColorController", $"Injecting seeker barrier particle color at {cursor.Index} in IL for {il.Method.Name}");
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Next.OpCode = OpCodes.Call;
                cursor.Next.Operand = typeof(SeekerBarrierColorController).GetMethod("GetCurrentParticleColor");
            }

            // reset the cursor...
            cursor.Index = 0;

            // ... and replace opacity (vanilla is 0.5).
            if (cursor.TryGotoNext(MoveType.AfterLabel, instr => instr.MatchLdcR4(0.5f))) {
                Logger.Log("MaxHelpingHand/SeekerBarrierColorController", $"Injecting seeker barrier particle transparency at {cursor.Index} in IL for {il.Method.Name}");
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Next.OpCode = OpCodes.Call;
                cursor.Next.Operand = typeof(SeekerBarrierColorController).GetMethod("GetCurrentParticleTransparency");
            }
        }

        // those are called from the IL hooks:

        public static Color GetCurrentBarrierColor(SeekerBarrierRenderer renderer) {
            if (renderer is CustomSeekerBarrier.Renderer customRenderer) {
                return customRenderer.color;
            }
            return getAndLerp(controller => controller.color, Color.White, Color.Lerp);
        }

        public static Color GetCurrentParticleColor(SeekerBarrier barrier) {
            if (barrier is CustomSeekerBarrier customBarrier) {
                return customBarrier.particleColor;
            }
            return getAndLerp(controller => controller.particleColor, Color.White, Color.Lerp);
        }

        public static float GetCurrentBarrierTransparency(SeekerBarrierRenderer renderer) {
            if (renderer is CustomSeekerBarrier.Renderer customRenderer) {
                return customRenderer.transparency;
            }
            return getAndLerp(controller => controller.transparency, 0.15f, MathHelper.Lerp);
        }

        public static float GetCurrentParticleTransparency(SeekerBarrier barrier) {
            if (barrier is CustomSeekerBarrier customBarrier) {
                return customBarrier.particleTransparency;
            }
            return getAndLerp(controller => controller.particleTransparency, 0.5f, MathHelper.Lerp);
        }

        /// <summary>
        /// Gets a value from the active seeker barrier color controller(s), using the given getter, and the lerp function if we are transitioning
        /// between rooms. defaultValue is used when going from/to a room with no controller.
        /// </summary>
        private static T getAndLerp<T>(Func<SeekerBarrierColorController, T> valueGetter, T defaultValue, Func<T, T, float, T> lerp) {
            if (transitionProgress == -1f) {
                if (controllerOnScreen == null) {
                    // no transition is ongoing.
                    // if only nextController is defined, move it into controllerOnScreen.
                    controllerOnScreen = nextController;
                    nextController = null;
                }

                if (controllerOnScreen != null) {
                    return valueGetter(controllerOnScreen);
                } else {
                    return defaultValue;
                }
            } else {
                // get the value in the room we're coming from.
                T fromRoomValue;
                if (controllerOnScreen != null) {
                    fromRoomValue = valueGetter(controllerOnScreen);
                } else {
                    fromRoomValue = defaultValue;
                }

                // get the value in the room we're going to.
                T toRoomValue;
                if (nextController != null) {
                    toRoomValue = valueGetter(nextController);
                } else {
                    toRoomValue = defaultValue;
                }

                // transition smoothly between both.
                return lerp(fromRoomValue, toRoomValue, transitionProgress);
            }
        }


        // ===== The mess ahead handles being able to set a depth for seeker barrier bloom.

        private static bool allowedToRenderBloom = false;

        private static void onSeekerBarrierRendererRenderBloom(On.Celeste.SeekerBarrierRenderer.orig_OnRenderBloom orig, SeekerBarrierRenderer self) {
            // only run RenderBloom if no depth setting is activated, or if we are allowed to do so.
            if (!(controllerOnScreen?.depth.HasValue ?? false) || allowedToRenderBloom) {
                orig(self);
            }
        }

        private static void modBloomRendererApply(ILContext il) {
            ILCursor cursor = new ILCursor(il);
            if (cursor.TryGotoNext(MoveType.After, instr => instr.MatchCallvirt<Tracker>("GetEntities"))) {
                Logger.Log("MaxHelpingHand/SeekerBarrierColorController", $"Disabling seeker barrier rendering in BloomRenderer.Apply at {cursor.Index} in IL");
                cursor.EmitDelegate<Func<List<Entity>, List<Entity>>>(orig => {
                    if (controllerOnScreen?.depth.HasValue ?? false) {
                        // pretend there is no seeker barrier.
                        return new List<Entity>();
                    }
                    return orig;
                });
            }
        }

        private static void onSeekerBarrierRendererRender(On.Celeste.SeekerBarrierRenderer.orig_Render orig, SeekerBarrierRenderer self) {
            orig(self);

            if ((controllerOnScreen?.depth.HasValue ?? false) && controllerOnScreen.Scene is Level level) {
                // stop rendering gameplay: we're going to render BLOOM now. Yeaaaaah
                GameplayRenderer.End();

                // first, build the scene with background + gameplay that was rendered so far.
                Engine.Instance.GraphicsDevice.SetRenderTarget(controllerOnScreen.levelRenderTarget);
                Engine.Instance.GraphicsDevice.Clear(Color.Transparent);
                level.Background.Render(level);
                Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
                Draw.SpriteBatch.Draw(GameplayBuffers.Gameplay, Vector2.Zero, Color.White);
                Draw.SpriteBatch.End();

                // blur it out.
                Texture2D blurredLevel = GaussianBlur.Blur(controllerOnScreen.levelRenderTarget, GameplayBuffers.TempA, GameplayBuffers.TempB);

                // paint all custom bloom for seeker barriers on buffer A
                Engine.Instance.GraphicsDevice.SetRenderTarget(GameplayBuffers.TempA);
                Engine.Instance.GraphicsDevice.Clear(Color.Transparent);

                Camera camera = level.Camera;
                Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone, null, camera.Matrix);
                allowedToRenderBloom = true;
                foreach (CustomBloom component in level.Tracker.GetComponents<CustomBloom>()) {
                    if (component.Visible && component.OnRenderBloom != null && component.Entity is SeekerBarrierRenderer) {
                        component.OnRenderBloom();
                    }
                }
                allowedToRenderBloom = false;
                Draw.SpriteBatch.End();

                // take cutouts into account
                List<Component> cutouts = level.Tracker.GetComponents<EffectCutout>();
                if (cutouts.Count > 0) {
                    Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BloomRenderer.CutoutBlendstate, SamplerState.PointClamp, null, null, null, camera.Matrix);
                    foreach (Component item2 in cutouts) {
                        EffectCutout effectCutout = item2 as EffectCutout;
                        if (effectCutout.Visible) {
                            Draw.Rect(effectCutout.Left, effectCutout.Top, effectCutout.Right - effectCutout.Left, effectCutout.Bottom - effectCutout.Top, Color.White * (1f - effectCutout.Alpha));
                        }
                    }
                    Draw.SpriteBatch.End();
                }

                // apply the mask on the screen
                Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BloomRenderer.BlurredScreenToMask);
                Draw.SpriteBatch.Draw(blurredLevel, Vector2.Zero, Color.White);
                Draw.SpriteBatch.End();

                // then apply it to the current gameplay
                Engine.Instance.GraphicsDevice.SetRenderTarget(GameplayBuffers.Gameplay);
                Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BloomRenderer.AdditiveMaskToScreen);
                Draw.SpriteBatch.Draw(GameplayBuffers.TempA, Vector2.Zero, Color.White);
                Draw.SpriteBatch.End();

                // resume normal gameplay rendering
                GameplayRenderer.Begin();
            }
        }
    }
}
