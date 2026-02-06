using Fusion;
using System.Collections;
using UnityEngine;

namespace MUES.Core
{
    public abstract class MUES_AnchoredNetworkBehaviour : NetworkBehaviour
    {
        [HideInInspector][Networked] public Vector3 LocalAnchorOffset { get; set; } // Offset from the anchor in local space
        [HideInInspector][Networked] public Quaternion LocalAnchorRotationOffset { get; set; }  // Offset from the anchor in local space

        [HideInInspector] public bool initialized;  // Indicates if the anchor has been initialized

        private readonly float anchorPositionSmoothTime = 0.35f;    // Smoothing time for position updates
        private readonly float anchorRotationSmoothSpeed = 7f;  // Smoothing speed for rotation updates

        private protected Transform anchor; // The anchor transform to which this object is anchored (sceneParent for both colocated and remote)
        private protected bool anchorReady; // Indicates if the anchor is ready for use

        private Vector3 anchorPosVelocity;  // Velocity used for position smoothing
        private Quaternion anchorSmoothRot; // Smoothed rotation
        private bool anchorSmoothingInitialized;    // Indicates if smoothing has been initialized

        /// <summary>
        /// Initializes the anchor by waiting for the networking instance and the scene parent to become available.
        /// </summary>
        protected IEnumerator InitAnchorRoutine()
        {
            yield return null;
            ConsoleMessage.Send(true, "Starting AnchoredNetworkBehavior init.", Color.green);

            while (MUES_Networking.Instance == null)
                yield return null;

            var net = MUES_Networking.Instance;
            var roomVis = MUES_RoomVisualizer.Instance;

            float timeout = 10f;
            float elapsed = 0f;

            while (anchor == null && elapsed < timeout)
            {
                if (net.isRemote)
                {
                    var remoteSceneParent = roomVis?.GetRemoteSceneParent();
                    if (remoteSceneParent != null)
                    {
                        anchor = remoteSceneParent;
                        ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Remote client using REMOTE_SCENE_PARENT as anchor: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.cyan);
                    }
                }
                else
                {
                    if (net.sceneParent != null)
                    {
                        anchor = net.sceneParent;
                        ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Using sceneParent as anchor: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.cyan);
                    }
                    else
                    {
                        if (net.anchorTransform == null)
                        {
                            var anchorGO = GameObject.FindWithTag("RoomCenterAnchor");
                            if (anchorGO != null)
                            {
                                net.anchorTransform = anchorGO.transform;
                                ConsoleMessage.Send(true, $"AnchoredNetworkBehavior - Found anchor via tag: {net.anchorTransform.name}", Color.cyan);
                            }
                        }

                        if (net.anchorTransform != null && net.sceneParent == null)
                            net.InitSceneParent();
                    }
                }

                if (anchor == null)
                {
                    ConsoleMessage.Send(true, $"Waiting for anchor... (isRemote={net.isRemote}, sceneParent={net.sceneParent != null}, anchorTransform={net.anchorTransform != null}, remoteSceneParent={roomVis?.GetRemoteSceneParent() != null})", Color.yellow);
                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }

            if (anchor == null)
            {
                ConsoleMessage.Send(true, "Timeout waiting for anchor!", Color.red);
                MUES_Networking.Instance.LeaveRoom();
                yield break;
            }


            transform.SetParent(anchor, true);
            ConsoleMessage.Send(true, $"ANCHORED BEHAVIOR - Parented to {anchor.name} at {anchor.position}", Color.green);

            anchorReady = true;
            ConsoleMessage.Send(true, $"ANCHORED BEHAVIOR - Anchor ready: {anchor.name} at {anchor.position}, rot: {anchor.rotation.eulerAngles}", Color.green);
        }

        /// <summary>
        /// Converts the local anchor offsets to world position and rotation.
        /// </summary>
        protected void AnchorToWorld()
        {
            if (!anchorReady || anchor == null) return;

            Vector3 targetPos;
            Quaternion targetRot;

            try
            {
                targetPos = anchor.TransformPoint(LocalAnchorOffset);
                targetRot = anchor.rotation * LocalAnchorRotationOffset;
            }
            catch (System.InvalidOperationException)
            {
                return;
            }

            bool hasInputAuth = Object != null && Object.IsValid && Object.HasInputAuthority;

            if (hasInputAuth || !anchorSmoothingInitialized)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                anchorPosVelocity = Vector3.zero;
                anchorSmoothRot = targetRot;
                anchorSmoothingInitialized = true;
                return;
            }

            var smoothedPos = Vector3.SmoothDamp(transform.position, targetPos, ref anchorPosVelocity, anchorPositionSmoothTime);
            anchorSmoothRot = Quaternion.Slerp(anchorSmoothRot, targetRot, Time.deltaTime * anchorRotationSmoothSpeed);
            transform.SetPositionAndRotation(smoothedPos, anchorSmoothRot);
        }

        /// <summary>
        /// Converts the current world position and rotation to local anchor offsets.
        /// </summary>
        protected void WorldToAnchor()
        {
            if (!anchorReady || anchor == null) return;

            try
            {
                transform.GetPositionAndRotation(out var pos, out var rot);
                LocalAnchorOffset = anchor.InverseTransformPoint(pos);
                LocalAnchorRotationOffset = Quaternion.Inverse(anchor.rotation) * rot;
            }
            catch (System.InvalidOperationException) { }
        }

        /// <summary>
        /// Public wrapper to force update the anchor offset from current world position.
        /// </summary>
        public void ForceUpdateAnchorOffset() => WorldToAnchor();
    }
}