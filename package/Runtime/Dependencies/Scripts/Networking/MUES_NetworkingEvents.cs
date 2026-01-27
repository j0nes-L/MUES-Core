using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MUES_NetworkingEvents : MonoBehaviour, INetworkRunnerCallbacks
{
    [Tooltip("Enable to see debug messages in the console.")]
    public bool debugMode = false;

    #region General

    /// <summary>
    /// Gets called when a player joins the session.
    /// </summary>
    public virtual void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        ConsoleMessage.Send(debugMode, $"Player {player} joined.", Color.green);

        var net = MUES_Networking.Instance;

        if (runner.IsSharedModeMasterClient) net._previousMasterClient = runner.LocalPlayer;  
        if (player != runner.LocalPlayer) return;
    
        if (runner.IsSharedModeMasterClient)
        {
            if (net.isJoiningAsClient)
            {
                ConsoleMessage.Send(debugMode, "ERROR: Joined as Master but expected Client (Room not found?). Leaving.", Color.red);
                net.LeaveRoom();
                return;
            }
            
            net.HandleHostJoin(player);
        }
        else StartCoroutine(net.HandleNonHostJoin(player));
    }

    /// <summary>
    /// Gets called when a player leaves the session.
    /// </summary>
    public virtual void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        ConsoleMessage.Send(debugMode, $"Player {player} left.", Color.yellow);
        MUES_Networking.Instance.CheckIfNewMaster(player);
    }   

    /// <summary>
    /// Gets called when the runner shuts down.
    /// </summary>
    public virtual void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
    {
        MUES_Networking net = MUES_Networking.Instance;

        ConsoleMessage.Send(debugMode, "Network Runner Shut Down -- Cleaning up.", Color.yellow);

        if (net != null)
        {
            if(MUES_Networking.Instance.spatialAnchorCore != null)
                MUES_Networking.Instance.spatialAnchorCore.EraseAllAnchors();
            
            if (net.isRemote && MUES_RoomVisualizer.Instance != null) 
                MUES_RoomVisualizer.Instance.ClearRoomVisualization();
        }
    }

    #endregion

    #region Other INetworkRunnerCallbacks Methods

    public virtual void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public virtual void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
    public virtual void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
    public virtual void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
    public virtual void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
    public virtual void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
    public virtual void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
    public virtual void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
    public virtual void OnInput(NetworkRunner runner, NetworkInput input) { }
    public virtual void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
    public virtual void OnConnectedToServer(NetworkRunner runner) { }
    public virtual void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
    public virtual void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
    public virtual void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
    public virtual void OnSceneLoadDone(NetworkRunner runner) { }
    public virtual void OnSceneLoadStart(NetworkRunner runner) { }

    #endregion
}