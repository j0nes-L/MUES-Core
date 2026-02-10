using Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using Photon.Voice.Unity;
using Oculus.Platform;

namespace MUES.Core
{
    public class MUES_AvatarMarker : MUES_AnchoredNetworkBehaviour
    {
        [Header("Debug Settings")]
        [Tooltip("If true, the local player's avatar parts will be destroyed to avoid self-occlusion.")]
        public bool destroyOwnMarker = true;
        [Tooltip("If true, debug information will be logged to the console.")]
        public bool debugMode = true;

        [HideInInspector][Networked] public NetworkString<_32> UserGuid { get; set; }
        [HideInInspector][Networked, OnChangedRender(nameof(OnPlayerJoined))] public NetworkString<_64> PlayerName { get; set; }

        [HideInInspector][Networked] public NetworkBool IsHmdMounted { get; set; } = true; // Default to true so avatars are visible by default
        [HideInInspector][Networked] public NetworkBool IsStabilizing { get; set; } = false; // True while avatar is stabilizing after HMD mount
        [HideInInspector][Networked] public NetworkBool IsPositionInitialized { get; set; } = false; // True when avatar position has been properly set

        [HideInInspector][Networked] public Vector3 HeadLocalPos { get; set; }  // Local position of the head relative to the avatar marker
        [HideInInspector][Networked] public Quaternion HeadLocalRot { get; set; }   // Local rotation of the head relative to the avatar marker

        [HideInInspector][Networked] public Vector3 HeadAnchorRelativePos { get; set; }       // Head position relative to the anchor (floor-level) for consistent height across different devices

        [HideInInspector][Networked] public Vector3 RightHandLocalPos { get; set; } // Local position of the right hand relative to the avatar marker
        [HideInInspector][Networked] public Vector3 LeftHandLocalPos { get; set; }  // Local position of the left hand relative to the avatar marker

        [HideInInspector][Networked] public Quaternion RightHandLocalRot { get; set; }  // Local rotation of the right hand relative to the avatar marker
        [HideInInspector][Networked] public Quaternion LeftHandLocalRot { get; set; }   // Local rotation of the left hand relative to the avatar marker

        [HideInInspector][Networked] public Vector3 RightHandAnchorRelativePos { get; set; }    // Right hand position relative to the anchor for consistent positioning in colocated scenarios
        [HideInInspector][Networked] public Vector3 LeftHandAnchorRelativePos { get; set; } // Left hand position relative to the anchor for consistent positioning in colocated scenarios

        [HideInInspector][Networked] public NetworkBool RightHandVisible { get; set; }  // Visibility state of the right hand marker
        [HideInInspector][Networked] public NetworkBool LeftHandVisible { get; set; }   // Visibility state of the left hand marker

        [HideInInspector][Networked] public NetworkBool IsRemote { get; set; }  // True if this avatar belongs to a remote player

        [HideInInspector][Networked] public NetworkBool IsReadyToBeVisible { get; set; }    // True when the avatar has received enough data to be shown (e.g. position initialized)

        [HideInInspector][Networked] public NetworkBool IsAfk { get; set; }  // True when HMD is unmounted or stabilizing
        [HideInInspector][Networked] public Vector3 AfkMarkerLocalPos { get; set; }  // Last known position for AFK marker (anchor-relative)
        [HideInInspector][Networked] public Quaternion AfkMarkerLocalRot { get; set; }  // Last known rotation for AFK marker (anchor-relative)

        [HideInInspector][Networked] public float TrackingSpaceYOffset { get; set; }  // Y offset of the tracking space relative to the anchor (for height correction)

        [HideInInspector] public bool IsLocallyMuted { get; private set; } = false; // True if this player is locally muted by the viewing user (not networked)
        [HideInInspector] public AudioSource voiceAudioSource;   // The AudioSource playing the users voice

        private GameObject afkMarker; // AFK marker object 
        private Transform mainCam, trackingSpace;   // OVR Camera Rig tracking space
        private Transform head, nameTag, handMarkerRight, handMarkerLeft;   // Avatar parts

        private MeshRenderer headRenderer, handRendererR, handRendererL;    // Renderers for visibility control
        private CanvasGroup nameTagCanvasGroup; // Canvas group for name tag visibility
        private UnityEngine.UI.Image nameTagBackground; // Background image for the name tag (for color changes)
        private TextMeshProUGUI nameText, nameTextAfk;   // Text component for displaying player name

        private readonly float rotationSmoothSpeed = 15f;    // Speed for smooth name tag rotation
        private readonly float handSmoothTime = 0.06f;  // Smoothing time for hand marker movement

        private const float defaultNameTagHeight = 0.15f;   // Default height offset for name tag above head
        private const float colocatedNameTagHeight = 0.25f;  // Height offset for name tag when avatar is hidden (colocated)
        private const string fallbackPlayerName = "MUES-User"; // Fallback name when username cannot be fetched
        private const float componentInitTimeout = 7f; // Default timeout for component initialization

        private Vector3 rightHandVel, leftHandVel;  // Velocity references for SmoothDamp
        private Quaternion nameSmoothRot, rightHandSmoothRot, leftHandSmoothRot;   // Smoothed rotation for name tag

        private Recorder voiceRecorder; // The Recorder component for the voice chat
        private Speaker voiceSpeaker;   // The Speaker component for the voice chat

        private bool isWaitingAfterMount; // Flag to delay visibility after HMD mount
        private string cachedPlayerName; // Locally cached player name for use when network state is unavailable
        private bool voiceSetupComplete; // Flag to track if voice setup has been completed
        private bool voiceSetupPending; // Flag für verzögertes Voice-Setup
        private float voiceCheckTimer; // Timer für periodische Voice-Checks

        private bool HasInputAuth => Object.HasInputAuthority;

        void OnEnable()
        {
            OVRManager.HMDMounted += OnHMDMounted;
            OVRManager.HMDUnmounted += OnHMDUnmounted;
        }

        void OnDisable()
        {
            OVRManager.HMDMounted -= OnHMDMounted;
            OVRManager.HMDUnmounted -= OnHMDUnmounted;
        }

        /// <summary>
        /// Shows the avatar when the HMD is mounted.
        /// </summary>
        private void OnHMDMounted()
        {
            if (!HasInputAuth) return;

            StartCoroutine(DelayedHMDMountVisibility());
            UpdateAfkMarker();

            if (voiceRecorder != null)
            {
                voiceRecorder.TransmitEnabled = true;
                ConsoleMessage.Send(debugMode, "HMD Mounted - Microphone unmuted.", Color.cyan);
            }
        }

        /// <summary>
        /// Delays the avatar visibility after HMD mount by 3 seconds.
        /// </summary>
        private IEnumerator DelayedHMDMountVisibility()
        {
            if (IsRemoteOrShowAvatars())
            {
                isWaitingAfterMount = true;
                IsStabilizing = true;

                ConsoleMessage.Send(debugMode, "HMD Mounted - Waiting 5s before showing Avatar...", Color.cyan);

                yield return new WaitForSeconds(5f);
                isWaitingAfterMount = false;
                IsStabilizing = false;
            }

            IsHmdMounted = true;
            IsAfk = false;
            UpdateAfkMarker();

            ConsoleMessage.Send(debugMode, "HMD Mounted - Player has returned.", Color.cyan);
        }

        /// <summary>
        /// Hides the avatar when the HMD is unmounted.
        /// </summary>
        private void OnHMDUnmounted()
        {
            if (!HasInputAuth) return;

            SaveCurrentPositionForAfkMarker();

            isWaitingAfterMount = false;
            IsStabilizing = false;
            StopCoroutine(DelayedHMDMountVisibility());

            IsHmdMounted = false;
            IsAfk = true;
            UpdateAfkMarker();

            if (voiceRecorder != null)
            {
                voiceRecorder.TransmitEnabled = false;
                ConsoleMessage.Send(debugMode, "HMD Unmounted - Microphone muted.", Color.cyan);
            }

            ConsoleMessage.Send(debugMode, "HMD Unmounted - Player is afk.", Color.cyan);
        }

        /// <summary>
        /// Gets executed when the avatar marker is spawned in the network.
        /// </summary>
        public override void Spawned()
        {
            CacheComponents();
            SetInitialVisibility(false);
            StartCoroutine(WaitForComponentInit());
        }

        /// <summary>
        /// Used for setting up the references to avatar parts and voice components. 
        /// </summary>
        private void CacheComponents()
        {
            head = transform.Find("Head");
            headRenderer = head.GetComponentInChildren<MeshRenderer>();

            nameTag = head.GetChild(0);
            nameTagCanvasGroup = nameTag.GetComponent<CanvasGroup>();
            nameTagBackground = nameTagCanvasGroup.GetComponentInChildren<UnityEngine.UI.Image>();
            nameText = nameTagCanvasGroup.GetComponentInChildren<TextMeshProUGUI>();

            handMarkerRight = transform.Find("HandMarkerR");
            handRendererR = handMarkerRight.GetComponentInChildren<MeshRenderer>();

            handMarkerLeft = transform.Find("HandMarkerL");
            handRendererL = handMarkerLeft.GetComponentInChildren<MeshRenderer>();

            voiceRecorder = head.GetComponent<Recorder>();
            voiceSpeaker = head.GetComponent<Speaker>();
            voiceAudioSource = head.GetComponent<AudioSource>();

            afkMarker = transform.GetChild(3).gameObject;
            nameTextAfk = afkMarker.GetComponentInChildren<TextMeshProUGUI>();

            ConsoleMessage.Send(debugMode, $"Avatar - Voice Components found: Recorder={voiceRecorder != null}, Speaker={voiceSpeaker != null}, AudioSource={voiceAudioSource != null}", Color.cyan);
        }

        /// <summary>
        /// Sets the initial visibility of avatar parts to false until the avatar is ready to be shown. This prevents floating body parts from appearing at the spawn location before the avatar is properly initialized and positioned.
        /// </summary>
        private void SetInitialVisibility(bool visible)
        {
            if (headRenderer != null) headRenderer.enabled = visible;
            if (handRendererR != null) handRendererR.enabled = visible;
            if (handRendererL != null) handRendererL.enabled = visible;
            if (nameTagCanvasGroup != null) nameTagCanvasGroup.alpha = visible ? 1f : 0f;
            afkMarker.SetActive(false);
        }

        /// <summary>
        /// Waits for component initialization and sets up the avatar marker based on networked data.
        /// </summary>
        public IEnumerator WaitForComponentInit()
        {
            yield return InitAnchorRoutine();
            
            bool dependenciesReady = false;
            yield return WaitForDependencies(success => dependenciesReady = success);
            
            if (!dependenciesReady)
            {
                ConsoleMessage.Send(debugMode, "Avatar - Failed to initialize dependencies, aborting.", Color.red);
                Net?.LeaveRoom();
                yield break;
            }

            mainCam = Camera.main.transform;

            if (HasInputAuth)
                yield return InitializeLocalPlayer();
            else
            {
                bool remoteInitSuccess = false;
                yield return InitializeRemotePlayer(success => remoteInitSuccess = success);
                
                if (!remoteInitSuccess)
                {
                    ConsoleMessage.Send(debugMode, "Avatar - Remote player initialization failed.", Color.red);
                    yield break;
                }
            }

            if (nameTag != null) nameSmoothRot = nameTag.rotation;
            yield return FetchOculusUsername();
            if (nameTag != null) nameTagCanvasGroup.alpha = 1f;

            SetupVoiceForPlayer();

            if (destroyOwnMarker && HasInputAuth) 
                StartCoroutine(DestroyOwnMarkerRoutine());
            
            if (headRenderer != null) 
                headRenderer.enabled = ShouldShowAvatar();
            
            initialized = true;

            ConsoleMessage.Send(debugMode, "Avatar - Component Init ready. - Avatar Setup complete", Color.green);
        }

        /// <summary>
        /// Waits for a condition to be true with a configurable timeout. Reports success/failure via callback.
        /// </summary>
        private IEnumerator WaitForConditionWithTimeout(Func<bool> condition, float timeout, string componentName, Action<bool> onComplete = null)
        {
            float elapsed = 0f;
            
            while (!condition() && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            bool success = condition();
            
            if (!success)
                ConsoleMessage.Send(debugMode, $"Avatar - Timeout ({timeout}s) waiting for {componentName}!", Color.red);
            
            onComplete?.Invoke(success);
        }

        /// <summary>
        /// Waits for all necessary dependencies (Session Meta, Main Camera, OVR Camera Rig) to be ready before proceeding with avatar initialization.
        /// </summary>
        private IEnumerator WaitForDependencies(Action<bool> onComplete)
        {
            bool sessionMetaReady = false;
            yield return WaitForConditionWithTimeout(
                () => MUES_SessionMeta.Instance != null,
                componentInitTimeout,
                "Session Meta",
                success => sessionMetaReady = success);
            
            if (!sessionMetaReady)
            {
                onComplete?.Invoke(false);
                yield break;
            }
            ConsoleMessage.Send(debugMode, "Avatar - Session Meta found.", Color.cyan);

            bool cameraReady = false;
            yield return WaitForConditionWithTimeout(
                () => Camera.main != null,
                componentInitTimeout,
                "Main Camera",
                success => cameraReady = success);
            
            if (!cameraReady)
            {
                onComplete?.Invoke(false);
                yield break;
            }
            ConsoleMessage.Send(debugMode, "Avatar - Main Camera found.", Color.cyan);

            bool trackingSpaceReady = false;
            yield return WaitForConditionWithTimeout(
                () => 
                {
                    if (trackingSpace != null) return true;
                    var rig = FindFirstObjectByType<OVRCameraRig>();
                    if (rig != null) trackingSpace = rig.trackingSpace;
                    return trackingSpace != null;
                },
                componentInitTimeout,
                "OVR Camera Rig / Tracking Space",
                success => trackingSpaceReady = success);
            
            if (!trackingSpaceReady)
            {
                onComplete?.Invoke(false);
                yield break;
            }
            ConsoleMessage.Send(debugMode, "Avatar - OVR Camera Rig found.", Color.cyan);

            onComplete?.Invoke(true);
        }

        /// <summary>
        /// Initializes the local player's avatar.
        /// </summary>
        private IEnumerator InitializeLocalPlayer()
        {
            var camPos = mainCam.position;
            float markerY = trackingSpace != null ? trackingSpace.position.y : (anchor != null ? anchor.position.y : 0f);

            transform.SetPositionAndRotation(
                new Vector3(camPos.x, markerY, camPos.z),
                Quaternion.Euler(0f, mainCam.eulerAngles.y, 0f));
            WorldToAnchor();

            if (anchor != null && trackingSpace != null)
            {
                TrackingSpaceYOffset = trackingSpace.position.y - anchor.position.y;
                ConsoleMessage.Send(debugMode, $"Avatar - TrackingSpaceYOffset set to: {TrackingSpaceYOffset}", Color.cyan);
            }

            UserGuid = Guid.NewGuid().ToString();
            IsHmdMounted = OVRManager.isHmdPresent;
            IsRemote = Net != null && Net.isRemote;
            IsPositionInitialized = true;

            ConsoleMessage.Send(debugMode, $"Avatar - Local player initialized. IsRemote={IsRemote}, MarkerY={markerY}", Color.green);
            yield break;
        }

        /// <summary>
        /// Initializes the remote player's avatar.
        /// </summary>
        private IEnumerator InitializeRemotePlayer(Action<bool> onComplete)
        {
            bool remoteDataReady = false;
            yield return WaitForConditionWithTimeout(
                () => !string.IsNullOrEmpty(UserGuid.ToString()) && IsPositionInitialized,
                componentInitTimeout,
                "Remote User Data (GUID + Position)",
                success => remoteDataReady = success);

            if (!remoteDataReady)
            {
                onComplete?.Invoke(false);
                yield break;
            }

            AnchorToWorld();
            ConsoleMessage.Send(debugMode, $"Avatar - Remote user initialized. IsRemote={IsRemote}, UserGuid={UserGuid}", Color.green);
            onComplete?.Invoke(true);
        }

        /// <summary>
        /// Sets up the voice components for the avatar.
        /// </summary>
        private void SetupVoiceForPlayer()
        {
            if (HasInputAuth)
                SetupVoiceComponents();
            else
            {
                voiceSetupPending = true;
                StartCoroutine(DelayedVoiceSetup());
            }
        }

        /// <summary>
        /// Delays the setup of voice components for remote players to ensure proper initialization and avoid potential issues with the voice system.
        /// </summary>
        private IEnumerator DelayedVoiceSetup()
        {
            yield return null;           
            ConsoleMessage.Send(debugMode, $"Avatar - Delayed Voice Setup starting. IsRemote={IsRemote}", Color.cyan);
            
            SetupVoiceComponents();
            voiceSetupPending = false;
        }

        /// <summary>
        /// Fetches the Oculus username using the Platform API with proper initialization.
        /// </summary>
        private IEnumerator FetchOculusUsername()
        {
            string fetchedName = null;
            bool fetchComplete = false;

            PlatformInit.GetEntitlementInformation(info =>
            {
                if (info.IsEntitled)
                {
                    fetchedName = !string.IsNullOrEmpty(info.OculusUser?.DisplayName) 
                        ? info.OculusUser.DisplayName 
                        : info.OculusUser?.OculusID;
                    
                    ConsoleMessage.Send(debugMode, $"Avatar - PlatformInit: Entitled={info.IsEntitled}, DisplayName='{info.OculusUser?.DisplayName}', OculusID='{info.OculusUser?.OculusID}'", Color.cyan);
                }
                else
                {
                    ConsoleMessage.Send(debugMode, "Avatar - PlatformInit: User not entitled!", Color.red);
                }
                fetchComplete = true;
            });

            yield return WaitForConditionWithTimeout(() => fetchComplete, 3f, "PlatformInit Entitlement");

            if (!string.IsNullOrEmpty(fetchedName))
            {
                SetPlayerName(fetchedName);
                yield break;
            }

            ConsoleMessage.Send(debugMode, "Avatar - PlatformInit failed, trying direct Oculus Platform API...", Color.yellow);

            bool platformInitialized = false;
            bool platformInitComplete = false;
            bool userFetchComplete = false;

            bool alreadyInitialized;
            try
            {
                alreadyInitialized = Oculus.Platform.Core.IsInitialized();
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(debugMode, $"Avatar - Exception checking platform init: {ex.Message}", Color.red);
                SetPlayerName(fallbackPlayerName);
                yield break;
            }

            if (!alreadyInitialized)
            {
                try
                {
                    Oculus.Platform.Core.AsyncInitialize().OnComplete(msg =>
                    {
                        platformInitialized = !msg.IsError;
                        platformInitComplete = true;
                        
                        if (msg.IsError)
                            ConsoleMessage.Send(debugMode, $"Avatar - Platform init error: {msg.GetError().Message}", Color.red);
                    });
                }
                catch (Exception ex)
                {
                    ConsoleMessage.Send(debugMode, $"Avatar - Exception during platform init: {ex.Message}", Color.red);
                    SetPlayerName(fallbackPlayerName);
                    yield break;
                }

                yield return WaitForConditionWithTimeout(() => platformInitComplete, 5f, "Oculus Platform Init");
            }
            else
            {
                platformInitialized = true;
            }

            if (platformInitialized)
            {
                try
                {
                    Users.GetLoggedInUser().OnComplete(userMsg =>
                    {
                        if (!userMsg.IsError && userMsg.Data != null)
                        {
                            fetchedName = !string.IsNullOrEmpty(userMsg.Data.DisplayName) 
                                ? userMsg.Data.DisplayName 
                                : userMsg.Data.OculusID;
                            
                            ConsoleMessage.Send(debugMode, $"Avatar - Oculus API: DisplayName='{userMsg.Data.DisplayName}', OculusID='{userMsg.Data.OculusID}', ID={userMsg.Data.ID}", Color.cyan);
                        }
                        else
                        {
                            string error = userMsg.IsError ? userMsg.GetError().Message : "No data";
                            ConsoleMessage.Send(debugMode, $"Avatar - Failed to get user: {error}", Color.red);
                        }
                        userFetchComplete = true;
                    });
                }
                catch (Exception ex)
                {
                    ConsoleMessage.Send(debugMode, $"Avatar - Exception fetching user: {ex.Message}", Color.red);
                    userFetchComplete = true;
                }

                yield return WaitForConditionWithTimeout(() => userFetchComplete, 5f, "Oculus User Fetch");
            }

            if (string.IsNullOrEmpty(fetchedName))
            {
                ConsoleMessage.Send(debugMode, $"Avatar - All methods failed! Using fallback: {fallbackPlayerName}", Color.yellow);
                fetchedName = fallbackPlayerName;
            }

            SetPlayerName(fetchedName);
        }

        /// <summary>
        /// Sets the player name and registers it with the session.
        /// </summary>
        private void SetPlayerName(string name)
        {
            PlayerName = name;
            if (HasInputAuth && MUES_SessionMeta.Instance != null)
                MUES_SessionMeta.Instance.RegisterPlayer(Object.InputAuthority, name);

            UpdateNameTagText();
        }

        /// <summary>
        /// Updates the nametag text with the current player name.
        /// </summary>
        private void UpdateNameTagText()
        {
            string name = PlayerName.ToString();

            if (string.IsNullOrEmpty(name))
                name = fallbackPlayerName;

            cachedPlayerName = name;

            if (nameText != null)
                nameText.text = name;

            if (nameTextAfk != null)
                nameTextAfk.text = name + " (AFK)";

            ConsoleMessage.Send(debugMode, $"Avatar - Nametag updated to: {name}", Color.cyan);
        }

        public void SetNameplateColor(Color color)
        {
            if (nameTagBackground != null)
                nameTagBackground.color = color;
        }   

        /// <summary>
        /// Gets executed every frame to update the avatar marker's position, rotation, and visibility.
        /// </summary>
        public override void Render()
        {
            if (!initialized) return;
            
            if (!HasInputAuth)
            {
                if (!IsPositionInitialized)
                {
                    SetInitialVisibility(false);
                    return;
                }
                
                if (anchorReady) AnchorToWorld();
            }

            bool isCurrentlyStabilizing = HasInputAuth ? isWaitingAfterMount : IsStabilizing;
            bool showFullAvatar = ShouldShowAvatar();
            bool showNameTagOnly = ShouldShowNameTagOnly();
            bool showNameTag = showFullAvatar || showNameTagOnly;

            if (!HasInputAuth)
                UpdateAfkMarker();

            UpdateNameTagVisibility(showNameTag, isCurrentlyStabilizing);

            if (isCurrentlyStabilizing)
            {
                SetAvatarRenderersEnabled(false);
                return;
            }

            UpdateNameTagTransform(showNameTag, showNameTagOnly);

            bool useAnchorRelative = ShouldUseAnchorRelativePositioning();
            UpdateHeadTransform(useAnchorRelative);

            if (headRenderer != null)
                headRenderer.enabled = showFullAvatar;

            UpdateHandMarker(showFullAvatar && RightHandVisible, handMarkerRight, handRendererR,
                RightHandLocalPos, RightHandLocalRot, RightHandAnchorRelativePos, 
                useAnchorRelative, ref rightHandVel, ref rightHandSmoothRot);

            UpdateHandMarker(showFullAvatar && LeftHandVisible, handMarkerLeft, handRendererL,
                LeftHandLocalPos, LeftHandLocalRot, LeftHandAnchorRelativePos,
                useAnchorRelative, ref leftHandVel, ref leftHandSmoothRot);

            voiceCheckTimer += Time.deltaTime;
            if (voiceCheckTimer >= 2f)
            {
                voiceCheckTimer = 0f;
                EnsureVoiceComponentsActive();
            }
        }

        /// <summary>
        /// Controls the visibility of the avatar's renderers based on the provided enabled state.
        /// </summary>
        private void SetAvatarRenderersEnabled(bool enabled)
        {
            if (headRenderer != null) headRenderer.enabled = enabled;
            if (handRendererR != null) handRendererR.enabled = enabled;
            if (handRendererL != null) handRendererL.enabled = enabled;
        }

        /// <summary>
        /// Controls the visibility of the name tag based on whether the full avatar is shown or if only the name tag should be shown.
        /// </summary>
        private void UpdateNameTagVisibility(bool showNameTag, bool isStabilizing)
        {
            if (nameTagCanvasGroup == null) return;

            float targetAlpha = (showNameTag && !isStabilizing) ? 1f : 0f;
            if (Mathf.Abs(nameTagCanvasGroup.alpha - targetAlpha) > 0.01f)
                nameTagCanvasGroup.alpha = Mathf.MoveTowards(nameTagCanvasGroup.alpha, targetAlpha, Time.deltaTime * 5f);
        }

        /// <summary>
        /// Updates the name tag's position and rotation to face the camera, with a height offset based on whether only the name tag is shown or the full avatar is shown.
        /// </summary>
        private void UpdateNameTagTransform(bool showNameTag, bool showNameTagOnly)
        {
            if (!showNameTag || nameTag == null || mainCam == null) return;

            var toCam = mainCam.position - nameTag.position;
            if (toCam.sqrMagnitude > 0.0001f)
            {
                var targetRot = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                nameSmoothRot = Quaternion.Slerp(nameSmoothRot, targetRot, Time.deltaTime * rotationSmoothSpeed);
                nameTag.rotation = nameSmoothRot;
            }

            float targetHeightOffset = showNameTagOnly ? colocatedNameTagHeight : defaultNameTagHeight;
            Vector3 nameTagTargetPos = new Vector3(0f, targetHeightOffset, 0f);
            nameTag.localPosition = Vector3.Lerp(nameTag.localPosition, nameTagTargetPos, Time.deltaTime * 5f);
        }

        /// <summary>
        /// Updates the head marker's position and rotation based on whether anchor-relative positioning should be used.
        /// </summary>
        /// <param name="useAnchorRelative"></param>
        private void UpdateHeadTransform(bool useAnchorRelative)
        {
            if (head == null) return;

            Vector3 headWorldPos = (useAnchorRelative && anchor != null && HeadAnchorRelativePos != Vector3.zero)
                ? anchor.TransformPoint(HeadAnchorRelativePos)
                : transform.TransformPoint(HeadLocalPos);

            head.SetPositionAndRotation(headWorldPos, transform.rotation * HeadLocalRot);
        }

        /// <summary>
        /// Determines if anchor-relative positioning should be used for this avatar.
        /// </summary>
        private bool ShouldUseAnchorRelativePositioning()
        {
            if (HasInputAuth) return false;
            return Net != null && (Net.isRemote || IsRemote);
        }

        /// <summary>
        /// Updates the position, rotation, and visibility of a hand marker based on the provided parameters.
        /// </summary>
        private void UpdateHandMarker(bool visible, Transform marker, MeshRenderer renderer,
            Vector3 localPos, Quaternion localRot, Vector3 anchorRelativePos,
            bool useAnchorRelative, ref Vector3 vel, ref Quaternion smoothRot)
        {
            if (renderer != null)
                renderer.enabled = visible;

            if (!visible || marker == null) return;

            Vector3 targetPos = (useAnchorRelative && anchor != null && anchorRelativePos != Vector3.zero)
                ? anchor.TransformPoint(anchorRelativePos)
                : transform.TransformPoint(localPos);
            
            var smoothPos = Vector3.SmoothDamp(marker.position, targetPos, ref vel, handSmoothTime);
            var targetRot = transform.rotation * localRot;
            smoothRot = Quaternion.Slerp(marker.rotation, targetRot, Time.deltaTime * rotationSmoothSpeed);
            marker.SetPositionAndRotation(smoothPos, smoothRot);
        }

        /// <summary>
        /// Gets executed every physics frame to update the avatar marker's networked data.
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (!initialized || trackingSpace == null || !anchorReady || !HasInputAuth) return;

            WorldToAnchor();

            mainCam.GetPositionAndRotation(out var headWorldPos, out var headWorldRot);

            HeadLocalPos = transform.InverseTransformPoint(headWorldPos);
            HeadLocalRot = Quaternion.Inverse(transform.rotation) * headWorldRot;

            if (anchor != null)
                HeadAnchorRelativePos = anchor.InverseTransformPoint(headWorldPos);

            if (!IsHmdMounted || IsAfk) return;

            UpdateHandTracking();
        }

        /// <summary>
        /// Updates the hand tracking data for both hands, determining visibility and calculating the local position and rotation for network synchronization.
        /// </summary>
        private void UpdateHandTracking()
        {
            bool handTracking =
                OVRInput.IsControllerConnected(OVRInput.Controller.RHand) ||
                OVRInput.IsControllerConnected(OVRInput.Controller.LHand);

            var ctrlR = handTracking ? OVRInput.Controller.RHand : OVRInput.Controller.RTouch;
            var ctrlL = handTracking ? OVRInput.Controller.LHand : OVRInput.Controller.LTouch;

            RightHandVisible = OVRInput.IsControllerConnected(ctrlR);
            LeftHandVisible = OVRInput.IsControllerConnected(ctrlL);

            if (RightHandVisible)
            {
                GetHandNetworkData(ctrlR, handTracking, out var posR, out var rotR, out var anchorPosR);
                RightHandLocalPos = posR;
                RightHandLocalRot = rotR;
                RightHandAnchorRelativePos = anchorPosR;
            }

            if (LeftHandVisible)
            {
                GetHandNetworkData(ctrlL, handTracking, out var posL, out var rotL, out var anchorPosL);
                LeftHandLocalPos = posL;
                LeftHandLocalRot = rotL;
                LeftHandAnchorRelativePos = anchorPosL;
            }
        }

        /// <summary>
        /// Gets the local position and rotation of a hand marker for network synchronization.
        /// </summary>
        private void GetHandNetworkData(OVRInput.Controller ctrl, bool handTracking,
            out Vector3 localPos, out Quaternion localRot, out Vector3 anchorRelativePos)
        {
            const float controllerBackOffset = 0.05f;

            var ctrlLocalPos = OVRInput.GetLocalControllerPosition(ctrl);
            var ctrlLocalRot = OVRInput.GetLocalControllerRotation(ctrl);

            var ctrlWorldPos = trackingSpace.TransformPoint(ctrlLocalPos);
            var ctrlWorldRot = trackingSpace.rotation * ctrlLocalRot;

            Vector3 markerWorldPos;
            Quaternion markerWorldRot;

            if (handTracking)
            {
                Vector3 forward = ctrlWorldRot * Vector3.right;
                Vector3 up = ctrlWorldRot * Vector3.up;
                markerWorldRot = Quaternion.LookRotation(forward, up);
                markerWorldPos = ctrlWorldPos;
            }
            else
            {
                markerWorldRot = ctrlWorldRot;
                markerWorldPos = ctrlWorldPos + markerWorldRot * Vector3.back * controllerBackOffset;
            }

            localPos = transform.InverseTransformPoint(markerWorldPos);
            localRot = Quaternion.Inverse(transform.rotation) * markerWorldRot;
            anchorRelativePos = anchor != null ? anchor.InverseTransformPoint(markerWorldPos) : localPos;
        }

        /// <summary>
        /// Updates the AFK marker visibility and position.
        /// </summary>
        private void UpdateAfkMarker()
        {
            if (afkMarker == null) return;

            if (HasInputAuth || !IsReadyToBeVisible || !IsLocalPlayerReadyToSeeOthers())
            {
                afkMarker.SetActive(false);
                return;
            }

            bool shouldShowAfk = IsAfk && IsRemoteOrShowAvatars();

            if (!shouldShowAfk)
            {
                afkMarker.SetActive(false);
                return;
            }

            afkMarker.SetActive(true);

            if (anchor != null)
            {
                Vector3 worldPos = anchor.TransformPoint(AfkMarkerLocalPos);
                Quaternion worldRot = anchor.rotation * AfkMarkerLocalRot;
                afkMarker.transform.SetPositionAndRotation(worldPos, worldRot);
            }

            if (afkMarker.transform.childCount > 0 && mainCam != null)
            {
                Transform afkCanvas = afkMarker.transform.GetChild(0);
                Vector3 toCam = mainCam.position - afkCanvas.position;
                if (toCam.sqrMagnitude > 0.0001f)
                    afkCanvas.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
            }
        }

        /// <summary>
        /// Signals when the muted state changes to update voice components.
        /// </summary>
        private bool IsRemoteOrShowAvatars() => Net != null && (Net.isRemote || IsRemote || Net.showAvatarsForColocated);

        /// <summary>
        /// Saves the current head position for the AFK marker (anchor-relative).
        /// </summary>
        private void SaveCurrentPositionForAfkMarker()
        {
            if (anchor == null || head == null) return;

            AfkMarkerLocalPos = anchor.InverseTransformPoint(head.position);
            AfkMarkerLocalRot = Quaternion.Inverse(anchor.rotation) * Quaternion.Euler(0f, head.eulerAngles.y, 0f);

            ConsoleMessage.Send(debugMode, $"Avatar - Saved AFK position: {AfkMarkerLocalPos}", Color.cyan);
        }

        /// <summary>
        /// Determines whether the avatar should be displayed based on the current networking state.
        /// </summary>
        private bool ShouldShowAvatar()
        {
            if (!IsPositionInitialized || !IsReadyToBeVisible) return false;
            if (!HasInputAuth && !IsLocalPlayerReadyToSeeOthers()) return false;
            
            bool isCurrentlyStabilizing = HasInputAuth ? isWaitingAfterMount : IsStabilizing;
            if (!IsHmdMounted || isCurrentlyStabilizing) return false;
            
            return IsRemoteOrShowAvatars();
        }

        /// <summary>
        /// Checks if only the name tag should be shown (colocated user with avatar hidden).
        /// </summary>
        private bool ShouldShowNameTagOnly()
        {
            if (!IsPositionInitialized || !IsReadyToBeVisible) return false;     
            if (!HasInputAuth && !IsLocalPlayerReadyToSeeOthers()) return false;
            
            bool isCurrentlyStabilizing = HasInputAuth ? isWaitingAfterMount : IsStabilizing;
            if (!IsHmdMounted || isCurrentlyStabilizing) return false;

            return Net != null && !Net.isRemote && !IsRemote && !Net.showAvatarsForColocated;
        }

        /// <summary>
        /// Determines if the local player is ready to see other avatars based on their own avatar's state.
        /// </summary>
        private bool IsLocalPlayerReadyToSeeOthers()
        {
            if (Net?.Runner == null) return false;
            
            var localPlayerObject = Net.Runner.GetPlayerObject(Net.Runner.LocalPlayer);
            var localAvatar = localPlayerObject?.GetComponent<MUES_AvatarMarker>();
            
            return localAvatar != null && localAvatar.IsReadyToBeVisible;
        }

        /// <summary>
        /// Destroys the local player's own avatar visual parts to prevent self-occlusion.
        /// </summary>
        private IEnumerator DestroyOwnMarkerRoutine()
        {
            if (head != null)
            {
                for (int i = head.childCount - 1; i >= 0; i--)
                    Destroy(head.GetChild(i).gameObject);

                if (headRenderer != null)
                    headRenderer.enabled = false;
            }

            if (handMarkerRight != null) Destroy(handMarkerRight.gameObject);
            if (handMarkerLeft != null) Destroy(handMarkerLeft.gameObject);

            yield return new WaitForEndOfFrame();

            nameTag = null;
            headRenderer = null;
            handRendererR = handRendererL = null;
            handMarkerRight = handMarkerLeft = null;
            nameTagCanvasGroup = null;

            ConsoleMessage.Send(debugMode, "Avatar - Own marker visuals destroyed, head tracking preserved for voice.", Color.cyan);
        }

        /// <summary>
        /// Determines if this avatar's audio should be played for the local user.
        /// </summary>
        private bool ShouldPlayAudioForThisAvatar()
        {
            if (HasInputAuth) return false;

            if (Net == null)
            {
                ConsoleMessage.Send(debugMode, "Avatar - ShouldPlayAudio: No MUES_Networking instance!", Color.red);
                return false;
            }

            bool shouldPlay = Net.isRemote || IsRemote;
            
            string status = shouldPlay ? "ENABLED" : "DISABLED";
            string reason = Net.isRemote ? "Local is remote" : (IsRemote ? "Avatar is remote" : "Both colocated");
            ConsoleMessage.Send(debugMode, $"Avatar - ShouldPlayAudio: {status} for {PlayerName} ({reason})", Color.green);

            return shouldPlay;
        }

        /// <summary>
        /// Configures voice components based on whether this is the local player or a remote player.
        /// </summary>
        private void SetupVoiceComponents()
        {
            if (voiceRecorder == null || voiceSpeaker == null || voiceAudioSource == null)
            {
                ConsoleMessage.Send(debugMode, "Avatar - Voice components not found, skipping voice setup.", Color.yellow);
                return;
            }

            ConsoleMessage.Send(debugMode, $"Avatar - SetupVoiceComponents: isLocal={HasInputAuth}, IsRemote={IsRemote}, net.isRemote={Net?.isRemote}", Color.cyan);

            if (HasInputAuth) SetupLocalVoice();
            else SetupRemoteVoice();

            voiceSetupComplete = true;
            ConsoleMessage.Send(debugMode, $"Avatar - {(HasInputAuth ? "Local" : "Remote")} Voice Setup complete. Avatar.IsRemote: {IsRemote}", Color.green);
        }

        /// <summary>
        /// Sets up the voice components for the local player, enabling transmission and disabling playback to prevent hearing oneself.
        /// </summary>
        private void SetupLocalVoice()
        {
            voiceRecorder.TransmitEnabled = true;
            voiceSpeaker.enabled = false;
            voiceAudioSource.enabled = false;
            voiceAudioSource.mute = true;
            
            ConsoleMessage.Send(debugMode, "Avatar - Voice Recorder TransmitEnabled for local player.", Color.cyan);
        }

        /// <summary>
        /// Sets up the voice components for remote players, enabling playback with spatial audio if appropriate, and ensuring local players do not hear themselves.
        /// </summary>
        private void SetupRemoteVoice()
        {
            bool shouldPlayAudio = ShouldPlayAudioForThisAvatar();
            
            voiceSpeaker.enabled = shouldPlayAudio;
            voiceAudioSource.enabled = shouldPlayAudio;
            voiceAudioSource.mute = !shouldPlayAudio;
            
            if (shouldPlayAudio)
                ConfigureSpatialAudio();
            
            string localStatus = Net?.isRemote == true ? "Remote" : "Colocated";
            string avatarStatus = IsRemote ? "Remote" : "Colocated";
            ConsoleMessage.Send(debugMode, $"Avatar - Voice playback for {PlayerName}: {(shouldPlayAudio ? "ENABLED" : "DISABLED")} (Local is {localStatus}, Avatar is {avatarStatus})", Color.green);
        }

        /// <summary>
        /// Configures the AudioSource for spatial audio playback, setting appropriate parameters for 3D sound in the VR environment.
        /// </summary>
        private void ConfigureSpatialAudio()
        {
            voiceAudioSource.spatialBlend = 1f;
            voiceAudioSource.minDistance = 0.5f;
            voiceAudioSource.maxDistance = 15f;
            voiceAudioSource.rolloffMode = AudioRolloffMode.Linear;
            voiceAudioSource.dopplerLevel = 0f;
            voiceAudioSource.spread = 0f;
            voiceAudioSource.loop = false;
            voiceAudioSource.playOnAwake = false;
            
            ConsoleMessage.Send(debugMode, $"Avatar - Spatial Audio configured for {PlayerName}: spatialBlend=1, minDist=0.5, maxDist=15", Color.green);
        }

        /// <summary>
        /// Ensures voice components remain properly configured during runtime.
        /// </summary>
        private void EnsureVoiceComponentsActive()
        {
            if (voiceSetupPending || !voiceSetupComplete || HasInputAuth) return;
            if (voiceSpeaker == null || voiceAudioSource == null) return;
            
            bool shouldPlayAudio = ShouldPlayAudioForThisAvatar();
            
            bool needsCorrection = voiceSpeaker.enabled != shouldPlayAudio ||
                                   voiceAudioSource.enabled != shouldPlayAudio ||
                                   (shouldPlayAudio && voiceAudioSource.mute && !IsLocallyMuted);
            
            if (needsCorrection)
                ConsoleMessage.Send(debugMode, $"Avatar - Voice state correction for {PlayerName}: shouldPlay={shouldPlayAudio}, speaker={voiceSpeaker.enabled}, audioSrc={voiceAudioSource.enabled}, muted={voiceAudioSource.mute}", Color.yellow);
            
            if (!shouldPlayAudio)
            {
                DisableVoicePlayback();
                return;
            }
            
            EnableVoicePlayback();
        }

        /// <summary>
        /// Disables voice playback components for this avatar, ensuring that the local player does not hear their own voice.
        /// </summary>
        private void DisableVoicePlayback()
        {
            if (voiceSpeaker.enabled)
            {
                voiceSpeaker.enabled = false;
                ConsoleMessage.Send(debugMode, $"Avatar - Disabled voiceSpeaker for {PlayerName} (shouldn't hear).", Color.yellow);
            }
            if (voiceAudioSource.enabled)
            {
                voiceAudioSource.enabled = false;
                ConsoleMessage.Send(debugMode, $"Avatar - Disabled voiceAudioSource for {PlayerName}.", Color.yellow);
            }
        }

        /// <summary>
        /// Enables voice playback components for this avatar if they should be active.
        /// </summary>
        private void EnableVoicePlayback()
        {
            if (!voiceSpeaker.enabled)
            {
                voiceSpeaker.enabled = true;
                ConsoleMessage.Send(debugMode, $"Avatar - Re-enabled voiceSpeaker for {PlayerName}.", Color.yellow);
            }
            
            if (!voiceAudioSource.enabled)
            {
                voiceAudioSource.enabled = true;
                ConfigureSpatialAudio();
                ConsoleMessage.Send(debugMode, $"Avatar - Re-enabled voiceAudioSource for {PlayerName} with spatial audio.", Color.yellow);
            }
            
            if (voiceAudioSource.mute && !IsLocallyMuted)
            {
                voiceAudioSource.mute = false;
                ConsoleMessage.Send(debugMode, $"Avatar - Unmuted voiceAudioSource for {PlayerName}.", Color.yellow);
            }
        }

        /// <summary>
        /// Called when the PlayerName networked property changes. Broadcasts join message for other clients.
        /// </summary>
        private void OnPlayerJoined()
        {
            if (!initialized) return;

            string name = PlayerName.ToString();
            if (string.IsNullOrEmpty(name)) return;

            cachedPlayerName = name;
            UpdateNameTagText();

            if (!HasInputAuth && voiceSetupComplete)
            {
                ConsoleMessage.Send(debugMode, $"Avatar - Player {name} name synced, re-checking voice setup.", Color.cyan);
                SetupVoiceComponents();
            }

            if (!HasInputAuth) 
                ConsoleMessage.Send(true, $"Player \"{name}\" joined the session.", Color.green);
        }

        /// <summary>
        /// Called when this avatar is being despawned. Broadcasts leave message.
        /// </summary>
        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            base.Despawned(runner, hasState);
            
            string name = GetPlayerNameForDespawn(hasState);

            TryUnregisterPlayer();

            bool isLocalPlayer = false;
            try
            {
                isLocalPlayer = hasState && Object != null && Object.IsValid && HasInputAuth;
            }
            catch { }

            if (!isLocalPlayer)
                ConsoleMessage.Send(true, $"Player \"{name}\" left the session.", Color.yellow);
        }

        /// <summary>
        /// Returns the player name for the despawn message.
        /// </summary>
        private string GetPlayerNameForDespawn(bool hasState)
        {
            if (!string.IsNullOrEmpty(cachedPlayerName))
                return cachedPlayerName;

            try
            {
                if (hasState)
                {
                    string netName = PlayerName.ToString();
                    if (!string.IsNullOrEmpty(netName))
                        return netName;
                }
            }
            catch { }

            return "Unknown";
        }

        /// <summary>
        /// Unregisters the player from the session meta if possible.
        /// </summary>
        private void TryUnregisterPlayer()
        {
            if (MUES_SessionMeta.Instance?.Object == null || !MUES_SessionMeta.Instance.Object.IsValid)
                return;

            try
            {
                MUES_SessionMeta.Instance.UnregisterPlayer(Object.InputAuthority);
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(debugMode, $"Avatar - Failed to unregister player: {ex.Message}", Color.yellow);
            }
        }

        /// <summary>
        /// Sets the locally muted state for this avatar. This only affects the local user's audio playback.
        /// </summary>
        public void SetLocallyMuted(bool muted)
        {
            IsLocallyMuted = muted;

            if (voiceAudioSource != null)
                voiceAudioSource.mute = muted;

            ConsoleMessage.Send(debugMode, $"Avatar - {PlayerName} locally muted: {muted}", Color.cyan);
        }
    }
}
