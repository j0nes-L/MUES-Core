using Fusion;
using Meta.XR.MultiplayerBlocks.Shared;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using Photon.Voice.Unity;

public class MUES_AvatarMarker : MUES_AnchoredNetworkBehaviour
{
    [Header("Avatar Marker Settings")]
    [Tooltip("The unique identifier for this user. (Debug only)")]
    [Networked] public NetworkString<_32> UserGuid { get; set; }

    [Tooltip("The display name of the player. (Debug only)")]
    [Networked, OnChangedRender(nameof(OnPlayerJoined))] public NetworkString<_64> PlayerName { get; set; }

    [Tooltip("If true, the local player's avatar parts will be destroyed to avoid self-occlusion.")]
    public bool destroyOwnMarker = true;
    [Tooltip("If true, debug information will be logged to the console.")]
    public bool debugMode = true;

    [HideInInspector][Networked] public NetworkBool IsHmdMounted { get; set; } = true; // Default to true so avatars are visible by default
    [HideInInspector][Networked] public NetworkBool IsStabilizing { get; set; } = false; // True while avatar is stabilizing after HMD mount
    [HideInInspector][Networked, OnChangedRender(nameof(OnMutedChanged))] public NetworkBool IsMuted { get; set; } = false; // True if this player is muted
    [HideInInspector][Networked] public NetworkBool IsMutedByHost { get; set; } = false; // True if this player was muted by the host (persistent mute)

    [HideInInspector][Networked] public Vector3 HeadLocalPos { get; set; }  // Local position of the head relative to the avatar marker
    [HideInInspector][Networked] public Quaternion HeadLocalRot { get; set; }   // Local rotation of the head relative to the avatar marker

    [HideInInspector][Networked] public Vector3 RightHandLocalPos { get; set; } // Local position of the right hand relative to the avatar marker
    [HideInInspector][Networked] public Vector3 LeftHandLocalPos { get; set; }  // Local position of the left hand relative to the avatar marker

    [HideInInspector][Networked] public Quaternion RightHandLocalRot { get; set; }  // Local rotation of the right hand relative to the avatar marker
    [HideInInspector][Networked] public Quaternion LeftHandLocalRot { get; set; }   // Local rotation of the left hand relative to the avatar marker

    [HideInInspector][Networked] public NetworkBool RightHandVisible { get; set; }  // Visibility state of the right hand marker
    [HideInInspector][Networked] public NetworkBool LeftHandVisible { get; set; }   // Visibility state of the left hand marker

    [HideInInspector][Networked] public NetworkBool IsRemote { get; set; }  // True if this avatar belongs to a remote player

    [HideInInspector][Networked] public NetworkBool IsAfk { get; set; }  // True when HMD is unmounted or stabilizing
    [HideInInspector][Networked] public Vector3 AfkMarkerLocalPos { get; set; }  // Last known position for AFK marker (anchor-relative)
    [HideInInspector][Networked] public Quaternion AfkMarkerLocalRot { get; set; }  // Last known rotation for AFK marker (anchor-relative)

    [HideInInspector] public AudioSource voiceAudioSource;   // The AudioSource playing the users voice

    private GameObject afkMarker; // AFK marker object 
    private Transform mainCam, trackingSpace;   // OVR Camera Rig tracking space
    private Transform head, nameTag, handMarkerRight, handMarkerLeft;   // Avatar parts

    private MeshRenderer headRenderer, handRendererR, handRendererL;    // Renderers for visibility control
    private CanvasGroup nameTagCanvasGroup; // Canvas group for name tag visibility
    private TextMeshProUGUI nameText, nameTextAfk;   // Text component for displaying player name

    private readonly float rotationSmoothSpeed = 15f;    // Speed for smooth name tag rotation
    private readonly float handSmoothTime = 0.06f;  // Smoothing time for hand marker movement

    private const float defaultNameTagHeight = 0.15f;   // Default height offset for name tag above head
    private const float colocatedNameTagHeight = 0.25f;  // Height offset for name tag when avatar is hidden (colocated)
    private const string fallbackPlayerName = "MUES-User"; // Fallback name when username cannot be fetched

    private Vector3 rightHandVel, leftHandVel;  // Velocity references for SmoothDamp
    private Quaternion nameSmoothRot, rightHandSmoothRot, leftHandSmoothRot;   // Smoothed rotation for name tag

    private Recorder voiceRecorder; // The Recorder component for the voice chat
    private Speaker voiceSpeaker;   // The Speaker component for the voice chat

    private bool isWaitingAfterMount = false; // Flag to delay visibility after HMD mount
    private Vector3 originalNameTagLocalPos; // Original local position of name tag
    
    private string cachedPlayerName; // Locally cached player name for use when network state is unavailable

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
        if (Object.HasInputAuthority)
        {
            StartCoroutine(DelayedHMDMountVisibility());
            UpdateAfkMarker();

            if (!IsMutedByHost && voiceRecorder != null)
            {
                voiceRecorder.TransmitEnabled = true;
                ConsoleMessage.Send(debugMode, "HMD Mounted - Microphone unmuted.", Color.cyan);
            }
        }

    }

    /// <summary>
    /// Delays the avatar visibility after HMD mount by 3 seconds.
    /// </summary>
    private IEnumerator DelayedHMDMountVisibility()
    {
        if (ShouldShowAvatarIgnoringHmd())
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
        if (Object.HasInputAuthority)
        {
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
    }

    /// <summary>
    /// Gets executed when the avatar marker is spawned in the network.
    /// </summary>
    public override void Spawned()
    {
        head = transform.Find("Head");
        headRenderer = head.GetComponentInChildren<MeshRenderer>();

        nameTag = head.GetChild(0);
        nameTagCanvasGroup = nameTag.GetComponent<CanvasGroup>();
        nameText = nameTagCanvasGroup.GetComponentInChildren<TextMeshProUGUI>();

        handMarkerRight = transform.Find("HandMarkerR");
        handRendererR = handMarkerRight.GetComponentInChildren<MeshRenderer>();

        handMarkerLeft = transform.Find("HandMarkerL");
        handRendererL = handMarkerLeft.GetComponentInChildren<MeshRenderer>();

        voiceRecorder = head.GetComponent<Recorder>();
        voiceSpeaker = head.GetComponent<Speaker>();
        voiceAudioSource = head.GetComponent<AudioSource>();

        headRenderer.enabled = handRendererR.enabled = handRendererL.enabled = false;
        nameTagCanvasGroup.alpha = 0f;

        afkMarker = transform.GetChild(3).gameObject;
        afkMarker.SetActive(false);

        nameTextAfk = afkMarker.GetComponentInChildren<TextMeshProUGUI>();

        StartCoroutine(WaitForComponentInit());
    }

    /// <summary>
    /// Waits for component initialization and sets up the avatar marker based on networked data.
    /// </summary>
    public IEnumerator WaitForComponentInit()
    {
        yield return InitAnchorRoutine();

        while (MUES_SessionMeta.Instance == null)
        {
            ConsoleMessage.Send(debugMode, "Avatar - Waiting for Session Meta...", Color.yellow);
            yield return null;
        }

        var meta = MUES_SessionMeta.Instance;

        while (Camera.main == null)
        {
            ConsoleMessage.Send(debugMode, "Avatar - Waiting for Main Camera...", Color.yellow);
            yield return null;
        }

        while (trackingSpace == null)
        {
            ConsoleMessage.Send(debugMode, "Avatar - Waiting for OVR Camera Rig...", Color.yellow);
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig != null) trackingSpace = rig.trackingSpace;

            yield return null;
        }

        mainCam = Camera.main.transform;

        if (Object.HasInputAuthority)
        {
            var camPos = mainCam.position;
            float markerY = anchor != null ? anchor.position.y : 0f;

            var markerWorldPos = new Vector3(camPos.x, markerY, camPos.z);
            var markerWorldRot = Quaternion.Euler(0f, mainCam.eulerAngles.y, 0f);

            transform.SetPositionAndRotation(markerWorldPos, markerWorldRot);
            WorldToAnchor();

            UserGuid = Guid.NewGuid().ToString();
            IsHmdMounted = OVRManager.isHmdPresent;

            if (MUES_Networking.Instance != null) IsRemote = MUES_Networking.Instance.isRemote;
        }
        else
        {
            while (string.IsNullOrEmpty(UserGuid.ToString()))
            {
                ConsoleMessage.Send(debugMode, "Avatar - Waiting for networked user GUID...", Color.yellow);
                yield return null;
            }

            AnchorToWorld();
            ConsoleMessage.Send(debugMode, $"Avatar - Remote user: Using networked anchor offset/rotation for UserGuid={UserGuid}", Color.green);
        }

        if (nameTag != null)
        {
            originalNameTagLocalPos = nameTag.localPosition;
            nameSmoothRot = nameTag.rotation;
        }

        PlatformInit.GetEntitlementInformation(info =>
        {
            if (info.IsEntitled && !string.IsNullOrEmpty(info.OculusUser?.DisplayName))
            {
                PlayerName = info.OculusUser.DisplayName;
                if (Object.HasInputAuthority) MUES_SessionMeta.Instance.RegisterPlayer(Object.InputAuthority, info.OculusUser.DisplayName);
            }
            else
            {
                PlayerName = fallbackPlayerName;
                if (Object.HasInputAuthority) MUES_SessionMeta.Instance.RegisterPlayer(Object.InputAuthority, fallbackPlayerName);
                ConsoleMessage.Send(debugMode, $"Avatar - Using fallback name: {fallbackPlayerName}", Color.yellow);
            }

            UpdateNameTagText();
        });

        if (nameTag != null)
        {
            nameTagCanvasGroup.alpha = 1f;
        }

        SetupVoiceComponents();

        if (destroyOwnMarker && Object.HasInputAuthority) StartCoroutine(DestroyOwnMarkerRoutine());
        if (headRenderer != null) headRenderer.enabled = ShouldShowAvatar();
        initialized = true;

        ConsoleMessage.Send(debugMode, "Avatar - Component Init ready. - Avatar Setup complete", Color.green);
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

    /// <summary>
    /// Gets executed every frame to update the avatar marker's position, rotation, and visibility.
    /// </summary>
    public override void Render()
    {
        if (!initialized) return;
        if (!Object.HasInputAuthority && anchorReady) AnchorToWorld();

        bool isCurrentlyStabilizing = Object.HasInputAuthority ? isWaitingAfterMount : IsStabilizing;

        bool showFullAvatar = ShouldShowAvatar();
        bool showNameTagOnly = ShouldShowNameTagOnly();
        bool showNameTag = showFullAvatar || showNameTagOnly;

        if (!Object.HasInputAuthority)
            UpdateAfkMarker();

        if (nameTagCanvasGroup != null)
        {
            float targetAlpha = showNameTag ? 1f : 0f;
            if (Mathf.Abs(nameTagCanvasGroup.alpha - targetAlpha) > 0.01f)
                nameTagCanvasGroup.alpha = Mathf.MoveTowards(nameTagCanvasGroup.alpha, targetAlpha, Time.deltaTime * 5f);
        }

        if (isCurrentlyStabilizing)
        {
            if (headRenderer != null) headRenderer.enabled = false;
            if (handRendererR != null) handRendererR.enabled = false;
            if (handRendererL != null) handRendererL.enabled = false;
            return;
        }

        if (nameTag != null && showNameTag)
        {
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

        if (head != null)
        {
            var headTargetPos = transform.TransformPoint(HeadLocalPos);
            var headTargetRot = transform.rotation * HeadLocalRot;
            head.SetPositionAndRotation(headTargetPos, headTargetRot);

            if (headRenderer != null) headRenderer.enabled = showFullAvatar;
        }

        if (handMarkerRight != null)
        {
            handRendererR.enabled = showFullAvatar && RightHandVisible;
            if (showFullAvatar && RightHandVisible)
            {
                var handTargetPosR = transform.TransformPoint(RightHandLocalPos);
                var handSmoothPosR = Vector3.SmoothDamp(handMarkerRight.position, handTargetPosR, ref rightHandVel, handSmoothTime);
                var handTargetRotR = transform.rotation * RightHandLocalRot;
                rightHandSmoothRot = Quaternion.Slerp(handMarkerRight.rotation, handTargetRotR, Time.deltaTime * rotationSmoothSpeed);
                handMarkerRight.SetPositionAndRotation(handSmoothPosR, rightHandSmoothRot);
            }
        }

        if (handMarkerLeft != null)
        {
            handRendererL.enabled = showFullAvatar && LeftHandVisible;
            if (showFullAvatar && LeftHandVisible)
            {
                var handTargetPosL = transform.TransformPoint(LeftHandLocalPos);
                var handSmoothPosL = Vector3.SmoothDamp(handMarkerLeft.position, handTargetPosL, ref leftHandVel, handSmoothTime);
                var handTargetRotL = transform.rotation * LeftHandLocalRot;
                leftHandSmoothRot = Quaternion.Slerp(handMarkerLeft.rotation, handTargetRotL, Time.deltaTime * rotationSmoothSpeed);
                handMarkerLeft.SetPositionAndRotation(handSmoothPosL, leftHandSmoothRot);
            }
        }
    }

    /// <summary>
    /// Gets executed every physics frame to update the avatar marker's networked data.
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!initialized || trackingSpace == null || !anchorReady) return;

        if (Object.HasInputAuthority) WorldToAnchor();
        else return;

        mainCam.GetPositionAndRotation(out var headWorldPos, out var headWorldRot);

        HeadLocalPos = transform.InverseTransformPoint(headWorldPos);
        HeadLocalRot = Quaternion.Inverse(transform.rotation) * headWorldRot;

        if (ShouldShowAvatar())
        {
            var handTracking =
                OVRInput.IsControllerConnected(OVRInput.Controller.RHand) ||
                OVRInput.IsControllerConnected(OVRInput.Controller.LHand);

            var ctrlR = handTracking ? OVRInput.Controller.RHand : OVRInput.Controller.RTouch;
            var ctrlL = handTracking ? OVRInput.Controller.LHand : OVRInput.Controller.LTouch;

            var rightConnected = OVRInput.IsControllerConnected(ctrlR);
            var leftConnected = OVRInput.IsControllerConnected(ctrlL);

            RightHandVisible = rightConnected;
            LeftHandVisible = leftConnected;

            const float controllerBackOffset = 0.05f;

            if (rightConnected)
            {
                var ctrlLocalPosR = OVRInput.GetLocalControllerPosition(ctrlR);
                var ctrlLocalRotR = OVRInput.GetLocalControllerRotation(ctrlR);

                var ctrlWorldPosR = trackingSpace.TransformPoint(ctrlLocalPosR);
                var ctrlWorldRotR = trackingSpace.rotation * ctrlLocalRotR;

                Quaternion markerWorldRotR;
                Vector3 markerWorldPosR;

                if (handTracking)
                {
                    Vector3 forwardR = ctrlWorldRotR * Vector3.right;
                    Vector3 upR = ctrlWorldRotR * Vector3.up;
                    markerWorldRotR = Quaternion.LookRotation(forwardR, upR);
                    markerWorldPosR = ctrlWorldPosR;
                }
                else
                {
                    markerWorldRotR = ctrlWorldRotR;
                    markerWorldPosR = ctrlWorldPosR + markerWorldRotR * Vector3.back * controllerBackOffset;
                }

                RightHandLocalPos = transform.InverseTransformPoint(markerWorldPosR);
                RightHandLocalRot = Quaternion.Inverse(transform.rotation) * markerWorldRotR;
            }

            if (leftConnected)
            {
                var ctrlLocalPosL = OVRInput.GetLocalControllerPosition(ctrlL);
                var ctrlLocalRotL = OVRInput.GetLocalControllerRotation(ctrlL);

                var ctrlWorldPosL = trackingSpace.TransformPoint(ctrlLocalPosL);
                var ctrlWorldRotL = trackingSpace.rotation * ctrlLocalRotL;

                Quaternion markerWorldRotL;
                Vector3 markerWorldPosL;

                if (handTracking)
                {
                    Vector3 forwardL = ctrlWorldRotL * Vector3.right;
                    Vector3 upL = ctrlWorldRotL * Vector3.up;
                    markerWorldRotL = Quaternion.LookRotation(forwardL, upL);
                    markerWorldPosL = ctrlWorldPosL;
                }
                else
                {
                    markerWorldRotL = ctrlWorldRotL;
                    markerWorldPosL = ctrlWorldPosL + markerWorldRotL * Vector3.back * controllerBackOffset;
                }

                LeftHandLocalPos = transform.InverseTransformPoint(markerWorldPosL);
                LeftHandLocalRot = Quaternion.Inverse(transform.rotation) * markerWorldRotL;
            }
        }
    }

    /// <summary>
    /// Updates the AFK marker visibility and position.
    /// </summary>
    private void UpdateAfkMarker()
    {
        if (Object.HasInputAuthority)
        {
            afkMarker.SetActive(false);
            return;
        }

        bool shouldShowAfk = IsAfk && ShouldShowAfkMarker();

        if (shouldShowAfk)
        {
            afkMarker.SetActive(true);

            if (anchor != null)
            {
                Vector3 worldPos = anchor.TransformPoint(AfkMarkerLocalPos);
                Quaternion worldRot = anchor.rotation * AfkMarkerLocalRot;
                afkMarker.transform.SetPositionAndRotation(worldPos, worldRot);
            }

            Transform afkCanvas = afkMarker.transform.GetChild(0);
            if (afkCanvas != null && mainCam != null)
            {
                Vector3 toCam = mainCam.position - afkCanvas.position;
                if (toCam.sqrMagnitude > 0.0001f)
                {
                    afkCanvas.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                }
            }
        }
        else afkMarker.SetActive(false);
    }

    /// <summary>
    /// Determines whether the AFK marker should be shown for this avatar.
    /// </summary>
    private bool ShouldShowAfkMarker()
    {
        var net = MUES_Networking.Instance;

        if (net == null) return false;
        if (net.isRemote) return true;
        if (IsRemote) return true;

        return net.showAvatarsForColocated;
    }

    /// <summary>
    /// Saves the current head position for the AFK marker (anchor-relative).
    /// </summary>
    private void SaveCurrentPositionForAfkMarker()
    {
        if (head == null || anchor == null) return;

        AfkMarkerLocalPos = anchor.InverseTransformPoint(head.position);

        Quaternion headYRotation = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        AfkMarkerLocalRot = Quaternion.Inverse(anchor.rotation) * headYRotation;

        ConsoleMessage.Send(debugMode, $"Avatar - Saved AFK position: {AfkMarkerLocalPos}", Color.cyan);
    }

    /// <summary>
    /// Determines whether the avatar should be displayed based on the current networking state.
    /// </summary>
    private bool ShouldShowAvatar()
    {
        bool isCurrentlyStabilizing = Object.HasInputAuthority ? isWaitingAfterMount : IsStabilizing;

        if (!IsHmdMounted || isCurrentlyStabilizing) return false;

        var net = MUES_Networking.Instance;

        if (net == null) return false;
        if (net.isRemote) return true;
        if (IsRemote) return true;

        return net.showAvatarsForColocated;
    }

    /// <summary>
    /// Checks if only the name tag should be shown (colocated user with avatar hidden).
    /// </summary>
    private bool ShouldShowNameTagOnly()
    {
        bool isCurrentlyStabilizing = Object.HasInputAuthority ? isWaitingAfterMount : IsStabilizing;

        if (!IsHmdMounted || isCurrentlyStabilizing) return false;

        var net = MUES_Networking.Instance;
        if (net == null) return false;

        if (net.isRemote) return false;
        if (IsRemote) return false;
        if (net.showAvatarsForColocated) return false;

        return true;
    }

    /// <summary>
    /// Checks if avatar would be visible (ignoring HMD state) - used for mount delay logic.
    /// </summary>
    private bool ShouldShowAvatarIgnoringHmd()
    {
        var net = MUES_Networking.Instance;

        if (net == null) return false;
        if (net.isRemote) return true;
        if (IsRemote) return true;

        return net.showAvatarsForColocated;
    }

    /// <summary>
    /// Destroys the local player's own avatar visual parts to prevent self-occlusion.
    /// </summary>
    private IEnumerator DestroyOwnMarkerRoutine()
    {
        if (head != null)
        {
            for (int i = head.childCount - 1; i >= 0; i--)
            {
                Transform child = head.GetChild(i);
                Destroy(child.gameObject);
            }

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
    /// Configures voice components based on whether this is the local player or a remote player.
    /// </summary>
    private void SetupVoiceComponents()
    {
        MUES_Networking net = MUES_Networking.Instance;

        bool isLocal = Object.HasInputAuthority;
        bool amIRemote = net == null || net.isRemote;

        voiceRecorder.TransmitEnabled = isLocal;
        voiceRecorder.enabled = isLocal;

        if (isLocal) ConsoleMessage.Send(debugMode, "Avatar - Voice Recorder enabled for local player.", Color.cyan);

        bool enablePlayback = !isLocal && (amIRemote || IsRemote);

        voiceSpeaker.enabled = enablePlayback;

        voiceAudioSource.enabled = enablePlayback;
        voiceAudioSource.mute = !enablePlayback;

        string playerType = isLocal ? "Local" : "Remote";
        string audioStatus = enablePlayback ? "ON" : "OFF (Muted/Local)";

        ConsoleMessage.Send(debugMode, $"Avatar - {playerType} Voice Setup. Rec: {(isLocal ? "ON" : "OFF")}, Playback: {audioStatus}, IsRemote: {IsRemote}", Color.green);
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

        if (!Object.HasInputAuthority) ConsoleMessage.Send(true, $"Player \"{name}\" joined the session.", Color.green);
    }

    /// <summary>
    /// Called when this avatar is being despawned. Broadcasts leave message.
    /// </summary>
    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        base.Despawned(runner, hasState);
        string name = cachedPlayerName;

        if (string.IsNullOrEmpty(name))
        {
            try
            {
                if (hasState)
                    name = PlayerName.ToString();
            }
            catch { }
        }

        if (string.IsNullOrEmpty(name))
            name = "Unknown";

        if (MUES_SessionMeta.Instance != null && MUES_SessionMeta.Instance.Object != null && MUES_SessionMeta.Instance.Object.IsValid)
        {
            try
            {
                MUES_SessionMeta.Instance.UnregisterPlayer(Object.InputAuthority);
            }
            catch (Exception ex)
            {
                ConsoleMessage.Send(debugMode, $"Avatar - Failed to unregister player: {ex.Message}", Color.yellow);
            }
        }

        bool isLocalPlayer = false;
        try
        {
            isLocalPlayer = hasState && Object != null && Object.IsValid && Object.HasInputAuthority;
        }
        catch { }

        if (!isLocalPlayer)
            ConsoleMessage.Send(true, $"Player \"{name}\" left the session.", Color.yellow);
    }

    /// <summary>
    /// Sets the muted state for this avatar. Only callable with state authority.
    /// </summary>
    public void SetMuted(bool muted)
    {
        if (!Object.HasStateAuthority)
        {
            RPC_RequestSetMuted(muted);
            return;
        }

        IsMuted = muted;
        IsMutedByHost = muted;

        ApplyMutedState();
        ConsoleMessage.Send(debugMode, $"Avatar - {PlayerName} muted by host: {muted}", Color.cyan);
    }

    /// <summary>
    /// RPC to request mute state change from clients without state authority.
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSetMuted(bool muted) => SetMuted(muted);

    /// <summary>
    /// Called when IsMuted networked property changes.
    /// </summary>
    private void OnMutedChanged() => ApplyMutedState();

    /// <summary>
    /// Applies the muted state to the voice audio source and recorder.
    /// </summary>
    private void ApplyMutedState()
    {
        voiceAudioSource.mute = IsMuted || Object.HasInputAuthority;

        if (Object.HasInputAuthority && voiceRecorder != null)
        {
            bool canTransmit = !IsMutedByHost && IsHmdMounted;
            voiceRecorder.TransmitEnabled = canTransmit;
            ConsoleMessage.Send(debugMode, $"Avatar - Transmit enabled: {canTransmit} (MutedByHost: {IsMutedByHost}, HmdMounted: {IsHmdMounted})", Color.cyan);
        }
    }
}
