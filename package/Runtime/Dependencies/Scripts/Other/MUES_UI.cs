using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MUES_UI : MonoBehaviour
{
    [Header("Containers")]
    [Tooltip("Container shown during normal operation")]
    public GameObject containerMain;
    [Tooltip("Container shown when user is prompted to enter scan mode")]
    public GameObject containerEnterScan;
    [Tooltip("Container shown when user is loading")]
    public GameObject containerLoading;
    [Tooltip("Container shown when advice is given to the user")]
    public GameObject containerAdvice;
    [Tooltip("Container shown when a device is joined")]
    public GameObject containerJoined;
    [Tooltip("Container shown when a device is disconnected")]
    public GameObject containerDisconnected;
    [Tooltip("Container shown when the user is prompted to rescan")]
    public GameObject containerRescan;

    private GameObject currentContainer;

    private Button disconnectButton;
    private Button hostButton, joinButton;

    private InputField codeInput;
    private Button codeSubmitButton;

    private TextMeshProUGUI codeDisplayText;

    private Button okButtonDisconnected;
    private Button okButtonRescan;
    private Button rescanButton;

    private TextMeshProUGUI adviceText;

    void Start()
    {
        disconnectButton = transform.GetChild(0).GetChild(0).GetComponentInChildren<Button>();
        disconnectButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.LeaveRoom();
            SwitchToContainer(containerDisconnected);
        });
        disconnectButton.interactable = false;

        adviceText = containerAdvice.GetComponentInChildren<TextMeshProUGUI>();

        SetupMainContainer();
        SetupEnterScanContainer();
        SetupJoinedContainer(); 
        SetupDisconnectedContainer();
        SetupRescanContainer();
    }

    private void OnEnable()
    {
        // Networking events    

        MUES_Networking.Instance.OnLobbyCreationStarted += () =>
        {
            SwitchToContainer(containerLoading);
        };

        MUES_Networking.Instance.OnRoomMeshLoadFailed += () =>
        {
            SwitchToContainer(containerRescan);
        };

        MUES_Networking.Instance.OnRoomCreationFailed += () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        MUES_Networking.Instance.OnRoomCreatedSuccessfully += (roomCode) =>
        {
            codeDisplayText.text = $"Lobby Code: {roomCode}";
        };

        MUES_Networking.Instance.OnHostJoined += (playerRef) =>
        {
            UpdateJoinContainerPermissions();
            SwitchToContainer(containerJoined);
        };

        MUES_Networking.Instance.OnColocatedClientJoined += (playerRef) =>
        {
            UpdateJoinContainerPermissions();
            SwitchToContainer(containerJoined);
        };

        MUES_Networking.Instance.OnRemoteClientJoined += (playerRef) =>
        {
            UpdateJoinContainerPermissions();
            SwitchToContainer(containerJoined);
        };

        MUES_Networking.Instance.OnBecameMasterClient += () =>
        {
            UpdateJoinContainerPermissions();
        };

        MUES_Networking.Instance.OnRoomLeft += () =>
        {
            SwitchToContainer(containerDisconnected);
        };

        // Room visualizer events

        MUES_RoomVisualizer.Instance.OnChairPlacementStarted += () =>
        {
            adviceText.text = "Press the PRIMARY TRIGGER to place a chair.\nPress A to end the chair placement.";
            SwitchToContainer(containerAdvice);
        };

        MUES_RoomVisualizer.Instance.OnChairPlacementEnded += () =>
        {
            SwitchToContainer(containerLoading);
        };
    }

    private void OnDisable()
    {
        
    }

    void Update()
    {
        disconnectButton.interactable = MUES_Networking.Instance.isConnected;
    }

    void SetupMainContainer()
    {
        Button[] buttons = containerMain.GetComponentsInChildren<Button>();

        hostButton = buttons[0];
        joinButton = buttons[1];

        hostButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.StartLobbyCreation();
            adviceText.text = "Try to view as much of the room! (until the reticle turns green / yellow)";
            SwitchToContainer(containerAdvice);
        });

        joinButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.EnableQRCodeScanning();
            SwitchToContainer(containerEnterScan);
        });
    }

    void SetupEnterScanContainer()
    {
        codeInput = containerEnterScan.GetComponentInChildren<InputField>();
        codeSubmitButton = codeInput.GetComponentInChildren<Button>();

        codeSubmitButton.onClick.AddListener(() =>
        {
            string code = codeInput.text;
            if (!string.IsNullOrEmpty(code))
            {
                MUES_Networking.Instance.JoinSessionFromCode(code);
                SwitchToContainer(containerLoading);
            }
        });
    }

    void SetupJoinedContainer()
    {
        codeDisplayText = containerJoined.GetComponentInChildren<TextMeshProUGUI>();
    }

    void SetupDisconnectedContainer()
    {
        okButtonDisconnected = containerDisconnected.GetComponentInChildren<Button>();
        okButtonDisconnected.onClick.AddListener(() =>
        {
            SwitchToContainer(containerMain);
        });
    }

    void SetupRescanContainer()
    {
        Button[] buttons = containerMain.GetComponentsInChildren<Button>();

        rescanButton = buttons[0];
        okButtonRescan = buttons[1];

        rescanButton.onClick.AddListener(() =>
        {
            MUES_Networking.Instance.LaunchSpaceSetup();
            SwitchToContainer(containerMain);
        });

        okButtonRescan.onClick.AddListener(() =>
        {
            SwitchToContainer(containerMain);
        });
    }

    void SwitchToContainer(GameObject newContainer)
    {
        currentContainer.SetActive(false);

        currentContainer = newContainer;
        currentContainer.SetActive(true);
    }

    void UpdateJoinContainerPermissions()
    {

    }
}
