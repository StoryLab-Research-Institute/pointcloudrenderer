// Prototype gaze controller for testing foveated LOD without eye-tracking hardware.
// Not intended for distribution — attach to any GameObject in the scene alongside an PointCloudRenderer.

using UnityEngine;

namespace StoryLabResearch.PointCloud
{
    public class GazeControllerPrototype : MonoBehaviour
    {
        public enum EGazeMode
        {
            Fixed,   // target set manually via inspector or script
            Random,  // pseudo-realistic saccade simulation
        }

        private PointCloudRenderer _renderer;

        [Header("Target")]
        public EGazeMode Mode = EGazeMode.Fixed;
        [Tooltip("Gaze target in normalised viewport space (0-1). Used in Fixed mode.")]
        public Vector2 FixedTarget = new Vector2(0.5f, 0.5f);

        [Header("Motion")]
        [Tooltip("Speed at which gaze centre moves toward target, in viewport units per second. " +
                 "~4-8 is a rough approximation of smooth pursuit; saccades snap instantly.")]
        public float PursuitSpeed = 6f;

        [Header("Jitter")]
        [Tooltip("Enable fixation jitter to simulate microsaccades.")]
        public bool JitterEnabled = false;
        [Tooltip("Maximum jitter amplitude in viewport units.")]
        public float JitterAmplitude = 0.005f;
        [Tooltip("Jitter frequency in Hz.")]
        public float JitterFrequency = 30f;

        [Header("Smoothing")]
        [Tooltip("Enable One Euro Filter on the final gaze output. " +
                 "Suppresses jitter at fixation while passing saccades through unsmoothed.")]
        public bool SmoothingEnabled = false;
        [Tooltip("Minimum cutoff frequency (Hz). Lower = more smoothing at rest, more lag. " +
                 "Controls how aggressively fixation noise is suppressed.")]
        public float MinCutoff = 1f;
        [Tooltip("Speed coefficient. Higher = less smoothing during fast movement. " +
                 "Controls how quickly the filter opens up during saccades.")]
        public float Beta = 10f;

        // Current gaze position written to the renderer each frame.
        private Vector2 _gazeCentre = new Vector2(0.5f, 0.5f);
        private Vector2 _gazeTarget = new Vector2(0.5f, 0.5f);

        // Random saccade state.
        private float   _saccadeTimer;
        private float   _saccadeDuration;
        private bool    _inSaccade;
        private Vector2 _saccadeFrom;
        private Vector2 _saccadeTo;

        // Jitter state.
        private float   _jitterTimer;
        private Vector2 _jitterOffset;
        private Vector2 _jitterTarget;

        // One Euro Filter state — one filter per axis.
        private OneEuroFilter _filterX;
        private OneEuroFilter _filterY;
        private bool _filterInitialised;

        private void Update()
        {
            if (_renderer == null) _renderer = GetComponent<PointCloudRenderer>();
            UpdateTarget();
            UpdateGaze();
            UpdateJitter();

            Vector2 raw = _gazeCentre + (JitterEnabled ? _jitterOffset : Vector2.zero);

            if (SmoothingEnabled)
            {
                if (!_filterInitialised)
                {
                    _filterX = new OneEuroFilter(raw.x, MinCutoff, Beta);
                    _filterY = new OneEuroFilter(raw.y, MinCutoff, Beta);
                    _filterInitialised = true;
                }
                raw = new Vector2(
                    _filterX.Filter(raw.x, Time.deltaTime, MinCutoff, Beta),
                    _filterY.Filter(raw.y, Time.deltaTime, MinCutoff, Beta));
            }
            else
            {
                _filterInitialised = false;
            }

            if (_renderer != null)
                _renderer.FoveationCentre = raw;
        }

        private void UpdateTarget()
        {
            if (Mode == EGazeMode.Fixed)
            {
                _gazeTarget = FixedTarget;
                return;
            }

            // Random mode: wait out a fixation dwell, then trigger a saccade to a new target.
            // Targets are Gaussian-biased toward screen centre — approximates natural viewing statistics.
            _saccadeTimer -= Time.deltaTime;
            if (_saccadeTimer <= 0f)
            {
                if (_inSaccade)
                {
                    // Saccade complete — begin fixation dwell (200-400ms).
                    _inSaccade    = false;
                    _saccadeTimer = Random.Range(0.2f, 0.4f);
                }
                else
                {
                    // Begin next saccade — pick destination and duration.
                    _saccadeFrom     = _gazeTarget;
                    _saccadeTo       = GaussianViewportPoint();
                    _saccadeDuration = Random.Range(0.03f, 0.08f); // 30-80ms saccade
                    _saccadeTimer    = _saccadeDuration;
                    _inSaccade       = true;
                }
            }

            if (_inSaccade)
            {
                float t = 1f - Mathf.Clamp01(_saccadeTimer / _saccadeDuration);
                _gazeTarget = Vector2.Lerp(_saccadeFrom, _saccadeTo, t);
            }
        }

        private void UpdateGaze()
        {
            if (_inSaccade)
                _gazeCentre = _gazeTarget; // saccades are ballistic — snap rather than smooth
            else
                _gazeCentre = Vector2.MoveTowards(_gazeCentre, _gazeTarget, PursuitSpeed * Time.deltaTime);
        }

        private void UpdateJitter()
        {
            if (!JitterEnabled) return;

            _jitterTimer += Time.deltaTime * JitterFrequency;
            if (_jitterTimer >= 1f)
            {
                _jitterTimer -= 1f;
                _jitterTarget = Random.insideUnitCircle * JitterAmplitude;
            }
            _jitterOffset = Vector2.Lerp(_jitterOffset, _jitterTarget, Time.deltaTime * JitterFrequency * 2f);
        }

        // Gaussian-distributed viewport point biased toward centre.
        private static Vector2 GaussianViewportPoint()
        {
            float x = Mathf.Clamp01(0.5f + SampleGaussian() * 0.2f);
            float y = Mathf.Clamp01(0.5f + SampleGaussian() * 0.2f);
            return new Vector2(x, y);
        }

        // Box-Muller, zero mean unit variance.
        private static float SampleGaussian()
        {
            float u = 1f - Random.value;
            float v = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u)) * Mathf.Cos(2f * Mathf.PI * v);
        }

        // ----- One Euro Filter -----
        // Jitter-adaptive low-pass filter. At low velocity (fixation) the cutoff is low → heavy
        // smoothing. At high velocity (saccade) the cutoff rises → signal passes through unsmoothed.
        // Reference: Casiez et al., "1€ Filter: A Simple Speed-based Low-pass Filter for Noisy
        // Input in Interactive Systems", CHI 2012.
        private struct OneEuroFilter
        {
            private float _prev;
            private float _prevDerivative;

            public OneEuroFilter(float initialValue, float minCutoff, float beta)
            {
                _prev           = initialValue;
                _prevDerivative = 0f;
                _ = minCutoff; _ = beta; // consumed by Filter, not constructor
            }

            public float Filter(float x, float dt, float minCutoff, float beta)
            {
                // Derivative estimate — low-pass filtered to reduce noise amplification.
                const float derivCutoff = 1f;
                float alpha     = Alpha(dt, derivCutoff);
                float dx        = dt > 0f ? (x - _prev) / dt : 0f;
                float dxHat     = _prevDerivative + alpha * (dx - _prevDerivative);

                // Adaptive cutoff: rises with signal speed, suppressing lag during fast movement.
                float cutoff    = minCutoff + beta * Mathf.Abs(dxHat);
                float alphaX    = Alpha(dt, cutoff);
                float xHat      = _prev + alphaX * (x - _prev);

                _prev           = xHat;
                _prevDerivative = dxHat;
                return xHat;
            }

            // EMA alpha from cutoff frequency and timestep.
            private static float Alpha(float dt, float cutoff)
            {
                float tau = 1f / (2f * Mathf.PI * cutoff);
                return 1f / (1f + tau / dt);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            var cam = Camera.current;
            if (cam == null) return;

            DrawCross(cam, _gazeTarget, Color.yellow, "Target");
            DrawCross(cam, _gazeCentre, Color.blue,   "Gaze");

            if (JitterEnabled)
                DrawCross(cam, _gazeCentre + _jitterOffset, Color.red, "Final");
        }

        private static void DrawCross(Camera cam, Vector2 vp, Color color, string label)
        {
            float depth = (cam.nearClipPlane + cam.farClipPlane) * 0.5f;
            Vector3 centre = cam.ViewportToWorldPoint(new Vector3(vp.x, vp.y, depth));
            float size = (cam.farClipPlane - cam.nearClipPlane) * 0.005f;

            Vector3 right = cam.transform.right * size;
            Vector3 up    = cam.transform.up    * size;

            UnityEditor.Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            UnityEditor.Handles.color = color;
            UnityEditor.Handles.DrawLine(centre - right, centre + right);
            UnityEditor.Handles.DrawLine(centre - up,    centre + up);
            UnityEditor.Handles.Label(centre + up * 1.5f, label);
        }
#endif
    }
}
