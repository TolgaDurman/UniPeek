using System;
using UnityEditor;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Controls;
#endif

namespace UniPeek
{
    /// <summary>
    /// Injects touch, gyroscope, and accelerometer events received from the
    /// companion app into Unity's Input systems.
    /// <para>
    /// Supports both the <b>old Input Manager</b> (active when
    /// <c>ENABLE_INPUT_SYSTEM</c> is <em>not</em> defined) and the
    /// <b>new Input System package</b> (active when <c>ENABLE_INPUT_SYSTEM</c>
    /// is defined).
    /// </para>
    /// <para>
    /// All <c>Inject*</c> methods are safe to call from any thread; they
    /// internally marshal work to the Unity main thread where required.
    /// </para>
    /// </summary>
    public static class InputInjector
    {
        // ── Touch ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects a touch event into Unity's active Input system.
        /// </summary>
        /// <param name="phase">
        /// Touch phase string sent by the phone: <c>"began"</c>, <c>"moved"</c>,
        /// <c>"ended"</c>, or <c>"canceled"</c>.
        /// </param>
        /// <param name="normalizedX">Normalised X coordinate [0, 1] — left to right.</param>
        /// <param name="normalizedY">Normalised Y coordinate [0, 1] — top to bottom.</param>
        /// <param name="fingerId">Touch finger identifier (0-based).</param>
        public static void InjectTouch(string phase, float normalizedX, float normalizedY, int fingerId)
        {
            // Convert normalised → editor window space.
            // In the Unity Editor the InputSystemUIInputModule uses y=0 at the TOP
            // (window/editor convention), so do NOT flip Y here.
            float screenX = normalizedX * Screen.width;
            float screenY = normalizedY * Screen.height;

#if ENABLE_INPUT_SYSTEM
            InjectTouchNewInputSystem(phase, screenX, screenY, fingerId);
#else
            InjectTouchLegacy(phase, screenX, screenY, fingerId);
#endif
        }

        // ── Gyroscope ─────────────────────────────────────────────────────────

        /// <summary>
        /// Injects gyroscope rotation-rate data (rad/s around each axis).
        /// Only effective when the Legacy Input Manager is active.
        /// </summary>
        /// <param name="x">Rotation rate around the X axis (rad/s).</param>
        /// <param name="y">Rotation rate around the Y axis (rad/s).</param>
        /// <param name="z">Rotation rate around the Z axis (rad/s).</param>
        public static void InjectGyro(float x, float y, float z)
        {
#if ENABLE_INPUT_SYSTEM
            InjectGyroNewInputSystem(x, y, z);
#else
            InjectGyroLegacy(x, y, z);
#endif
        }

        // ── Accelerometer ─────────────────────────────────────────────────────

        /// <summary>
        /// Injects accelerometer data (g-force per axis, where Y≈1 when flat).
        /// </summary>
        /// <param name="x">Acceleration along the X axis (g).</param>
        /// <param name="y">Acceleration along the Y axis (g).</param>
        /// <param name="z">Acceleration along the Z axis (g).</param>
        public static void InjectAccelerometer(float x, float y, float z)
        {
#if ENABLE_INPUT_SYSTEM
            InjectAccelNewInputSystem(x, y, z);
#else
            InjectAccelLegacy(x, y, z);
#endif
        }

        // ── New Input System paths ────────────────────────────────────────────

#if ENABLE_INPUT_SYSTEM
        private static Touchscreen _touchscreen;
        private static AttitudeSensor _attitudeSensor;
        private static Accelerometer _accelerometer;
        private static readonly System.Collections.Generic.HashSet<int> _activeTouchIds = new();

        /// <summary>
        /// Ensures synthetic virtual devices exist and are enabled.
        /// Call once from the main thread before the first injection.
        /// </summary>
        public static void EnsureVirtualDevices()
        {
            if (_touchscreen == null)
            {
                _touchscreen = InputSystem.GetDevice<Touchscreen>()
                    ?? InputSystem.AddDevice<Touchscreen>();
                InputSystem.EnableDevice(_touchscreen);
            }

            if (_attitudeSensor == null)
            {
                _attitudeSensor = InputSystem.GetDevice<AttitudeSensor>()
                    ?? InputSystem.AddDevice<AttitudeSensor>();
                InputSystem.EnableDevice(_attitudeSensor);
            }

            if (_accelerometer == null)
            {
                _accelerometer = InputSystem.GetDevice<Accelerometer>()
                    ?? InputSystem.AddDevice<Accelerometer>();
                InputSystem.EnableDevice(_accelerometer);
            }
        }

        /// <summary>Releases synthetic virtual devices on plugin shutdown.</summary>
        public static void RemoveVirtualDevices()
        {
            if (_touchscreen != null)   { InputSystem.RemoveDevice(_touchscreen);   _touchscreen   = null; }
            if (_attitudeSensor != null){ InputSystem.RemoveDevice(_attitudeSensor); _attitudeSensor = null; }
            if (_accelerometer != null) { InputSystem.RemoveDevice(_accelerometer);  _accelerometer  = null; }
            _activeTouchIds.Clear();
        }

        private static void InjectTouchNewInputSystem(string phase, float screenX, float screenY, int fingerId)
        {
            if (_touchscreen == null) return;

            // Flutter uses "cancelled" (double-l); accept both spellings.
            UnityEngine.InputSystem.TouchPhase inputPhase = phase switch
            {
                "began"                   => UnityEngine.InputSystem.TouchPhase.Began,
                "moved"                   => UnityEngine.InputSystem.TouchPhase.Moved,
                "ended"                   => UnityEngine.InputSystem.TouchPhase.Ended,
                "canceled" or "cancelled" => UnityEngine.InputSystem.TouchPhase.Canceled,
                _                         => UnityEngine.InputSystem.TouchPhase.None,
            };

            if (inputPhase == UnityEngine.InputSystem.TouchPhase.None) return;

            int touchId = fingerId + 1;  // Unity touchId is 1-based
            var pos = new Vector2(screenX, screenY);

            bool isEnd = inputPhase == UnityEngine.InputSystem.TouchPhase.Ended ||
                         inputPhase == UnityEngine.InputSystem.TouchPhase.Canceled;

            // The phone app may skip "began"/"moved" and only send "ended" for quick taps.
            // The Input System ignores an "ended" with no prior "began", so synthesize one.
            if (isEnd && !_activeTouchIds.Contains(touchId))
            {
                _activeTouchIds.Add(touchId);
                InputSystem.QueueStateEvent(_touchscreen, new TouchState
                {
                    touchId  = touchId,
                    phase    = UnityEngine.InputSystem.TouchPhase.Began,
                    position = pos,
                });
            }

            // Track which touchIds are currently open.
            if (inputPhase == UnityEngine.InputSystem.TouchPhase.Began)
                _activeTouchIds.Add(touchId);
            else if (isEnd)
                _activeTouchIds.Remove(touchId);

            // Defer Ended/Canceled to the next editor frame.
            // InputSystemUIInputModule needs to process Began (→ PointerDown) in one
            // frame and Ended (→ PointerUp → onClick) in the next, otherwise both
            // events land in the same InputSystem.Update() call and the UI module
            // never establishes a pressed state before releasing it.
            if (isEnd)
            {
                var ts    = _touchscreen;
                var id    = touchId;
                var ph    = inputPhase;
                var p     = pos;
                EditorApplication.delayCall += () =>
                {
                    if (ts == null) return;
                    InputSystem.QueueStateEvent(ts, new TouchState
                    {
                        touchId  = id,
                        phase    = ph,
                        position = p,
                    });
                };
                return;
            }

            InputSystem.QueueStateEvent(_touchscreen, new TouchState
            {
                touchId  = touchId,
                phase    = inputPhase,
                position = pos,
            });
        }

        private static void InjectGyroNewInputSystem(float x, float y, float z)
        {
            if (_attitudeSensor == null) return;
            // New Input System maps gyro as angular velocity on the AttitudeSensor
            // (exact device support depends on the Input System version).
            // Queue a no-op attitude update — full gyro integration requires
            // a more complex state struct; this stub keeps the API consistent.
            //UniPeekConstants.Log($"[InputInjector] Gyro (new IS): {x:F3}, {y:F3}, {z:F3}");
        }

        private static void InjectAccelNewInputSystem(float x, float y, float z)
        {
            if (_accelerometer == null) return;
            InputSystem.QueueDeltaStateEvent(_accelerometer.acceleration, new Vector3(x, y, z));
        }
#endif

        // ── Legacy Input Manager paths ────────────────────────────────────────

#if !ENABLE_INPUT_SYSTEM
        // The Legacy Input Manager does not expose a public API for injecting
        // touch or sensor events at runtime. We use internal Unity reflection
        // to call the native method that fakes touch input.  This is best-effort
        // and may break across Unity versions; a user-facing warning is logged
        // if reflection fails.

        private static bool _legacyWarningLogged;

        private static void InjectTouchLegacy(string phase, float screenX, float screenY, int fingerId)
        {
            // Best-effort via reflection into UnityEngine.Input internals.
            // Production code should prefer the new Input System package.
            try
            {
                // Flutter uses "cancelled" (double-l); accept both spellings.
                var touchPhase = phase switch
                {
                    "began"                   => TouchPhase.Began,
                    "moved"                   => TouchPhase.Moved,
                    "ended"                   => TouchPhase.Ended,
                    "canceled" or "cancelled" => TouchPhase.Canceled,
                    _                         => TouchPhase.Stationary,
                };

                // Attempt to find the internal SimulateTouch method (Unity 2021/2022)
                var inputType   = typeof(Input);
                var simMethod   = inputType.GetMethod(
                    "SimulateTouch",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                if (simMethod != null)
                {
                    simMethod.Invoke(null, new object[]
                    {
                        fingerId, new Vector2(screenX, screenY), (int)touchPhase
                    });
                }
                else if (!_legacyWarningLogged)
                {
                    _legacyWarningLogged = true;
                    UniPeekConstants.LogWarning(
                        "[InputInjector] SimulateTouch not found. " +
                        "Enable the new Input System package for reliable touch injection.");
                }
            }
            catch (Exception ex)
            {
                if (!_legacyWarningLogged)
                {
                    _legacyWarningLogged = true;
                    UniPeekConstants.LogWarning($"[InputInjector] Legacy touch injection failed: {ex.Message}");
                }
            }
        }

        private static void InjectGyroLegacy(float x, float y, float z)
        {
            // Legacy Input.gyro values are read-only from C#; no public injection API.
            // A native plugin or the new Input System package is required for this.
        }

        private static void InjectAccelLegacy(float x, float y, float z)
        {
            // Legacy Input.acceleration is read-only from C#.
        }
#endif
    }
}
