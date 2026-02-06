using UnityEngine;

namespace MUES.Core
{
    public class MUES_SceneParentStabilizer
    {
        private readonly float _positionSmoothTime;
        private readonly float _rotationSmoothSpeed;

        private readonly float _maxDistanceThreshold;
        private readonly float _glitchTimeThreshold;

        private Vector3 _velocity;
        private float _glitchTimer;
        private Vector3 _lastValidPosition;
        private Quaternion _lastValidRotation;
        private bool _isFirstFrame = true;
        private bool _initialized;

        /// <summary>
        /// Indicates whether the stabilizer has been initialized with valid anchor data.
        /// </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Creates a new SceneParentStabilizer with default parameters matching the original implementation.
        /// </summary>
        public MUES_SceneParentStabilizer() : this(positionSmoothTime: 0.5f, rotationSmoothSpeed: 2f, maxDistanceThreshold: 0.5f, glitchTimeThreshold: 0.5f)
        {
        }

        /// <summary>
        /// Creates a new SceneParentStabilizer with custom parameters.
        /// </summary>

        public MUES_SceneParentStabilizer(float positionSmoothTime, float rotationSmoothSpeed, float maxDistanceThreshold, float glitchTimeThreshold)
        {
            _positionSmoothTime = positionSmoothTime;
            _rotationSmoothSpeed = rotationSmoothSpeed;
            _maxDistanceThreshold = maxDistanceThreshold;
            _glitchTimeThreshold = glitchTimeThreshold;
        }

        /// <summary>
        /// Initializes the stabilizer with the starting position and rotation.
        /// </summary>
        public void Initialize(Vector3 initialPosition, Quaternion initialRotation)
        {
            _lastValidPosition = initialPosition;
            _lastValidRotation = initialRotation;
            _velocity = Vector3.zero;
            _glitchTimer = 0f;
            _isFirstFrame = false;
            _initialized = true;
        }

        /// <summary>
        /// Resets the stabilizer to its initial state.
        /// </summary>
        public void Reset()
        {
            _velocity = Vector3.zero;
            _glitchTimer = 0f;
            _lastValidPosition = Vector3.zero;
            _lastValidRotation = Quaternion.identity;
            _isFirstFrame = true;
            _initialized = false;
        }

        /// <summary>
        /// Updates the scene parent transform based on the anchor transform, applying glitch filtering and smoothing.
        /// </summary>
        public bool UpdateSceneParent(Transform sceneParent, Transform anchorTransform, bool debugMode = false, string debugPrefix = "[MUES_SceneParentStabilizer]")
        {
            if (sceneParent == null || anchorTransform == null)
                return false;

            Vector3 currentAnchorPos = GetFloorAlignedPosition(anchorTransform);
            Quaternion currentAnchorRot = GetFlatRotation(anchorTransform);

            if (_isFirstFrame)
            {
                _lastValidPosition = currentAnchorPos;
                _lastValidRotation = currentAnchorRot;
                _isFirstFrame = false;
                _initialized = true;
            }

            float distance = Vector3.Distance(_lastValidPosition, currentAnchorPos);
            Vector3 targetPos;
            Quaternion targetRot;

            if (distance > _maxDistanceThreshold)
            {
                _glitchTimer += Time.deltaTime;

                if (_glitchTimer > _glitchTimeThreshold)
                {
                    targetPos = currentAnchorPos;
                    targetRot = currentAnchorRot;
                    _lastValidPosition = currentAnchorPos;
                    _lastValidRotation = currentAnchorRot;
                    
                    ConsoleMessage.Send(debugMode, $"{debugPrefix} Anchor jump accepted after {_glitchTimer:F2}s - likely HMD recenter.", Color.yellow);
                }
                else
                {
                    targetPos = _lastValidPosition;
                    targetRot = _lastValidRotation;
                }
            }
            else
            {
                _glitchTimer = 0f;
                targetPos = currentAnchorPos;
                targetRot = currentAnchorRot;
                _lastValidPosition = currentAnchorPos;
                _lastValidRotation = currentAnchorRot;
            }

            sceneParent.position = Vector3.SmoothDamp(sceneParent.position, targetPos, ref _velocity, _positionSmoothTime);
            sceneParent.rotation = Quaternion.Slerp(sceneParent.rotation, targetRot, Time.deltaTime * _rotationSmoothSpeed);

            return true;
        }

        /// <summary>
        /// Calculates a floor-aligned position from a transform, using the tracking space Y if available.
        /// </summary>
        public static Vector3 GetFloorAlignedPosition(Transform sourceTransform, float? overrideY = null)
        {
            float yPos = overrideY ?? 0f;

            if (!overrideY.HasValue)
            {
                var rig = Object.FindFirstObjectByType<OVRCameraRig>();
                yPos = rig != null ? rig.trackingSpace.position.y : 0f;
            }

            return new Vector3(sourceTransform.position.x, yPos, sourceTransform.position.z);
        }

        /// <summary>
        /// Calculates a flat (Y-axis only) rotation from a transform's forward direction.
        /// </summary>
        public static Quaternion GetFlatRotation(Transform sourceTransform)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(sourceTransform.forward, Vector3.up).normalized;
            return flatForward.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(flatForward, Vector3.up)
                : Quaternion.identity;
        }

        /// <summary>
        /// Gets both floor-aligned position and flat rotation from a transform.
        /// </summary>

        public static (Vector3 position, Quaternion rotation) GetFloorAlignedPose(Transform sourceTransform, float? overrideY = null)
        {
            return (GetFloorAlignedPosition(sourceTransform, overrideY), GetFlatRotation(sourceTransform));
        }
    }
}
